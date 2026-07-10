// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// One track's worth of "now playing" state, in the union of what every OS surface needs.
/// Each backend reads only the fields it can use: MPRIS (Linux) takes <see cref="ArtUri"/>;
/// MPNowPlayingInfoCenter (macOS) and SMTC (Windows) take <see cref="ArtBytes"/> and, on macOS,
/// <see cref="Duration"/>. A field left null is simply omitted from that platform's payload.
/// </summary>
public sealed record NowPlayingMetadata(
    string? Title,
    string? Artist,
    string? Album,
    TimeSpan? Duration = null,
    string? ArtUri = null,
    byte[]? ArtBytes = null);

/// <summary>
/// The single OS media-integration surface the view-model drives, so it never branches per
/// platform: MPRIS on Linux, MPNowPlayingInfoCenter on macOS, SMTC on Windows. Metadata and
/// transport state flow app → OS; media-key / lock-screen / Control-Center commands flow back
/// via the events. Every method is best-effort - an unavailable backend just no-ops.
/// </summary>
public interface INowPlayingIntegration : IDisposable
{
    event Action? PlayRequested;
    event Action? PauseRequested;
    event Action? PlayPauseRequested;
    event Action? NextRequested;
    event Action? PreviousRequested;
    event Action? StopRequested;
    event Action? RaiseRequested;

    /// <summary>Bring the backend online. macOS initialises in its constructor and SMTC via a
    /// window handle, so those return a completed task; MPRIS does its D-Bus connect here.</summary>
    Task InitializeAsync();

    /// <summary>Publish a new track. Resets the widget to the start of that track.</summary>
    void SetMetadata(NowPlayingMetadata metadata);

    /// <summary>Update just the artwork of the current track (art often arrives after the
    /// metadata). MPRIS no-ops - its art is the URL already carried in the last SetMetadata.</summary>
    void SetArtwork(byte[] artBytes);

    /// <summary>"Playing" / "Paused" / "Stopped" - mirrors transport state to the OS widget.</summary>
    void SetPlaybackStatus(string status);

    /// <summary>Elapsed time + rate, so the widget's scrubber tracks real playback. No-op on
    /// backends without a position surface.</summary>
    void SetPlaybackPosition(TimeSpan elapsed, double rate);
}
