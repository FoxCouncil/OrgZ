// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

public sealed record RipTrackMetadata
{
    public string? Title { get; init; }
    public string? Artist { get; init; }
    public string? Album { get; init; }
    public int? TrackNumber { get; init; }
    public uint? Year { get; init; }
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

        return options.Format switch
        {
            RipFormat.Wav => new WavEncoder(outputPath, pcmByteCount),
            RipFormat.Flac => new SubprocessEncoder("flac", BuildFlacArgs(outputPath, metadata, options), outputPath, _log),
            RipFormat.Mp3 => new SubprocessEncoder("lame", BuildLameArgs(outputPath, metadata, options), outputPath, _log),
            _ => throw new ArgumentOutOfRangeException(nameof(options), "Unknown rip format"),
        };
    }

    /// <summary>
    /// Builds the command-line arguments passed to <c>flac</c> for stdin-piped
    /// raw PCM input.  Exposed for tests; no subprocess is spawned here.
    /// </summary>
    internal static List<string> BuildFlacArgs(string outputPath, RipTrackMetadata metadata, CdRipOptions options)
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

        if (metadata.TrackNumber is int n && n > 0)
        {
            AppendFlacTag(args, "TRACKNUMBER", n.ToString());
        }

        if (metadata.Year is uint y && y > 0)
        {
            AppendFlacTag(args, "DATE", y.ToString());
        }

        args.Add("-");
        return args;
    }

    /// <summary>
    /// Builds the command-line arguments passed to <c>lame</c> for stdin-piped
    /// raw PCM input.  VBR (<c>-V 0..9</c>, 0 is highest) or CBR
    /// (<c>-b &lt;kbps&gt;</c>) per <paramref name="options"/>.
    /// </summary>
    internal static List<string> BuildLameArgs(string outputPath, RipTrackMetadata metadata, CdRipOptions options)
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
        private bool _disposed;

        public SubprocessEncoder(string exeName, List<string> args, string outputPath, ILogger log)
        {
            _outputPath = outputPath;
            _log = log;

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
            }
        }

        private static string ResolveExecutable(string name)
        {
            if (OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return name + ".exe";
            }

            return name;
        }
    }
}
