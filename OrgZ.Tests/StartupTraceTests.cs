// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Startup timing. The point of this instrumentation is diagnosis: a clean install is
/// ~1100 files and several hundred MB, and before the window appears OrgZ creates three
/// SQLite databases, asks Velopack about staged updates, and brings up Avalonia - none of
/// which was logged, so a slow launch and a hang were indistinguishable.
///
/// The summary is what someone reads first, so what it puts in front of them is the part
/// worth pinning: the slowest phases, named, in order.
/// </summary>
public class StartupTraceTests
{
    [Fact]
    public void The_summary_leads_with_the_slowest_phase()
    {
        var marks = new[] { ("logging", 5L), ("library db", 900L), ("velopack", 120L) };

        Assert.Equal("library db 900 ms, velopack 120 ms, logging 5 ms", StartupTrace.Summarize(marks));
    }

    [Fact]
    public void Only_the_worst_offenders_are_listed()
    {
        // A dozen phases at two decimal places is a wall of text nobody reads; the top few
        // are what you go and fix.
        var marks = Enumerable.Range(1, 12).Select(i => ($"phase{i}", (long)(i * 10))).ToArray();

        var summary = StartupTrace.Summarize(marks, take: 3);

        Assert.Equal("phase12 120 ms, phase11 110 ms, phase10 100 ms", summary);
        Assert.DoesNotContain("phase9", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void An_empty_run_says_so_rather_than_printing_nothing()
    {
        // An empty string in the log reads as a bug in the logging, not as "no data".
        Assert.Equal("(nothing recorded)", StartupTrace.Summarize([]));
    }

    [Fact]
    public void Ties_do_not_drop_phases()
    {
        var marks = new[] { ("a", 50L), ("b", 50L), ("c", 50L) };

        var summary = StartupTrace.Summarize(marks);

        foreach (var name in new[] { "a", "b", "c" })
        {
            Assert.Contains($"{name} 50 ms", summary, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_zero_millisecond_phase_is_still_reported()
    {
        // Fast phases matter to the reader: their absence would look like a missing step.
        Assert.Contains("instant 0 ms", StartupTrace.Summarize([("instant", 0L)]), StringComparison.Ordinal);
    }

    [Fact]
    public void Marking_works_before_anything_configures_a_logger()
    {
        // Mark runs during startup, potentially before Serilog is fully configured, and
        // must never be the thing that breaks the launch it is measuring.
        var ex = Record.Exception(() => StartupTrace.Mark("test phase"));

        Assert.Null(ex);
        Assert.Contains("test phase", StartupTrace.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void Elapsed_since_process_start_is_sane()
    {
        // Wall-clock since exec, which includes the loader and JIT time before Main - on a
        // cold first launch that is often most of the wait, so it must not read as zero.
        Assert.InRange(StartupTrace.SinceProcessStart, 0, TimeSpan.FromHours(24).TotalMilliseconds);
    }
}
