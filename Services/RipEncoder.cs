// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

public sealed record RipTrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public string? Genre { get; init; }
    public int? TrackNumber { get; init; }
    public uint? Year { get; init; }

    /// <summary>
    /// Encoded-by string (FLAC ENCODER tag / MP3 TENC frame). Defaulted to
    /// "OrgZ {version}" by <see cref="CdRipService"/> when the caller leaves
    /// it null, so every ripped file is self-identifying.
    /// </summary>
    public string? EncodedBy { get; init; }

    /// <summary>
    /// Front-cover image bytes (JPEG or PNG). Embedded into the output file
    /// as a FLAC METADATA_BLOCK_PICTURE (FLAC output) or APIC frame (MP3).
    /// For WAV output, the same bytes are dropped into the rip directory as
    /// <c>cover.jpg</c> instead - WAV has no standard art tag.
    /// </summary>
    public byte[]? CoverArt { get; init; }
}

/// <summary>
/// Abstraction over the per-track output sink used during ripping.
/// Implementations either write directly (WAV) or pipe raw 16-bit stereo
/// 44.1 kHz PCM into a subprocess encoder (FLAC, MP3).  PCM is written in
/// whole sectors (2,352 bytes) from <see cref="RipSession"/>.
/// </summary>
public interface IRipEncoder : IAsyncDisposable
{
    Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken);
}

public static class RipEncoder
{
    private static readonly ILogger _log = Logging.For("RipEncoder");

