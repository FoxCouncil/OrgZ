// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

#if WINDOWS
namespace OrgZ.Services;

/// <summary>
/// Adapts the Windows <see cref="SmtcService"/> to <see cref="INowPlayingIntegration"/>: maps the
/// string playback status to SMTC's enum, the metadata record to its DisplayUpdater call, and
/// SMTC's Play/Pause/Next/Previous buttons back to the interface events. SMTC needs the app's
/// window handle, so it comes up via <see cref="Initialize"/> (called from the view once the HWND
/// exists) rather than the interface's <see cref="InitializeAsync"/>.
/// </summary>
internal sealed class SmtcNowPlaying : INowPlayingIntegration
{
    private readonly SmtcService _smtc = new();
    private NowPlayingMetadata? _current;

    // SMTC only surfaces PlayPause / Next / Previous; the rest are declared for the interface.
    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopRequested;
    public event Action? RaiseRequested;

    /// <summary>Diagnostics string from the underlying SMTC init attempt.</summary>
    public string? Diagnostics => _smtc.InitDiagnostics;

    /// <summary>
    /// Brings SMTC online against the app window. Returns false when unavailable (pre-Win10, no
    /// session), so the caller leaves now-playing unwired - exactly as before this existed.
    /// </summary>
    public bool Initialize(IntPtr hwnd)
    {
        if (!_smtc.Initialize(hwnd))
        {
            return false;
        }
        _smtc.PlayPauseRequested += () => PlayPauseRequested?.Invoke();
        _smtc.NextRequested += () => NextRequested?.Invoke();
        _smtc.PreviousRequested += () => PreviousRequested?.Invoke();
        return true;
    }

    // Real initialisation is HWND-based (see Initialize); nothing to await here.
    public Task InitializeAsync() => Task.CompletedTask;

    public void SetMetadata(NowPlayingMetadata metadata)
    {
        _current = metadata;
        _smtc.UpdateMetadata(metadata.Title, metadata.Artist, metadata.Album, metadata.ArtBytes);
    }

    public void SetArtwork(byte[] artBytes)
        => _smtc.UpdateMetadata(_current?.Title, _current?.Artist, _current?.Album, artBytes);

    public void SetPlaybackStatus(string status)
        => _smtc.SetPlaybackStatus(status switch
        {
            "Playing" => MediaPlaybackStatus.Playing,
            "Paused" => MediaPlaybackStatus.Paused,
            "Stopped" => MediaPlaybackStatus.Stopped,
            _ => MediaPlaybackStatus.Closed,
        });

    // SMTC's DisplayUpdater carries no elapsed-time surface in the interop we expose.
    public void SetPlaybackPosition(TimeSpan elapsed, double rate)
    {
    }

    public void Dispose() => _smtc.Dispose();
}
#endif
