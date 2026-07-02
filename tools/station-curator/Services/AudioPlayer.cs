// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Threading;
using LibVLCSharp.Shared;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// LibVLC audition player. Streams the given URL and reports state transitions; open
/// failures surface via EncounteredError (libvlc does not throw for network errors).
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly LibVLC? _vlc;
    private readonly MediaPlayer? _player;

    // The player's native transition to a new Media completes on a worker thread after
    // Play() returns - disposing the Media inline races that transition and corrupts
    // native state (see MainWindowViewModel.DeferDispose in OrgZ). Keep the current
    // Media alive for the whole playback and free the previous one at Background
    // priority once the player has claimed its new ref.
    private Media? _currentMedia;

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    /// <summary>Raised off the UI thread with "opening" / "buffering" / "playing" / "stopped" / "error" / "ended".</summary>
    public event Action<string>? StateChanged;

    public AudioPlayer()
    {
        try
        {
            _vlc = new LibVLC();
            _vlc.SetUserAgent(Web.BrowserUa, Web.BrowserUa);
            _player = new MediaPlayer(_vlc) { Volume = 100 };
            _player.Opening += (_, _) => StateChanged?.Invoke("opening");
            _player.Buffering += (_, e) => { if (e.Cache >= 100f) { StateChanged?.Invoke("playing"); } };
            _player.Playing += (_, _) => StateChanged?.Invoke("playing");
            _player.Stopped += (_, _) => StateChanged?.Invoke("stopped");
            _player.EndReached += (_, _) => StateChanged?.Invoke("ended");
            _player.EncounteredError += (_, _) => StateChanged?.Invoke("error");
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            IsAvailable = false;
            UnavailableReason = ex.Message;
        }
    }

    public void Play(string url)
    {
        if (_vlc == null || _player == null)
        {
            return;
        }

        var previous = _currentMedia;
        _currentMedia = new Media(_vlc, url, FromType.FromLocation);
        _player.Play(_currentMedia);
        DeferDispose(previous);
    }

    public void Stop()
    {
        _player?.Stop();
        DeferDispose(_currentMedia);
        _currentMedia = null;
    }

    private static void DeferDispose(Media? media)
    {
        if (media == null)
        {
            return;
        }
        Dispatcher.UIThread.Post(media.Dispose, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _player?.Dispose();
        _currentMedia?.Dispose();
        _vlc?.Dispose();
    }
}
