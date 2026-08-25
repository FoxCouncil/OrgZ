// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Computes a track's ReplayGain (the loudness offset iTunes called "Sound Check") once and writes
/// it into the file's tags, so playback can apply the precise value forever after. Loudness is
/// measured with the bundled ffmpeg's EBU R128 meter. Jobs are serialized (one ffmpeg at a time) so
/// tagging a library in the background can't storm the CPU.
/// </summary>
public static class ReplayGainService
{
    /// <summary>
    /// The loudness every track is brought to.
    ///
    /// NOT ReplayGain 2.0's -18 LUFS. That reference dates from an era of quieter masters, and
    /// against a modern library - which mostly sits between -6 and -10 LUFS - it only ever
    /// attenuates, by 8 to 12 dB. The result is a library that is uniformly QUIET rather than
    /// level, which is the opposite of what someone turning normalization on wants.
    ///
    /// -14 is where Spotify, YouTube and Tidal settled (Apple Music sits at -16), so a library
    /// normalized here sounds like the rest of what a listener hears. Going louder buys little:
    /// every dB above this pushes more tracks into a boost the peak ceiling below has to refuse,
    /// and a track that cannot reach the target is unevenness reintroduced from the other side.
    ///
    /// Apple's own Sound Check reference is not public and is not LUFS-based; the value written
    /// to a device is converted to its units in ITunesDbWriter regardless of what is chosen here.
    /// </summary>
    private const double ReferenceLufs = -14.0;

    /// <summary>
    /// Where a boosted track's true peak is allowed to land. Gain is capped so nothing crosses
    /// it, because the playback scaler CLAMPS rather than wraps - so an over-boost would not
    /// overflow, it would hard-clip, and the loudest part of the track is exactly where that is
    /// most audible. 1 dB of headroom also covers the inter-sample peaks a decoder can produce
    /// that a sample-peak meter never sees.
    /// </summary>
    private const double PeakCeilingDbfs = -1.0;

    /// <summary>Ceiling on boost, so a near-silent recording is lifted, not amplified into its own noise floor.</summary>
    private const double MaxBoostDb = 6.0;

