// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;
using Tmds.DBus.Protocol;

namespace OrgZ.Services;

/// <summary>
/// Linux single-instance guard using D-Bus name ownership. The first instance claims
/// <c>com.foxcouncil.orgz.Singleton</c> on the session bus and keeps the connection
/// alive for the process lifetime. A second instance that fails to claim the name
/// sends <c>org.mpris.MediaPlayer2.Raise</c> to the existing instance so it foregrounds
/// its window, then exits cleanly.
///
/// No file locks, no kill-and-takeover, no stale lock files — whoever owns the bus
/// name is the primary instance, and the kernel releases it automatically on crash.
/// </summary>
public static class SingleInstanceGuard
{
    private const string SingletonBusName = "com.foxcouncil.orgz.Singleton";
    private const string MprisBusName = "org.mpris.MediaPlayer2.orgz";
    private const string MprisPath = "/org/mpris/MediaPlayer2";
    private const string MprisRootInterface = "org.mpris.MediaPlayer2";

    private static readonly ILogger _log = Logging.For("SingleInstance");
    private static readonly object _connectionLock = new();

    private static Connection? _connection;

    /// <summary>
    /// Attempts to claim the singleton name. Returns <c>true</c> when this process is
    /// the primary instance (call returns successfully holding the name); returns
    /// <c>false</c> when another instance is already running — in which case that
    /// existing instance has been asked to raise its window and this process should
    /// exit. Non-Linux platforms always return <c>true</c> (no singleton guard applies;
    /// Velopack + SMTC handle that on Windows). Bus unavailability also returns <c>true</c>
    /// so a headless session still launches — you just lose the guarantee.
    /// </summary>
    public static bool TryAcquirePrimary()
    {
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
    // We don't wait for a reply — the target may or may not have its MPRIS up yet; either
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
    /// Releases the singleton name and closes the session-bus connection. Called from
    /// Program.Main's finally block on clean shutdown.
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
    }
}
