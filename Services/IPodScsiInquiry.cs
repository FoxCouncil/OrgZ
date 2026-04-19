// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Reads iPod device info via SCSI INQUIRY VPD pages. Starting with the 4th-generation
/// iPod (and continuing through Classic 7G / Nano 7G), Apple stores device info in the
/// firmware itself, queryable via SCSI INQUIRY with EVPD=1 and page codes 0xC0-0xE8.
///
/// Page 0xC0 returns a list of the device's supported VPD page codes.
/// Pages 0xC2 onward contain chunks of a single XML plist document. Concatenating the
/// payloads (stripping the 4-byte INQUIRY response header from each) reconstructs the
/// full plist, which iTunes itself uses to identify the connected iPod.
///
/// Reference: https://www.ipodlinux.org/Device_Information/
/// </summary>
public static class IPodScsiInquiry
{
#if WINDOWS

    // ---- P/Invoke: CreateFile / DeviceIoControl / CloseHandle ----

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
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;

    // IOCTL_SCSI_PASS_THROUGH = CTL_CODE(IOCTL_SCSI_BASE=0x04, 0x0401, METHOD_BUFFERED, FILE_READ_ACCESS | FILE_WRITE_ACCESS)
    private const uint IOCTL_SCSI_PASS_THROUGH = 0x0004D004;

    // IOCTL_STORAGE_GET_DEVICE_NUMBER — maps a volume handle to its underlying PhysicalDriveN.
    // CTL_CODE(IOCTL_STORAGE_BASE=0x2D, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

    // SCSI data direction
    private const byte SCSI_IOCTL_DATA_IN = 1;

    // SCSI INQUIRY opcode
    private const byte SCSI_INQUIRY = 0x12;

    // Apple vendor-specific read opcode. Returns a ~256-byte ASCII blob containing the
    // SysInfo key/value text straight out of iPod firmware. Works on 5G/5.5G Video and
    // 6G/6.5G Classic units whose firmware reports zero supported EVPD pages (i.e.,
    // exactly the gap that killed the VPD path on this 5.5G after a restore wiped the
    // factory SysInfo diag blob). Not documented publicly — reverse-engineered by the
    // libipod/gtkpod project. CDB: C6 00 00 00 00 00 00 01 00 00 — byte 7 is the
    // transfer length high byte, so 0x01 means read 256 bytes.
    private const byte APPLE_READ_SYSINFO = 0xC6;

    // Standard SAT (SCSI/ATA Translation) ATA PASS-THROUGH opcodes. Every compliant USB
    // storage bridge — including interposers like iFlash — must translate these to a
    // native ATA IDENTIFY DEVICE (0xEC) on the underlying drive, making this the most
    // portable way to get the ATA serial, firmware revision, and model string back.
    // Returns 512 bytes of IDENTIFY data: serial at bytes 20–39, firmware rev 46–53,
    // model number 54–93 — all ASCII, byte-swapped within each 16-bit word.
    private const byte ATA_PASS_THROUGH_16 = 0x85;
    private const byte ATA_PASS_THROUGH_12 = 0xA1;
    private const byte ATA_IDENTIFY_DEVICE = 0xEC;

    [StructLayout(LayoutKind.Sequential)]
    private struct STORAGE_DEVICE_NUMBER
    {
        public uint DeviceType;
        public uint DeviceNumber;
        public uint PartitionNumber;
    }

    // Must use default alignment (no Pack=1) so the kernel sees the right field offsets:
    // DataTransferLength @ 12, TimeOutValue @ 16, DataBufferOffset @ 24 (x64) / 20 (x86),
    // SenseInfoOffset @ 32 (x64) / 24 (x86), Cdb @ 36 (x64) / 28 (x86). With Pack=1 the
    // struct shrinks to 29 bytes and every field lands in the wrong place, which causes
    // IOCTL_SCSI_PASS_THROUGH to bounce back as ERROR_NOT_ALL_ASSIGNED (1306) at the port
    // driver before ever reaching the device.
    //
    // Cdb[16] MUST be declared inline here so Marshal.SizeOf returns the full 44/56 bytes
    // that Windows expects in sptd.Length.
    [StructLayout(LayoutKind.Sequential)]
    private struct SCSI_PASS_THROUGH
    {
        public ushort Length;
        public byte ScsiStatus;
        public byte PathId;
        public byte TargetId;
        public byte Lun;
        public byte CdbLength;
        public byte SenseInfoLength;
        public byte DataIn;
        public uint DataTransferLength;
        public uint TimeOutValue;
        public IntPtr DataBufferOffset;
        public uint SenseInfoOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] Cdb;
    }

