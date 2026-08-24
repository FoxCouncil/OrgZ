// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.Versioning;
using Serilog;
using Tmds.DBus.Protocol;

namespace OrgZ.Services;

/// <summary>
/// Single-instance guard. On Linux it uses D-Bus name ownership: the first instance claims
/// <c>com.foxcouncil.orgz.Singleton</c> on the session bus and keeps the connection
/// alive for the process lifetime. A second instance that fails to claim the name
/// sends <c>org.mpris.MediaPlayer2.Raise</c> to the existing instance so it foregrounds
/// its window, then exits cleanly.
///
/// No file locks, no kill-and-takeover, no stale lock files - whoever owns the bus
/// name is the primary instance, and the kernel releases it automatically on crash.
///
/// On Windows the same shape is built from a named mutex plus a named event as the
/// doorbell (see <see cref="TryAcquirePrimaryWindows"/>). macOS needs neither: LaunchServices
/// already re-activates the running app instead of starting a second copy.
/// </summary>
public static class SingleInstanceGuard
{
    private const string SingletonBusName = "com.foxcouncil.orgz.Singleton";
    private const string MprisBusName = "org.mpris.MediaPlayer2.orgz";
    private const string MprisPath = "/org/mpris/MediaPlayer2";
    private const string MprisRootInterface = "org.mpris.MediaPlayer2";

    private const string DBusBusName = "org.freedesktop.DBus";
    private const string DBusPath = "/org/freedesktop/DBus";
    private const string DBusInterface = "org.freedesktop.DBus";
    private const string StatusNotifierWatcherBusName = "org.kde.StatusNotifierWatcher";

    // Local\, not Global\: the guard is per-session, so a second Windows user still gets
    // their own OrgZ (they have their own settings.json and library.db), and creating the
    // objects needs no privilege.
    private const string WindowsMutexName = @"Local\com.foxcouncil.orgz.Singleton";
    private const string WindowsRestoreEventName = @"Local\com.foxcouncil.orgz.Restore";

    private static readonly ILogger _log = Logging.For("SingleInstance");
    private static readonly object _connectionLock = new();

    private static Connection? _connection;
    private static Mutex? _windowsSingleton;
    private static EventWaitHandle? _windowsRestoreEvent;
    private static bool? _trayHostAvailable;

    /// <summary>
    /// Raised on a background thread when a second launch asks this instance to come
    /// forward. Windows only - on Linux the second process sends MPRIS <c>Raise</c> instead,
    /// which the now-playing integration already handles.
    /// </summary>
    public static event Action? RestoreRequested;

    /// <summary>
    /// Attempts to claim the singleton name. Returns <c>true</c> when this process is
    /// the primary instance (call returns successfully holding the name); returns
    /// <c>false</c> when another instance is already running - in which case that
    /// existing instance has been asked to raise its window and this process should
    /// exit. macOS always returns <c>true</c>: LaunchServices re-activates the running
    /// app rather than starting a second one. A guard that cannot be built (no session
    /// bus, mutex creation refused) also returns <c>true</c> so the app still launches -
    /// you just lose the guarantee.
    /// </summary>
    public static bool TryAcquirePrimary()
    {
        if (OperatingSystem.IsWindows())
        {
            return TryAcquirePrimaryWindows();
        }

        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        try
        {
            return TryAcquirePrimaryLinuxAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Singleton check failed — allowing startup without guard");
            return true;
        }
    }

