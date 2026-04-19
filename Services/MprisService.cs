// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics.CodeAnalysis;
using System.Text;
using Serilog;
using Tmds.DBus.Protocol;

namespace OrgZ.Services;

/// <summary>
/// Linux equivalent of SmtcService — exposes org.mpris.MediaPlayer2 over the D-Bus
/// session bus so shell widgets (GNOME, KDE, XFCE panel, playerctl, media-key daemons)
/// can control OrgZ playback and read current track metadata.
///
/// Implements two interfaces at path /org/mpris/MediaPlayer2:
///   - org.mpris.MediaPlayer2              (Raise, Quit + identity properties)
///   - org.mpris.MediaPlayer2.Player       (PlayPause/Next/Previous + Metadata, PlaybackStatus)
/// plus org.freedesktop.DBus.Properties (Get/GetAll/Set + PropertiesChanged signal).
///
/// Non-fatal: if the session bus isn't reachable the service logs a warning and goes
/// silent — the app keeps working without shell integration.
/// </summary>
public sealed class MprisService : IDisposable
{
    private const string BusName = "org.mpris.MediaPlayer2.orgz";
    private const string ObjectPath = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private static readonly ILogger _log = Logging.For<MprisService>();

    private Connection? _connection;
    private bool _initialized;

    // Exposed state — read/written from the UI thread, read by the D-Bus handler on
    // whatever thread Tmds.DBus dispatches from. Lock for publication only; the fields
    // themselves are simple references.
    private readonly object _stateLock = new();
    private string _playbackStatus = "Stopped";
    private string? _title;
    private string? _artist;
    private string? _album;
    private string? _artUrl;
    private long _trackId;

    public event Action? PlayPauseRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action? StopRequested;
    public event Action? RaiseRequested;

