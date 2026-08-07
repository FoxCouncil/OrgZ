// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Threading;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Keeps an open Avalonia DataGrid bug from terminating the process.
///
/// AvaloniaUI/Avalonia#16233: after the machine suspends and resumes with an app running, the
/// DataGrid's display-data bookkeeping goes stale, and the next expand of a collapsed row group
/// throws ArgumentOutOfRangeException from inside DataGridDisplayData.UnloadScrollingElement.
/// Open since 11.0.5, unfixed in 12.0.
///
/// The throw originates in Avalonia's own pointer-pressed handler, so no OrgZ try/catch is on
/// the stack to intercept it - it unwinds straight into the Win32 message pump and kills the
/// app. <see cref="Dispatcher.UnhandledException"/> is the only place we can see it.
///
/// Deliberately narrow. Only this one signature is survivable; every other exception keeps the
/// existing fatal path, because a guard broad enough to swallow real bugs is worse than the
/// crash it prevents.
///
/// Reproduced on 2026-08-06: a 6.2 hour suspend, then opening a genre in the Radio view. Not
/// reproducible headlessly - the corrupt state comes from the suspend, not from anything the
/// grid is asked to do - so the tests pin the predicate, not the recovery.
/// </summary>
internal static class DataGridRowGroupGuard
{
    private static readonly ILogger _log = Logging.For("DataGridGuard");

    private static bool _installed;

    /// <summary>Marks the known row-group toggle failure handled instead of fatal.</summary>
    internal static void Install()
    {
        if (_installed)
        {
            return;
        }
        _installed = true;

        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            if (!IsKnownRowGroupToggleBug(e.Exception))
            {
                return;
            }

            e.Handled = true;
            _log.Warning(e.Exception,
                "Avalonia#16233: row group toggle failed after a suspend/resume. The group stays as it was; " +
                "switching views rebuilds the grid and clears it.");
        };
    }

    /// <summary>
    /// Whether <paramref name="ex"/> is the Avalonia#16233 row-group toggle failure. Matches on
    /// the frames unique to it: the fault has to come from the DataGrid's own group-visibility
    /// path, not merely from anything that happens to be an index error.
    /// </summary>
    internal static bool IsKnownRowGroupToggleBug(Exception? ex)
    {
        if (ex is not ArgumentOutOfRangeException)
        {
            return false;
        }

        var stack = ex.StackTrace;
        if (string.IsNullOrEmpty(stack) || !stack.Contains("Avalonia.Controls.DataGrid", StringComparison.Ordinal))
        {
            return false;
        }

        return stack.Contains("UpdateRowGroupVisibility", StringComparison.Ordinal)
            || stack.Contains("OnRowGroupHeaderToggled", StringComparison.Ordinal)
            || stack.Contains("RemoveNonDisplayedRows", StringComparison.Ordinal);
    }
}
