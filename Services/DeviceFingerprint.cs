// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;
using System.Xml.Linq;
using Serilog;

#if WINDOWS
using System.Management;
#endif

namespace OrgZ.Services;

/// <summary>
/// Identifies a mounted drive as a known portable audio player by checking for
/// marker files and parsing device-specific metadata files.
/// </summary>
public static class DeviceFingerprint
{
    private static readonly ILogger _log = Logging.For("DeviceFingerprint");

    /// <summary>
    /// Inspects a mounted drive and returns a ConnectedDevice if it looks like
    /// a portable audio player, otherwise null.
    /// </summary>
    public static ConnectedDevice? Identify(DriveInfo drive)
    {
        if (!drive.IsReady)
        {
            return null;
        }

        // Only consider removable drives — skip fixed disks, network, CD-ROM
        if (drive.DriveType != DriveType.Removable)
        {
            return null;
        }

        var root = drive.RootDirectory.FullName;

        bool hasRockbox = Directory.Exists(Path.Combine(root, ".rockbox"));
        bool hasIPodControl = Directory.Exists(Path.Combine(root, "iPod_Control"));

        if (!hasRockbox && !hasIPodControl)
        {
            return null;
        }

        DeviceType type;
        if (hasRockbox && hasIPodControl)
        {
            // iPod hardware running Rockbox — prefer Rockbox file-based mode
            type = DeviceType.RockboxIPod;
        }
        else if (hasRockbox)
        {
            // Non-iPod hardware running Rockbox (Sansa, iRiver, Cowon, etc.)
            type = DeviceType.RockboxOther;
        }
        else
        {
            // iPod with stock Apple firmware — iTunesDB required for browse
            type = DeviceType.StockIPod;
        }

        var device = new ConnectedDevice
        {
            MountPath = root,
            DeviceType = type,
            Name = !string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.VolumeLabel : "Portable Player",
        };

        // Read the on-device /.orgz/device record first — it's the authoritative cache
        // that travels with the iPod. Any field it supplies becomes the initial baseline;
        // live detection below only overwrites a field when it actually finds new data.
        if (hasIPodControl)
        {
            var record = OrgZDeviceRecord.TryLoad(root);
            if (record != null)
            {
                device.Serial = record.Serial;
                device.Model = record.Model;
                device.AppleModelNumber = record.AppleModelNumber;
                device.IpodGeneration = record.IpodGeneration;
                device.FireWireGuid = record.FireWireGuid;
                device.HardwareModel = record.HardwareModel;

                // Reject any leftover hex build-ID from prior sessions — early code wrote
                // "iPod OS 0x0000B012" into records before we had the translation table;
                // those are stale and should be re-resolved via IPodBuildIdDatabase.
                if (!string.IsNullOrWhiteSpace(record.AppleFirmwareVersion)
                    && !record.AppleFirmwareVersion.Contains("0x", StringComparison.OrdinalIgnoreCase))
                {
                    device.AppleFirmwareVersion = record.AppleFirmwareVersion;
                }

                _log.Debug("Loaded /.orgz/device record at {MountPath}: Model={Model} Serial={Serial}", drive.RootDirectory.FullName, record.Model, record.Serial);
            }
        }

        if (hasIPodControl)
        {
            PopulateIPodSysInfo(root, device);
        }

        if (hasRockbox)
        {
            PopulateRockboxVersion(root, device);
        }

        // USB descriptor via WMI Win32_DiskDrive runs FIRST now so we can detect bridges
        // that are hostile to SCSI pass-through (iFlash especially — it hangs on 0xC6
        // and ATA PASS-THROUGH for the full 10-second timeout, costing 30s per connect
        // for zero result). We also extract the FireWire GUID from the USB iSerial here
        // so the cache lookup below can run even on modded iPods where the real serial
        // is unavailable live.
        if (hasIPodControl)
        {
            PopulateFromDiskInfo(drive, device);
        }

        // TODO(device-service): SCSI INQUIRY pass-through and firmware partition raw
        // read both need SeManageVolumePrivilege / admin elevation because opening
        // \\.\PhysicalDriveN with GENERIC_READ|GENERIC_WRITE fails with ACCESS_DENIED
        // otherwise. They're commented out until we have a Windows service helper
        // (OrgZDeviceHelper) running as LocalSystem that handles the elevated paths
        // over a named pipe, mirroring iTunes's AppleMobileDeviceService architecture.
        //
        // Once the helper exists, restore:
        //   PopulateFromScsiInquiry(drive, device);
        //   IPodFirmwarePartition.TryReadOsosVersion(drive.Name, device.IpodGeneration, ...);
        //
        // The current non-admin pipeline (WMI + libgpod serial suffix + /.orgz/device
        // merge) is sufficient for Model / Serial / Generation / FireWireGuid on every
        // iPod we care about. AppleFirmwareVersion is the only field that strictly
        // needs the elevated path.

        // Refresh the drive stats first so we know the physical capacity before we run
        // the libgpod lookup — it needs the actual bytes to detect drive mods.
        device.RefreshSpace();

        // libgpod-style lookup: the last 3 characters of the USB serial uniquely identify
        // the iPod model (Apple assigned serial suffixes per production batch). This is
        // authoritative — always overwrite whatever generic string WMI or the USB descriptor
        // gave us, because libgpod's table knows the exact generation + color + factory
        // capacity. When the mounted drive is ≥20% larger than the factory capacity we
        // render "Modded" in place of the original GB — e.g. an 80GB iPod Video 5.5G
        // with a 512GB SSD swap becomes "iPod Video 5.5G Modded White".
        if (hasIPodControl && !string.IsNullOrWhiteSpace(device.Serial))
        {
            var info = IPodModelDatabase.LookupBySerial(device.Serial);
            if (info != null)
            {
                device.Model = info.DisplayNameForActualCapacity(device.TotalSpace);
                device.IpodGeneration = info.Generation;
                _log.Debug("libgpod serial lookup matched: Serial={Serial} -> Model={Model} Generation={Generation}", device.Serial, device.Model, info.Generation);
            }
        }

        return device;
    }

