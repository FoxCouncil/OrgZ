// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Re-measures every track in the library and rewrites its ReplayGain tag.
///
/// The per-track analysis that runs during playback only fires for a track that has NO gain yet,
/// which is right for the steady state and useless when the REFERENCE changes: every file already
/// carries a value, so nothing would ever be recomputed and a library would stay normalized to
/// whatever target it was first measured against. This is the pass that exists for that day.
///
/// Runs headless, from `OrgZ --rescan-gain`, so it can be left going against a large library
/// without holding the UI open.
/// </summary>
public static class ReplayGainRescan
{
    private static readonly ILogger _log = Logging.For("ReplayGainRescan");

    /// <summary>
    /// Measures and re-tags every music/audiobook file in the library.
    ///
    /// Parallel across files, because ebur128 decodes the whole track and is CPU-bound - serialized,
    /// a real library is measured in days rather than hours. One less than the core count leaves the
    /// machine usable; ffmpeg itself is told to stay single-threaded so the outer parallelism is the
    /// only one, rather than N processes each spawning N threads.
    /// </summary>
    public static async Task<int> RunAsync(CancellationToken ct = default)
    {
        var ffmpeg = ExecutableResolver.Find("ffmpeg");
        if (ffmpeg is null)
        {
            Console.Error.WriteLine("ffmpeg not found - cannot measure loudness.");
            return 1;
        }

        MediaCache.EnsureCreated();

        var tracks = MediaCache.LoadAll()
            .Where(i => i.Kind is MediaKind.Music or MediaKind.Audiobook)
            .Where(i => i.Source?.StartsWith("device:", StringComparison.Ordinal) != true)
            .Where(i => !string.IsNullOrEmpty(i.FilePath) && File.Exists(i.FilePath))
            .ToList();

        if (tracks.Count == 0)
        {
            Console.WriteLine("No local tracks in the library to measure.");
            return 0;
        }

        var workers = Math.Max(1, Environment.ProcessorCount - 1);
        Console.WriteLine($"Re-measuring {tracks.Count:N0} track(s) with {workers} worker(s). This rewrites each file's ReplayGain tag.");
        _log.Information("ReplayGain rescan starting: {Count} track(s), {Workers} worker(s)", tracks.Count, workers);

        var done = 0;
        var failed = 0;
        var started = DateTime.UtcNow;

        await Parallel.ForEachAsync(tracks, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = ct }, async (item, token) =>
        {
            try
            {
                var gain = await ReplayGainService.ComputeAndTagUngatedAsync(item.FilePath!, ffmpeg, token);
                if (gain is { } g)
                {
                    MediaCache.UpdateReplayGain(item.Id, g);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failed);
                _log.Debug(ex, "ReplayGain rescan failed for {Path}", item.FilePath);
            }

            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0 || n == tracks.Count)
            {
                var elapsed = DateTime.UtcNow - started;
                var rate = n / Math.Max(elapsed.TotalSeconds, 0.001);
                var remaining = rate > 0 ? TimeSpan.FromSeconds((tracks.Count - n) / rate) : TimeSpan.Zero;
                var line = $"  {n:N0}/{tracks.Count:N0}  ({n * 100.0 / tracks.Count:0.0}%)  {rate:0.0}/s  ~{remaining:hh\\:mm\\:ss} left  failed {failed:N0}";

                // A carriage return redraws one line in a terminal and writes NOTHING useful to a
                // redirected file - which is exactly where an hours-long job's output ends up. Pick
                // the form that suits where it is actually going.
                if (Console.IsOutputRedirected)
                {
                    Console.WriteLine(line);
                }
                else
                {
                    Console.Write($"\r{line}   ");
                }
            }
        });

        Console.WriteLine();
        var took = DateTime.UtcNow - started;
        Console.WriteLine($"Done in {took:hh\\:mm\\:ss}. Measured {tracks.Count - failed:N0}, failed {failed:N0}.");
        _log.Information("ReplayGain rescan finished in {Took} - measured {Ok}, failed {Failed}", took, tracks.Count - failed, failed);

        return 0;
    }
}
