// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services.AudioVisualization;

/// <summary>
/// Calling-thread QoS / priority hints for the audio decode + tap path.
/// On macOS, we ask the kernel to put the LibVLC decoder thread into
/// <c>QOS_CLASS_USER_INTERACTIVE</c> so background tasks (UI animations,
/// I/O completion, GC helper threads) don't preempt it under load.
/// </summary>
/// <remarks>
/// Hard real-time scheduling (Mach <c>THREAD_TIME_CONSTRAINT_POLICY</c>) is
/// gated behind joining coreaudiod's I/O workgroup and is typically only
/// granted to signed binaries with the right entitlements. QoS classes are
/// the next-best option and available to any process.
/// </remarks>
internal static class ThreadQos
{
    private const string LibSystem = "/usr/lib/libSystem.B.dylib";

    // From <pthread/qos.h>: enum qos_class_t values.
    private const uint QOS_CLASS_USER_INTERACTIVE = 0x21;

    [DllImport(LibSystem, EntryPoint = "pthread_set_qos_class_self_np")]
    private static extern int pthread_set_qos_class_self_np(uint qosClass, int relativePriority);

    public static void BumpToUserInteractive(ILogger log)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var rc = pthread_set_qos_class_self_np(QOS_CLASS_USER_INTERACTIVE, 0);
            if (rc == 0)
            {
                log.Information("Audio thread QoS bumped to USER_INTERACTIVE");
            }
            else
            {
                log.Warning("pthread_set_qos_class_self_np returned {Rc} — audio thread keeps default priority", rc);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Failed to bump audio thread QoS — keeping default priority");
        }
    }
}
