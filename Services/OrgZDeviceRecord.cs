// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Per-device persistent identity record stored in <c>/.orgz/device</c> on the mounted
/// iPod volume. Designed to merge information from both stock Apple firmware boots
/// (where SCSI INQUIRY / opcode 0xC6 reveal the engraved serial, FireWire GUID, and
/// Apple OS version) and Rockbox boots (where the USB bridge hides the iPod identity
/// but the Rockbox target string still resolves the generation).
///
/// Format: simple UTF-8 INI-style <c>Key=Value</c> lines, one per line, with <c>#</c>
/// prefix lines for comments. Atomic write via temp-file-and-rename so a crash mid-flush
/// leaves the old record intact.
///
/// Philosophy: this file IS the device's OrgZ identity cache. When it's present the
/// fingerprint pipeline trusts it and only overwrites fields when the live detection
/// actually yields a non-empty value.
/// </summary>
public sealed class OrgZDeviceRecord
{
    public int Version { get; set; } = 1;

    public string? Serial { get; set; }
    public string? Model { get; set; }
    public string? AppleModelNumber { get; set; }
    public string? IpodGeneration { get; set; }
    public string? AppleFirmwareVersion { get; set; }
    public string? FireWireGuid { get; set; }
    public string? HardwareModel { get; set; }

    public string? RockboxTarget { get; set; }
    public string? RockboxVersion { get; set; }

    public DateTime? FirstSeen { get; set; }
    public DateTime? LastSeen { get; set; }
    public DateTime? LastSeenStock { get; set; }
    public DateTime? LastSeenRockbox { get; set; }

    private const string Subfolder = ".orgz";
    private const string Filename = "device";

    private static string GetRecordPath(string mountPath)
        => Path.Combine(mountPath, Subfolder, Filename);

    /// <summary>
    /// Loads the device record from <c>{mountPath}/.orgz/device</c>. Returns null when
    /// the file doesn't exist or can't be parsed — no exception propagation to callers.
    /// </summary>
    public static OrgZDeviceRecord? TryLoad(string mountPath)
    {
        var path = GetRecordPath(mountPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var record = new OrgZDeviceRecord();
            foreach (var line in File.ReadAllLines(path))
            {
                var trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
                {
                    continue;
                }

                var idx = trimmed.IndexOf('=');
                if (idx <= 0)
                {
                    continue;
                }

                var key = trimmed[..idx].Trim();
                var value = trimmed[(idx + 1)..].Trim();
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                switch (key)
                {
                    case "Version":
                        if (int.TryParse(value, out var v)) record.Version = v;
                        break;
                    case "Serial":               record.Serial = value; break;
                    case "Model":                record.Model = value; break;
                    case "AppleModelNumber":     record.AppleModelNumber = value; break;
                    case "IpodGeneration":       record.IpodGeneration = value; break;
                    case "AppleFirmwareVersion": record.AppleFirmwareVersion = value; break;
                    case "FireWireGuid":         record.FireWireGuid = value; break;
                    case "HardwareModel":        record.HardwareModel = value; break;
                    case "RockboxTarget":        record.RockboxTarget = value; break;
                    case "RockboxVersion":       record.RockboxVersion = value; break;
                    case "FirstSeen":            record.FirstSeen = ParseDate(value); break;
                    case "LastSeen":             record.LastSeen = ParseDate(value); break;
                    case "LastSeenStock":        record.LastSeenStock = ParseDate(value); break;
                    case "LastSeenRockbox":      record.LastSeenRockbox = ParseDate(value); break;
                }
            }
            return record;
        }
        catch (Exception ex)
        {
            Logging.For("OrgZDeviceRecord").Warning(ex, "Load failed for {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Writes the record to <c>{mountPath}/.orgz/device</c>, creating the directory if
    /// it doesn't exist. Uses atomic temp-file-and-rename so a crash mid-write can't
    /// leave a truncated record. Silently no-ops on write failure (read-only mount, etc).
    /// </summary>
    public bool TrySave(string mountPath)
    {
        try
        {
            var dir = Path.Combine(mountPath, Subfolder);
            Directory.CreateDirectory(dir);

            var finalPath = Path.Combine(dir, Filename);
            var tempPath  = finalPath + ".tmp";

            var sb = new StringBuilder();
            sb.AppendLine("# OrgZ device record — auto-generated, safe to delete");
            sb.AppendLine("# https://github.com/FoxCouncil/OrgZ");
            sb.AppendLine($"Version={Version}");

            AppendIfSet(sb, "Serial", Serial);
            AppendIfSet(sb, "Model", Model);
            AppendIfSet(sb, "AppleModelNumber", AppleModelNumber);
            AppendIfSet(sb, "IpodGeneration", IpodGeneration);
            AppendIfSet(sb, "AppleFirmwareVersion", AppleFirmwareVersion);
            AppendIfSet(sb, "FireWireGuid", FireWireGuid);
            AppendIfSet(sb, "HardwareModel", HardwareModel);
            AppendIfSet(sb, "RockboxTarget", RockboxTarget);
            AppendIfSet(sb, "RockboxVersion", RockboxVersion);
            AppendIfSet(sb, "FirstSeen", FirstSeen?.ToString("o"));
            AppendIfSet(sb, "LastSeen", LastSeen?.ToString("o"));
            AppendIfSet(sb, "LastSeenStock", LastSeenStock?.ToString("o"));
            AppendIfSet(sb, "LastSeenRockbox", LastSeenRockbox?.ToString("o"));

            File.WriteAllText(tempPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Atomic replace: delete old, rename new. File.Move overwrite behavior varies
            // across runtimes, so do it explicitly.
            if (File.Exists(finalPath))
            {
                File.Delete(finalPath);
            }
            File.Move(tempPath, finalPath);
            return true;
        }
        catch (Exception ex)
        {
            Logging.For("OrgZDeviceRecord").Warning(ex, "Save failed for {MountPath}", mountPath);
            return false;
        }
    }

    private static void AppendIfSet(StringBuilder sb, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            sb.AppendLine($"{key}={value}");
        }
    }

    private static DateTime? ParseDate(string s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt) ? dt : null;
}
