// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Text;

namespace OrgZ.Services;

/// <summary>
/// Renames a device the way iTunes does. On a stock iPod the user-visible name lives in
/// <c>iPod_Control/iTunes/DeviceInfo</c> - a u16 LE character count followed by the UTF-16LE name,
/// zero-padded to 0x600 bytes - and the volume label merely mirrors it for file managers (FAT32
/// clips labels at 11 chars, which is why "DEBBIE'S IPOD" mounts as "DEBBIE'S IP"). DeviceInfo is
/// authoritative: detection prefers it over the label. Rockbox/generic players have no DeviceInfo,
/// so for them the volume label IS the name.
/// </summary>
public static class IPodRename
{
    private static readonly Serilog.ILogger _log = Logging.For("IPodRename");

    private const int DeviceInfoSize = 0x600;                       // what iTunes writes
    private const int MaxNameChars = (DeviceInfoSize - 2) / 2;      // hard ceiling the layout can hold

    public static string DeviceInfoPath(string mountPath) => Path.Combine(IPodPaths.ITunesDir(mountPath), "DeviceInfo");

    /// <summary>Reads the iTunes-written device name; null when the file is absent or garbled.</summary>
    public static string? TryReadName(string mountPath)
    {
        try
        {
            var path = DeviceInfoPath(mountPath);
            if (!File.Exists(path))
            {
                return null;
            }
            var b = File.ReadAllBytes(path);
            if (b.Length < 2)
            {
                return null;
            }
            int chars = b[0] | (b[1] << 8);
            if (chars <= 0 || 2 + chars * 2 > b.Length)
            {
                return null;
            }
            var name = Encoding.Unicode.GetString(b, 2, chars * 2).Trim('\0').Trim();
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "DeviceInfo read failed on {Mount}", mountPath);
            return null;
        }
    }

    /// <summary>Writes the DeviceInfo name in iTunes's exact layout. Only meaningful on a mount that
    /// has an iPod_Control tree - callers skip it for Rockbox/generic players.</summary>
    public static void WriteName(string mountPath, string name)
    {
        var trimmed = name.Length > MaxNameChars ? name[..MaxNameChars] : name;
        var buffer = new byte[DeviceInfoSize];
        buffer[0] = (byte)(trimmed.Length & 0xFF);
        buffer[1] = (byte)((trimmed.Length >> 8) & 0xFF);
        Encoding.Unicode.GetBytes(trimmed, 0, trimmed.Length, buffer, 2);
        var path = DeviceInfoPath(mountPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        AtomicFile.WriteAllBytes(path, buffer, backup: path + ".orgzbak");
    }

    /// <summary>A volume-label-safe rendering of the name: FAT-invalid characters stripped, clipped
    /// to FAT32's 11-character ceiling.</summary>
    internal static string SanitizeVolumeLabel(string name)
    {
        const string invalid = "*?/\\|,;:+=<>[]\".";
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c >= 0x20 && !invalid.Contains(c))
            {
                sb.Append(c);
            }
        }
        var s = sb.ToString().Trim();
        return s.Length > 11 ? s[..11].TrimEnd() : s;
    }

    /// <summary>Sets the volume label to mirror the name. Returns the applied label, or null when the
    /// platform couldn't do it (the DeviceInfo name still stands - it's the authoritative one).</summary>
    public static string? TrySetVolumeLabel(string mountPath, string name)
    {
        var label = SanitizeVolumeLabel(name);
        if (label.Length == 0)
        {
            return null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                new DriveInfo(mountPath).VolumeLabel = label;
                return label;
            }
            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("diskutil") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                psi.ArgumentList.Add("rename");
                psi.ArgumentList.Add(mountPath.TrimEnd('/'));
                psi.ArgumentList.Add(label);
                using var proc = Process.Start(psi);
                proc?.WaitForExit(15000);
                return proc?.ExitCode == 0 ? label : null;
            }
            // Linux: relabeling FAT needs the block device + root (fatlabel); not worth a polkit
            // prompt for a cosmetic mirror - the DeviceInfo name carries the rename.
            _log.Information("Volume relabel skipped on this platform for {Mount}", mountPath);
            return null;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Volume relabel failed on {Mount}", mountPath);
            return null;
        }
    }
}
