// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Keeps a connected device's volume mounted on macOS by holding an open read handle on a
/// file inside it. Finder's device-sync stack (AMPDevicesAgent, the iTunes successor)
/// auto-attaches to every classic iPod on mount and - when the device isn't flagged for
/// disk use - ejects the volume seconds later. Its unmount is non-forced, so a single open
/// handle makes it fail with "busy" and the volume stays: the same possession model iTunes
/// used - hold the device while in use, eject deliberately. Validated on hardware
/// 2026-07-06 (Video 5.5G on macOS 26: unheld volume ejected within ~2-13s of every mount,
/// held volume survived indefinitely). No-op on Windows/Linux, where nothing contests the
/// mount.
/// </summary>
public static class DeviceVolumeHold
{
    private static readonly ILogger _log = Logging.For("DeviceVolumeHold");
    private static readonly object _sync = new();
    private static readonly Dictionary<string, FileStream> _holds = new(StringComparer.Ordinal);

    // Any one open handle keeps the volume busy; tried in order. The iTunesDB rewrite is an
    // atomic temp+rename, which POSIX allows over an open handle - the held (now unlinked)
    // inode still pins the volume.
    private static readonly string[][] _candidateFiles =
    [
        ["iPod_Control", "iTunes", "iTunesDB"],
        [".rockbox", "rockbox-info.txt"],
    ];

    public static void Acquire(string mountPath)
    {
        if (!OperatingSystem.IsMacOS() || string.IsNullOrWhiteSpace(mountPath))
        {
            return;
        }

        lock (_sync)
        {
            if (_holds.ContainsKey(mountPath))
            {
                return;
            }

            foreach (var candidate in _candidateFiles)
            {
                var path = Path.Combine([mountPath, .. candidate]);
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    _holds[mountPath] = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    _log.Debug("Volume hold acquired: {MountPath} via {File}", mountPath, path);
                    return;
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Volume hold open failed for {Path}", path);
                }
            }

            _log.Debug("No holdable file found on {MountPath} — volume unprotected against auto-eject", mountPath);
        }
    }

    public static void Release(string mountPath)
    {
        if (string.IsNullOrWhiteSpace(mountPath))
        {
            return;
        }

        lock (_sync)
        {
            if (_holds.Remove(mountPath, out var stream))
            {
                stream.Dispose();
                _log.Debug("Volume hold released: {MountPath}", mountPath);
            }
        }
    }
}