#endif

    /// <summary>
    /// Attempts to read the iPod device-info XML plist via SCSI INQUIRY VPD pages.
    /// Returns a flat key/value dictionary of the parsed fields, or null on failure.
    /// The <paramref name="diagnostic"/> out parameter always gets a human-readable
    /// breakdown of each step so the UI can display it via the "Raw SysInfo" panel.
    /// </summary>
    public static Dictionary<string, string>? TryReadDeviceInfo(string driveLetter, out string rawXml, out string diagnostic)
    {
        rawXml = string.Empty;
        var log = new StringBuilder();
        log.AppendLine($"=== SCSI INQUIRY attempt on {driveLetter} ===");

#if WINDOWS
        // Normalize "F:\" → "F:"
        var letter = driveLetter.TrimEnd('\\', '/');
        if (letter.Length != 2 || letter[1] != ':')
        {
            log.AppendLine($"FAIL: invalid drive letter '{driveLetter}'");
            diagnostic = log.ToString();
            return null;
        }

        // SCSI pass-through doesn't work against volume handles — Windows's volume stack
        // eats the CDB and returns 1306 (ERROR_NO_NETWORK). We need the PhysicalDriveN
        // handle, which maps via IOCTL_STORAGE_GET_DEVICE_NUMBER on the volume.
        var volumePath = $@"\\.\{letter}";
        log.AppendLine($"Opening volume {volumePath} (to resolve PhysicalDrive number)...");

        int physicalDriveNumber;
        using (var volHandle = CreateFile(
            volumePath,
            0, // no access — FILE_ANY_ACCESS IOCTL only
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero))
        {
            if (volHandle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                log.AppendLine($"  CreateFile failed, err={err} ({Win32ErrorName(err)})");
                diagnostic = log.ToString();
                return null;
            }

            int sdnSize = Marshal.SizeOf<STORAGE_DEVICE_NUMBER>();
            var sdnBuf = Marshal.AllocHGlobal(sdnSize);
            try
            {
                bool ok = DeviceIoControl(
                    volHandle,
                    IOCTL_STORAGE_GET_DEVICE_NUMBER,
                    IntPtr.Zero, 0,
                    sdnBuf, (uint)sdnSize,
                    out _,
                    IntPtr.Zero);
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    log.AppendLine($"  IOCTL_STORAGE_GET_DEVICE_NUMBER failed, err={err} ({Win32ErrorName(err)})");
                    diagnostic = log.ToString();
                    return null;
                }
                var sdn = Marshal.PtrToStructure<STORAGE_DEVICE_NUMBER>(sdnBuf);
                physicalDriveNumber = (int)sdn.DeviceNumber;
                log.AppendLine($"  resolved to PhysicalDrive{physicalDriveNumber}");
            }
            finally
            {
                Marshal.FreeHGlobal(sdnBuf);
            }
        }

        // Now open the physical drive with R+W. This ABSOLUTELY requires elevation on
        // Windows 10+ for fixed and removable storage alike — without admin you get
        // ERROR_ACCESS_DENIED (5) and we stop here with a clear message.
        var physicalPath = $@"\\.\PhysicalDrive{physicalDriveNumber}";
        log.AppendLine($"Opening {physicalPath} for SCSI pass-through...");
        var handle = CreateFile(
            physicalPath,
            GENERIC_READ | GENERIC_WRITE,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero,
            OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            int err = Marshal.GetLastWin32Error();
            log.AppendLine($"  CreateFile failed, err={err} ({Win32ErrorName(err)})");
            if (err == 5)
            {
                log.AppendLine("  → Opening PhysicalDriveN with write access requires running OrgZ as Administrator.");
                log.AppendLine("  → Right-click the OrgZ shortcut → Run as administrator, then reconnect the iPod.");
            }
            handle.Dispose();
            diagnostic = log.ToString();
            return null;
        }
        log.AppendLine("  opened read+write");

        using (handle)
        {
            // Step 1: read VPD 0xC0 to discover which VPD pages this device supports.
            // Three possible outcomes:
            //   (a) Success with pages ≥0xC2 → read XML plist from newer iPods
            //   (b) Success with 0 pages → firmware implements EVPD but has no device-info pages
            //   (c) CHECK CONDITION → firmware doesn't implement EVPD at all (5G/5.5G/6G typical)
            // All three of (b) and (c) fall through to the Apple vendor opcode 0xC6 path.
            log.AppendLine("Reading VPD page 0xC0 (supported pages list)...");
            bool vpdAvailable = false;
            List<byte> supportedPages = new();

            var page0xC0 = InquiryVpd(handle, 0xC0, out var err0xC0);
            if (page0xC0 == null || page0xC0.Length < 4)
            {
                log.AppendLine($"  FAIL: {err0xC0 ?? "no data"}");
                log.AppendLine("  (device rejects EVPD — will try Apple vendor opcode 0xC6 instead)");
            }
            else
            {
                int page0LenBE = (page0xC0[2] << 8) | page0xC0[3];
                int supportedCount = Math.Min(page0LenBE, page0xC0.Length - 4);
                log.AppendLine($"  got {supportedCount} supported page codes");

                var codeList = new StringBuilder();
                for (int i = 0; i < supportedCount; i++)
                {
                    var code = page0xC0[4 + i];
                    codeList.Append($"0x{code:X2} ");
                    if (code >= 0xC2 && code <= 0xFF)
                    {
                        supportedPages.Add(code);
                    }
                }
                log.AppendLine($"  pages: {codeList.ToString().TrimEnd()}");
                vpdAvailable = supportedPages.Count > 0;
            }

            if (!vpdAvailable)
            {
                log.AppendLine("Falling back to Apple vendor opcode 0xC6 (SysInfo blob read)...");

                var blob = ReadAppleSysInfoBlob(handle, out var blobErr);
                if (blob != null)
                {
                    var (blobFields, blobText) = ParseAppleSysInfoBlob(blob);
                    rawXml = blobText; // not XML but same dump slot — the user sees it in Raw SysInfo
                    log.AppendLine($"  got {blobFields.Count} key/value pairs from opcode 0xC6");
                    foreach (var kvp in blobFields)
                    {
                        log.AppendLine($"  {kvp.Key} = {Truncate(kvp.Value, 60)}");
                    }
                    diagnostic = log.ToString();
                    return blobFields;
                }
                log.AppendLine($"  FAIL: {blobErr ?? "no data"}");

                // Final fallback: standard SAT ATA PASS-THROUGH → IDENTIFY DEVICE. Every
                // compliant USB storage bridge must implement this, so even interposers
                // like iFlash that reject Apple's vendor extensions will translate this
                // to a native ATA command and return the drive's identity block.
                log.AppendLine("Falling back to ATA PASS-THROUGH (16) → IDENTIFY DEVICE...");
                var ataBuf = AtaIdentifyDevice16(handle, out var ata16Err);
                if (ataBuf == null)
                {
                    log.AppendLine($"  ATA PT(16) FAIL: {ata16Err ?? "no data"}");
                    log.AppendLine("Retrying with ATA PASS-THROUGH (12)...");
                    ataBuf = AtaIdentifyDevice12(handle, out var ata12Err);
                    if (ataBuf == null)
                    {
                        log.AppendLine($"  ATA PT(12) FAIL: {ata12Err ?? "no data"}");
                        log.AppendLine("  (all SCSI paths exhausted)");
                        diagnostic = log.ToString();
                        return null;
                    }
                }

                var ataFields = ParseAtaIdentify(ataBuf);
                log.AppendLine($"  ATA IDENTIFY returned {ataFields.Count} fields");
                foreach (var kvp in ataFields)
                {
                    log.AppendLine($"  {kvp.Key} = {kvp.Value}");
                }
                rawXml = "ATA IDENTIFY DEVICE (parsed):\r\n" + string.Join("\r\n", ataFields.Select(k => $"  {k.Key} = {k.Value}"));
                diagnostic = log.ToString();
                return ataFields;
            }

            // Step 2: read each page's payload and concatenate into a single XML stream.
            log.AppendLine($"Reading {supportedPages.Count} XML-bearing pages...");
            var xmlBytes = new List<byte>(8192);
            foreach (var code in supportedPages)
            {
                var pageBytes = InquiryVpd(handle, code, out var pageErr);
                if (pageBytes == null || pageBytes.Length < 4)
                {
                    log.AppendLine($"  page 0x{code:X2}: FAIL {pageErr ?? "no data"}");
                    continue;
                }

                int payloadLen = Math.Min((pageBytes[2] << 8) | pageBytes[3], pageBytes.Length - 4);
                log.AppendLine($"  page 0x{code:X2}: {payloadLen} bytes");
                if (payloadLen <= 0)
                {
                    continue;
                }

                for (int i = 0; i < payloadLen; i++)
                {
                    xmlBytes.Add(pageBytes[4 + i]);
                }
            }

            if (xmlBytes.Count == 0)
            {
                log.AppendLine("  no XML data accumulated");
                diagnostic = log.ToString();
                return null;
            }

            // The payload is UTF-8 XML. Some devices include trailing 0x00 padding — strip it.
            int end = xmlBytes.Count;
            while (end > 0 && xmlBytes[end - 1] == 0)
            {
                end--;
            }

            rawXml = Encoding.UTF8.GetString(xmlBytes.ToArray(), 0, end);
            log.AppendLine($"Reassembled {end} bytes of XML");

            // Step 3: parse the plist and flatten key/string pairs.
            var fields = ParsePlist(rawXml);
            if (fields == null)
            {
                log.AppendLine("FAIL: plist parse returned null");
            }
            else
            {
                log.AppendLine($"parsed {fields.Count} top-level keys");
                foreach (var kvp in fields)
                {
                    log.AppendLine($"  {kvp.Key} = {Truncate(kvp.Value, 60)}");
                }
            }
            diagnostic = log.ToString();
            return fields;
        }
