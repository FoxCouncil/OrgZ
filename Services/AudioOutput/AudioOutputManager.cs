// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using Serilog;

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// Top-level coordinator for OrgZ's audio output: knows every available
/// provider (waveOut, CoreAudio, PulseAudio, AirPlay…), owns the
/// <see cref="AudioSinkBus"/> that actually plays audio, and persists the
/// user's device selection + per-device volumes between sessions.
/// </summary>
/// <remarks>
/// <para>
/// Settings schema (stored under <c>OrgZ.AudioOutput.Sinks</c> as a JSON string):
/// <code>
/// [
///   { "Id": "waveout:default", "Volume": 1.0, "IsMuted": false },
///   { "Id": "waveout:2",       "Volume": 0.5, "IsMuted": false },
///   { "Id": "airplay:Kitchen._raop._tcp.local", "Volume": 0.8, "IsMuted": false }
/// ]
/// </code>
/// </para>
/// </remarks>
public sealed class AudioOutputManager : IDisposable
{
    internal const string SettingsKey = "OrgZ.AudioOutput.Sinks";

    private static readonly ILogger _log = Logging.For("AudioOutput");

    private readonly List<IAudioSinkProvider> _providers = [];
    private readonly AudioSinkBus _bus = new();
    private AudioFormat? _format;

    private readonly Dictionary<string, HashSet<string>> _lastSeenIds = [];
    private System.Threading.Timer? _pollTimer;
    private bool _disposed;

    /// <summary>
    /// Fires when any provider's device list changes — e.g., a USB DAC plugs
    /// in, PulseAudio registers a new sink, or an AirPlay receiver wakes up.
    /// The Settings dialog subscribes to refresh its list; downstream
    /// consumers (the bus) keep whatever sinks are already open.
    /// </summary>
    public event EventHandler? DevicesChanged;

    public AudioSinkBus Bus => _bus;

