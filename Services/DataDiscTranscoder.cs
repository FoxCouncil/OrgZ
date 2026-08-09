// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Converts data-disc burn sources into the format chosen in Settings > Burning
/// (OrgZ.Burn.DataFormat / OrgZ.Burn.LossyQualityKbps). AAC, ALAC, FLAC and WAV come
/// straight out of the bundled ffmpeg; MP3 chains ffmpeg's decode into the bundled
/// lame - mirroring the CD-rip pipeline - because the ffmpeg build isn't guaranteed
/// to carry libmp3lame. "Keep original format" never reaches this class.
/// </summary>
public static class DataDiscTranscoder
{
    private static readonly ILogger _log = Logging.For("DataDiscTranscode");

    /// <summary>Output extension for a format tag, or null for "original" / unknown (no conversion).</summary>
    public static string? ExtensionFor(string format) => format switch
    {
        "mp3"  => ".mp3",
        "aac"  => ".m4a",
        "alac" => ".m4a",
        "flac" => ".flac",
        "wav"  => ".wav",
        _      => null,
    };

    /// <summary>
    /// True when the source already is the target codec, so it copies to disc untouched.
    /// Extension-driven except .m4a, which hides both AAC and ALAC in the same container -
    /// the actual codec comes from TagLib, the same discriminator the iPod importer uses.
    /// </summary>
    public static bool AlreadyTargetFormat(string sourcePath, string format)
    {
        var ext = Path.GetExtension(sourcePath);
        return format switch
        {
            "mp3"  => ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase),
            "flac" => ext.Equals(".flac", StringComparison.OrdinalIgnoreCase),
            "wav"  => ext.Equals(".wav", StringComparison.OrdinalIgnoreCase),
            "aac"  => ext.Equals(".aac", StringComparison.OrdinalIgnoreCase) || (ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && !IsAlac(sourcePath)),
            "alac" => ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase) && IsAlac(sourcePath),
            _      => true,
        };
    }

    private static bool IsAlac(string path)
    {
        try
        {
            using var f = TagLib.File.Create(path);
            return f.Properties.Codecs?.OfType<TagLib.IAudioCodec>().Any(c => c.Description?.Contains("alac", StringComparison.OrdinalIgnoreCase) == true) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The ffmpeg argument list for a single-process conversion (everything but MP3).
    /// Split out so the shape is testable without spawning processes.
    /// </summary>
    internal static List<string> BuildFfmpegArgs(string sourcePath, string outputPath, string format, int lossyKbps)
    {
        var args = new List<string>
        {
            "-hide_banner",
            "-nostdin",
            "-y",
            "-i", sourcePath,
            "-map", "0:a:0",        // first audio stream only; embedded art rides back in via CopyPicturesIfMissing
            "-map_metadata", "0",
        };

        switch (format)
        {
            case "aac":
            {
                args.AddRange(["-c:a", "aac", "-b:a", $"{Math.Clamp(lossyKbps, 32, 320)}k", "-movflags", "+faststart"]);
            }
            break;

            case "alac":
            {
                args.AddRange(["-c:a", "alac", "-movflags", "+faststart"]);
            }
            break;

            case "flac":
            {
                args.AddRange(["-c:a", "flac"]);
            }
            break;

            case "wav":
            {
                args.AddRange(["-c:a", "pcm_s16le"]);
            }
            break;

            default:
            {
                throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown data-disc conversion format");
            }
        }

        args.Add(outputPath);
        return args;
    }

    /// <summary>The lame argument list for the MP3 chain - raw 16-bit 44.1 kHz stereo PCM on stdin, CBR out.</summary>
    internal static List<string> BuildLameArgs(string outputPath, int lossyKbps) =>
    [
        "--silent",
        "-r", "-s", "44.1", "--bitwidth", "16", "--signed", "--little-endian", "-m", "s",
        "-b", Math.Clamp(lossyKbps, 32, 320).ToString(), "--cbr",
        "-", outputPath,
    ];

    /// <summary>
    /// Converts <paramref name="sourcePath"/> to <paramref name="format"/> at
    /// <paramref name="outputPath"/>. Metadata carries over (ffmpeg's -map_metadata, or a
    /// TagLib copy on the MP3 chain), and embedded cover art is re-attached via TagLib
    /// since the audio-only stream map drops picture streams.
    /// </summary>
    public static async Task TranscodeAsync(string sourcePath, string outputPath, string format, int lossyKbps, CancellationToken ct = default)
    {
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Burn source missing.", sourcePath);
        }

        var ffmpeg = ExecutableResolver.Find("ffmpeg") ?? throw new FileNotFoundException("ffmpeg was not found (bundled tools missing and not on PATH).", "ffmpeg");

        if (format == "mp3")
        {
            await FfmpegPipeToLameAsync(ffmpeg, sourcePath, outputPath, lossyKbps, ct);
            CopyTags(sourcePath, outputPath, fullTag: true);
            return;
        }

        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var a in BuildFfmpegArgs(sourcePath, outputPath, format, lossyKbps))
        {
            psi.ArgumentList.Add(a);
        }

        _log.Information("Data-disc transcode ({Format}): {Input} -> {Output}", format, sourcePath, outputPath);
        await RunAsync(psi, "ffmpeg", ct);
        CopyTags(sourcePath, outputPath, fullTag: false);
    }

    private static async Task FfmpegPipeToLameAsync(string ffmpeg, string sourcePath, string outputPath, int lossyKbps, CancellationToken ct)
    {
        var lame = ExecutableResolver.Find("lame") ?? throw new FileNotFoundException("lame was not found (bundled tools missing and not on PATH).", "lame");

        var dec = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-i", sourcePath, "-map", "0:a:0", "-ar", "44100", "-ac", "2", "-f", "s16le", "pipe:1" })
        {
            dec.ArgumentList.Add(a);
        }

        var enc = new ProcessStartInfo(lame)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in BuildLameArgs(outputPath, lossyKbps))
        {
            enc.ArgumentList.Add(a);
        }

        _log.Information("Data-disc transcode (mp3 {Kbps}k): {Input} -> {Output}", lossyKbps, sourcePath, outputPath);

        using var decProc = Process.Start(dec) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        using var encProc = Process.Start(enc) ?? throw new InvalidOperationException("Failed to start lame.");

        // Both stderr pipes drain concurrently so neither child can block on a full pipe.
        var decErr = decProc.StandardError.ReadToEndAsync(ct);
        var encErr = encProc.StandardError.ReadToEndAsync(ct);

        try
        {
            await decProc.StandardOutput.BaseStream.CopyToAsync(encProc.StandardInput.BaseStream, ct);
            encProc.StandardInput.Close();
            await decProc.WaitForExitAsync(ct);
            await encProc.WaitForExitAsync(ct);
        }
        catch
        {
            KillQuietly(decProc);
            KillQuietly(encProc);
            throw;
        }

        if (decProc.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited {decProc.ExitCode}: {Tail(await decErr)}");
        }

        if (encProc.ExitCode != 0)
        {
            throw new InvalidOperationException($"lame exited {encProc.ExitCode}: {Tail(await encErr)}");
        }
    }

    private static async Task RunAsync(ProcessStartInfo psi, string name, CancellationToken ct)
    {
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start {name}.");
        try
        {
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                throw new InvalidOperationException($"{name} exited {proc.ExitCode}: {Tail(stderr)}");
            }
        }
        catch (OperationCanceledException)
        {
            KillQuietly(proc);
            throw;
        }
    }

    private static void KillQuietly(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to kill transcode child");
        }
    }

    private static string Tail(string stderr) => stderr.Length > 800 ? stderr[^800..] : stderr;

    /// <summary>
    /// Best-effort tag carry-over. The MP3 chain loses all metadata (raw PCM through
    /// lame), so it takes the full tag; ffmpeg outputs already carry text metadata and
    /// only need the pictures re-attached (TagLib's CopyTo skips pictures by design).
    /// A tag failure never fails the burn - the audio is what matters.
    /// </summary>
    private static void CopyTags(string sourcePath, string outputPath, bool fullTag)
    {
        try
        {
            using var src = TagLib.File.Create(sourcePath);
            using var dst = TagLib.File.Create(outputPath);

            if (fullTag)
            {
                src.Tag.CopyTo(dst.Tag, overwrite: true);
            }

            if ((dst.Tag.Pictures?.Length ?? 0) == 0 && src.Tag.Pictures is { Length: > 0 } pictures)
            {
                dst.Tag.Pictures = pictures;
            }

            dst.Save();
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Tag carry-over failed for {Output}", outputPath);
        }
    }
}