    private static readonly ILogger _log = Logging.For("ReplayGain");
    private static readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// Measures <paramref name="filePath"/> and writes its REPLAYGAIN_TRACK_GAIN tag. Returns the
    /// gain in dB, or null on any failure (unreadable file, ffmpeg missing, tag write refused).
    /// Best-effort by design - a file that can't be analyzed simply keeps using real-time normvol.
    /// </summary>
    public static async Task<double?> ComputeAndTagAsync(string filePath, string ffmpegPath, CancellationToken ct = default)
    {
        // Serialized: this fires from playback, where one ffmpeg competing with the decoder is
        // acceptable and several are not. The bulk rescan wants its own parallelism and so calls
        // the ungated form directly.
        await _gate.WaitAsync(ct);
        try
        {
            return await MeasureAndTagAsync(filePath, ffmpegPath, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The same work without the one-at-a-time gate, for <see cref="ReplayGainRescan"/>, which
    /// runs its own bounded parallelism across files.
    /// </summary>
    internal static Task<double?> ComputeAndTagUngatedAsync(string filePath, string ffmpegPath, CancellationToken ct = default)
        => MeasureAndTagAsync(filePath, ffmpegPath, ct);

    private static async Task<double?> MeasureAndTagAsync(string filePath, string ffmpegPath, CancellationToken ct)
    {
        {
            if (!File.Exists(filePath))
            {
                return null;
            }

            var measured = await MeasureLoudnessAsync(filePath, ffmpegPath, ct);
            if (measured is not { } m)
            {
                return null;
            }

            var gainDb = GainFromLoudness(m.Lufs, m.TruePeakDbfs);
            try
            {
                // Through a copy, never in place: TagLib rewrites the whole audio payload when the
                // new frame outgrows the existing padding, and a 90 MB FLAC on a USB drive that
                // loses power mid-shift is unrecoverable. The temp is named .orgztmp so the folder
                // watcher ignores it, which is why the parser comes from the MIME type here.
                var mimeType = "taglib/" + Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
                AtomicFile.MutateCopy(filePath, temp =>
                {
                    using var file = TagLib.File.Create(temp, mimeType, TagLib.ReadStyle.Average);
                    file.Tag.ReplayGainTrackGain = gainDb;
                    file.Save();
                });
                _log.Information("ReplayGain {Gain:0.00} dB (from {Lufs:0.0} LUFS, peak {Peak}) -> {Path}", gainDb, m.Lufs, m.TruePeakDbfs is { } tp ? $"{tp:0.0} dBFS" : "unknown", filePath);
            }
            catch (Exception ex)
            {
                // The file is usually locked by the very PLAYER we're analyzing for. Losing the tag
                // write must never lose the MEASUREMENT - that bug silently disabled Sound Check for
                // half of all plays (gain computed, then discarded with the failed tag). The caller
                // applies the gain live and caches it in the library DB, so normalization works this
                // play and every one after; the file tag lands whenever the file is next writable.
                _log.Debug(ex, "ReplayGain tag deferred (file busy) on {Path} - gain {Gain:0.00} dB still applies", filePath, gainDb);
            }

            return gainDb;
        }
    }

    // ── pure pieces (unit-tested) ──────────────────────────────────────────────

    /// <summary>
    /// The gain that brings a track's integrated loudness to <see cref="ReferenceLufs"/>, held
    /// back so the result neither clips nor over-amplifies.
    ///
    /// Attenuation is unbounded - a track 12 dB too loud is turned down 12 dB, that is the whole
    /// job. Boost is bounded twice: by the track's own true peak, so raising a quiet-but-hot
    /// master cannot drive it into the clamp in the playback scaler, and by
    /// <see cref="MaxBoostDb"/> so a near-silent recording is not amplified into its own hiss.
    /// Pass <paramref name="truePeakDbfs"/> as null when the meter did not report one; the peak
    /// limit is then simply not applied.
    /// </summary>
    internal static double GainFromLoudness(double integratedLufs, double? truePeakDbfs = null)
    {
        var gain = ReferenceLufs - integratedLufs;

        if (truePeakDbfs is { } peak)
        {
            gain = Math.Min(gain, PeakCeilingDbfs - peak);
        }

        return Math.Min(gain, MaxBoostDb);
    }

    /// <summary>
    /// Pulls the true-peak figure out of ffmpeg's ebur128 summary ("Peak: -0.3 dBFS"), which it
    /// only prints when the filter is asked for it. Returns null when absent, which is the
    /// signal to skip peak limiting rather than to assume a peak.
    /// </summary>
    internal static double? ParseTruePeak(string ffmpegStderr)
    {
        double? last = null;
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(ffmpegStderr, @"\bPeak:\s*(-?\d+(?:\.\d+)?)\s*dBFS"))
        {
            if (double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                last = v;
            }
        }

        return last;
    }

    /// <summary>
    /// Pulls the integrated-loudness value ("I: -14.2 LUFS") out of ffmpeg's ebur128 summary, which
    /// prints to stderr. Returns null when the summary carries no valid I line (silent track / ffmpeg
    /// error). The last "I:" line is the final integrated figure ebur128 emits.
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

    private static async Task<(double Lufs, double? TruePeakDbfs)?> MeasureLoudnessAsync(string filePath, string ffmpegPath, CancellationToken ct)
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
            psi.ArgumentList.Add("-threads"); psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(filePath);
            psi.ArgumentList.Add("-af"); psi.ArgumentList.Add("ebur128=peak=true");
            psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("null");
            psi.ArgumentList.Add("-");

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            return ParseIntegratedLoudness(stderr) is { } lufs ? (lufs, ParseTruePeak(stderr)) : null;
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
