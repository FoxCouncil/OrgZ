// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

#if WINDOWS
using System.Management;
#endif

namespace OrgZ.Services;

/// <summary>
/// Event-driven detection of portable audio devices (iPods, Rockbox players).
/// Uses WMI's Win32_VolumeChangeEvent on Windows - no polling, no window hooks.
/// </summary>
public sealed class DeviceDetectionService : IDisposable
{
    private static readonly ILogger _log = Logging.For<DeviceDetectionService>();

    public event Action<ConnectedDevice>? DeviceConnected;
    public event Action<string>? DeviceDisconnected;

    /// <summary>
    /// Fires when a CD-ROM drive sees media arrival or removal. The subscriber is expected
    /// to run its own TOC/MusicBrainz scan - this service only signals "something changed".
    /// </summary>
    public event Action? CdDriveEvent;

    private readonly Dictionary<string, ConnectedDevice> _connected = new(StringComparer.OrdinalIgnoreCase);

#if WINDOWS
    private ManagementEventWatcher? _watcher;
#endif

    private readonly List<FileSystemWatcher> _linuxMountWatchers = [];

    // Debounce: a flaky USB port can re-mount the same path multiple times per second.
    // Track the latest debounce generation per mount path - when the timer fires we only
    // act if the generation matches what we captured at scheduling time.
    private readonly Dictionary<string, int> _linuxMountDebounce = new(StringComparer.Ordinal);
    private readonly object _linuxMountDebounceLock = new();

    public IReadOnlyCollection<ConnectedDevice> Connected => _connected.Values;

    /// <summary>
    /// Enumerates currently-mounted devices and installs the platform hot-plug watcher.
    /// </summary>
    public void Start()
    {
        // Pick up devices that were already mounted at startup
        EnumerateExistingDrives();

#if WINDOWS
        try
        {
            // EventType 2 = Arrival, 3 = Complete Removal
            var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2 OR EventType = 3");
            _watcher = new ManagementEventWatcher(query);
            _watcher.EventArrived += OnVolumeEvent;
            _watcher.Start();
        }
        catch (Exception ex)
        {
            _log.Error(ex, "WMI watcher failed to start — device hot-plug detection disabled for this session");
        }
#else
        if (OperatingSystem.IsLinux())
        {
            StartLinuxMountWatchers();
        }
        else if (OperatingSystem.IsMacOS())
        {
            StartMacVolumeWatcher();
        }
#endif
    }

    // macOS auto-mounts removable media (USB sticks, audio CDs, disk images)
    // under /Volumes. Watching that directory for subdirectory creation/deletion
    // covers hot-plug events without depending on DiskArbitration callbacks -
    // good enough for the CD/iPod use cases without dragging in another runloop.
    private FileSystemWatcher? _macVolumeWatcher;

