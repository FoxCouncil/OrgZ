// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput;

/// <summary>
/// A single audio output destination offered by an
/// <see cref="IAudioSinkProvider"/> - a physical device (speakers, USB
/// interface), a virtual device (WASAPI loopback rendering sink), or a
/// network endpoint (AirPlay receiver, Chromecast, Sonos).
/// </summary>
/// <remarks>
/// <see cref="DeviceId"/> is opaque and scoped to the provider - cross-
/// provider uniqueness is guaranteed by combining <see cref="ProviderId"/>
/// with <see cref="DeviceId"/> (see <see cref="QualifiedId"/>).  Settings
/// persistence stores <see cref="QualifiedId"/> so a "Logitech USB DAC"
/// stays identifiable even if the waveOut and WASAPI providers both list
/// it.
/// </remarks>
public sealed record AudioDeviceInfo
{
    public required string DeviceId { get; init; }
    public required string DisplayName { get; init; }
    public required string ProviderId { get; init; }
    public required string ProviderName { get; init; }
    public bool IsDefault { get; init; }
    public bool IsAvailable { get; init; } = true;

    /// <summary>
    /// Status flags the receiver broadcasts in its mDNS TXT record - live state, read
    /// passively off the LAN with no connection. Zero for local devices and for network
    /// receivers whose record carried none.
    /// </summary>
    public long StateFlags { get; init; }

    /// <summary>The receiver is inside an AirPlay session right now (status bit 17).</summary>
    public bool IsReceivingAirPlay => (StateFlags & 0x20000) != 0;

    /// <summary>The receiver's audio pipeline is running - it is audibly playing (status bit 20).</summary>
    public bool IsPlayingAudio => (StateFlags & 0x100000) != 0;

    /// <summary>
    /// Selecting this receiver would take it over from whatever is driving it now.
    /// AirPlay 2 receivers preempt SILENTLY - a new session simply wins - so anything
    /// that connects while this is true should say so to the user first.
    /// </summary>
    public bool IsBusy => IsReceivingAirPlay || IsPlayingAudio;

    public string QualifiedId => $"{ProviderId}:{DeviceId}";

    public static (string ProviderId, string DeviceId) SplitQualified(string qualifiedId)
    {
        var idx = qualifiedId.IndexOf(':');
        if (idx < 0)
        {
            return (string.Empty, qualifiedId);
        }
        return (qualifiedId[..idx], qualifiedId[(idx + 1)..]);
    }
}