    /// <summary>
    /// Builds an <see cref="OrgZDeviceRecord"/> reflecting the current state of a device
    /// and writes it to <c>/.orgz/device</c> on the mount. Merges with any existing record
    /// so fields we don't currently know (e.g., AppleFirmwareVersion when running Rockbox)
    /// are preserved from prior stock-firmware boots. Updates FirstSeen/LastSeen and the
    /// firmware-specific timestamps based on which mode is currently booted.
    /// </summary>
    public static void PersistDeviceRecord(ConnectedDevice device)
    {
        if (device.DeviceType != DeviceType.StockIPod
            && device.DeviceType != DeviceType.RockboxIPod)
        {
            return;
        }

        var existing = OrgZDeviceRecord.TryLoad(device.MountPath) ?? new OrgZDeviceRecord();
        var now = DateTime.UtcNow;

        // Merge — current live data wins when present, otherwise preserve what's there.
        existing.Serial               = PreferNonEmpty(device.Serial, existing.Serial);
        existing.Model                = PreferNonEmpty(device.Model, existing.Model);
        existing.AppleModelNumber     = PreferNonEmpty(device.AppleModelNumber, existing.AppleModelNumber);
        existing.IpodGeneration       = PreferNonEmpty(device.IpodGeneration, existing.IpodGeneration);
        existing.AppleFirmwareVersion = PreferNonEmpty(device.AppleFirmwareVersion, existing.AppleFirmwareVersion);
        existing.FireWireGuid         = PreferNonEmpty(device.FireWireGuid, existing.FireWireGuid);
        existing.HardwareModel        = PreferNonEmpty(device.HardwareModel, existing.HardwareModel);

        // Rockbox fields come from rockbox-info.txt directly; re-parse here so the
        // record captures both the target name and version string for later reference.
        if (device.DeviceType == DeviceType.RockboxIPod)
        {
            var infoPath = Path.Combine(device.MountPath, ".rockbox", "rockbox-info.txt");
            if (File.Exists(infoPath))
            {
                try
                {
                    foreach (var line in File.ReadLines(infoPath))
                    {
                        if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                        {
                            existing.RockboxVersion = line[8..].Trim();
                        }
                        else if (line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase))
                        {
                            existing.RockboxTarget = line[7..].Trim();
                        }
                    }
                }
                catch { /* best-effort */ }
            }
            existing.LastSeenRockbox = now;
        }
        else if (device.DeviceType == DeviceType.StockIPod)
        {
            existing.LastSeenStock = now;
        }

