// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Threading;
using LibVLCSharp.Shared;

using OrgZ.Services;

namespace OrgZ.StationCurator.Services;

/// <summary>
/// One-shot preview player for auditioning streams, single-connection like the main app:
/// a <see cref="StreamSession"/> owns the ONE upstream pull (ICY de-interleave or HLS
/// client) and pumps clean audio to VLC through <see cref="PipeMediaInput"/> - VLC never
/// opens a network connection. Titles ride the same bytes and are injected via SetMeta,
/// and the session's settled facts double as a free health probe for the audition
/// (<see cref="FactsSettled"/>): every listen IS a probe, off the same connection.
/// </summary>
public sealed class AudioPlayer : IDisposable
{
    private readonly LibVLC? _vlc;
    private readonly MediaPlayer? _player;

    private StreamSession? _session;
    private PipeMediaInput? _input;
    private Media? _currentMedia;

    // Captured per-Media so late MetaChanged events from a disposed previous Media can be
    // detached before DeferDispose - same latent-reentrancy guard the main app uses.
    private EventHandler<MediaMetaChangedEventArgs>? _currentMetaHandler;

    // Generation counter: a Play() issued while a previous connect is still resolving
    // supersedes it - the stale session lands, sees a newer request, and disposes itself.
    private int _playRequest;

    public bool IsAvailable { get; }
    public string? UnavailableReason { get; }

    /// <summary>Raised off the UI thread with "opening" / "buffering" / "playing" / "stopped" / "error" / "ended".</summary>
    public event Action<string>? StateChanged;

    /// <summary>Raised off the UI thread with the stream's live NowPlaying metadata; null when playback moves on or stops.</summary>
    public event Action<string?>? NowPlayingChanged;

    /// <summary>The audition's free probe: raised once per Play with the session's facts - live measurements on success, the failure detail otherwise. No second connection anywhere.</summary>
    public event Action<StreamFacts>? FactsSettled;

    public AudioPlayer()
    {
        try
        {
            Core.Initialize();
            _vlc = new LibVLC("--no-video");
            _player = new MediaPlayer(_vlc);
            _player.Opening += (_, _) => StateChanged?.Invoke("opening");
            _player.Buffering += (_, e) => { if (e.Cache >= 100f) { StateChanged?.Invoke("playing"); } };
            _player.Playing += (_, _) => StateChanged?.Invoke("playing");
            _player.Stopped += (_, _) => StateChanged?.Invoke("stopped");
            // Dead stream → close our upstream too. (NOT hooked on Stopped: VLC fires that
            // mid media-switch, which would kill the session just started for the new one.)
            _player.EndReached += (_, _) => { _session?.Dispose(); StateChanged?.Invoke("ended"); };
            _player.EncounteredError += (_, _) => { _session?.Dispose(); StateChanged?.Invoke("error"); };
            IsAvailable = true;
        }
        catch (Exception ex)
        {
            UnavailableReason = ex.Message;
        }
    }

    public void Play(string url)
    {
        if (_player == null || _vlc == null)
        {
            return;
        }
        var request = ++_playRequest;
        StateChanged?.Invoke("opening");
        _ = ConnectAndPlayAsync(url, request);
    }

    private async Task ConnectAndPlayAsync(string url, int request)
    {
        var session = await StreamSession.ConnectAsync(url, CancellationToken.None);
        Dispatcher.UIThread.Post(() =>
        {
            if (request != _playRequest)
            {
                session.Dispose(); // superseded while connecting
                return;
            }
            if (!session.IsLive)
            {
                FactsSettled?.Invoke(session.Facts); // a dead station is a probe result too
                session.Dispose();
                StateChanged?.Invoke("error");
                return;
            }
            StartPlayback(session);
        });
    }

    private void StartPlayback(StreamSession session)
    {
        var previousMedia = _currentMedia;
        var previousInput = _input;
        _session?.Dispose();   // old station's upstream closes NOW
        DetachMetaHandler();
        NowPlayingChanged?.Invoke(null);

        // Subscribe before the pump starts: measured facts can settle within the first
        // seconds of audio, and the audition must not miss them.
        session.FactsSettled += () => FactsSettled?.Invoke(session.Facts);

        var pipe = session.StartPumping();
        var input = new PipeMediaInput(pipe);
        var media = new Media(_vlc!, input);
        // Callback media reads through libvlc's imem-style access - file-caching is the
        // buffering knob. Same 3s headroom as the main app's radio path.
        media.AddOption(":file-caching=3000");

        EventHandler<MediaMetaChangedEventArgs> handler = (_, e) =>
        {
            if (e.MetadataType == MetadataType.NowPlaying)
            {
                NowPlayingChanged?.Invoke(media.Meta(MetadataType.NowPlaying));
            }
        };
        media.MetaChanged += handler;
        _currentMedia = media;
        _currentMetaHandler = handler;
        _session = session;
        _input = input;
        _player!.Play(media);

        // Titles come off the SAME connection the audio rides; injected on the UI thread,
        // guarded against media switches - a title that lands after this Media is gone
        // must not stamp its successor (or touch a disposed native handle). SetMeta fires
        // MetaChanged, so the handler above stays the single metadata consumer. (The
        // curator has no art surface, so per-track ArtUrl is ignored here.)
        session.NowPlayingChanged += nowPlaying => Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_currentMedia, media))
            {
                return;
            }
            if (nowPlaying == null)
            {
                // Ad/talk break. LibVLCSharp's SetMeta throws ArgumentNullException on
                // null AND empty strings (it killed the process once), so breaks bypass
                // the meta channel entirely - tell the toolbar directly.
                NowPlayingChanged?.Invoke(null);
            }
            else
            {
                media.SetMeta(MetadataType.NowPlaying, nowPlaying.Title);
            }
        });

        // A fast station can deliver its first title before the subscription above ran.
        if (session.Facts.LiveTitle is { } earlyTitle)
        {
            media.SetMeta(MetadataType.NowPlaying, earlyTitle);
        }

        DeferDispose(previousMedia, previousInput);
    }

    public void Stop()
    {
        _playRequest++;        // supersede any connect in flight
        _session?.Dispose();   // unblocks VLC's callback read (EOF) before Stop waits on it
        _session = null;
        _player?.Stop();
        DetachMetaHandler();
        NowPlayingChanged?.Invoke(null);
        DeferDispose(_currentMedia, _input);
        _currentMedia = null;
        _input = null;
    }

    private void DetachMetaHandler()
    {
        if (_currentMedia != null && _currentMetaHandler != null)
        {
            _currentMedia.MetaChanged -= _currentMetaHandler;
        }
        _currentMetaHandler = null;
    }

    private static void DeferDispose(Media? media, PipeMediaInput? input)
    {
        if (media == null && input == null)
        {
            return;
        }
        // The player's native transition to a new Media completes on a worker thread after
        // Play() returns - disposing the Media inline races that transition and corrupts
        // native state (see MainWindowViewModel.DeferDispose in OrgZ). The MediaInput must
        // outlive its Media: once the deferred media dispose has run, VLC's input thread is
        // done with the Read callback and the GCHandle can go.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                media?.Dispose();
            }
            catch
            {
                // Best-effort: the native handle may already be gone.
            }
            input?.Dispose();
        }, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _session?.Dispose();
        _player?.Dispose();
        _currentMedia?.Dispose();
        _input?.Dispose();
        _vlc?.Dispose();
    }
}