    private void StartMacVolumeWatcher()
    {
        const string Volumes = "/Volumes";
        if (!Directory.Exists(Volumes))
        {
            _log.Information("/Volumes missing — macOS hot-plug detection disabled for this session");
            return;
        }

        try
        {
            _macVolumeWatcher = new FileSystemWatcher(Volumes)
            {
                NotifyFilter = NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _macVolumeWatcher.Created += OnMacVolumeCreated;
            _macVolumeWatcher.Deleted += OnMacVolumeDeleted;
            _log.Debug("macOS /Volumes watcher installed");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to install macOS /Volumes watcher — hot-plug detection disabled");
        }
    }

    private void OnMacVolumeCreated(object sender, FileSystemEventArgs e)
    {
        // cddafs synthesizes the .aiff files lazily; give the kernel a beat to
        // finish publishing the new volume before we ask DriveInfo about it.
        _ = Task.Delay(TimeSpan.FromMilliseconds(600)).ContinueWith(_ =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    var drive = new DriveInfo(e.FullPath);
                    if (drive.DriveType == DriveType.CDRom)
                    {
                        // CDs route through ScanForCdAsync just like the startup scan.
                        CdDriveEvent?.Invoke();
                    }
                    else
                    {
                        TryAddDrive(drive);
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Volume {MountPath} not readable shortly after mount-create event", e.FullPath);
                }
            });
        });
    }

    private void OnMacVolumeDeleted(object sender, FileSystemEventArgs e)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => RemoveDrive(e.FullPath));
    }

    // udisks2 auto-mounts removable media under /media/$USER on Debian/Ubuntu and
    // /run/media/$USER on Fedora/Arch. Watching those directories for subdirectory
    // creation/deletion gives us hot-plug without polling /proc/self/mountinfo.
    private void StartLinuxMountWatchers()
    {
        var user = Environment.UserName;
        var roots = new[]
        {
            $"/media/{user}",
            $"/run/media/{user}",
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(root)
                {
                    NotifyFilter = NotifyFilters.DirectoryName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Created += OnLinuxMountCreated;
                watcher.Deleted += OnLinuxMountDeleted;
                _linuxMountWatchers.Add(watcher);
                _log.Debug("Linux mount watcher installed at {Root}", root);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to install Linux mount watcher at {Root}", root);
            }
        }

        if (_linuxMountWatchers.Count == 0)
        {
            _log.Information("No udisks2 mount root present — Linux hot-plug detection disabled for this session");
        }
    }

    private async void OnLinuxMountCreated(object sender, FileSystemEventArgs e)
    {
        // Debounce: if the same mount path fires multiple Created events within the
        // delay window (flaky USB port, udisks2 retry logic, Finder probe), only the
        // latest one wins. Each event bumps the generation counter; the timer checks
        // at dispatch time that its captured generation still matches current.
        int myGeneration;
        lock (_linuxMountDebounceLock)
        {
            _linuxMountDebounce.TryGetValue(e.FullPath, out var current);
            myGeneration = current + 1;
            _linuxMountDebounce[e.FullPath] = myGeneration;
        }

        // Give udisks2 a beat to finish mounting the filesystem before we try to read it
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(600));
        }
        catch
        {
            // Delay doesn't throw in practice; defensive only
            return;
        }

        lock (_linuxMountDebounceLock)
        {
            if (!_linuxMountDebounce.TryGetValue(e.FullPath, out var current) || current != myGeneration)
            {
                // Superseded by a newer Created event - drop this one
                return;
            }
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            try
            {
                TryAddDrive(new DriveInfo(e.FullPath));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Drive {MountPath} not readable 600ms after mount-create event", e.FullPath);
            }
        });
    }

    private void OnLinuxMountDeleted(object sender, FileSystemEventArgs e)
    {
        // Any pending debounce for this path is now moot - the mount is gone
        lock (_linuxMountDebounceLock)
        {
            _linuxMountDebounce.Remove(e.FullPath);
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(() => RemoveDrive(e.FullPath));
    }

    private void EnumerateExistingDrives()
    {
        bool sawCdDrive = false;
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType == DriveType.CDRom)
            {
                sawCdDrive = true;
                continue;
            }
            TryAddDrive(drive);
        }

        // Trigger one CD scan at startup if the machine has any CD drives.
        // The scan itself figures out which drives actually contain audio media.
        if (sawCdDrive)
        {
            CdDriveEvent?.Invoke();
        }
    }

    private void TryAddDrive(DriveInfo drive)
    {
        try
        {
            var device = DeviceFingerprint.Identify(drive);
            if (device == null)
            {
                _log.Verbose("Drive {DriveName} did not fingerprint as a known device", drive.Name);
                return;
            }

            if (_connected.ContainsKey(device.MountPath))
            {
                _log.Verbose("Drive {MountPath} already connected — ignoring duplicate arrival", device.MountPath);
                return;
            }

            _connected[device.MountPath] = device;
            _log.Information("Device connected: {MountPath} Type={DeviceType} Name={Name} Model={Model} Serial={Serial}", device.MountPath, device.DeviceType, device.Name, device.Model, device.Serial);
            DeviceConnected?.Invoke(device);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "TryAddDrive failed for {DriveName}", drive.Name);
        }
    }

    private static bool IsCdDrive(string mountPath)
    {
        try
        {
            return new DriveInfo(mountPath).DriveType == DriveType.CDRom;
        }
        catch
        {
            return false;
        }
    }

    private void RemoveDrive(string mountPath)
    {
        if (_connected.Remove(mountPath))
        {
            _log.Information("Device disconnected: {MountPath}", mountPath);
            DeviceDisconnected?.Invoke(mountPath);
        }
    }

#if WINDOWS
    private void OnVolumeEvent(object sender, EventArrivedEventArgs e)
    {
        var evt = e.NewEvent;
        var eventType = Convert.ToInt32(evt["EventType"]);
        var driveName = evt["DriveName"]?.ToString();

        if (string.IsNullOrEmpty(driveName))
        {
            _log.Verbose("WMI volume event with no DriveName; ignored");
            return;
        }

        // DriveName from WMI is like "E:" - append separator for DriveInfo
        var mountPath = driveName.EndsWith('\\') ? driveName : driveName + "\\";

        _log.Debug("WMI volume event: EventType={EventType} MountPath={MountPath}", eventType, mountPath);

        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // CD-ROM drives go through a separate path: we fire CdDriveEvent and let the
            // subscriber re-scan disc media. Removable drives (iPods, Rockbox) go through
            // the DeviceFingerprint identification flow.
            if (IsCdDrive(mountPath))
            {
                _log.Debug("CD drive event for {MountPath} — deferring CD scan 750ms", mountPath);
                // Small delay so Windows has time to finish mounting the disc filesystem
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    CdDriveEvent?.Invoke();
                }, TimeSpan.FromMilliseconds(750));
                return;
            }

            if (eventType == 2)
            {
                // Give the drive a moment to fully mount before fingerprinting
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    try
                    {
                        TryAddDrive(new DriveInfo(mountPath));
                    }
                    catch (Exception ex)
                    {
                        _log.Warning(ex, "Drive {MountPath} not mountable 500ms after WMI arrival event", mountPath);
                    }
                }, TimeSpan.FromMilliseconds(500));
            }
            else if (eventType == 3)
            {
                RemoveDrive(mountPath);
            }
        });
    }
#endif

    public void Dispose()
    {
#if WINDOWS
        try
        {
            _watcher?.Stop();
            _watcher?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error tearing down WMI watcher");
        }
        _watcher = null;
#endif

        foreach (var w in _linuxMountWatchers)
        {
            try
            {
                w.EnableRaisingEvents = false;
                w.Dispose();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Error tearing down Linux mount watcher at {Path}", w.Path);
            }
        }
        _linuxMountWatchers.Clear();
    }
}
