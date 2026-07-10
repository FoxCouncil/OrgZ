// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace OrgZ.Services;

/// <summary>
/// Reads the iPod's Apple firmware partition directly from raw disk sectors to extract
/// the <c>osos</c> image's version field. Works on any iPod where we can open
/// <c>\\.\PhysicalDriveN</c> for read access (admin required), bypassing both SCSI
/// vendor commands AND interposing USB bridges like iFlash - because we're reading
/// the actual ATA blocks, not asking the bridge to interpret them.
///
/// iPod firmware partition layout:
///   LBA 0:      MBR with 4-entry partition table (entry at offset 0x1BE)
///   Partition 1 (firmware, typically ~40MB):
///     offset 0x100: magic bytes "{{~~ ppBoOt" (identifies the partition as iPod firmware)
///     offset 0x200: firmware image directory. 40 bytes per entry:
///       [00..03] name       - 4 ASCII chars, stored byte-reversed in memory
///                              ("osos" appears as "soso" on disk due to little-endian ULONG reads)
///       [04..07] id         - incrementing index
///       [08..0B] devOffset  - byte offset from partition start to the image payload
///       [0C..0F] length     - image length in bytes
///       [10..13] addr       - RAM load address
///       [14..17] entryOffset- offset into image where execution starts
///       [18..1B] checksum   - image checksum
///       [1C..1F] vers       - version ULONG (Apple-internal encoding)
///       [20..23] loadAddr   - secondary load address
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
    ///      <paramref name="ipodGeneration"/> - Apple encrypts the osos payload on
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
        // Apple encrypts the osos image on 5G+ iPods - the human version string "1.3"
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
    ///   3. BUFFER POINTER in memory must be sector-aligned - Marshal.AllocHGlobal only
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

    internal static uint ReadUInt32LE(byte[] bytes, int offset) => LittleEndian.ReadUInt32(bytes, offset);

    /// <summary>
    /// One firmware image directory entry candidate found by <see cref="ScanForImageEntries"/>.
    /// <see cref="Display"/> is the on-disk 4-char name as it actually appears (may be
    /// word-swapped on 5G/5.5G/6G iPods); <see cref="Canonical"/> is the intended firmware
    /// image name (e.g. on-disk "soso" → canonical "osos"). Use Canonical for downstream
    /// logic, Display for diagnostic output.
    /// </summary>
    internal sealed record ImageEntry(int Offset, string Display, string Canonical, uint DevOffset, uint Length, uint Vers);

    /// <summary>
    /// Image-name patterns the scanner recognizes. Plain ASCII forms (1G-4G iPods) and
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
    internal static List<ImageEntry> ScanForImageEntries(byte[] buf, int maxMatches = 32, int maxScan = int.MaxValue)
    {
        var matches = new List<ImageEntry>();
        if (buf == null || buf.Length < 40)
        {
            return matches;
        }

        int limit = Math.Min(buf.Length, maxScan) - 40;
        for (int i = 0; i < limit; i++)
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
    /// Scans the matches for the first plausible <c>osos</c> (firmware OS image) entry -
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

    // Plaintext version-string scanner removed - Apple encrypts the osos image on 5G+
    // iPods, so the human version string is only reachable via IPodBuildIdDatabase
    // lookup keyed by the plaintext osos.vers field from the firmware directory.

    // ── macOS ─────────────────────────────────────────────────────────────────

    /// <summary>Result of the macOS raw-firmware identity read.</summary>
    public sealed record MacFirmwareIdentity(string? Version, string? Serial, string? ModelNumber, string Diagnostic);

    /// <summary>
    /// Generations whose firmware build-ID tables may apply to a device whose generation
    /// was inferred at family granularity (artwork correlation IDs can't split e.g.
    /// Video 5G from 5.5G) - tried in order when the direct lookup misses.
    /// </summary>
    private static readonly Dictionary<string, string[]> _generationSiblings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Video 5G"] = ["Video 5.5G"],
        ["Video 5.5G"] = ["Video 5G"],
        ["Classic 6G"] = ["Classic 6.5G", "Classic 7G"],
        ["Classic 6.5G"] = ["Classic 6G", "Classic 7G"],
        ["Classic 7G"] = ["Classic 6.5G", "Classic 6G"],
        ["Nano 1G"] = ["Nano 2G"],
        ["Nano 2G"] = ["Nano 1G"],
    };

    /// <summary>
    /// macOS counterpart of the Windows elevated read. Streams the head of the device's
    /// raw disk through <c>/usr/libexec/authopen</c> - the setuid helper GUI apps use for
    /// raw device access; it presents one authorization dialog, the macOS equivalent of
    /// our Windows UAC prompt - and extracts two things from the firmware region:
    ///   1. the osos directory entry's <c>vers</c> field → <see cref="IPodBuildIdDatabase"/>
    ///   2. the SysCfg block's <c>SrNm</c>/<c>ModN</c> tags (serial + Apple model number)
    ///      when they land in the streamed window - the only serial source on a device
    ///      with a blank SysInfo, since macOS gives unprivileged processes no SCSI VPD.
    /// A serial found here is validated through <see cref="IPodModelDatabase"/> and its
    /// generation replaces the caller's artwork-inferred family guess for the version
    /// lookup (the build-ID table is keyed by exact generation).
    /// </summary>
    /// <summary>
    /// Reads the privileged identity from whatever raw-disk path the current OS+privilege
    /// level allows. Called by the device-helper daemon, which runs as root / LocalSystem -
    /// so it opens the disk directly, with no authopen/UAC prompt. The single entry point
    /// the service dispatches every platform through.
    /// </summary>
    public static MacFirmwareIdentity ReadIdentityElevated(string mountPath, string? ipodGeneration)
    {
        if (OperatingSystem.IsMacOS())
        {
            return ReadIdentityMacOS(mountPath, ipodGeneration, elevated: true);
        }

        if (OperatingSystem.IsLinux())
        {
            return ReadIdentityLinux(mountPath, ipodGeneration);
        }

        if (OperatingSystem.IsWindows())
        {
            // Windows already recovers the serial from WMI unprivileged; the elevated read
            // fills the OS version by opening \\.\PhysicalDriveN (LocalSystem has the rights).
            var version = TryReadOsosVersion(mountPath, ipodGeneration, out var diag);
            return new MacFirmwareIdentity(version, Serial: null, ModelNumber: null, diag);
        }

        return new MacFirmwareIdentity(null, null, null, "unsupported platform");
    }

    public static MacFirmwareIdentity ReadIdentityMacOS(string mountPath, string? ipodGeneration, bool elevated = false)
    {
        var log = new StringBuilder();
        log.AppendLine($"=== macOS firmware identity read for {mountPath} (elevated={elevated}) ===");

        var bsdPartition = DeviceFingerprint.ResolveMacBsdName(mountPath.TrimEnd('/'));
        if (bsdPartition == null)
        {
            log.AppendLine("FAIL: no BSD device node for mount");
            return new MacFirmwareIdentity(null, null, null, log.ToString());
        }

        var digits = 4;
        while (digits < bsdPartition.Length && char.IsDigit(bsdPartition[digits]))
        {
            digits++;
        }
        var wholeDisk = bsdPartition[..digits];   // "disk10s3" → "disk10"
        log.AppendLine($"Mount {mountPath} → /dev/{bsdPartition} → whole disk /dev/{wholeDisk}");

        // 224 MB covers the whole Apple firmware partition (the Apple_MDFW slice runs
        // ~168 MB on a Video 5.5G) - the osos directory sits near its start, but the
        // SysCfg block holding SrNm/ModN can land anywhere inside it, so we need the
        // full partition in-window, not just the head the version lookup needs.
        const int headSize = 224 * 1024 * 1024;
        // Elevated (the daemon, running as root) opens the device directly - no prompt.
        // Prefer the UNMOUNTED Apple_MDFW firmware partition: macOS is unreliable reading a
        // whole-disk block device while its HFS partition is mounted (returns short/zero),
        // but the firmware partition isn't mounted and holds exactly the osos + SysCfg we
        // scan. Fall back to the whole disk if we can't resolve it. Unprivileged (a normal
        // OrgZ click) still streams the whole disk through authopen's one auth dialog.
        byte[]? head;
        if (elevated)
        {
            // Raw CHAR devices (/dev/rdiskN) via a direct libc open()/read() - the canonical
            // disk read (what dd does), reporting the exact errno so a denial's cause is
            // unambiguous (EACCES=13 / EPERM=1 = TCC or perms, EBUSY=16 = mounted-exclusive).
            var firmwarePartition = ResolveMacFirmwarePartition(wholeDisk, log);
            var rawPart = firmwarePartition?.Replace("/dev/disk", "/dev/rdisk", StringComparison.Ordinal);
            head = rawPart != null ? ReadRawHeadNative(rawPart, headSize, log) : null;
            if (head == null || head.Length < 4096)
            {
                log.AppendLine("firmware-partition raw read unavailable — falling back to whole raw disk");
                head = ReadRawHeadNative($"/dev/r{wholeDisk}", headSize, log);
            }
        }
        else
        {
            head = AuthopenReadHead($"/dev/{wholeDisk}", headSize, log);
        }
        if (head == null || head.Length < 4096)
        {
            log.AppendLine(elevated
                ? "FAIL: raw device read returned no data"
                : "FAIL: authopen returned no data (authorization declined, or no GUI session for the dialog)");
            return new MacFirmwareIdentity(null, null, null, log.ToString());
        }
        log.AppendLine($"Streamed {head.Length / (1024 * 1024)} MB of raw disk head");

        // Parse Apple's own SysCfg dictionary out of the raw flash for the serial + model
        // number. macOS hides these behind its private iPod driver, but they physically live in
        // the firmware we can read raw - so we read them straight off the metal.
        var (serial, modelNumber) = ScanSysCfg(head, log);
        var generation = ipodGeneration;
        if (serial != null && IPodModelDatabase.LookupBySerial(serial) is { } serInfo)
        {
            generation = serInfo.Generation;
        }

        // The osos directory is near the partition start - cap the (pattern-heavy) image
        // scan to the first 48 MB so widening the SysCfg window doesn't blow up its cost.
        var matches = ScanForImageEntries(head, maxMatches: 64, maxScan: 48 * 1024 * 1024);
        log.AppendLine($"Firmware directory scan: {matches.Count} image-entry candidates");
        var osos = FindFirstPlausibleOsos(matches, partitionBytes: 512L * 1024 * 1024);
        string? version = null;
        if (osos != null)
        {
            log.AppendLine($"osos entry at 0x{osos.Offset:X} vers=0x{osos.Vers:X8}");
            version = LookupVersionWithSiblings(generation, osos.Vers, log);
        }
        else
        {
            log.AppendLine("No plausible osos entry in streamed window");
        }

        return new MacFirmwareIdentity(version, serial, modelNumber, log.ToString());
    }

    private static string? LookupVersionWithSiblings(string? generation, uint vers, StringBuilder log)
    {
        var direct = IPodBuildIdDatabase.LookupVersion(generation, vers);
        if (direct != null)
        {
            log.AppendLine($"Build ID 0x{vers:X8} ({generation}) → {direct}");
            return direct;
        }

        if (generation != null && _generationSiblings.TryGetValue(generation, out var siblings))
        {
            foreach (var sibling in siblings)
            {
                var hit = IPodBuildIdDatabase.LookupVersion(sibling, vers);
                if (hit != null)
                {
                    log.AppendLine($"Build ID 0x{vers:X8} missed on '{generation}', hit sibling '{sibling}' → {hit}");
                    return hit;
                }
            }
        }

        log.AppendLine($"Build ID 0x{vers:X8} not in IPodBuildIdDatabase for '{generation}' or siblings");
        return null;
    }

    /// <summary>
    /// Runs <c>/usr/libexec/authopen &lt;device&gt;</c> (read mode: contents stream to
    /// stdout after the auth dialog) and captures the first <paramref name="count"/>
    /// bytes, then kills the stream. Returns whatever was read - zero-length on a
    /// declined or unavailable authorization.
    /// </summary>
    private static byte[]? AuthopenReadHead(string devicePath, int count, StringBuilder log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/libexec/authopen",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(devicePath);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                log.AppendLine("authopen failed to start");
                return null;
            }

            var buffer = new byte[count];
            int total = 0;
            var stream = process.StandardOutput.BaseStream;
            while (total < count)
            {
                int read = stream.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }

            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // already exited - fine
            }

            if (total == 0)
            {
                var stderr = process.StandardError.ReadToEnd();
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    log.AppendLine($"authopen stderr: {stderr.Trim()}");
                }
                return null;
            }

            return total == count ? buffer : buffer[..total];
        }
        catch (Exception ex)
        {
            log.AppendLine($"authopen exception: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Reads the first <paramref name="count"/> bytes of a block device directly. Works when
    /// the process already has the privilege to open it (root on macOS/Linux inside the
    /// device-helper daemon) - no authopen, no prompt. Block devices (<c>/dev/diskN</c>,
    /// <c>/dev/sdX</c>) are buffered, so no sector-alignment dance is needed.
    /// </summary>
    private static byte[]? ReadRawHead(string devicePath, int count, StringBuilder log)
    {
        try
        {
            using var fs = new FileStream(devicePath, FileMode.Open, FileAccess.Read);
            var buffer = new byte[count];
            int total = 0;
            while (total < count)
            {
                int read = fs.Read(buffer, total, count - total);
                if (read <= 0)
                {
                    break;
                }
                total += read;
            }
            if (total == 0)
            {
                log.AppendLine($"raw read of {devicePath} returned no bytes");
                return null;
            }
            return total == count ? buffer : buffer[..total];
        }
        catch (Exception ex)
        {
            log.AppendLine($"raw read of {devicePath} failed: {ex.Message}");
            return null;
        }
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int LibcOpen(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "read")]
    private static extern nint LibcRead(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int LibcClose(int fd);

    /// <summary>
    /// Reads the head of a raw character device with a direct libc open()/read() - the
    /// canonical Unix raw-disk read (what dd does), in 1 MB block-aligned chunks so it works
    /// on /dev/rdiskN where reads must be sector-aligned. On failure it records the exact
    /// errno, which is what lets us tell a macOS Full-Disk-Access denial (EACCES/EPERM) apart
    /// from a busy-mount (EBUSY) - different problems, different fixes.
    /// </summary>
    private static byte[]? ReadRawHeadNative(string devicePath, int count, StringBuilder log)
    {
        const int O_RDONLY = 0;
        int fd = LibcOpen(devicePath, O_RDONLY);
        if (fd < 0)
        {
            log.AppendLine($"open({devicePath}) failed: errno={Marshal.GetLastWin32Error()}");
            return null;
        }
        try
        {
            var buffer = new byte[count];
            var chunk = new byte[1 << 20];
            int total = 0;
            while (total < count)
            {
                var want = Math.Min(chunk.Length, count - total);
                var n = (int)LibcRead(fd, chunk, want);
                if (n <= 0)
                {
                    if (total == 0)
                    {
                        log.AppendLine($"read({devicePath}) failed: errno={Marshal.GetLastWin32Error()}");
                    }
                    break;
                }
                Buffer.BlockCopy(chunk, 0, buffer, total, n);
                total += n;
            }
            log.AppendLine($"raw read of {devicePath}: {total / (1024 * 1024)} MB");
            return total == 0 ? null : (total == count ? buffer : buffer[..total]);
        }
        finally
        {
            LibcClose(fd);
        }
    }

    /// <summary>
    /// Linux elevated read: resolve the mount to its backing block device via
    /// <c>/proc/self/mountinfo</c>, strip to the whole disk, and scan it exactly like the
    /// macOS path. Runs inside the root daemon, so the open needs no escalation.
    /// </summary>
    private static MacFirmwareIdentity ReadIdentityLinux(string mountPath, string? ipodGeneration)
    {
        var log = new StringBuilder();
        log.AppendLine($"=== Linux firmware identity read for {mountPath} ===");

        string? partitionDev = null;
        try
        {
            foreach (var line in File.ReadLines("/proc/self/mountinfo"))
            {
                // Fields: ... <mountpoint> ... " - " <fstype> <source> <superopts>
                var dash = line.IndexOf(" - ", StringComparison.Ordinal);
                if (dash < 0)
                {
                    continue;
                }
                var fields = line[..dash].Split(' ');
                if (fields.Length >= 5 && Unescape(fields[4]) == mountPath.TrimEnd('/'))
                {
                    partitionDev = line[(dash + 3)..].Split(' ') is { Length: >= 2 } tail ? tail[1] : null;
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            log.AppendLine($"mountinfo parse failed: {ex.Message}");
        }

        if (partitionDev == null || !partitionDev.StartsWith("/dev/", StringComparison.Ordinal))
        {
            log.AppendLine("could not resolve backing block device");
            return new MacFirmwareIdentity(null, null, null, log.ToString());
        }

        // /dev/sdb3 → /dev/sdb ; /dev/mmcblk0p3 → /dev/mmcblk0
        var whole = partitionDev.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (whole.EndsWith('p'))
        {
            whole = whole[..^1];
        }
        log.AppendLine($"mount {mountPath} → {partitionDev} → whole disk {whole}");

        const int headSize = 224 * 1024 * 1024;
        var head = ReadRawHead(whole, headSize, log);
        if (head == null || head.Length < 4096)
        {
            return new MacFirmwareIdentity(null, null, null, log.ToString());
        }

        // Version only - no firmware serial/model byte-hunt (see ReadIdentityMacOS).
        var osos = FindFirstPlausibleOsos(ScanForImageEntries(head, maxMatches: 64, maxScan: 48 * 1024 * 1024), partitionBytes: 512L * 1024 * 1024);
        var version = osos != null ? LookupVersionWithSiblings(ipodGeneration, osos.Vers, log) : null;
        return new MacFirmwareIdentity(version, Serial: null, ModelNumber: null, log.ToString());
    }

    /// <summary>
    /// Finds the <c>Apple_MDFW</c> firmware partition's device node for a whole disk (e.g.
    /// "disk10" → "/dev/disk10s2") by parsing <c>diskutil list</c>. That partition is never
    /// mounted, so root can read it cleanly even while the iPod's HFS volume is in use.
    /// </summary>
    private static string? ResolveMacFirmwarePartition(string wholeDisk, StringBuilder log)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/usr/sbin/diskutil",
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add(wholeDisk);

            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("Apple_MDFW", StringComparison.Ordinal))
                {
                    continue;
                }
                var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var id = tokens[^1];   // identifier is the last column, e.g. "disk10s2"
                if (id.StartsWith("disk", StringComparison.Ordinal))
                {
                    log.AppendLine($"firmware partition resolved: /dev/{id}");
                    return $"/dev/{id}";
                }
            }
            log.AppendLine("no Apple_MDFW partition in diskutil list");
        }
        catch (Exception ex)
        {
            log.AppendLine($"diskutil list failed: {ex.Message}");
        }
        return null;
    }

    private static string Unescape(string mountField) =>
        mountField.Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    /// <summary>
    /// Reads the iPod's serial + Apple model number out of the firmware's SysInfo record. On
    /// the disk-based iPods (5.5G etc.) that record holds the serial as a plain null-terminated
    /// ASCII string sitting right after the board-name string <c>"iPod M&lt;nn&gt;"</c>, with the
    /// model number <c>"M&lt;x&gt;&lt;nnn&gt;"</c> in the same record. So we anchor on that board name:
    /// an 11-12 char A-Z0-9 run with <c>"iPod M"</c> within 64 bytes before it IS the serial.
    /// Verified unique against a full 5.5G firmware dump - 32k random 11-char runs, exactly one
    /// board-anchored. (This deliberately replaced a byte-scan for FourCC tags that produced
    /// false positives, and the freemyipod 'SCfg' 20-byte-entry format, which is the NOR-flash
    /// layout of the Nano 3G / Classic, not this HDD record.)
    /// </summary>
    internal static (string? Serial, string? ModelNumber) ScanSysCfg(byte[] buf, StringBuilder log)
    {
        ReadOnlySpan<byte> board = "iPod M"u8;

        int i = 0;
        while (i < buf.Length)
        {
            if (!IsSerialChar(buf[i]))
            {
                i++;
                continue;
            }

            int start = i;
            while (i < buf.Length && IsSerialChar(buf[i]))
            {
                i++;
            }

            int len = i - start;
            if (len is 11 or 12)
            {
                int windowStart = Math.Max(0, start - 64);
                if (buf.AsSpan(windowStart, start - windowStart).IndexOf(board) >= 0)
                {
                    var serial = System.Text.Encoding.ASCII.GetString(buf, start, len);
                    var model = FindModelNumber(buf, start, log);
                    log.AppendLine($"Serial at 0x{start:X}: {serial} (board-name anchored); ModelNumber={model}");
                    return (serial, model);
                }
            }
        }

        // Fallback: the freemyipod 'SCfg' dictionary - the NOR-flash layout used by the
        // Nano 3G / Classic, where the HDD board-name record above doesn't apply.
        var scfg = ParseScfgDict(buf, log);
        if (scfg.Serial != null || scfg.ModelNumber != null)
        {
            return scfg;
        }

        log.AppendLine("No board-anchored serial or SCfg dict in firmware");
        return (null, null);
    }

    /// <summary>
    /// The freemyipod 'SCfg' dictionary (NOR-flash iPods - Nano 3G, Classic): a 24-byte header
    /// (magic 'SCfg' stored little-endian = "gfCS" on disk, num_entries at +0x14), then fixed
    /// 20-byte entries - a 4-byte little-endian FourCC tag + 16 data bytes. Tags read reversed:
    /// 'SrNm' → "mNrS", 'ModN' → "NdoM", 'Mod#' → "#doM". Documented, not yet hardware-validated
    /// (no NOR dump on hand - flagged as a named gap).
    /// </summary>
    private static (string? Serial, string? ModelNumber) ParseScfgDict(byte[] buf, StringBuilder log)
    {
        ReadOnlySpan<byte> magic = [0x67, 0x66, 0x43, 0x53];
        ReadOnlySpan<byte> srnm = [0x6D, 0x4E, 0x72, 0x53];
        ReadOnlySpan<byte> modn = [0x4E, 0x64, 0x6F, 0x4D];
        ReadOnlySpan<byte> modh = [0x23, 0x64, 0x6F, 0x4D];

        for (int hdr = 0; hdr + 0x18 <= buf.Length; hdr++)
        {
            if (buf[hdr] != magic[0] || buf[hdr + 1] != magic[1] || buf[hdr + 2] != magic[2] || buf[hdr + 3] != magic[3])
            {
                continue;
            }
            uint numEntries = ReadUInt32LE(buf, hdr + 0x14);
            if (numEntries == 0 || numEntries > 4096)
            {
                continue;
            }

            string? serial = null, model = null;
            int p = hdr + 0x18;
            for (uint i = 0; i < numEntries && p + 20 <= buf.Length; i++, p += 20)
            {
                var tag = buf.AsSpan(p, 4);
                var data = buf.AsSpan(p + 4, 16);
                if (tag.SequenceEqual(srnm))
                {
                    serial ??= AsciiZ(data);
                }
                else if (tag.SequenceEqual(modn) || tag.SequenceEqual(modh))
                {
                    model ??= AsciiZ(data);
                }
            }
            if (serial != null || model != null)
            {
                log.AppendLine($"SysCfg 'SCfg' dict at 0x{hdr:X}: serial={serial} model={model}");
                return (serial, model);
            }
        }
        return (null, null);
    }

    private static string? AsciiZ(ReadOnlySpan<byte> data)
    {
        var sb = new StringBuilder(16);
        foreach (var b in data)
        {
            if (b == 0) { break; }
            if (b is >= 0x20 and < 0x7F) { sb.Append((char)b); }
        }
        var s = sb.ToString().Trim();
        return s.Length > 0 ? s : null;
    }

    private static bool IsSerialChar(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z') || (b >= (byte)'0' && b <= (byte)'9');

    /// <summary>
    /// Finds the Apple model number (<c>M&lt;letter&gt;&lt;3 digits&gt;</c>, e.g. MA446) that lives in the
    /// same SysInfo record as the serial and validates against the model database.
    /// </summary>
    private static string? FindModelNumber(byte[] buf, int nearSerial, StringBuilder log)
    {
        int s = Math.Max(0, nearSerial - 64);
        int e = Math.Min(buf.Length - 5, nearSerial + 256);
        for (int i = s; i <= e; i++)
        {
            if (buf[i] != (byte)'M' || buf[i + 1] < (byte)'A' || buf[i + 1] > (byte)'Z')
            {
                continue;
            }
            if (!IsDigit(buf[i + 2]) || !IsDigit(buf[i + 3]) || !IsDigit(buf[i + 4]))
            {
                continue;
            }
            var cand = System.Text.Encoding.ASCII.GetString(buf, i, 5);
            if (IPodModelDatabase.LookupByModelNumber(cand) != null)
            {
                return cand;
            }
        }
        return null;

        static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
    }
}
