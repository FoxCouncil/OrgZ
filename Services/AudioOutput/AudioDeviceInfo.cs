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
