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

    private static Connection? _connection;

    /// <summary>
    /// Attempts to become the primary instance. Returns <c>true</c> if another instance
    /// is already running (in which case it was asked to raise itself and this process
    /// should exit). Returns <c>false</c> if this process is the primary and should
    /// continue its normal startup.
    ///
    /// Linux only — other platforms return false without side effects. If the session
    /// bus isn't available (headless, broken) we also return false so the app still
    /// launches; you just lose the singleton guarantee.
    /// </summary>
    public static bool TryBecomePrimary()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        try
        {
            return TryBecomePrimaryLinuxAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Singleton check failed — allowing startup without guard");
            return false;
        }
    }

    private static async Task<bool> TryBecomePrimaryLinuxAsync()
    {
        var conn = new Connection(new ClientConnectionOptions(Address.Session!));
        await conn.ConnectAsync();

        // Request the singleton name WITHOUT ReplaceExisting. If someone else already
        // owns it, we throw / get rejected and fall through to the raise-and-exit path.
        try
        {
            await conn.RequestNameAsync(SingletonBusName, RequestNameOptions.None);
            _connection = conn;
            _log.Information("Singleton bus name claimed: {BusName}", SingletonBusName);
            return false;
        }
        catch (Exception ex)
        {
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
            _log.Debug(ex, "Detail on RequestName rejection");
            return true;
        }
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
        try
        {
            _connection?.Dispose();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Error disposing singleton connection");
        }
        _connection = null;
    }
}
