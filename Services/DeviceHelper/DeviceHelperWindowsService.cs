// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

#if WINDOWS
using System.ServiceProcess;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// Hosts <see cref="DeviceHelperDaemon"/> under the Windows Service Control Manager. A process
/// launched by the SCM MUST call the control dispatcher and report SERVICE_RUNNING within the
/// start timeout, or the SCM tears it down with error 1053 ("did not respond in a timely
/// fashion"). <see cref="ServiceBase.Run"/> performs that handshake; OnStart just kicks the
/// listener onto a background task and returns immediately, OnStop cancels and drains it.
/// </summary>
internal sealed class DeviceHelperWindowsService : ServiceBase
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public DeviceHelperWindowsService()
    {
        ServiceName = "OrgZDeviceHelper";
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        // Must return promptly - the SCM reports RUNNING once OnStart completes, so we only
        // start the loop here and let it run for the service's lifetime.
        _loop = DeviceHelperDaemon.RunAsync(_cts.Token);
    }

    protected override void OnStop() => Drain();

    protected override void OnShutdown() => Drain();

    private void Drain()
    {
        _cts.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // A cancelled/aborted listener throwing on the way down is expected; nothing to do.
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _cts.Dispose();
        }
        base.Dispose(disposing);
    }
}
#endif
