// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// A backend that enumerates audio output devices and constructs
/// <see cref="IAudioSink"/> instances for them.  One provider per platform
/// subsystem - waveOut on Windows, CoreAudio on macOS, PulseAudio on Linux,
/// and future network-protocol providers (AirPlay, Chromecast, Sonos) all
/// expose the same shape.
/// </summary>
/// <remarks>
/// <para>
/// Providers are stateless except for caches (device-name lookups, mDNS
/// browser state).  <see cref="EnumerateDevices"/> should be cheap enough
/// to call on every Settings-dialog open without noticeable lag.
/// </para>
/// <para>
/// Hotplug / discovery events surface through <see cref="DevicesChanged"/>.
/// Listeners re-enumerate.  Network providers (AirPlay) fire this as new
/// receivers appear on the LAN; local providers typically fire on device
/// arrival / removal from the OS audio graph.
/// </para>
/// </remarks>
public interface IAudioSinkProvider
{
    string ProviderId { get; }
    string ProviderName { get; }

    /// <summary>Whether this provider can run on the current platform.</summary>
    bool IsSupported { get; }

    IReadOnlyList<AudioDeviceInfo> EnumerateDevices();

    IAudioSink CreateSink(AudioDeviceInfo device);

    event EventHandler? DevicesChanged;
}