    public static string ExtensionFor(RipFormat format) => format switch
    {
        RipFormat.Wav => ".wav",
        RipFormat.Flac => ".flac",
        RipFormat.Mp3 => ".mp3",
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Opens a per-track encoder writing to <paramref name="outputPath"/>.
    /// <paramref name="pcmByteCount"/> is the total raw PCM size expected;
    /// WAV needs it up front for the RIFF header, the other encoders stream
    /// without needing it.
    /// </summary>
    public static IRipEncoder Open(string outputPath, long pcmByteCount, RipTrackMetadata metadata, CdRipOptions options)
    {
        ArgumentNullException.ThrowIfNull(outputPath);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(options);

        // The cover art bytes need to live on disk for the duration of the
        // encoder run - both flac and lame take a file path, not stdin pic data.
        // The temp file is deleted by SubprocessEncoder.DisposeAsync once the
        // child has exited and the embedded copy is in the output.
        string? coverArtTempFile = null;
        if (metadata.CoverArt is { Length: > 0 } && options.Format != RipFormat.Wav)
        {
            coverArtTempFile = WriteCoverArtTempFile(metadata.CoverArt);
        }

        // WAV has no standard for embedded artwork. Drop the bytes into the
        // output folder as cover.jpg / cover.png so any media player that
        // looks for sidecar art (foobar2000, Plex, Jellyfin, ...) picks it up.
        if (metadata.CoverArt is { Length: > 0 } && options.Format == RipFormat.Wav)
        {
            WriteWavSidecarCover(outputPath, metadata.CoverArt);
        }

        return options.Format switch
        {
            RipFormat.Wav => new WavEncoder(outputPath, pcmByteCount),
            RipFormat.Flac => new SubprocessEncoder("flac", BuildFlacArgs(outputPath, metadata, options, coverArtTempFile), outputPath, _log, coverArtTempFile),
            RipFormat.Mp3 => new SubprocessEncoder("lame", BuildLameArgs(outputPath, metadata, options, coverArtTempFile), outputPath, _log, coverArtTempFile),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown rip format"),
        };
    }

    /// <summary>
    /// Writes the cover-art bytes to a temp file with the right extension so
    /// flac/lame can sniff the MIME type. Returns the absolute path; caller
    /// is responsible for cleaning it up.
    /// </summary>
    /// <summary>
    /// Writes the album cover next to a WAV-format rip's output directory.
    /// Idempotent - repeated calls overwrite. Track 1's cover wins (and is
    /// identical to every other track's cover for a single-disc rip anyway).
    /// </summary>
    private static void WriteWavSidecarCover(string outputPath, byte[] bytes)
    {
        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            var isPng = bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47;
            var coverName = isPng ? "cover.png" : "cover.jpg";
            var coverPath = Path.Combine(dir, coverName);
            if (!File.Exists(coverPath))
            {
                File.WriteAllBytes(coverPath, bytes);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Failed to write WAV sidecar cover for {Path}", outputPath);
        }
    }

    private static string WriteCoverArtTempFile(byte[] bytes)
    {
        // JPEG magic FFD8 FF, PNG magic 89 50 4E 47. Default to .jpg - both
        // encoders accept either and FLAC's parser uses the actual file bytes.
        var ext = (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            ? ".png"
            : ".jpg";
        var path = Path.Combine(Path.GetTempPath(), $"orgz-cover-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Builds the command-line arguments passed to <c>flac</c> for stdin-piped
    /// raw PCM input.  Exposed for tests; no subprocess is spawned here.
    /// </summary>
    internal static List<string> BuildFlacArgs(string outputPath, RipTrackMetadata metadata, CdRipOptions options, string? coverArtPath = null)
    {
        var compression = Math.Clamp(options.FlacCompression, 0, 8);

        var args = new List<string>
        {
            "--silent",
            $"-{compression}",
            "--force-raw-format",
            "--endian=little",
            "--sign=signed",
            "--channels=2",
            "--bps=16",
            "--sample-rate=44100",
            "-o", outputPath,
        };

        AppendFlacTag(args, "TITLE", metadata.Title);
        AppendFlacTag(args, "ARTIST", metadata.Artist);
        AppendFlacTag(args, "ALBUM", metadata.Album);
        AppendFlacTag(args, "GENRE", metadata.Genre);
        AppendFlacTag(args, "ENCODER", metadata.EncodedBy);

        if (metadata.TrackNumber is int n && n > 0)
        {
            AppendFlacTag(args, "TRACKNUMBER", n.ToString());
        }

        if (metadata.Year is uint y && y > 0)
        {
            AppendFlacTag(args, "DATE", y.ToString());
        }

        if (!string.IsNullOrEmpty(coverArtPath))
        {
            // --picture=FILE: flac auto-detects MIME and dimensions from the
            // file contents and embeds as METADATA_BLOCK_PICTURE type 3 (front).
            args.Add($"--picture={coverArtPath}");
        }

        args.Add("-");
        return args;
    }

    /// <summary>
    /// Builds the command-line arguments passed to <c>lame</c> for stdin-piped
    /// raw PCM input.  VBR (<c>-V 0..9</c>, 0 is highest) or CBR
    /// (<c>-b &lt;kbps&gt;</c>) per <paramref name="options"/>.
    /// </summary>
    internal static List<string> BuildLameArgs(string outputPath, RipTrackMetadata metadata, CdRipOptions options, string? coverArtPath = null)
    {
        var args = new List<string>
        {
            "--silent",
            "-r",
            "-s", "44.1",
            "--bitwidth", "16",
            "--signed",
            "--little-endian",
            "-m", "s",
        };

        if (options.Mp3Mode == Mp3Mode.Cbr)
        {
            var kbps = Math.Clamp(options.Mp3Quality, 32, 320);
            args.Add("-b");
            args.Add(kbps.ToString());
            args.Add("--cbr");
        }
        else
        {
            var v = Math.Clamp(options.Mp3Quality, 0, 9);
            args.Add("-V");
            args.Add(v.ToString());
        }

        if (!string.IsNullOrEmpty(metadata.Title))
        {
            args.Add("--tt");
            args.Add(metadata.Title);
        }

        if (!string.IsNullOrEmpty(metadata.Artist))
        {
            args.Add("--ta");
            args.Add(metadata.Artist);
        }

        if (!string.IsNullOrEmpty(metadata.Album))
        {
            args.Add("--tl");
            args.Add(metadata.Album);
        }

        if (metadata.TrackNumber is int n && n > 0)
        {
            args.Add("--tn");
            args.Add(n.ToString());
        }

        if (metadata.Year is uint y && y > 0)
        {
            args.Add("--ty");
            args.Add(y.ToString());
        }

        if (!string.IsNullOrEmpty(metadata.Genre))
        {
            args.Add("--tg");
            args.Add(metadata.Genre);
        }

        if (!string.IsNullOrEmpty(metadata.EncodedBy))
        {
            // ID3v2 TENC frame = "Encoded by". --tv KEY=VALUE adds an arbitrary
            // ID3v2 user-text frame; lame happily accepts the standard 4-letter
            // codes here, so we get a real TENC frame in the output.
            args.Add("--tv");
            args.Add($"TENC={metadata.EncodedBy}");
        }

        if (!string.IsNullOrEmpty(coverArtPath))
        {
            // lame's --ti embeds the image as an ID3v2 APIC frame
            // (picture type 0x03 = front cover).
            args.Add("--ti");
            args.Add(coverArtPath);
        }

        args.Add("-");
        args.Add(outputPath);
        return args;
    }

    private static void AppendFlacTag(List<string> args, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        args.Add($"--tag={key}={value}");
    }

    // -- Encoder implementations --------------------------------------------

    private sealed class WavEncoder : IRipEncoder
    {
        private readonly FileStream _fs;

        public WavEncoder(string outputPath, long pcmByteCount)
        {
            _fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            CdRipService.WriteWavHeader(_fs, pcmByteCount);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
        {
            return _fs.WriteAsync(pcm, cancellationToken).AsTask();
        }

        public async ValueTask DisposeAsync()
        {
            await _fs.FlushAsync();
            _fs.Dispose();
        }
    }

    private sealed class SubprocessEncoder : IRipEncoder
    {
        private readonly Process _proc;
        private readonly Stream _stdin;
        private readonly string _outputPath;
        private readonly ILogger _log;
        private readonly string? _coverArtTempFile;
        private bool _disposed;

        public SubprocessEncoder(string exeName, List<string> args, string outputPath, ILogger log, string? coverArtTempFile = null)
        {
            _outputPath = outputPath;
            _log = log;
            _coverArtTempFile = coverArtTempFile;

            var psi = new ProcessStartInfo
            {
                FileName = ResolveExecutable(exeName),
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            try
            {
                _proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start '{exeName}'.");
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new FileNotFoundException(
                    $"Encoder '{exeName}' was not found on PATH. Install it (Windows: winget install flac / lame; Debian/Ubuntu: sudo apt install flac lame; macOS: brew install flac lame) or choose a different rip format.",
                    exeName,
                    ex);
            }

            _stdin = _proc.StandardInput.BaseStream;

            // Drain stderr on a background task so the child doesn't block on a full pipe.
            _ = Task.Run(async () =>
            {
                try
                {
                    var err = await _proc.StandardError.ReadToEndAsync();
                    if (!string.IsNullOrWhiteSpace(err))
                    {
                        _log.Debug("{Exe} stderr: {Err}", exeName, err.Trim());
                    }
                }
                catch { /* process exited */ }
            });
        }

        public async Task WriteAsync(ReadOnlyMemory<byte> pcm, CancellationToken cancellationToken)
        {
            await _stdin.WriteAsync(pcm, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                await _stdin.FlushAsync();
                _stdin.Close();
                await _proc.WaitForExitAsync();

                if (_proc.ExitCode != 0)
                {
                    throw new InvalidOperationException($"Encoder exited with code {_proc.ExitCode} while producing {_outputPath}.");
                }
            }
            finally
            {
                _proc.Dispose();
                if (_coverArtTempFile != null)
                {
                    try { File.Delete(_coverArtTempFile); }
                    catch (Exception ex) { _log.Debug(ex, "Failed to clean up cover art temp file {Path}", _coverArtTempFile); }
                }
            }
        }

        private static string ResolveExecutable(string name)
        {
            var fileName = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name + ".exe"
                : name;

            // 1. Prefer a system install on PATH - distro packagers and power
            //    users keep their own versions current. If we shipped a bundled
            //    binary and silently shadowed a newer system version, security
            //    fixes wouldn't reach our users until the next OrgZ release.
            if (TryFindOnPath(fileName, out var fromPath))
            {
                return fromPath;
            }

            // 2. Fall back to a copy bundled next to OrgZ.dll - the AppImage /
            //    .app / Windows portable layout drops these into "tools/" so the
            //    first-run rip works without forcing the user to apt-install.
            var bundled = Path.Combine(AppContext.BaseDirectory, "tools", fileName);
            if (File.Exists(bundled))
            {
                return bundled;
            }

            // 3. Nowhere to be found - Process.Start will throw Win32Exception,
            //    which the SubprocessEncoder ctor catches and rewrites into a
            //    user-facing "install flac/lame" message.
            return fileName;
        }

        private static bool TryFindOnPath(string fileName, out string fullPath)
        {
            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                var sep = OperatingSystem.IsWindows() ? ';' : ':';
                foreach (var dir in pathEnv.Split(sep, StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = Path.Combine(dir, fileName);
                    if (File.Exists(candidate))
                    {
                        fullPath = candidate;
                        return true;
                    }
                }
            }

            fullPath = "";
            return false;
        }
    }
}
