// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// macOS equivalent of <see cref="SmtcService"/> (Windows) and
/// <see cref="MprisService"/> (Linux): pushes current-track metadata to
/// <c>MPNowPlayingInfoCenter</c> so it appears in Control Center's media widget,
/// on the lock screen, on AirPods readouts, and anywhere else AppKit surfaces
/// the "now playing" state.
///
/// Two-way: metadata + transport state flow OrgZ → OS via
/// <c>MPNowPlayingInfoCenter</c>; play/pause/next/previous from media keys,
/// Control Center, and AirPods routes flow OS → OrgZ via
/// <c>MPRemoteCommandCenter</c>. macOS will not display the widget at all
/// unless at least one remote command is enabled, so registration is mandatory
/// for the metadata path to be visible.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacNowPlayingService : INowPlayingIntegration
{
    private static readonly ILogger _log = Logging.For<MacNowPlayingService>();

    // The OS calls our remote-command callbacks via an unmanaged function pointer
    // installed on a runtime-created ObjC class. The callback signature is fixed
    // by the Objective-C ABI ([UnmanagedCallersOnly] static method, no instance
    // state), so we route through this singleton to reach instance event handlers.
    private static MacNowPlayingService? _current;

    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopRequested;
    // macOS has no "raise the app from the widget" command; declared for the interface, never fired.
    public event Action? RaiseRequested;

    // Cached selector / class pointers. Once registered with the Objective-C
    // runtime these are valid for the lifetime of the process, so we resolve
    // once and stash them.
    private readonly IntPtr _nsStringClass;
    private readonly IntPtr _nsNumberClass;
    private readonly IntPtr _nsMutableDictClass;
    private readonly IntPtr _nowPlayingInfoCenter;
    private readonly IntPtr _selSetNowPlayingInfo;
    private readonly IntPtr _selSetPlaybackState;
    private readonly IntPtr _selSetObjectForKey;
    private readonly IntPtr _selStringWithUtf8;
    private readonly IntPtr _selNumberWithDouble;
    private readonly IntPtr _selDictAlloc;
    private readonly IntPtr _selDictInit;
    private readonly IntPtr _selRelease;

    private readonly bool _initialized;

    // MPNowPlayingPlaybackState values per Apple's MediaPlayer headers.
    private const long MPNowPlayingPlaybackStateUnknown = 0;
    private const long MPNowPlayingPlaybackStatePlaying = 1;
    private const long MPNowPlayingPlaybackStatePaused = 2;
    private const long MPNowPlayingPlaybackStateStopped = 3;

    // Standard MPMediaItemProperty / MPNowPlayingInfoProperty keys we set. These are
    // string constants per Apple's headers - using their literal values directly
    // avoids needing to dlsym() the framework's exported NSString globals.
    private const string KeyTitle = "title";
    private const string KeyArtist = "artist";
    private const string KeyAlbumTitle = "albumTitle";
    private const string KeyPlaybackDuration = "playbackDuration";
    private const string KeyElapsedPlaybackTime = "MPNowPlayingInfoPropertyElapsedPlaybackTime";
    private const string KeyPlaybackRate = "MPNowPlayingInfoPropertyPlaybackRate";
    private const string KeyArtwork = "artwork";

    public MacNowPlayingService()
    {
        try
        {
            // Force the MediaPlayer framework to load - its classes aren't part of
            // the default Objective-C image, so without an explicit load NSClassFromString
            // returns nil.
            _ = NativeLibrary.Load("/System/Library/Frameworks/MediaPlayer.framework/MediaPlayer");

            _nsStringClass = objc_getClass("NSString");
            _nsNumberClass = objc_getClass("NSNumber");
            _nsMutableDictClass = objc_getClass("NSMutableDictionary");

            var centerClass = objc_getClass("MPNowPlayingInfoCenter");
            if (centerClass == IntPtr.Zero)
            {
                _log.Warning("MPNowPlayingInfoCenter class not found — MediaPlayer.framework didn't load");
                return;
            }

            _selStringWithUtf8 = sel_registerName("stringWithUTF8String:");
            _selNumberWithDouble = sel_registerName("numberWithDouble:");
            _selDictAlloc = sel_registerName("alloc");
            _selDictInit = sel_registerName("init");
            _selSetObjectForKey = sel_registerName("setObject:forKey:");
            _selRelease = sel_registerName("release");
            _selSetNowPlayingInfo = sel_registerName("setNowPlayingInfo:");
            _selSetPlaybackState = sel_registerName("setPlaybackState:");

            var selDefaultCenter = sel_registerName("defaultCenter");
            _nowPlayingInfoCenter = objc_msgSend_IntPtr(centerClass, selDefaultCenter);
            if (_nowPlayingInfoCenter == IntPtr.Zero)
            {
                _log.Warning("MPNowPlayingInfoCenter defaultCenter returned nil");
                return;
            }

            _current = this;
            RegisterRemoteCommands();

            _initialized = true;
            _log.Information("MacNowPlayingService initialized");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MacNowPlayingService init failed — Now Playing widget will be inert");
        }
    }

    /// <summary>
    /// Pushes the current track to the system's now-playing widget. Pass
    /// <see langword="null"/> for any field you don't have. Safe to call from
    /// any thread; AppKit's NSDictionary mutation isn't main-thread-only.
    /// </summary>
    // Cached so SetPlaybackPosition can rebuild the full info dictionary without the
    // caller having to re-supply identity fields every tick. macOS's widget reads the
    // whole dict each time we set it, so a partial dict would wipe artwork/title etc.
    private string? _cachedTitle;
    private string? _cachedArtist;
    private string? _cachedAlbum;
    private TimeSpan? _cachedDuration;
    private byte[]? _cachedArtwork;
    private double _cachedElapsedSeconds;
    private double _cachedRate;

    // macOS sets everything up in the constructor, so there's nothing async to await.
    public Task InitializeAsync() => Task.CompletedTask;

    public void SetMetadata(NowPlayingMetadata metadata)
        => SetMetadata(metadata.Title, metadata.Artist, metadata.Album, metadata.Duration, metadata.ArtBytes);

    /// <summary>Swap just the cover for the current track (art usually arrives after the
    /// metadata) without disturbing the cached title/position.</summary>
    public void SetArtwork(byte[] artBytes)
    {
        _cachedArtwork = artBytes;
        Publish();
    }

    public void SetMetadata(string? title, string? artist, string? album, TimeSpan? duration, byte[]? artworkBytes = null)
    {
        _cachedTitle = title;
        _cachedArtist = artist;
        _cachedAlbum = album;
        _cachedDuration = duration;
        _cachedArtwork = artworkBytes;
        // New track - start the widget paused at 0. The Playing event will flip the
        // rate to 1.0 once libvlc actually produces audio (after file-caching), so
        // the widget doesn't extrapolate during the buffering delay and run ahead.
        _cachedElapsedSeconds = 0;
        _cachedRate = 0.0;
        Publish();
    }

    /// <summary>
    /// Updates the widget's elapsed-time counter and playback rate. macOS extrapolates
    /// position between updates (elapsed + wall_seconds_since_update * rate), so calling
    /// this every ~1 s is enough to stay aligned with what the user actually hears.
    /// </summary>
    public void SetPlaybackPosition(TimeSpan elapsed, double rate)
    {
        _cachedElapsedSeconds = elapsed.TotalSeconds;
        _cachedRate = rate;
        Publish();
    }

    private void Publish()
    {
        if (!_initialized)
        {
            return;
        }

        try
        {
            var dict = objc_msgSend_IntPtr(_nsMutableDictClass, _selDictAlloc);
            dict = objc_msgSend_IntPtr(dict, _selDictInit);
            try
            {
                if (!string.IsNullOrEmpty(_cachedTitle))  DictSet(dict, KeyTitle, NSString(_cachedTitle));
                if (!string.IsNullOrEmpty(_cachedArtist)) DictSet(dict, KeyArtist, NSString(_cachedArtist));
                if (!string.IsNullOrEmpty(_cachedAlbum))  DictSet(dict, KeyAlbumTitle, NSString(_cachedAlbum));
                if (_cachedDuration is { } d)             DictSet(dict, KeyPlaybackDuration, NSNumber(d.TotalSeconds));

                DictSet(dict, KeyElapsedPlaybackTime, NSNumber(_cachedElapsedSeconds));
                DictSet(dict, KeyPlaybackRate, NSNumber(_cachedRate));

                if (_cachedArtwork is { Length: > 0 })
                {
                    var artwork = BuildArtwork(_cachedArtwork);
                    if (artwork != IntPtr.Zero)
                    {
                        DictSet(dict, KeyArtwork, artwork);
                        _ = objc_msgSend_IntPtr(artwork, _selRelease);
                    }
                }

                objc_msgSend_IntPtr_IntPtr(_nowPlayingInfoCenter, _selSetNowPlayingInfo, dict);
            }
            finally
            {
                _ = objc_msgSend_IntPtr(dict, _selRelease);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MacNowPlayingService.Publish threw");
        }
    }

    // MPMediaItemArtwork can be built from an NSImage. We deliberately use the
    // deprecated -initWithImage: ctor because the modern -initWithBoundsSize:requestHandler:
    // takes an Objective-C block, and bridging blocks from .NET requires hand-rolling
    // the _NSConcreteGlobalBlock literal. -initWithImage: still works on every
    // shipping macOS and is plenty for static cover art.
    private IntPtr BuildArtwork(byte[] bytes)
    {
        // [NSData dataWithBytes:length:]
        var nsDataClass = objc_getClass("NSData");
        if (nsDataClass == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }
        IntPtr nsData;
        unsafe
        {
            fixed (byte* p = bytes)
            {
                nsData = objc_msgSend_dataWithBytes(
                    nsDataClass,
                    sel_registerName("dataWithBytes:length:"),
                    (IntPtr)p,
                    (UIntPtr)bytes.Length);
            }
        }
        if (nsData == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // [[NSImage alloc] initWithData:nsData]
        var nsImageClass = objc_getClass("NSImage");
        var image = objc_msgSend_IntPtr(nsImageClass, _selDictAlloc);
        image = objc_msgSend_IntPtr_IntPtr(image, sel_registerName("initWithData:"), nsData);
        if (image == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        // [[MPMediaItemArtwork alloc] initWithImage:image]
        var artworkClass = objc_getClass("MPMediaItemArtwork");
        if (artworkClass == IntPtr.Zero)
        {
            _ = objc_msgSend_IntPtr(image, _selRelease);
            return IntPtr.Zero;
        }
        var artwork = objc_msgSend_IntPtr(artworkClass, _selDictAlloc);
        artwork = objc_msgSend_IntPtr_IntPtr(artwork, sel_registerName("initWithImage:"), image);

        // Balance the NSImage alloc - MPMediaItemArtwork retains it.
        _ = objc_msgSend_IntPtr(image, _selRelease);

        return artwork;
    }

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_dataWithBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, UIntPtr length);

    /// <summary>
    /// Mirrors transport state to the OS widget. macOS uses this to dim/brighten
    /// the play button and to publish state to AirPods etc.
    /// </summary>
    public void SetPlaybackStatus(string status)
    {
        if (!_initialized)
        {
            return;
        }

        var stateValue = status switch
        {
            "Playing" => MPNowPlayingPlaybackStatePlaying,
            "Paused" => MPNowPlayingPlaybackStatePaused,
            "Stopped" => MPNowPlayingPlaybackStateStopped,
            _ => MPNowPlayingPlaybackStateUnknown,
        };

        // Pause/Stop must freeze the widget's extrapolation immediately. Play
        // does NOT bump rate to 1 here: the libvlc "Playing" event fires the
        // instant the state machine flips, but audio output (and the first
        // TimeChanged callback) lags by 200-500 ms. If we set rate=1 now the
        // widget extrapolates ahead of the actual playback during that gap and
        // stays ~1 s ahead for the whole track. SetPlaybackPosition is what
        // promotes rate to 1, called from the first TimeChanged when libvlc
        // actually has a position to report.
        if (status != "Playing")
        {
            _cachedRate = 0.0;
        }

        try
        {
            objc_msgSend_long(_nowPlayingInfoCenter, _selSetPlaybackState, stateValue);
            Publish();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MacNowPlayingService.SetPlaybackStatus threw");
        }
    }

    public void Dispose()
    {
        // Nothing to release: cached selectors/classes are process-globals owned
        // by the Objective-C runtime, and the singleton info center is not ours
        // to retain or release.
    }

    private IntPtr NSString(string value)
    {
        // [NSString stringWithUTF8String:value] - autoreleased; lifetime spans
        // the calling autorelease pool. The OS-side dictionary retains it before
        // we return.
        var bytes = System.Text.Encoding.UTF8.GetBytes(value + "\0");
        unsafe
        {
            fixed (byte* p = bytes)
            {
                return objc_msgSend_IntPtr_IntPtr(_nsStringClass, _selStringWithUtf8, (IntPtr)p);
            }
        }
    }

    private IntPtr NSNumber(double value)
    {
        return objc_msgSend_IntPtr_double(_nsNumberClass, _selNumberWithDouble, value);
    }

    private void DictSet(IntPtr dict, string key, IntPtr value)
    {
        var keyNs = NSString(key);
        objc_msgSend_IntPtr_IntPtr_IntPtr(dict, _selSetObjectForKey, value, keyNs);
    }

    // ── Remote command bridge ────────────────────────────────────────
    // macOS's MPRemoteCommandCenter expects its commands' targets to be
    // Objective-C objects with action selectors. .NET doesn't ship Apple
    // framework bindings, so we synthesize a tiny ObjC class at runtime,
    // install [UnmanagedCallersOnly] static methods as its action methods,
    // and register an instance with each command we care about.
    //
    // Returning kSuccess (=0) from MPRemoteCommandHandlerStatus tells the
    // OS we handled the event; the widget animates immediately.
    private const long MPRemoteCommandHandlerStatusSuccess = 0;
    private IntPtr _targetInstance;

    private unsafe void RegisterRemoteCommands()
    {
        // Build the target class: subclass NSObject with one method per command.
        // The "instance size" arg is zero because we don't add any ivars - the
        // shared singleton state lives in the managed _current static.
        var nsObject = objc_getClass("NSObject");
        var cls = objc_allocateClassPair(nsObject, "OrgZRemoteCommandTarget", IntPtr.Zero);
        if (cls == IntPtr.Zero)
        {
            // Already registered from a previous service instance in this process -
            // grab the existing class so re-init still works (e.g. after a
            // theoretical Dispose + reconstruct).
            cls = objc_getClass("OrgZRemoteCommandTarget");
            if (cls == IntPtr.Zero)
            {
                _log.Warning("Failed to allocate ObjC class for remote command target");
                return;
            }
        }
        else
        {
            // Method type encoding "q@:@" = returns long, takes (self id, _cmd SEL, event id).
            // The Objective-C runtime requires self+_cmd to be the first two args of every
            // method implementation; the framework supplies them automatically.
            const string enc = "q@:@";
            class_addMethod(cls, sel_registerName("handlePlay:"),         (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&HandlePlay,         enc);
            class_addMethod(cls, sel_registerName("handlePause:"),        (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&HandlePause,        enc);
            class_addMethod(cls, sel_registerName("handleTogglePlay:"),   (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&HandleTogglePlay,   enc);
            class_addMethod(cls, sel_registerName("handleNext:"),         (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&HandleNext,         enc);
            class_addMethod(cls, sel_registerName("handlePrevious:"),     (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&HandlePrevious,     enc);
            class_addMethod(cls, sel_registerName("handleStop:"),         (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, long>)&HandleStop,         enc);
            objc_registerClassPair(cls);
        }

        // [[OrgZRemoteCommandTarget alloc] init]
        var instance = objc_msgSend_IntPtr(cls, _selDictAlloc);
        instance = objc_msgSend_IntPtr(instance, _selDictInit);
        _targetInstance = instance;

        // MPRemoteCommandCenter.shared returns the singleton command center.
        var commandCenterClass = objc_getClass("MPRemoteCommandCenter");
        var commandCenter = objc_msgSend_IntPtr(commandCenterClass, sel_registerName("sharedCommandCenter"));

        WireCommand(commandCenter, "playCommand",            "handlePlay:");
        WireCommand(commandCenter, "pauseCommand",           "handlePause:");
        WireCommand(commandCenter, "togglePlayPauseCommand", "handleTogglePlay:");
        WireCommand(commandCenter, "nextTrackCommand",       "handleNext:");
        WireCommand(commandCenter, "previousTrackCommand",   "handlePrevious:");
        WireCommand(commandCenter, "stopCommand",            "handleStop:");
    }

    private void WireCommand(IntPtr center, string commandName, string handlerSelector)
    {
        // [center <commandName>] → MPRemoteCommand*
        var command = objc_msgSend_IntPtr(center, sel_registerName(commandName));
        if (command == IntPtr.Zero)
        {
            return;
        }

        // [command setEnabled:YES]
        objc_msgSend_byte(command, sel_registerName("setEnabled:"), 1);

        // [command addTarget:_targetInstance action:@selector(<handlerSelector>)]
        objc_msgSend_IntPtr_IntPtr_IntPtr(
            command,
            sel_registerName("addTarget:action:"),
            _targetInstance,
            sel_registerName(handlerSelector));
    }

    [UnmanagedCallersOnly]
    private static long HandlePlay(IntPtr self, IntPtr sel, IntPtr ev)        { _current?.PlayRequested?.Invoke();        return MPRemoteCommandHandlerStatusSuccess; }
    [UnmanagedCallersOnly]
    private static long HandlePause(IntPtr self, IntPtr sel, IntPtr ev)       { _current?.PauseRequested?.Invoke();       return MPRemoteCommandHandlerStatusSuccess; }
    [UnmanagedCallersOnly]
    private static long HandleTogglePlay(IntPtr self, IntPtr sel, IntPtr ev)  { _current?.PlayPauseRequested?.Invoke();   return MPRemoteCommandHandlerStatusSuccess; }
    [UnmanagedCallersOnly]
    private static long HandleNext(IntPtr self, IntPtr sel, IntPtr ev)        { _current?.NextRequested?.Invoke();        return MPRemoteCommandHandlerStatusSuccess; }
    [UnmanagedCallersOnly]
    private static long HandlePrevious(IntPtr self, IntPtr sel, IntPtr ev)    { _current?.PreviousRequested?.Invoke();    return MPRemoteCommandHandlerStatusSuccess; }
    [UnmanagedCallersOnly]
    private static long HandleStop(IntPtr self, IntPtr sel, IntPtr ev)        { _current?.StopRequested?.Invoke();        return MPRemoteCommandHandlerStatusSuccess; }

    // ── Objective-C runtime P/Invokes ────────────────────────────────
    private const string Libobjc = "/usr/lib/libobjc.A.dylib";

    [DllImport(Libobjc, EntryPoint = "objc_allocateClassPair", CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);

    [DllImport(Libobjc, EntryPoint = "objc_registerClassPair")]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(Libobjc, EntryPoint = "class_addMethod", CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr name, IntPtr imp, string types);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_byte(IntPtr receiver, IntPtr selector, byte arg1);

    [DllImport(Libobjc, EntryPoint = "objc_getClass", CharSet = CharSet.Ansi)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(Libobjc, EntryPoint = "sel_registerName", CharSet = CharSet.Ansi)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend has a different signature per return type / arg list. .NET's
    // P/Invoke needs distinct DllImport stubs for each arity we use.

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_IntPtr_IntPtr_IntPtr(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern IntPtr objc_msgSend_IntPtr_double(IntPtr receiver, IntPtr selector, double arg1);

    [DllImport(Libobjc, EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_long(IntPtr receiver, IntPtr selector, long arg1);
}