    public AudioOutputManager()
    {
#if WINDOWS
        AddProvider(new Windows.WaveOutDeviceProvider());
#endif
        if (OperatingSystem.IsLinux())
        {
            AddProvider(new Linux.PulseAudioDeviceProvider());
        }
        if (OperatingSystem.IsMacOS())
        {
            AddProvider(new MacOS.CoreAudioDeviceProvider());
        }

        // AirPlay works on any OS — mDNS discovery is cross-platform.
        AddProvider(new AirPlay.AirPlayDeviceProvider());

        // Snapshot current device IDs so the first poll tick doesn't fire
        // a spurious DevicesChanged event.
        SnapshotProviderIds();

        // Poll-based hotplug detection — 5s cadence keeps CPU negligible
        // while still making USB-DAC plug/unplug and AirPlay arrivals show
        // up in the Settings UI without a manual Refresh click.
        _pollTimer = new System.Threading.Timer(_ => PollForChanges(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    private void AddProvider(IAudioSinkProvider provider)
    {
        if (!provider.IsSupported)
        {
            return;
        }

        _providers.Add(provider);
        provider.DevicesChanged += OnProviderDevicesChanged;
    }

    private void SnapshotProviderIds()
    {
        foreach (var provider in _providers)
        {
            try
            {
                _lastSeenIds[provider.ProviderId] = provider.EnumerateDevices().Select(d => d.DeviceId).ToHashSet();
            }
            catch
            {
                _lastSeenIds[provider.ProviderId] = [];
            }
        }
    }

    private void PollForChanges()
    {
        if (_disposed)
        {
            return;
        }

        bool anyChanged = false;
        foreach (var provider in _providers)
        {
            HashSet<string> current;
            try
            {
                current = provider.EnumerateDevices().Select(d => d.DeviceId).ToHashSet();
            }
            catch
            {
                continue;
            }

            if (!_lastSeenIds.TryGetValue(provider.ProviderId, out var previous) || !previous.SetEquals(current))
            {
                _lastSeenIds[provider.ProviderId] = current;
                anyChanged = true;
            }
        }

        if (anyChanged)
        {
            _log.Debug("AudioOutputManager: device topology changed");
            DevicesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnProviderDevicesChanged(object? sender, EventArgs e)
    {
        // Mirror provider-level events up to the manager's consumers.  Used
        // by providers that raise their own notifications (none today, but
        // AirPlay-RAOP and Windows MMNotificationClient will eventually).
        DevicesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<IAudioSinkProvider> Providers => _providers;

    /// <summary>
    /// Returns every device from every provider — one flat list for UI
    /// display.  Providers are queried fresh each call so hot-plug /
    /// newly-discovered AirPlay receivers show up.
    /// </summary>
    public IReadOnlyList<AudioDeviceInfo> EnumerateAllDevices()
    {
        var all = new List<AudioDeviceInfo>();
        foreach (var provider in _providers)
        {
            try
            {
                all.AddRange(provider.EnumerateDevices());
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Provider {Id} enumeration failed", provider.ProviderId);
            }
        }
        return all;
    }

    /// <summary>
    /// Sets the bus's expected stream format.  Sinks are opened against this
    /// format when added and when the format changes.
    /// </summary>
    public void SetFormat(AudioFormat format)
    {
        _format = format;
        _bus.SetFormat(format);
    }

    /// <summary>
    /// Replaces the active sink set with sinks created from
    /// <paramref name="selections"/>.  Sinks no longer selected are disposed;
    /// new sinks are opened against the current format.
    /// </summary>
    public void ApplySelections(IEnumerable<SinkSelection> selections)
    {
        var desired = selections.ToList();
        var desiredIds = desired.Select(s => s.QualifiedId).ToHashSet();

        // Remove sinks that are no longer selected.
        foreach (var existing in _bus.Sinks)
        {
            if (!desiredIds.Contains(existing.Id))
            {
                _bus.Remove(existing.Id);
            }
        }

        // Add sinks that are newly selected.
        var existingIds = _bus.Sinks.Select(s => s.Id).ToHashSet();
        foreach (var sel in desired)
        {
            if (existingIds.Contains(sel.QualifiedId))
            {
                var existing = _bus.Sinks.First(s => s.Id == sel.QualifiedId);
                existing.Volume = sel.Volume;
                existing.IsMuted = sel.IsMuted;
                continue;
            }

            var (providerId, deviceId) = AudioDeviceInfo.SplitQualified(sel.QualifiedId);
            var provider = _providers.FirstOrDefault(p => p.ProviderId == providerId);
            if (provider == null)
            {
                _log.Debug("ApplySelections: no provider {ProviderId} for device {DeviceId}", providerId, deviceId);
                continue;
            }

            var deviceInfo = provider.EnumerateDevices().FirstOrDefault(d => d.DeviceId == deviceId);
            if (deviceInfo == null)
            {
                _log.Debug("ApplySelections: device {DeviceId} not currently available on {ProviderId}", deviceId, providerId);
                continue;
            }

            try
            {
                var sink = provider.CreateSink(deviceInfo);
                sink.Volume = sel.Volume;
                sink.IsMuted = sel.IsMuted;
                _bus.Add(sink);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "ApplySelections: failed to create sink for {Id}", sel.QualifiedId);
            }
        }
    }

    /// <summary>
    /// Loads persisted selections from settings and applies them.  On first
    /// run (no persisted state), falls back to the platform's default device.
    /// </summary>
    public void LoadAndApplyPersistedSelections()
    {
        var json = Settings.Get(SettingsKey, "");
        List<SinkSelection> selections;
        if (string.IsNullOrEmpty(json))
        {
            selections = DefaultSelection();
        }
        else
        {
            try
            {
                selections = JsonSerializer.Deserialize<List<SinkSelection>>(json) ?? DefaultSelection();
            }
            catch
            {
                selections = DefaultSelection();
            }
        }

        ApplySelections(selections);
    }

    private List<SinkSelection> DefaultSelection()
    {
        // Pick the first default device from the first local (non-AirPlay)
        // provider — that gives Windows users their system default on first run.
        foreach (var provider in _providers)
        {
            if (provider.ProviderId == AirPlay.AirPlayDeviceProvider.Id)
            {
                continue;
            }

            var devices = provider.EnumerateDevices();
            var defaultDevice = devices.FirstOrDefault(d => d.IsDefault) ?? devices.FirstOrDefault();
            if (defaultDevice != null)
            {
                return [new SinkSelection { QualifiedId = defaultDevice.QualifiedId, Volume = 1f }];
            }
        }

        return [];
    }

    /// <summary>
    /// Persists the current bus state back to settings so the next session
    /// restores it.  Called by the Settings dialog after the user changes
    /// selections and by the ViewModel on shutdown as a safety net.
    /// </summary>
    public void SavePersistedSelections()
    {
        var selections = _bus.Sinks
            .Select(s => new SinkSelection
            {
                QualifiedId = s.Id,
                Volume = s.Volume,
                IsMuted = s.IsMuted,
            })
            .ToList();

        Settings.Set(SettingsKey, JsonSerializer.Serialize(selections));
        Settings.Save();
    }

    public void Dispose()
    {
        _disposed = true;
        _pollTimer?.Dispose();
        _pollTimer = null;
        _bus.Dispose();
    }

    public sealed record SinkSelection
    {
        public required string QualifiedId { get; init; }
        public float Volume { get; init; } = 1f;
        public bool IsMuted { get; init; }
    }
}