    /// <summary>
    /// Windows guard: a named mutex is the singleton, a named auto-reset event is the doorbell.
    /// Two OrgZ processes share one settings.json and one library.db, and Settings.Save rewrites
    /// the whole in-memory map - so whichever process saves last silently discards every
    /// preference the other changed. Minimize-to-tray is what makes that easy to hit: with the
    /// window hidden there is no taskbar button, so the Start-menu shortcut is the natural
    /// gesture and it used to start a second copy.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryAcquirePrimaryWindows()
    {
        try
        {
            // initiallyOwned: false - the kernel object lives as long as any handle is open, so
            // ownership buys nothing here and would tie the release to this exact thread.
            var mutex = new Mutex(initiallyOwned: false, WindowsMutexName, out var createdNew);

            if (!createdNew)
            {
                mutex.Dispose();
                _log.Information("Another OrgZ already holds {Mutex} — asking it to come forward and exiting", WindowsMutexName);
                SignalWindowsRestore();
                return false;
            }

            _windowsSingleton = mutex;
            _windowsRestoreEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, WindowsRestoreEventName);
            StartWindowsRestoreListener(_windowsRestoreEvent);
            _log.Information("Windows singleton claimed: {Mutex}", WindowsMutexName);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Windows singleton check failed — allowing startup without guard");
            return true;
        }
    }

    /// <summary>Rings the primary instance's doorbell. Silent when nobody is listening.</summary>
    [SupportedOSPlatform("windows")]
    private static void SignalWindowsRestore()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(WindowsRestoreEventName, out var doorbell))
            {
                using (doorbell)
                {
                    doorbell.Set();
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not ask the running instance to restore");
        }
    }

    // One background thread parked on the doorbell for the process lifetime. Disposing the
    // handle in Release() is what ends it - the wait throws and the loop exits.
    [SupportedOSPlatform("windows")]
    private static void StartWindowsRestoreListener(EventWaitHandle doorbell)
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    doorbell.WaitOne();
                    RestoreRequested?.Invoke();
                }
                catch (Exception ex)
                {
                    _log.Debug(ex, "Singleton restore listener stopped");
                    return;
                }
            }
        })
        {
            IsBackground = true,
            Name = "OrgZ singleton restore",
        };

        thread.Start();
    }

    /// <summary>
    /// Whether the desktop can actually host a tray icon. Avalonia's Linux tray icon is a D-Bus
    /// StatusNotifierItem: with nothing owning <c>org.kde.StatusNotifierWatcher</c> on the session
    /// bus (vanilla GNOME, plain window managers) setting IsVisible succeeds and no icon ever
    /// appears - so a window hidden "to the tray" has no affordance left to bring it back.
    /// Windows and macOS always have somewhere to put it. Probed once and cached; anything that
    /// goes wrong answers false, which callers read as "keep the window reachable".
    /// </summary>
    public static bool TrayHostAvailable()
    {
        if (!OperatingSystem.IsLinux())
        {
            return true;
        }

        lock (_connectionLock)
        {
            if (_trayHostAvailable is bool cached)
            {
                return cached;
            }
        }

        var available = false;
        try
        {
            var probe = Task.Run(ProbeTrayHostLinuxAsync);
            available = probe.Wait(TimeSpan.FromSeconds(2)) && probe.Result;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "StatusNotifierWatcher probe failed — assuming no tray host");
        }

        lock (_connectionLock)
        {
            _trayHostAvailable = available;
        }

        _log.Debug("Tray host available: {Available}", available);
        return available;
    }

    private static async Task<bool> ProbeTrayHostLinuxAsync()
    {
        Connection? conn;
        lock (_connectionLock)
        {
            conn = _connection;
        }

        if (conn is null)
        {
            // No session bus at all (headless, or the guard fell back at startup) - and without
            // one there is no StatusNotifierWatcher either.
            return false;
        }

        return await conn.CallMethodAsync<bool>(BuildNameHasOwnerCall(conn, StatusNotifierWatcherBusName), static (Message message, object? _) =>
        {
            var reader = message.GetBodyReader();
            return reader.ReadBool();
        });
    }

    // MessageWriter is a ref struct, so it can't be a local of the async method that awaits
    // the reply - build the call here and hand back the buffer.
    private static MessageBuffer BuildNameHasOwnerCall(Connection conn, string busName)
    {
        var writer = conn.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: DBusBusName,
            path: DBusPath,
            @interface: DBusInterface,
            member: "NameHasOwner",
            signature: "s",
            flags: MessageFlags.None);
        writer.WriteString(busName);

        return writer.CreateMessage();
    }

    private static async Task<bool> TryAcquirePrimaryLinuxAsync()
    {
        var conn = new Connection(new ClientConnectionOptions(Address.Session!));
        await conn.ConnectAsync();

        // TryRequestNameAsync returns Task<bool>: true if we became the primary owner,
        // false otherwise (name already held, in queue, etc).
        var becamePrimary = await conn.TryRequestNameAsync(SingletonBusName, RequestNameOptions.None);

        if (becamePrimary)
        {
            lock (_connectionLock)
            {
                _connection = conn;
            }
            _log.Information("Singleton bus name claimed: {BusName}", SingletonBusName);
            return true;
        }

        _log.Information("Singleton bus name already held — focusing running instance and exiting");
        try
        {
            SendRaise(conn);
        }
        catch (Exception rex)
        {
            _log.Warning(rex, "Failed to send Raise to running instance (they may still be starting up)");
        }
        conn.Dispose();
        return false;
    }

    // Send a fire-and-forget method call to the existing MPRIS service asking it to raise.
    // We don't wait for a reply - the target may or may not have its MPRIS up yet; either
    // way we're about to exit, so there's no point blocking.
    private static void SendRaise(Connection conn)
    {
        var writer = conn.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: MprisBusName,
            path: MprisPath,
            @interface: MprisRootInterface,
            member: "Raise",
            signature: string.Empty,
            flags: MessageFlags.NoReplyExpected);

        conn.TrySendMessage(writer.CreateMessage());
    }

    /// <summary>
    /// Releases the singleton (the D-Bus name and its connection on Linux, the mutex and
    /// doorbell on Windows). Called from Program.Main's finally block on clean shutdown.
    /// </summary>
    public static void Release()
    {
        Connection? conn;
        lock (_connectionLock)
        {
            conn = _connection;
            _connection = null;
        }

        try
        {
            conn?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error disposing singleton connection");
        }

        try
        {
            // Order matters: closing the doorbell first ends the listener thread's wait.
            _windowsRestoreEvent?.Dispose();
            _windowsSingleton?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error releasing the Windows singleton");
        }

        _windowsRestoreEvent = null;
        _windowsSingleton = null;
    }
}
