// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Computes a track's ReplayGain (the loudness offset iTunes called "Sound Check") once and writes
/// it into the file's tags, so playback can apply the precise value forever after. Loudness is
/// measured with the bundled ffmpeg's EBU R128 meter; gain targets the ReplayGain 2.0 reference of
/// -18 LUFS. Jobs are serialized (one ffmpeg at a time) so tagging a library in the background can't
/// storm the CPU.
/// </summary>
public static class ReplayGainService
{
    private const double ReferenceLufs = -18.0;

    private static readonly ILogger _log = Logging.For("ReplayGain");
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Measures <paramref name="filePath"/> and writes its REPLAYGAIN_TRACK_GAIN tag. Returns the
    /// gain in dB, or null on any failure (unreadable file, ffmpeg missing, tag write refused).
    /// Best-effort by design - a file that can't be analyzed simply keeps using real-time normvol.
    /// </summary>
    public static async Task<double?> ComputeAndTagAsync(string filePath, string ffmpegPath, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var loudness = await MeasureIntegratedLoudnessAsync(filePath, ffmpegPath, ct);
            if (loudness is not { } lufs)
            {
                return null;
            }

            var gainDb = GainFromLoudness(lufs);
            try
            {
                using var file = TagLib.File.Create(filePath);
                file.Tag.ReplayGainTrackGain = gainDb;
                file.Save();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not write the ReplayGain tag on {Path}", filePath);
                return null;
            }

            _log.Information("ReplayGain {Gain:0.00} dB (from {Lufs:0.0} LUFS) -> {Path}", gainDb, lufs, filePath);
            return gainDb;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── pure pieces (unit-tested) ──────────────────────────────────────────────

    /// <summary>ReplayGain 2.0: the gain that brings a track's integrated loudness to -18 LUFS.</summary>
    internal static double GainFromLoudness(double integratedLufs) => ReferenceLufs - integratedLufs;

    /// <summary>
    /// Pulls the integrated-loudness value ("I: -14.2 LUFS") out of ffmpeg's ebur128 summary, which
    /// prints to stderr. Returns null when the summary carries no valid I line (silent track / ffmpeg
    /// error). The LAST "I:" line is the final integrated figure ebur128 emits.
    /// </summary>
    internal static double? ParseIntegratedLoudness(string ffmpegStderr)
    {
        double? last = null;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(ffmpegStderr, @"\bI:\s*(-?\d+(?:\.\d+)?)\s*LUFS"))
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                last = v;
            }
        }
        // -70 LUFS is ebur128's floor for silence - not a real measurement to normalize against.
        return last is { } lufs && lufs > -70.0 ? last : null;
    }

    // ── plumbing ──────────────────────────────────────────────────────────────

    private static async Task<double?> MeasureIntegratedLoudnessAsync(string filePath, string ffmpegPath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-nostats");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-af"); psi.ArgumentList.Add("ebur128");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return ParseIntegratedLoudness(await stderrTask);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "ffmpeg loudness measurement failed for {Path}", filePath);
            return null;
        }
    }
}
