// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// The user-session half of the macOS helper. macOS gates raw reads of a removable disk
/// behind the <c>Removable Volumes</c> TCC permission (<c>kTCCServiceSystemPolicyRemovableVolumes</c>) -
/// a narrow, promptable permission, NOT Full Disk Access. A background daemon can never obtain
/// it (no GUI session for TCC to prompt). This agent runs in the logged-in user's GUI session
/// with the SAME code signature as the daemon: when it touches a removable raw device, macOS
/// shows the one-time consent dialog, and the resulting grant - keyed to the shared signature -
/// is what the root daemon then inherits and reads under, with no prompt of its own.
///
/// Reached via <c>--device-helper-agent</c>. This is the good-citizen path: one narrow prompt
/// for removable-media access, versus demanding the whole disk.
/// </summary>
public static class DeviceHelperAgent
{
    private static readonly ILogger _log = Logging.For("DeviceHelperAgent");

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int LibcOpen(string path, int flags);

    [DllImport("libc", SetLastError = true, EntryPoint = "close")]
    private static extern int LibcClose(int fd);

    /// <summary>
    /// Triggers the Removable Volumes consent dialog in this (GUI) session so the grant is
    /// recorded against our code signature and the same-signed root daemon inherits it.
    /// Two triggers, because the documented one is FILE access on a mounted removable volume
    /// (Apple: "the first time an app tries to access a file on a removable volume the system
    /// prompts"), while the daemon's actual need is the RAW device - we exercise both so
    /// whichever the grant keys on, it's covered. Returns the count of removable targets touched.
    /// </summary>
    public static int PrimeRemovableAccess()
    {
        var touched = 0;

        // 1) File access on each mounted removable volume - the documented prompt trigger.
        foreach (var mount in EnumerateRemovableMounts())
        {
            try
            {
                var entries = Directory.EnumerateFileSystemEntries(mount).Take(1).ToList();
                foreach (var entry in entries)
                {
                    if (File.Exists(entry))
                    {
                        using var fs = File.OpenRead(entry);
                        fs.ReadByte();
                    }
                }
                _log.Information("agent primed volume {Mount}: file access OK", mount);
                touched++;
            }
            catch (Exception ex)
            {
                _log.Information("agent primed volume {Mount}: {Msg} (prompt may have appeared)", mount, ex.Message);
                touched++;
            }
        }

        // 2) Raw device open - matches exactly what the daemon does; expected to fail on
        //    non-root POSIX perms, but forces TCC to evaluate the raw-device path too.
        foreach (var dev in EnumerateExternalRawDevices())
        {
            const int O_RDONLY = 0;
            int fd = LibcOpen(dev, O_RDONLY);
            var errno = Marshal.GetLastWin32Error();
            if (fd >= 0)
            {
                LibcClose(fd);
                _log.Information("agent primed {Dev}: opened OK", dev);
            }
            else
            {
                _log.Information("agent primed {Dev}: open failed errno={Errno} (expected — daemon does the read)", dev, errno);
            }
        }

        return touched;
    }

    /// <summary>Mounted removable/external volumes under /Volumes, excluding the boot volume.</summary>
    private static List<string> EnumerateRemovableMounts()
    {
        var mounts = new List<string>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/Volumes"))
            {
                // Skip the boot volume (its real path resolves to "/").
                var real = new DirectoryInfo(dir).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? dir;
                if (real == "/")
                {
                    continue;
                }
                mounts.Add(dir);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "agent: enumerating /Volumes failed");
        }
        return mounts;
    }

    /// <summary>Raw char devices of external physical disks, plus their firmware partition.</summary>
    private static List<string> EnumerateExternalRawDevices()
    {
        var result = new List<string>();
        try
        {
            var psi = new ProcessStartInfo { FileName = "/usr/sbin/diskutil", RedirectStandardOutput = true, UseShellExecute = false };
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("external");
            psi.ArgumentList.Add("physical");
            using var process = Process.Start(psi);
            if (process == null)
            {
                return result;
            }
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10_000);

            foreach (var line in output.Split('\n'))
            {
                // Whole-disk header lines look like "/dev/disk10 (external, physical):"
                var trimmed = line.Trim();
                if (trimmed.StartsWith("/dev/disk", StringComparison.Ordinal) && trimmed.Contains("external", StringComparison.Ordinal))
                {
                    var dev = trimmed.Split(' ')[0];                 // /dev/disk10
                    var raw = dev.Replace("/dev/disk", "/dev/rdisk", StringComparison.Ordinal);
                    result.Add(raw);                                 // /dev/rdisk10 (whole disk)
                }
                // Firmware partitions we actually read from
                if (trimmed.Contains("Apple_MDFW", StringComparison.Ordinal))
                {
                    var id = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
                    if (id.StartsWith("disk", StringComparison.Ordinal))
                    {
                        result.Add($"/dev/r{id}");                   // /dev/rdisk10s2
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "agent: enumerating external raw devices failed");
        }
        return result;
    }
}