#else
        log.AppendLine("(SCSI INQUIRY not supported on this platform)");
        diagnostic = log.ToString();
        return null;
#endif
    }

#if WINDOWS
    /// <summary>
    /// Sends Apple's vendor-specific 0xC6 READ command and returns the 256-byte ASCII
    /// SysInfo blob. Same SRB setup as InquiryVpd but 10-byte CDB and 256-byte data.
    /// </summary>
    private static byte[]? ReadAppleSysInfoBlob(SafeFileHandleWrapper handle, out string? error)
    {
        error = null;
        const int senseSize = 32;
        const int dataSize = 256;

        int structSize = Marshal.SizeOf<SCSI_PASS_THROUGH>();
        int totalSize = structSize + senseSize + dataSize;
        var buffer = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            // CDB: C6 00 00 00 00 00 00 01 00 00 — byte 7 = 0x01 → 256-byte transfer length (hi byte)
            var cdb = new byte[16];
            cdb[0] = APPLE_READ_SYSINFO;
            cdb[7] = 0x01;

            var spt = new SCSI_PASS_THROUGH
            {
                Length             = (ushort)structSize,
                PathId             = 0,
                TargetId           = 0,
                Lun                = 0,
                CdbLength          = 10,
                SenseInfoLength    = senseSize,
                DataIn             = SCSI_IOCTL_DATA_IN,
                DataTransferLength = dataSize,
                TimeOutValue       = 10,
                SenseInfoOffset    = (uint)structSize,
                DataBufferOffset   = new IntPtr(structSize + senseSize),
                Cdb                = cdb,
            };
            Marshal.StructureToPtr(spt, buffer, false);

            bool ok = DeviceIoControl(
                handle,
                IOCTL_SCSI_PASS_THROUGH,
                buffer,
                (uint)totalSize,
                buffer,
                (uint)totalSize,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                error = $"DeviceIoControl err={err} ({Win32ErrorName(err)})";
                return null;
            }

            var result = Marshal.PtrToStructure<SCSI_PASS_THROUGH>(buffer);
            if (result.ScsiStatus != 0)
            {
                // Decode sense key/ASC/ASCQ so we can tell why the device rejected it
                var sense = new byte[senseSize];
                Marshal.Copy(buffer + structSize, sense, 0, senseSize);
                int senseKey = sense.Length > 2 ? (sense[2] & 0x0F) : 0;
                int asc = sense.Length > 12 ? sense[12] : 0;
                int ascq = sense.Length > 13 ? sense[13] : 0;
                error = $"SCSI status=0x{result.ScsiStatus:X2} sense K/C/Q = {senseKey:X}/{asc:X2}/{ascq:X2}";
                return null;
            }

            var data = new byte[dataSize];
            Marshal.Copy(buffer + structSize + senseSize, data, 0, dataSize);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Issues an ATA IDENTIFY DEVICE command wrapped in an ATA PASS-THROUGH (16) SCSI CDB.
    /// Opcode 0x85. This is the preferred SAT command; falls back to 12-byte variant if
    /// the bridge rejects it. Returns the raw 512-byte IDENTIFY response from the drive.
    /// </summary>
    private static byte[]? AtaIdentifyDevice16(SafeFileHandleWrapper handle, out string? error)
    {
        // CDB: 85 08 0E 00 00 00 01 00 00 00 00 00 00 00 EC 00
        //  [0]=0x85 opcode
        //  [1]=0x08 protocol 4 (PIO data-in) << 1
        //  [2]=0x0E ck_cond=0, t_dir=1(in), byte_block=1, t_length=2(sector_count)
        //  [6]=0x01 sector count = 1
        //  [14]=0xEC IDENTIFY DEVICE
        var cdb = new byte[16];
        cdb[0]  = ATA_PASS_THROUGH_16;
        cdb[1]  = 0x08;
        cdb[2]  = 0x0E;
        cdb[6]  = 0x01;
        cdb[14] = ATA_IDENTIFY_DEVICE;
        return SendAtaPassThrough(handle, cdb, 16, 512, out error);
    }

    /// <summary>
    /// ATA PASS-THROUGH (12) variant — opcode 0xA1, CDB is 12 bytes. Used when the
    /// bridge rejects the 16-byte variant. Same behavior as the 16-byte call — same
    /// ATA IDENTIFY DEVICE command wrapped differently.
    /// </summary>
    private static byte[]? AtaIdentifyDevice12(SafeFileHandleWrapper handle, out string? error)
    {
        // CDB: A1 08 0E 00 01 00 00 00 00 EC 00 00
        var cdb = new byte[16];
        cdb[0] = ATA_PASS_THROUGH_12;
        cdb[1] = 0x08;
        cdb[2] = 0x0E;
        cdb[4] = 0x01;
        cdb[9] = ATA_IDENTIFY_DEVICE;
        return SendAtaPassThrough(handle, cdb, 12, 512, out error);
    }
#endif

#if WINDOWS
    /// <summary>
    /// Shared SCSI pass-through workhorse for ATA commands. Same SRB setup as
    /// InquiryVpd / ReadAppleSysInfoBlob, just with a caller-supplied CDB + length.
    /// </summary>
    private static byte[]? SendAtaPassThrough(SafeFileHandleWrapper handle, byte[] cdb, int cdbLength, int dataSize, out string? error)
    {
        error = null;
        const int senseSize = 32;

        int structSize = Marshal.SizeOf<SCSI_PASS_THROUGH>();
        int totalSize = structSize + senseSize + dataSize;
        var buffer = Marshal.AllocHGlobal(totalSize);

        try
        {
            for (int i = 0; i < totalSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            var spt = new SCSI_PASS_THROUGH
            {
                Length             = (ushort)structSize,
                PathId             = 0,
                TargetId           = 0,
                Lun                = 0,
                CdbLength          = (byte)cdbLength,
                SenseInfoLength    = senseSize,
                DataIn             = SCSI_IOCTL_DATA_IN,
                DataTransferLength = (uint)dataSize,
                TimeOutValue       = 10,
                SenseInfoOffset    = (uint)structSize,
                DataBufferOffset   = new IntPtr(structSize + senseSize),
                Cdb                = cdb,
            };
            Marshal.StructureToPtr(spt, buffer, false);

            bool ok = DeviceIoControl(
                handle,
                IOCTL_SCSI_PASS_THROUGH,
                buffer,
                (uint)totalSize,
                buffer,
                (uint)totalSize,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                error = $"DeviceIoControl err={err} ({Win32ErrorName(err)})";
                return null;
            }

            var result = Marshal.PtrToStructure<SCSI_PASS_THROUGH>(buffer);
            if (result.ScsiStatus != 0)
            {
                var sense = new byte[senseSize];
                Marshal.Copy(buffer + structSize, sense, 0, senseSize);
                int senseKey = sense.Length > 2 ? (sense[2] & 0x0F) : 0;
                int asc = sense.Length > 12 ? sense[12] : 0;
                int ascq = sense.Length > 13 ? sense[13] : 0;
                error = $"SCSI status=0x{result.ScsiStatus:X2} sense K/C/Q = {senseKey:X}/{asc:X2}/{ascq:X2}";
                return null;
            }

            var data = new byte[dataSize];
            Marshal.Copy(buffer + structSize + senseSize, data, 0, dataSize);
            return data;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
#endif

    /// <summary>
    /// Extracts an ASCII string from an ATA IDENTIFY response. ATA stores text fields
    /// as 16-bit words in big-endian-within-word order, so within each 2-byte pair the
    /// bytes are reversed relative to natural ASCII reading order. We swap every pair
    /// before decoding. Length is always even. Trailing spaces are trimmed.
    /// </summary>
    private static string ExtractAtaString(byte[] identify, int offset, int length)
    {
        if (offset < 0 || length <= 0 || offset + length > identify.Length)
        {
            return string.Empty;
        }

        var swapped = new byte[length];
        for (int i = 0; i < length; i += 2)
        {
            swapped[i] = identify[offset + i + 1];
            swapped[i + 1] = identify[offset + i];
        }
        return Encoding.ASCII.GetString(swapped).Trim();
    }

    /// <summary>
    /// Parses an ATA IDENTIFY DEVICE response into a flat dictionary using the same
    /// shape as the other sources so <c>PopulateFromScsiInquiry</c> can route the
    /// fields into <c>ConnectedDevice</c> without special-casing the caller.
    ///   Serial:   bytes 20–39 (20 chars) → "AtaSerialNumber"
    ///   Firmware: bytes 46–53  (8 chars) → "AtaFirmwareRev"
    ///   Model:    bytes 54–93 (40 chars) → "AtaModelNumber"
    /// </summary>
    private static Dictionary<string, string> ParseAtaIdentify(byte[] identify)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var serial = ExtractAtaString(identify, 20, 20);
        if (!string.IsNullOrWhiteSpace(serial))
        {
            fields["AtaSerialNumber"] = serial;
        }

        var fwRev = ExtractAtaString(identify, 46, 8);
        if (!string.IsNullOrWhiteSpace(fwRev))
        {
            fields["AtaFirmwareRev"] = fwRev;
        }

        var model = ExtractAtaString(identify, 54, 40);
        if (!string.IsNullOrWhiteSpace(model))
        {
            fields["AtaModelNumber"] = model;
        }

        return fields;
    }

    /// <summary>
    /// Parses the ASCII key/value text returned by opcode 0xC6. Stops at the first run
    /// of NUL bytes, then splits the rest line-by-line and extracts "key: value" pairs.
    /// The blob is the same format as the legacy plain-text SysInfo file.
    /// </summary>
    private static (Dictionary<string, string> fields, string text) ParseAppleSysInfoBlob(byte[] blob)
    {
        // Cut off at the first run of 4+ zero bytes (firmware trailer/padding)
        int end = blob.Length;
        for (int i = 0; i < blob.Length - 4; i++)
        {
            if (blob[i] == 0 && blob[i + 1] == 0 && blob[i + 2] == 0 && blob[i + 3] == 0)
            {
                end = i;
                break;
            }
        }

        // Also stop at first single NUL if scanning didn't find a run yet (defensive)
        for (int i = 0; i < end; i++)
        {
            if (blob[i] == 0)
            {
                end = i;
                break;
            }
        }

        var text = Encoding.ASCII.GetString(blob, 0, end);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\0').TrimStart();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
            {
                fields[key] = value;
            }
        }

        return (fields, text);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "...";

#if WINDOWS
    private static string Win32ErrorName(int err) => err switch
    {
        1 => "ERROR_INVALID_FUNCTION",
        2 => "ERROR_FILE_NOT_FOUND",
        3 => "ERROR_PATH_NOT_FOUND",
        5 => "ERROR_ACCESS_DENIED (need elevation)",
        6 => "ERROR_INVALID_HANDLE",
        21 => "ERROR_NOT_READY",
        32 => "ERROR_SHARING_VIOLATION",
        50 => "ERROR_NOT_SUPPORTED",
        87 => "ERROR_INVALID_PARAMETER",
        1117 => "ERROR_IO_DEVICE",
        _ => $"(unknown)",
    };
#endif

#if WINDOWS

    /// <summary>
    /// Issues INQUIRY EVPD=1 for a single VPD page and returns the raw response bytes.
    /// Builds a contiguous SCSI_PASS_THROUGH + sense + data buffer, sets offsets relative
    /// to the start of SCSI_PASS_THROUGH, and hands the whole thing to DeviceIoControl.
    /// </summary>
    private static byte[]? InquiryVpd(SafeFileHandleWrapper handle, byte pageCode, out string? error)
    {
        error = null;
        const int senseSize = 32;
        const int dataSize = 4096;

        // sizeof(SCSI_PASS_THROUGH) with default alignment — includes Cdb[16] inline.
        // x64 = 56, x86 = 44. This is what goes into sptd.Length and is the base for offsets.
        int structSize = Marshal.SizeOf<SCSI_PASS_THROUGH>();
        int totalSize = structSize + senseSize + dataSize;
        var buffer = Marshal.AllocHGlobal(totalSize);

        try
        {
            // Zero the whole allocation so any padding bytes and the sense/data regions start clean
            for (int i = 0; i < totalSize; i++)
            {
                Marshal.WriteByte(buffer, i, 0);
            }

            // Build the CDB: [opcode=INQUIRY] [EVPD=1] [page] [alloc-len MSB] [alloc-len LSB] [ctrl]
            var cdb = new byte[16];
            cdb[0] = SCSI_INQUIRY;
            cdb[1] = 0x01;
            cdb[2] = pageCode;
            cdb[3] = (byte)((dataSize >> 8) & 0xFF);
            cdb[4] = (byte)(dataSize & 0xFF);
            cdb[5] = 0x00;

            var spt = new SCSI_PASS_THROUGH
            {
                Length             = (ushort)structSize,
                PathId             = 0,
                TargetId           = 0,
                Lun                = 0,
                CdbLength          = 6,
                SenseInfoLength    = senseSize,
                DataIn             = SCSI_IOCTL_DATA_IN,
                DataTransferLength = dataSize,
                TimeOutValue       = 10,
                SenseInfoOffset    = (uint)structSize,                   // sense immediately after the struct
                DataBufferOffset   = new IntPtr(structSize + senseSize), // data immediately after sense
                Cdb                = cdb,
            };
            Marshal.StructureToPtr(spt, buffer, false);

            bool ok = DeviceIoControl(
                handle,
                IOCTL_SCSI_PASS_THROUGH,
                buffer,
                (uint)totalSize,
                buffer,
                (uint)totalSize,
                out _,
                IntPtr.Zero);

            if (!ok)
            {
                int err = Marshal.GetLastWin32Error();
                error = $"DeviceIoControl err={err} ({Win32ErrorName(err)})";
                return null;
            }

            // Read the struct back to get the actual ScsiStatus. Non-zero means the device
            // rejected the command (CHECK CONDITION → sense data explains why).
            var resultSpt = Marshal.PtrToStructure<SCSI_PASS_THROUGH>(buffer);
            if (resultSpt.ScsiStatus != 0)
            {
                error = $"SCSI status=0x{resultSpt.ScsiStatus:X2} (device CHECK CONDITION — page unsupported?)";
                return null;
            }

            // Copy the data payload out of the buffer
            int dataOffset = structSize + senseSize;
            var result = new byte[dataSize];
            Marshal.Copy(buffer + dataOffset, result, 0, dataSize);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

#endif

    /// <summary>
    /// Flattens an Apple plist XML document (as returned by iPod VPD pages) into a
    /// key→value dictionary. Only primitive values (&lt;string&gt;, &lt;integer&gt;,
    /// &lt;true/&gt;, &lt;false/&gt;) are extracted at the top level; nested dicts
    /// and arrays are ignored since we only care about the scalar metadata fields
    /// (SerialNumber, FireWireGUID, BuildID, VisibleBuildID, FamilyID, VolumeFormat).
    /// </summary>
    private static Dictionary<string, string>? ParsePlist(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var dict = doc.Root?.Element("dict");
            if (dict == null)
            {
                return null;
            }

            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? currentKey = null;

            foreach (var element in dict.Elements())
            {
                if (element.Name.LocalName == "key")
                {
                    currentKey = element.Value.Trim();
                    continue;
                }

                if (currentKey == null)
                {
                    continue;
                }

                var value = element.Name.LocalName switch
                {
                    "string" => element.Value.Trim(),
                    "integer" => element.Value.Trim(),
                    "real" => element.Value.Trim(),
                    "true" => "true",
                    "false" => "false",
                    _ => null,
                };

                if (!string.IsNullOrEmpty(value))
                {
                    fields[currentKey] = value;
                }

                currentKey = null; // consume, even if we didn't capture (nested dict/array)
            }

            return fields;
        }
        catch (Exception ex)
        {
            Logging.For("IPodScsiInquiry").Warning(ex, "VPD plist parse failed");
            return null;
        }
    }

    /// <summary>
    /// Maps Apple's FamilyID integer to a human-readable iPod generation. FamilyID is
    /// the most authoritative identifier Apple exposes — each generation has a unique
    /// value and it's set by firmware, not derived from model-number strings.
    /// </summary>
    public static string? DecodeFamilyId(string familyId)
    {
        if (!int.TryParse(familyId, out var id))
        {
            return null;
        }

        return id switch
        {
            1 => "iPod 1G",
            2 => "iPod 2G",
            3 => "iPod 3G",
            4 => "iPod Mini 1G",
            5 => "iPod 4G",
            6 => "iPod Photo / Color",
            7 => "iPod Mini 2G",
            8 => "iPod 5G Video",
            9 => "iPod Nano 1G",
            10 => "iPod Shuffle 1G",
            11 => "iPod Nano 2G",
            12 => "iPod Shuffle 2G",
            13 => "iPod 5G Video (enhanced)",    // 5.5G
            14 => "iPod Classic 6G",
            15 => "iPod Nano 3G",
            16 => "iPod Touch 1G",
            17 => "iPod Nano 4G",
            18 => "iPod Classic 6.5G (120GB)",
            19 => "iPod Touch 2G",
            20 => "iPod Shuffle 3G",
            21 => "iPod Classic 7G (160GB)",
            22 => "iPod Nano 5G (camera)",
            23 => "iPod Touch 3G",
            24 => "iPod Nano 6G",
            25 => "iPod Touch 4G",
            26 => "iPod Shuffle 4G",
            27 => "iPod Nano 7G",
            28 => "iPod Touch 5G",
            _ => $"iPod (Family {id})",
        };
    }
}