        existing.FirstSeen ??= now;
        existing.LastSeen = now;

        existing.TrySave(device.MountPath);
    }

    private static string? PreferNonEmpty(string? fresh, string? fallback)
        => !string.IsNullOrWhiteSpace(fresh) ? fresh : fallback;

    // TODO(device-service): PopulateFromScsiInquiry + IsKnownPassThroughHostileBridge
    // removed from the non-admin detection pipeline. All SCSI INQUIRY / ATA PASS-THROUGH
    // / Apple opcode 0xC6 code paths live in IPodScsiInquiry.cs and will be driven by
    // the elevated helper service once it exists.

    /// <summary>
    /// Queries WMI for the underlying physical disk that backs a drive letter, and lifts
    /// its USB-reported Model and SerialNumber. This bridges LogicalDisk → Partition →
    /// DiskDrive via two ASSOCIATORS queries. Any failure is silent — it's a best-effort
    /// fallback for when the on-disk SysInfo file doesn't give us what we need.
    /// </summary>
    private static void PopulateFromDiskInfo(DriveInfo drive, ConnectedDevice device)
    {
#if WINDOWS
        try
        {
            // Win32_LogicalDisk.DeviceID is "F:" (no trailing slash), but drive.Name is "F:\".
            var letter = drive.Name.TrimEnd('\\', '/');

            var logicalToPart = $"ASSOCIATORS OF {{Win32_LogicalDisk.DeviceID='{letter}'}} WHERE AssocClass = Win32_LogicalDiskToPartition";
            using var partSearcher = new ManagementObjectSearcher(logicalToPart);

            foreach (ManagementObject partition in partSearcher.Get())
            {
                var partId = partition["DeviceID"]?.ToString();
                if (string.IsNullOrEmpty(partId))
                {
                    continue;
                }

                var partToDrive = $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partId}'}} WHERE AssocClass = Win32_DiskDriveToDiskPartition";
                using var diskSearcher = new ManagementObjectSearcher(partToDrive);

                foreach (ManagementObject disk in diskSearcher.Get())
                {
                    var model = disk["Model"]?.ToString()?.Trim();
                    var serial = disk["SerialNumber"]?.ToString()?.Trim();
                    var firmwareRev = disk["FirmwareRevision"]?.ToString()?.Trim();
                    var pnpId = disk["PNPDeviceID"]?.ToString()?.Trim();

                    _log.Debug("WMI DiskDrive {DriveLetter}: Model={Model} Serial={Serial} FirmwareRev={FirmwareRev} PnpId={PnpId}", letter, model, serial, firmwareRev, pnpId);

                    // Always store the WMI hardware model separately so the UI can
                    // surface it on click — this is where mods like "iFlash" show up.
                    if (!string.IsNullOrWhiteSpace(model))
                    {
                        device.HardwareModel = CleanupUsbModelString(model);
                    }

                    if (string.IsNullOrWhiteSpace(device.Model) && !string.IsNullOrWhiteSpace(model))
                    {
                        device.Model = CleanupUsbModelString(model);
                    }

                    if (string.IsNullOrWhiteSpace(device.Serial) && !string.IsNullOrWhiteSpace(serial))
                    {
                        device.Serial = CleanupUsbSerial(serial);
                    }

                    // Extract the iPod's FireWire GUID from any field that might carry it.
                    // On iFlash-modded iPods running Rockbox the USB descriptor is the iFlash
                    // bridge's own, but it embeds the real iPod GUID inside its synthetic
                    // iSerial as "...000A27xxxxxxxxxx...". We scan model + serial + pnpId.
                    var fwGuid = ExtractAppleFireWireGuid(serial)
                                ?? ExtractAppleFireWireGuid(model)
                                ?? ExtractAppleFireWireGuid(pnpId);
                    if (fwGuid != null)
                    {
                        device.FireWireGuid = fwGuid;
                        _log.Debug("FireWire GUID extracted from USB descriptor: {FireWireGuid}", fwGuid);
                    }

                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "WMI disk query failed for {DriveName}", drive.Name);
        }
#endif
    }

    /// <summary>
    /// USB model strings often look like "Apple iPod USB Device" — strip the boilerplate.
    /// </summary>
    internal static string CleanupUsbModelString(string raw)
    {
        var cleaned = raw;
        foreach (var junk in new[] { " USB Device", " USB", " Device" })
        {
            if (cleaned.EndsWith(junk, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[..^junk.Length].Trim();
            }
        }
        return cleaned;
    }

    /// <summary>
    /// Scans a string for Apple's FireWire OUI (<c>000A27</c>) and extracts the 16-hex-char
    /// FireWire GUID that follows. iFlash storage adapters embed the real iPod GUID inside
    /// their synthetic USB iSerial string — e.g. "100000000000A2700153A9E6B" contains the
    /// iPod GUID "000A2700153A9E6B" after an 11-char iFlash prefix. Returns the formatted
    /// GUID (without the 0x prefix) or null if no Apple OUI is present.
    /// </summary>
    internal static string? ExtractAppleFireWireGuid(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        // Normalize to uppercase hex and strip non-hex characters for searching
        var upper = input.ToUpperInvariant();
        const string oui = "000A27";
        var idx = upper.IndexOf(oui, StringComparison.Ordinal);
        if (idx < 0)
        {
            // Some USB bridges strip one leading zero → "00A27..." — try that too
            idx = upper.IndexOf("00A27", StringComparison.Ordinal);
            if (idx >= 0)
            {
                // Back up one char to land on the full "000A27" we need to reconstruct
                // Only valid if we have room to the left to add a synthetic leading 0
                if (idx >= 0 && upper.Length - idx >= 15)
                {
                    var partial = "0" + upper[idx..Math.Min(upper.Length, idx + 15)];
                    return partial.Length >= 16 ? partial[..16] : null;
                }
            }
            return null;
        }

        // Need 16 chars total starting at the OUI for a full 64-bit GUID
        if (upper.Length - idx < 16)
        {
            return null;
        }

        var candidate = upper.Substring(idx, 16);
        // Validate it's all hex
        foreach (var c in candidate)
        {
            if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'F')))
            {
                return null;
            }
        }
        return candidate;
    }

    /// <summary>
    /// USB iSerial from Win32_DiskDrive sometimes comes back as hex-encoded pairs with
    /// nibble-swapped byte order ("3030303034343733..." → "00004473..."). Attempt to
    /// decode it if it looks like even-length hex; otherwise return as-is.
    /// </summary>
    internal static string CleanupUsbSerial(string raw)
    {
        var s = raw.Trim();
        if (s.Length % 2 != 0 || s.Length < 8 || !s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
        {
            return s;
        }

        try
        {
            // Nibble-swapped hex → bytes
            var decoded = new StringBuilder(s.Length / 2);
            for (int i = 0; i < s.Length; i += 2)
            {
                var hi = Convert.ToByte(s[i + 1].ToString() + s[i], 16);
                if (hi >= 32 && hi < 127)
                {
                    decoded.Append((char)hi);
                }
            }

            var result = decoded.ToString().Trim();
            // If decoding produced something reasonable, use it; otherwise fall back to raw
            return result.Length >= 6 ? result : s;
        }
        catch
        {
            return s;
        }
    }

    /// <summary>
    /// Populates Model / Serial / FirmwareVersion from whichever iPod metadata file(s) exist.
    /// Tries SysInfo (plain text, older iPods), SysInfoExtended (XML plist, newer iPods),
    /// and the iTunesDB MHBD header as a last resort. All matching is case-insensitive
    /// because Apple's key names differ across firmware generations.
    /// </summary>
    private static void PopulateIPodSysInfo(string root, ConnectedDevice device)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sysInfoPath = Path.Combine(root, "iPod_Control", "Device", "SysInfo");
        var sysInfoExtPath = Path.Combine(root, "iPod_Control", "Device", "SysInfoExtended");

        if (File.Exists(sysInfoPath))
        {
            ReadSysInfoPlainText(sysInfoPath, fields);
        }
        if (File.Exists(sysInfoExtPath))
        {
            ReadSysInfoExtendedPlist(sysInfoExtPath, fields);
        }

        if (fields.Count == 0)
        {
            _log.Debug("SysInfo: empty or missing at {SysInfoPath}", sysInfoPath);
            return;
        }

        _log.Debug("SysInfo: {KeyCount} keys parsed at {SysInfoPath}", fields.Count, sysInfoPath);
        if (_log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            foreach (var kvp in fields)
            {
                _log.Verbose("SysInfo  {Key}={Value}", kvp.Key, kvp.Value);
            }
        }

        // Model — ModelNumStr is Apple's authoritative identifier. Look it up in libgpod's
        // table via IPodModelDatabase for the definitive generation/color/capacity decoding.
        // Also store the raw string in AppleModelNumber so the /.orgz/device record keeps
        // the verbatim "MA446LL/A"-style identifier alongside the decoded display form.
        if (fields.TryGetValue("ModelNumStr", out var modelNum) && !string.IsNullOrWhiteSpace(modelNum))
        {
            device.AppleModelNumber = modelNum;
            device.Model = IPodModelDatabase.LookupByModelNumber(modelNum)?.DisplayName ?? $"iPod ({modelNum})";
        }
        else if (fields.TryGetValue("ModelNum", out modelNum) && !string.IsNullOrWhiteSpace(modelNum))
        {
            device.AppleModelNumber = modelNum;
            device.Model = IPodModelDatabase.LookupByModelNumber(modelNum)?.DisplayName ?? $"iPod ({modelNum})";
        }

        // Serial — try several known key names
        foreach (var key in new[] { "pszSerialNumber", "SerialNumber", "HdSerialNumber" })
        {
            if (fields.TryGetValue(key, out var serial) && !string.IsNullOrWhiteSpace(serial))
            {
                device.Serial = serial;
                break;
            }
        }

        // Apple iPod OS version — stored in AppleFirmwareVersion so it can co-exist
        // with a Rockbox firmware string on dual-firmware iPods.
        if (fields.TryGetValue("visibleBuildID", out var buildId) && !string.IsNullOrWhiteSpace(buildId))
        {
            device.AppleFirmwareVersion = FormatFirmwareVersion(buildId);
        }
        else if (fields.TryGetValue("FirmwareVersionString", out buildId) && !string.IsNullOrWhiteSpace(buildId))
        {
            device.AppleFirmwareVersion = $"iPod OS {buildId}";
        }

        // Fallback serial: FirewireGuid is unique per device
        if (string.IsNullOrWhiteSpace(device.Serial) && fields.TryGetValue("FirewireGuid", out var guid) && !string.IsNullOrWhiteSpace(guid))
        {
            device.Serial = guid;
        }
    }

    /// <summary>
    /// Reads the legacy plain-text SysInfo file ("key: value" per line). Appends into the
    /// shared dictionary so SysInfoExtended can override with richer values if both exist.
    /// </summary>
    private static void ReadSysInfoPlainText(string path, Dictionary<string, string> fields)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var idx = line.IndexOf(':');
                if (idx < 0)
                {
                    continue;
                }

                var key = line[..idx].Trim();
                var value = line[(idx + 1)..].Trim();

                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
                {
                    fields[key] = value;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "SysInfo parse error at {Path}", path);
        }
    }

    /// <summary>
    /// Reads the newer SysInfoExtended (Apple plist XML) used on iPod Classic 7G, Nano 5G+,
    /// and later. Format is a <plist><dict><key>X</key><string>Y</string>... nesting.
    /// </summary>
    private static void ReadSysInfoExtendedPlist(string path, Dictionary<string, string> fields)
    {
        try
        {
            var doc = XDocument.Load(path);
            var dict = doc.Root?.Element("dict");
            if (dict == null)
            {
                return;
            }

            string? currentKey = null;
            foreach (var element in dict.Elements())
            {
                if (element.Name.LocalName == "key")
                {
                    currentKey = element.Value.Trim();
                }
                else if (currentKey != null)
                {
                    // For simple values — string/integer/true/false — capture the element text.
                    // We don't recurse into nested <dict>/<array>; none of our target fields are nested.
                    var value = element.Name.LocalName switch
                    {
                        "string" => element.Value.Trim(),
                        "integer" => element.Value.Trim(),
                        "true" => "true",
                        "false" => "false",
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(value))
                    {
                        fields[currentKey] = value;
                    }
                    currentKey = null;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "SysInfoExtended parse error at {Path}", path);
        }
    }

    /// <summary>
    /// visibleBuildID can be either hex ("0x011d1420 (1.3)") or a plain version string.
    /// Extract the human-readable version when present.
    /// </summary>
    internal static string FormatFirmwareVersion(string buildId)
    {
        var openParen = buildId.IndexOf('(');
        var closeParen = buildId.IndexOf(')');
        if (openParen >= 0 && closeParen > openParen)
        {
            return $"iPod OS {buildId[(openParen + 1)..closeParen]}";
        }
        return $"iPod OS {buildId}";
    }

    /// <summary>
    /// Reads rockbox-info.txt from .rockbox/. Parses BOTH Version: and Target: — the
    /// latter identifies the iPod generation unambiguously (one of "ipod1g2g", "ipod3g",
    /// "ipod4g", "ipodmini1g/2g", "ipodcolor", "ipodnano1g-4g", "ipodvideo", "ipod6g")
    /// and lets us fill in Model/IpodGeneration when SysInfo and SCSI both come up empty.
    /// Rockbox doesn't expose the iPod's hardware serial or FW GUID — those live in the
    /// PL2506 bridge chip, not the SoC Rockbox runs on — so serial still has to come
    /// from the FW-GUID cache or live SCSI on stock firmware.
    /// </summary>
    private static void PopulateRockboxVersion(string root, ConnectedDevice device)
    {
        var infoPath = Path.Combine(root, ".rockbox", "rockbox-info.txt");
        if (!File.Exists(infoPath))
        {
            device.FirmwareVersion = "Rockbox";
            return;
        }

        string? version = null;
        string? target = null;

        try
        {
            foreach (var line in File.ReadLines(infoPath))
            {
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    version = line[8..].Trim();
                }
                else if (line.StartsWith("Target:", StringComparison.OrdinalIgnoreCase))
                {
                    target = line[7..].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to read rockbox-info.txt at {InfoPath}", infoPath);
        }

        device.FirmwareVersion = string.IsNullOrWhiteSpace(version) ? "Rockbox" : $"Rockbox {version}";

        if (!string.IsNullOrWhiteSpace(target) && string.IsNullOrWhiteSpace(device.IpodGeneration))
        {
            var generation = MapRockboxTargetToGeneration(target);
            if (generation != null)
            {
                device.IpodGeneration = generation;
                // Only use this as the display model when libgpod serial lookup hasn't
                // filled in something more specific already. This is lower-priority than
                // the serial-suffix lookup + cache restoration above.
                if (string.IsNullOrWhiteSpace(device.Model))
                {
                    device.Model = $"iPod {generation}";
                }
                _log.Debug("Rockbox target mapped: Target={Target} -> Generation={Generation}", target, generation);
            }
        }
    }

    /// <summary>
    /// Maps Rockbox target names to libgpod-style generation strings so they can share
    /// the same image lookup and display paths as SCSI-sourced identifications. Some
    /// Rockbox targets bundle multiple Apple generations that share a board layout —
    /// "ipodvideo" covers both 5G and 5.5G, "ipod6g" covers 6G/6.5G/7G Classic — in
    /// which case we pick the earliest one; serial-suffix lookup will override with the
    /// exact variant when a real serial is available.
    /// </summary>
    internal static string? MapRockboxTargetToGeneration(string target)
    {
        return target.ToLowerInvariant() switch
        {
            "ipod1g2g"                  => "2G",
            "ipod3g"                    => "3G",
            "ipod4g"                    => "4G",
            "ipodcolor" or "ipodphoto"  => "Photo",
            "ipodmini1g"                => "Mini 1G",
            "ipodmini2g"                => "Mini 2G",
            "ipodnano1g"                => "Nano 1G",
            "ipodnano2g"                => "Nano 2G",
            "ipodnano3g"                => "Nano 3G",
            "ipodnano4g"                => "Nano 4G",
            "ipodvideo"                 => "Video 5G",  // also covers 5.5G — serial refines
            "ipod6g"                    => "Classic 6G", // also 6.5G/7G — serial refines
            _                           => null,
        };
    }
}
