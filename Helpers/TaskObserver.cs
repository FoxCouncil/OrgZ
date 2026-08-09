// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Helpers;

/// <summary>
/// Starts a task nobody awaits and OBSERVES its failure. A bare <c>_ = SomethingAsync()</c>
/// discards the fault entirely: at best it's an unobserved-task exception nobody sees, at
/// worst it's half-finished sidebar or device state with no trace of why. Promoted out of
/// MainWindowViewModel so every fire-and-forget in the app can use it.
/// </summary>
public static class TaskObserver
{
    private static readonly Serilog.ILogger _log = Services.Logging.For("FireAndForget");

    public static void FireAndForget(Task task, string what)
    {
        _ = task.ContinueWith(
            t => _log.Error(t.Exception, "{What} failed", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);
    }
}