    /// <summary>
    /// Connects to the session bus, requests the MPRIS2 name, and registers the method
    /// handler. Safe to call more than once — subsequent calls are no-ops.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        try
        {
            _connection = new Connection(new ClientConnectionOptions(Address.Session!));
            await _connection.ConnectAsync();

            _connection.AddMethodHandler(new MprisHandler(this));

            await _connection.RequestNameAsync(BusName, RequestNameOptions.ReplaceExisting);
            _log.Information("MPRIS2 name requested: name={BusName}", BusName);

            _initialized = true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "MPRIS2 init failed — shell integration disabled for this session");
            _connection?.Dispose();
            _connection = null;
        }
    }

    /// <summary>
    /// Updates PlaybackStatus and emits PropertiesChanged. Valid values per the MPRIS
    /// spec: "Playing", "Paused", "Stopped".
    /// </summary>
    public void SetPlaybackStatus(string status)
    {
        lock (_stateLock)
        {
            if (_playbackStatus == status)
            {
                return;
            }
            _playbackStatus = status;
        }

        EmitPropertiesChanged(new Dictionary<string, VariantValue>
        {
            ["PlaybackStatus"] = VariantValue.String(status),
        });
    }

    /// <summary>
    /// Updates track metadata and emits PropertiesChanged with the new Metadata dict.
    /// Null fields are omitted from the payload; the MPRIS spec expects absent keys
    /// rather than empty strings for "no value".
    /// </summary>
    public void SetMetadata(string? title, string? artist, string? album, string? artUrl)
    {
        lock (_stateLock)
        {
            _title = title;
            _artist = artist;
            _album = album;
            _artUrl = artUrl;
            _trackId++;
        }

        EmitPropertiesChanged(new Dictionary<string, VariantValue>
        {
            ["Metadata"] = BuildMetadataVariant(),
        });
    }

    private VariantValue BuildMetadataVariant()
    {
        // MPRIS requires "mpris:trackid" to be a valid D-Bus object path. Use a synthetic
        // path rooted under our object path and tagged with a monotonic counter so listeners
        // see a distinct track when we swap metadata.
        var pairs = new List<KeyValuePair<string, VariantValue>>(6);

        long trackId;
        string? title, artist, album, artUrl;
        lock (_stateLock)
        {
            trackId = _trackId;
            title = _title;
            artist = _artist;
            album = _album;
            artUrl = _artUrl;
        }

        var dict = new Dict<string, VariantValue>();
        dict.Add("mpris:trackid", VariantValue.ObjectPath($"/org/mpris/MediaPlayer2/OrgZ/Track/{trackId}"));

        if (!string.IsNullOrWhiteSpace(title))
        {
            dict.Add("xesam:title", VariantValue.String(title));
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            // xesam:artist is "as" (array of strings) — wrap the single artist in an Array.
            var artistArr = new Tmds.DBus.Protocol.Array<string> { artist };
            dict.Add("xesam:artist", artistArr);
        }
        if (!string.IsNullOrWhiteSpace(album))
        {
            dict.Add("xesam:album", VariantValue.String(album));
        }
        if (!string.IsNullOrWhiteSpace(artUrl))
        {
            dict.Add("mpris:artUrl", VariantValue.String(artUrl));
        }

        return dict;
    }

    private void EmitPropertiesChanged(Dictionary<string, VariantValue> changed)
    {
        if (_connection is not { } conn)
        {
            return;
        }

        try
        {
            var writer = conn.GetMessageWriter();
            writer.WriteSignalHeader(
                destination: null!,
                path: ObjectPath,
                @interface: PropertiesInterface,
                member: "PropertiesChanged",
                signature: "sa{sv}as");

            writer.WriteString(PlayerInterface);
            writer.WriteDictionary(changed);
            writer.WriteArray(Array.Empty<string>());

            conn.TrySendMessage(writer.CreateMessage());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to emit MPRIS PropertiesChanged");
        }
    }

    public void Dispose()
    {
        try
        {
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error disposing MPRIS connection");
        }
        _connection = null;
        _initialized = false;
    }

    // ============================================================================
    // Method handler — one handler covers both the root and Player interfaces since
    // they share the object path. Incoming requests are dispatched by (interface,
    // member) and replied inline; long-running work (Raise/Quit) just no-ops today.
    // ============================================================================

    private sealed class MprisHandler : IPathMethodHandler
    {
        private readonly MprisService _svc;

        public MprisHandler(MprisService svc) => _svc = svc;

        public string Path => ObjectPath;

        public bool HandlesChildPaths => false;

        public bool RunMethodHandlerSynchronously(Message message) => true;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var msg = context.Request;
            var iface = msg.InterfaceAsString;
            var member = msg.MemberAsString;

            try
            {
                if (iface == PropertiesInterface)
                {
                    HandleProperties(context, member);
                    return ValueTask.CompletedTask;
                }

                if (iface == RootInterface)
                {
                    HandleRoot(context, member);
                    return ValueTask.CompletedTask;
                }

                if (iface == PlayerInterface)
                {
                    HandlePlayer(context, member);
                    return ValueTask.CompletedTask;
                }

                if (context.IsDBusIntrospectRequest)
                {
                    context.ReplyIntrospectXml([IntrospectionXmlUtf8]);
                    return ValueTask.CompletedTask;
                }

                context.ReplyUnknownMethodError();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "MPRIS handler error for {Interface}.{Member}", iface, member);
                try
                {
                    context.ReplyError("org.freedesktop.DBus.Error.Failed", ex.Message);
                }
                catch
                {
                    // If reply fails, we've already done our best — swallow
                }
            }

            return ValueTask.CompletedTask;
        }

        private void HandleRoot(MethodContext context, string? member)
        {
            switch (member)
            {
                case "Raise":
                    _svc.RaiseRequested?.Invoke();
                    ReplyEmpty(context);
                    return;
                case "Quit":
                    // CanQuit=false, but reply cleanly if a client calls anyway.
                    ReplyEmpty(context);
                    return;
                default:
                    context.ReplyUnknownMethodError();
                    return;
            }
        }

        private void HandlePlayer(MethodContext context, string? member)
        {
            switch (member)
            {
                case "PlayPause":
                case "Play":
                case "Pause":
                    _svc.PlayPauseRequested?.Invoke();
                    ReplyEmpty(context);
                    return;
                case "Next":
                    _svc.NextRequested?.Invoke();
                    ReplyEmpty(context);
                    return;
                case "Previous":
                    _svc.PreviousRequested?.Invoke();
                    ReplyEmpty(context);
                    return;
                case "Stop":
                    _svc.StopRequested?.Invoke();
                    ReplyEmpty(context);
                    return;
                case "Seek":
                case "SetPosition":
                case "OpenUri":
                    // Advertised as unsupported (CanSeek=false); silent ack.
                    ReplyEmpty(context);
                    return;
                default:
                    context.ReplyUnknownMethodError();
                    return;
            }
        }

        private void HandleProperties(MethodContext context, string? member)
        {
            switch (member)
            {
                case "Get":
                    HandlePropertiesGet(context);
                    return;
                case "GetAll":
                    HandlePropertiesGetAll(context);
                    return;
                case "Set":
                    // OrgZ exposes all MPRIS props read-only for now; accept the call silently.
                    ReplyEmpty(context);
                    return;
                default:
                    context.ReplyUnknownMethodError();
                    return;
            }
        }

        private void HandlePropertiesGet(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var interfaceName = reader.ReadString();
            var propName = reader.ReadString();

            var value = LookupProperty(interfaceName, propName);
            if (!value.HasValue)
            {
                context.ReplyError(
                    "org.freedesktop.DBus.Error.UnknownProperty",
                    $"{interfaceName}.{propName} not exposed");
                return;
            }

            var writer = context.CreateReplyWriter("v");
            writer.WriteVariant(value.Value);
            context.Reply(writer.CreateMessage());
        }

        private void HandlePropertiesGetAll(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            var interfaceName = reader.ReadString();

            var props = interfaceName switch
            {
                RootInterface => RootProperties(),
                PlayerInterface => _svc.PlayerProperties(),
                _ => null,
            };

            if (props == null)
            {
                context.ReplyError(
                    "org.freedesktop.DBus.Error.UnknownInterface",
                    $"{interfaceName} not exposed");
                return;
            }

            var writer = context.CreateReplyWriter("a{sv}");
            writer.WriteDictionary(props);
            context.Reply(writer.CreateMessage());
        }

        private VariantValue? LookupProperty(string? interfaceName, string? propName)
        {
            if (interfaceName == RootInterface)
            {
                return propName switch
                {
                    "CanQuit"              => VariantValue.Bool(false),
                    "CanRaise"             => VariantValue.Bool(true),
                    "HasTrackList"         => VariantValue.Bool(false),
                    "Identity"             => VariantValue.String("OrgZ"),
                    "SupportedUriSchemes"  => StringArrayVariant("file"),
                    "SupportedMimeTypes"   => StringArrayVariant(SupportedMimeTypes),
                    _ => null,
                };
            }

            if (interfaceName == PlayerInterface)
            {
                return propName switch
                {
                    "PlaybackStatus" => VariantValue.String(_svc.SnapshotPlaybackStatus()),
                    "LoopStatus"     => VariantValue.String("None"),
                    "Rate"           => VariantValue.Double(1.0),
                    "Shuffle"        => VariantValue.Bool(false),
                    "Metadata"       => _svc.BuildMetadataVariant(),
                    "Volume"         => VariantValue.Double(1.0),
                    "Position"       => VariantValue.Int64(0),
                    "MinimumRate"    => VariantValue.Double(1.0),
                    "MaximumRate"    => VariantValue.Double(1.0),
                    "CanGoNext"      => VariantValue.Bool(true),
                    "CanGoPrevious"  => VariantValue.Bool(true),
                    "CanPlay"        => VariantValue.Bool(true),
                    "CanPause"       => VariantValue.Bool(true),
                    "CanSeek"        => VariantValue.Bool(false),
                    "CanControl"     => VariantValue.Bool(true),
                    _ => null,
                };
            }

            return null;
        }

        private static Dictionary<string, VariantValue> RootProperties() => new()
        {
            ["CanQuit"]             = VariantValue.Bool(false),
            ["CanRaise"]            = VariantValue.Bool(true),
            ["HasTrackList"]        = VariantValue.Bool(false),
            ["Identity"]            = VariantValue.String("OrgZ"),
            ["SupportedUriSchemes"] = StringArrayVariant("file"),
            ["SupportedMimeTypes"]  = StringArrayVariant(SupportedMimeTypes),
        };

        private static VariantValue StringArrayVariant(params string[] values)
        {
            var arr = new Tmds.DBus.Protocol.Array<string>();
            foreach (var v in values)
            {
                arr.Add(v);
            }
            return arr;
        }

        private static void ReplyEmpty(MethodContext context)
        {
            var writer = context.CreateReplyWriter(string.Empty);
            context.Reply(writer.CreateMessage());
        }
    }

    private string SnapshotPlaybackStatus()
    {
        lock (_stateLock)
        {
            return _playbackStatus;
        }
    }

    private Dictionary<string, VariantValue> PlayerProperties() => new()
    {
        ["PlaybackStatus"] = VariantValue.String(SnapshotPlaybackStatus()),
        ["LoopStatus"]     = VariantValue.String("None"),
        ["Rate"]           = VariantValue.Double(1.0),
        ["Shuffle"]        = VariantValue.Bool(false),
        ["Metadata"]       = BuildMetadataVariant(),
        ["Volume"]         = VariantValue.Double(1.0),
        ["Position"]       = VariantValue.Int64(0),
        ["MinimumRate"]    = VariantValue.Double(1.0),
        ["MaximumRate"]    = VariantValue.Double(1.0),
        ["CanGoNext"]      = VariantValue.Bool(true),
        ["CanGoPrevious"]  = VariantValue.Bool(true),
        ["CanPlay"]        = VariantValue.Bool(true),
        ["CanPause"]       = VariantValue.Bool(true),
        ["CanSeek"]        = VariantValue.Bool(false),
        ["CanControl"]     = VariantValue.Bool(true),
    };

    private static readonly string[] SupportedMimeTypes =
    {
        "audio/mpeg",
        "audio/flac",
        "audio/ogg",
        "audio/mp4",
        "audio/wav",
        "audio/x-wav",
        "audio/x-m4a",
        "audio/aac",
        "audio/opus",
    };

    private static readonly byte[] IntrospectionXmlUtf8 = Encoding.UTF8.GetBytes("""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE node PUBLIC "-//freedesktop//DTD D-BUS Object Introspection 1.0//EN" "http://www.freedesktop.org/standards/dbus/1.0/introspect.dtd">
<node>
  <interface name="org.mpris.MediaPlayer2">
    <method name="Raise"/>
    <method name="Quit"/>
    <property name="CanQuit" type="b" access="read"/>
    <property name="CanRaise" type="b" access="read"/>
    <property name="HasTrackList" type="b" access="read"/>
    <property name="Identity" type="s" access="read"/>
    <property name="SupportedUriSchemes" type="as" access="read"/>
    <property name="SupportedMimeTypes" type="as" access="read"/>
  </interface>
  <interface name="org.mpris.MediaPlayer2.Player">
    <method name="Next"/>
    <method name="Previous"/>
    <method name="Pause"/>
    <method name="PlayPause"/>
    <method name="Stop"/>
    <method name="Play"/>
    <method name="Seek"><arg direction="in" type="x"/></method>
    <method name="SetPosition"><arg direction="in" type="o"/><arg direction="in" type="x"/></method>
    <method name="OpenUri"><arg direction="in" type="s"/></method>
    <signal name="Seeked"><arg type="x"/></signal>
    <property name="PlaybackStatus" type="s" access="read"/>
    <property name="LoopStatus" type="s" access="readwrite"/>
    <property name="Rate" type="d" access="readwrite"/>
    <property name="Shuffle" type="b" access="readwrite"/>
    <property name="Metadata" type="a{sv}" access="read"/>
    <property name="Volume" type="d" access="readwrite"/>
    <property name="Position" type="x" access="read"/>
    <property name="MinimumRate" type="d" access="read"/>
    <property name="MaximumRate" type="d" access="read"/>
    <property name="CanGoNext" type="b" access="read"/>
    <property name="CanGoPrevious" type="b" access="read"/>
    <property name="CanPlay" type="b" access="read"/>
    <property name="CanPause" type="b" access="read"/>
    <property name="CanSeek" type="b" access="read"/>
    <property name="CanControl" type="b" access="read"/>
  </interface>
</node>
""");
}
