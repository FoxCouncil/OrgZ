// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Timestamps each phase of startup, so "it took forever to open" becomes a number
/// attached to a name.
///
/// A clean install of OrgZ is ~1100 files and several hundred MB, and before the window
/// appears it creates three SQLite databases, asks Velopack whether an update is staged,
/// and brings up Avalonia. None of that was logged, so a slow launch and a hung launch
/// were indistinguishable - to the user AND to us, which is how a startup regression would
/// reach a release unnoticed.
///
/// Cheap by construction: a stopwatch and one Information line per phase.
/// </summary>
public static class StartupTrace
{
    private static readonly Stopwatch _sw = Stopwatch.StartNew();
    private static readonly List<(string Phase, long Ms)> _marks = [];
    private static readonly Lock _gate = new();

    private static long _lastMs;
    private static bool _reported;

    /// <summary>
    /// Milliseconds since the OS actually started the process - which includes the loader
    /// and JIT work that happens before Main, and on a cold first launch that is often
    /// most of the wait.
    /// </summary>
    public static long SinceProcessStart
    {
        get
        {
            try
            {
                return (long)(DateTime.Now - Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            }
            catch
            {
                return _sw.ElapsedMilliseconds;
            }
        }
    }

    /// <summary>Records the end of a startup phase.</summary>
    public static void Mark(string phase)
    {
        long total, delta;
        lock (_gate)
        {
            total = _sw.ElapsedMilliseconds;
            delta = total - _lastMs;
            _lastMs = total;
            _marks.Add((phase, delta));
        }

        Log.Information("startup: {Phase} took {Delta} ms ({Total} ms into Main, {Wall} ms since process start)",
            phase, delta, total, SinceProcessStart);
    }

    /// <summary>
    /// One line naming the worst offenders, logged when the window is finally up. The
    /// per-phase lines answer "what happened"; this answers "what do I go fix".
    /// </summary>
    public static void ReportOnce(string finalPhase)
    {
        lock (_gate)
        {
            if (_reported)
            {
                return;
            }
            _reported = true;
        }

        Mark(finalPhase);
        Log.Information("startup summary: {Summary}", Summary());
    }

    /// <summary>Phases slowest-first. Pure, so the shape is testable without starting an app.</summary>
    internal static string Summary()
    {
        (string Phase, long Ms)[] snapshot;
        lock (_gate)
        {
            snapshot = [.. _marks];
        }

        if (snapshot.Length == 0)
        {
            return "(nothing recorded)";
        }

        return string.Join(", ", snapshot
            .OrderByDescending(m => m.Ms)
            .Take(5)
            .Select(m => $"{m.Phase} {m.Ms} ms"));
    }

    /// <summary>Formats a phase list slowest-first. Separated out so the ordering is pinned by tests.</summary>
    internal static string Summarize(IEnumerable<(string Phase, long Ms)> marks, int take = 5)
    {
        var ordered = marks.OrderByDescending(m => m.Ms).Take(take).ToList();
        return ordered.Count == 0
            ? "(nothing recorded)"
            : string.Join(", ", ordered.Select(m => $"{m.Phase} {m.Ms} ms"));
    }
}
