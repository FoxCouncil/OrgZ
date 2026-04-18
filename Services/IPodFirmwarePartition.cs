// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OrgZ.Services;

/// <summary>
/// Reads the iPod's Apple firmware partition directly from raw disk sectors to extract
/// the <c>osos</c> image's version field. Works on any iPod where we can open
/// <c>\\.\PhysicalDriveN</c> for read access (admin required), bypassing both SCSI
/// vendor commands AND interposing USB bridges like iFlash — because we're reading
/// the actual ATA blocks, not asking the bridge to interpret them.
///
/// iPod firmware partition layout:
///   LBA 0:      MBR with 4-entry partition table (entry at offset 0x1BE)
///   Partition 1 (firmware, typically ~40MB):
///     offset 0x100: magic bytes "{{~~ ppBoOt" (identifies the partition as iPod firmware)
///     offset 0x200: firmware image directory. 40 bytes per entry:
///       [00..03] name       — 4 ASCII chars, stored byte-reversed in memory
///                              ("osos" appears as "soso" on disk due to little-endian ULONG reads)
///       [04..07] id         — incrementing index
///       [08..0B] devOffset  — byte offset from partition start to the image payload
///       [0C..0F] length     — image length in bytes
///       [10..13] addr       — RAM load address
///       [14..17] entryOffset— offset into image where execution starts
///       [18..1B] checksum   — image checksum
///       [1C..1F] vers       — version ULONG (Apple-internal encoding)
///       [20..23] loadAddr   — secondary load address
///     Up to 20 entries, terminated when name == 0x00000000
///
/// Reference: https://www.ipodlinux.org/Firmware/
/// </summary>
public static class IPodFirmwarePartition
{
#if WINDOWS

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandleWrapper CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        SafeFileHandleWrapper hFile,
        IntPtr lpBuffer,
        uint nNumberOfBytesToRead,
        out uint lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(
        SafeFileHandleWrapper hFile,
        long liDistanceToMove,
        out long lpNewFilePointer,
        uint dwMoveMethod);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandleWrapper hDevice,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    private sealed class SafeFileHandleWrapper : Microsoft.Win32.SafeHandles.SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFileHandleWrapper() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_BEGIN = 0;
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    // IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = CTL_CODE(IOCTL_DISK_BASE=0x07, 0x0028, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_GEOMETRY
    {
        public long Cylinders;
        public uint MediaType;
        public uint TracksPerCylinder;
        public uint SectorsPerTrack;
        public uint BytesPerSector;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISK_GEOMETRY_EX
    {
        public DISK_GEOMETRY Geometry;
        public long DiskSize;
        public byte Data;
    }

#endif

    /// <summary>
    /// Reads the iPod firmware image directory from raw disk sectors and returns the
    /// decoded Apple OS version. Resolves in two steps:
    ///   1. Parse the firmware image directory on partition 1 to find the <c>osos</c>
    ///      entry's <c>vers</c> field (the Apple-internal 32-bit build ID).
    ///   2. Look that build ID up in <see cref="IPodBuildIdDatabase"/> keyed by
    ///      <paramref name="ipodGeneration"/> — Apple encrypts the osos payload on
    ///      5G+ so the human version string is only reachable via translation.
    /// Returns null if the partition can't be read, the directory can't be found,
    /// or the (generation, buildId) pair isn't in the lookup table yet.
    /// </summary>
    public static string? TryReadOsosVersion(string driveLetter, string? ipodGeneration, out string diagnostic)
    {
        var log = new StringBuilder();
        log.AppendLine($"=== Firmware partition read attempt on {driveLetter} ===");

#if WINDOWS
        var letter = driveLetter.TrimEnd('\\', '/');
        if (letter.Length != 2 || letter[1] != ':')
        {
            log.AppendLine($"FAIL: invalid drive letter '{driveLetter}'");
            diagnostic = log.ToString();
            return null;
        }

        // Step 1: resolve volume letter → physical drive number via the IOCTL we already use elsewhere.
        int physicalDriveNumber;
        var volumePath = $@"\\.\{letter}";
        log.AppendLine($"Resolving {volumePath} → physical drive...");

        using (var volHandle = CreateFile(volumePath, 0,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero))
        {
            if (volHandle.IsInvalid)
            {
                log.AppendLine($"  CreateFile failed, err={Marshal.GetLastWin32Error()}");
                diagnostic = log.ToString();
                return null;
            }

            int sdnSize = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
            var sdnBuf = Marshal.AllocHGlobal(sdnSize);
            try
            {
                if (!DeviceIoControl(volHandle, IOCTL_STORAGE_GET_DEVICE_NUMBER,
                    IntPtr.Zero, 0, sdnBuf, (uint)sdnSize, out _, IntPtr.Zero))
                {
                    log.AppendLine($"  IOCTL_STORAGE_GET_DEVICE_NUMBER failed, err={Marshal.GetLastWin32Error()}");
                    diagnostic = log.ToString();
                    return null;
                }
                var sdn = Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(sdnBuf);
                physicalDriveNumber = (int)sdn.DeviceNumber;
                log.AppendLine($"  PhysicalDrive{physicalDriveNumber}");
            }
            finally
            {
                Marshal.FreeHGlobal(sdnBuf);
            }
        }

        // Step 2: open the raw physical drive for reading. Requires admin on Win10+.
        var physicalPath = $@"\\.\PhysicalDrive{physicalDriveNumber}";
        log.AppendLine($"Opening {physicalPath}...");
        using var disk = CreateFile(physicalPath, GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (disk.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            log.AppendLine($"  CreateFile failed, err={err}");
            if (err == 5)
            {
                log.AppendLine("  → requires Administrator");
            }
            diagnostic = log.ToString();
            return null;
        }

        // Step 2b: query the drive's logical sector size. All ReadFile calls on a raw
        // disk handle require offset, length, AND the user buffer pointer to be aligned
        // to this value. Typical values: 512 (512e drives) or 4096 (4Kn drives, modern
        // SSDs, and some iFlash models). Without this, ReadFile returns err=87.
        int sectorSize = 512;
        int geomSize = Marshal.SizeOf<DISK_GEOMETRY_EX>();
        var geomBuf = Marshal.AllocHGlobal(geomSize);
        try
        {
            if (DeviceIoControl(disk, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX,
                IntPtr.Zero, 0, geomBuf, (uint)geomSize, out _, IntPtr.Zero))
            {
                var geom = Marshal.PtrToStructure<DISK_GEOMETRY_EX>(geomBuf);
                sectorSize = (int)geom.Geometry.BytesPerSector;
                log.AppendLine($"  Logical sector size: {sectorSize} bytes");
            }
            else
            {
                log.AppendLine($"  IOCTL_DISK_GET_DRIVE_GEOMETRY_EX failed, err={Marshal.GetLastWin32Error()}, defaulting to 512");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(geomBuf);
        }

        // Step 3: read the MBR to locate partition 1 for diagnostic output, then scan
        // 32 MB of the partition for the "osos" image name bytes. The "{{~~" magic at
        // partition+0x100 is actually an Apple STOP-sign ASCII art deterrent copied
        // forward on every firmware restore since 2001, so we can't use it as a
        // reliable anchor. The "osos" 4-byte name is always present inside the firmware
        // image directory (wherever Apple decided to put it on that generation) unless
        // the partition has been truly wiped. 32 MB covers the full firmware directory
        // + the first several megabytes of osos payload on every iPod generation.
        var mbr = ReadBytes(disk, 0, sectorSize, sectorSize);
        if (mbr == null)
        {
            log.AppendLine($"  MBR read failed, err={Marshal.GetLastWin32Error()}");
            diagnostic = log.ToString();
            return null;
        }
        uint part1LbaStart = ReadUInt32LE(mbr, 0x1BE + 8);
        uint part1LbaCount = ReadUInt32LE(mbr, 0x1BE + 12);
        byte part1Type     = mbr[0x1BE + 4];
        log.AppendLine($"  MBR partition 1: type=0x{part1Type:X2} start LBA={part1LbaStart} count={part1LbaCount}");

        // iPod MBR was originally written with 512-byte LBAs but iFlash adapters
        // may re-present the drive with a different sector size. The partition bytes
        // themselves didn't move, but the LBA interpretation depends on which side
        // wrote the MBR last. We just try 512 first, then sectorSize if that fails.
        long part1ByteStart = (long)part1LbaStart * sectorSize;
        log.AppendLine($"  Partition 1 byte start (assuming LBA × sectorSize): 0x{part1ByteStart:X}");

        // Scan the first 32 MB of partition 1 for the "osos" name bytes (0x6F 0x73 0x6F 0x73).
        // The directory entry is always 40 bytes with name[0..3] = "osos", so when found we
        // have an entry start with devOffset at +0x08, length at +0x0C, vers at +0x1C.
        const int scanSize = 32 * 1024 * 1024;
        log.AppendLine($"Scanning {scanSize / (1024 * 1024)} MB of partition 1 for 'osos' image name...");
        var scanBuf = ReadBytes(disk, part1ByteStart, scanSize, sectorSize);
        if (scanBuf == null)
        {
            log.AppendLine($"  Partition read failed, err={Marshal.GetLastWin32Error()}");
            diagnostic = log.ToString();
            return null;
        }

        log.AppendLine("  Scanning for known image name patterns (both plain and word-swapped):");
        var matches = ScanForImageEntries(scanBuf, maxMatches: 32);

        foreach (var m in matches)
        {
            log.AppendLine($"    0x{m.Offset:X6} '{m.Display}'({m.Canonical}) devOffset=0x{m.DevOffset:X} length={m.Length} vers=0x{m.Vers:X8}");
        }

        // Find the first `osos` (plain or word-swapped form) with sane field values.
        // Plausibility check: devOffset and length must both be non-zero and under the
        // partition size (count × sectorSize). False positives inside image payloads
        // typically have huge or nonsensical values like 0xE24DDF45.
        long partitionBytes = (long)part1LbaCount * sectorSize;
        var bestOsos = FindFirstPlausibleOsos(matches, partitionBytes);
        int ososScanStart = bestOsos?.Offset ?? -1;

        // Always dump the first 2 KB of partition 1 so we can see the actual layout
        log.AppendLine("  First 2 KB of partition 1:");
        for (int row = 0; row < 128 && row * 16 < scanBuf.Length; row++)
        {
            int rowOff = row * 16;
            var sb = new StringBuilder();
            sb.Append($"    +{rowOff:X4}: ");
            for (int col = 0; col < 16 && rowOff + col < scanBuf.Length; col++)
            {
                sb.Append($"{scanBuf[rowOff + col]:X2} ");
            }
            sb.Append(" | ");
            for (int col = 0; col < 16 && rowOff + col < scanBuf.Length; col++)
            {
                byte b = scanBuf[rowOff + col];
                sb.Append(b >= 32 && b < 127 ? (char)b : '.');
            }
            log.AppendLine(sb.ToString());
        }

        if (ososScanStart < 0)
        {
            log.AppendLine("  No usable 'osos' entry found (no match had plausible devOffset/length)");
            diagnostic = log.ToString();
            return null;
        }

        log.AppendLine($"  Using 'osos' directory entry at partition offset 0x{ososScanStart:X}");

        uint osOsDevOffset = ReadUInt32LE(scanBuf, ososScanStart + 0x08);
        uint osOsLength    = ReadUInt32LE(scanBuf, ososScanStart + 0x0C);
        uint osOsVers      = ReadUInt32LE(scanBuf, ososScanStart + 0x1C);
        log.AppendLine($"  osos: devOffset=0x{osOsDevOffset:X} length={osOsLength} vers=0x{osOsVers:X8}");

        // Step 6: translate the osos build ID through the per-generation lookup table.
        // Apple encrypts the osos image on 5G+ iPods — the human version string "1.3"
        // never appears as plaintext anywhere on disk, it's generated at runtime by a
        // baked-in table inside the decrypted firmware. We keep our own copy of that
        // table in IPodBuildIdDatabase seeded from community dumps.
        log.AppendLine($"  Translating buildID via IPodBuildIdDatabase (generation='{ipodGeneration}')...");
        var translated = IPodBuildIdDatabase.LookupVersion(ipodGeneration, osOsVers);
        if (translated != null)
        {
            log.AppendLine($"  → {translated}");
            diagnostic = log.ToString();
            return translated;
        }

        log.AppendLine($"  MISS: no entry for ('{ipodGeneration}', 0x{osOsVers:X8})");
        log.AppendLine("  Add this pair to IPodBuildIdDatabase once the human version is known.");
        diagnostic = log.ToString();
        return null;
#else
        log.AppendLine("(platform not supported)");
        diagnostic = log.ToString();
        return null;
#endif
    }

#if WINDOWS

    /// <summary>
    /// Reads a byte range from the raw disk. Handles the three alignment requirements
    /// Windows's raw-disk I/O enforces, all to the drive's logical sector size:
    ///   1. File offset must be sector-aligned
    ///   2. Read length must be sector-aligned
    ///   3. BUFFER POINTER in memory must be sector-aligned — Marshal.AllocHGlobal only
    ///      guarantees 16-byte alignment, so we over-allocate by sectorSize and round up
    ///      the base pointer to the next sector boundary. Without this, ReadFile returns
    ///      ERROR_INVALID_PARAMETER (87) because the storage stack does direct I/O from
    ///      the user buffer and requires page-like alignment.
    /// </summary>
    private static byte[]? ReadBytes(SafeFileHandleWrapper handle, long byteOffset, int byteCount, int sectorSize)
    {
        long mask = sectorSize - 1;
        long alignedOffset = byteOffset & ~mask;
        int shift = (int)(byteOffset - alignedOffset);
        int totalRead = (int)((shift + byteCount + mask) & ~mask);

        if (!SetFilePointerEx(handle, alignedOffset, out _, FILE_BEGIN))
        {
            return null;
        }

        // Over-allocate by one sector so we have room to round the pointer up to a sector boundary.
        var raw = Marshal.AllocHGlobal(totalRead + sectorSize);
        try
        {
            long rawAddr = raw.ToInt64();
            long alignedAddr = (rawAddr + mask) & ~mask;
            var alignedBuf = new IntPtr(alignedAddr);

            if (!ReadFile(handle, alignedBuf, (uint)totalRead, out uint read, IntPtr.Zero)
                || read < (uint)(shift + byteCount))
            {
                return null;
            }

            var result = new byte[byteCount];
            Marshal.Copy(alignedBuf + shift, result, 0, byteCount);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(raw);
        }
    }

#endif

    internal static uint ReadUInt32LE(byte[] bytes, int offset)
    {
        if (offset + 4 > bytes.Length) return 0;
        return (uint)(bytes[offset] | (bytes[offset + 1] << 8) | (bytes[offset + 2] << 16) | (bytes[offset + 3] << 24));
    }

    /// <summary>
    /// One firmware image directory entry candidate found by <see cref="ScanForImageEntries"/>.
    /// <see cref="Display"/> is the on-disk 4-char name as it actually appears (may be
    /// word-swapped on 5G/5.5G/6G iPods); <see cref="Canonical"/> is the intended firmware
    /// image name (e.g. on-disk "soso" → canonical "osos"). Use Canonical for downstream
    /// logic, Display for diagnostic output.
    /// </summary>
    internal sealed record ImageEntry(int Offset, string Display, string Canonical, uint DevOffset, uint Length, uint Vers);

    /// <summary>
    /// Image-name patterns the scanner recognizes. Plain ASCII forms (1G–4G iPods) and
    /// the word-swapped variants found on 5G/5.5G/6G iPods where the firmware loader
    /// reads names 16 bits at a time. Each tuple = (on-disk display, canonical name,
    /// 4 bytes to match).
    /// </summary>
    internal static readonly (string Display, string Canonical, byte B0, byte B1, byte B2, byte B3)[] KnownImageNames =
    [
        // Plain ASCII form (older iPods)
        ("osos", "osos", 0x6F, 0x73, 0x6F, 0x73),
        ("aupd", "aupd", 0x61, 0x75, 0x70, 0x64),
        ("rsrc", "rsrc", 0x72, 0x73, 0x72, 0x63),
        ("osbk", "osbk", 0x6F, 0x73, 0x62, 0x6B),
        ("hibe", "hibe", 0x68, 0x69, 0x62, 0x65),
        ("fhdr", "fhdr", 0x66, 0x68, 0x64, 0x72),
        ("diag", "diag", 0x64, 0x69, 0x61, 0x67),
        // Word-swapped form (5G/5.5G/6G iPods)
        ("soso", "osos", 0x73, 0x6F, 0x73, 0x6F),
        ("dpua", "aupd", 0x64, 0x70, 0x75, 0x61),
        ("crsr", "rsrc", 0x63, 0x72, 0x73, 0x72),
        ("kbso", "osbk", 0x6B, 0x62, 0x73, 0x6F),
        ("ebih", "hibe", 0x65, 0x62, 0x69, 0x68),
        ("rdhf", "fhdr", 0x72, 0x64, 0x68, 0x66),
        ("gaid", "diag", 0x67, 0x61, 0x69, 0x64),
    ];

    /// <summary>
    /// Pure scanner: walks <paramref name="buf"/> byte-by-byte looking for known firmware
    /// image directory entries (40-byte structures starting with one of the names in
    /// <see cref="KnownImageNames"/>). Returns up to <paramref name="maxMatches"/> hits
    /// in scan order, each one carrying the parsed <c>devOffset</c>, <c>length</c>, and
    /// <c>vers</c> fields read from the directory entry. Caller filters for plausibility.
    /// </summary>
    internal static List<ImageEntry> ScanForImageEntries(byte[] buf, int maxMatches = 32)
    {
        var matches = new List<ImageEntry>();
        if (buf == null || buf.Length < 40)
        {
            return matches;
        }

        for (int i = 0; i < buf.Length - 40; i++)
        {
            foreach (var (display, canonical, b0, b1, b2, b3) in KnownImageNames)
            {
                if (buf[i] == b0 && buf[i + 1] == b1 && buf[i + 2] == b2 && buf[i + 3] == b3)
                {
                    uint dev = ReadUInt32LE(buf, i + 0x08);
                    uint len = ReadUInt32LE(buf, i + 0x0C);
                    uint ver = ReadUInt32LE(buf, i + 0x1C);
                    matches.Add(new ImageEntry(i, display, canonical, dev, len, ver));
                    if (matches.Count >= maxMatches) return matches;
                }
            }
        }

        return matches;
    }

    /// <summary>
    /// Scans the matches for the first plausible <c>osos</c> (firmware OS image) entry —
    /// devOffset and length both non-zero and under the partition size. Without the
    /// plausibility filter, false-positive matches inside image payloads (the byte sequence
    /// "osos" or "soso" can appear randomly in encrypted firmware data) would yield
    /// nonsensical version values.
    /// </summary>
    internal static ImageEntry? FindFirstPlausibleOsos(List<ImageEntry> matches, long partitionBytes)
    {
        foreach (var m in matches)
        {
            if (m.Canonical == "osos"
                && m.Length > 0 && m.Length < partitionBytes
                && m.DevOffset > 0 && m.DevOffset < partitionBytes)
            {
                return m;
            }
        }
        return null;
    }

    // Plaintext version-string scanner removed — Apple encrypts the osos image on 5G+
    // iPods, so the human version string is only reachable via IPodBuildIdDatabase
    // lookup keyed by the plaintext osos.vers field from the firmware directory.
}
