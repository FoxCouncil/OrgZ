// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Diagnostics;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Transcodes arbitrary audio sources (MP3, AAC, FLAC, ALAC, ...) into the CD-DA
/// native WAV format - 16-bit stereo 44.1 kHz PCM - that <see cref="CdBurnService"/>
/// requires.  Crucially, the produced WAV's data chunk is zero-padded up to a whole
/// multiple of one CD sector (2352 bytes), because the burn path rejects any PCM
/// length that isn't sector-aligned.  Ripped WAVs are aligned by construction; a
/// transcode of an arbitrary-length song is not, so we pad the final partial sector
/// with silence (exactly what a CD recorder does for the last block).
/// </summary>
public static class CdAudioTranscoder
{
    private const int BytesPerSector = 2352;
    private const uint RedbookSampleRate = 44100;
    private const ushort RedbookChannels = 2;
    private const ushort RedbookBitsPerSample = 16;
    private const uint RedbookByteRate = RedbookSampleRate * RedbookChannels * (RedbookBitsPerSample / 8); // 176400
    private const ushort RedbookBlockAlign = RedbookChannels * (RedbookBitsPerSample / 8);                 // 4

    private static readonly ILogger _log = Logging.For("CdTranscode");

    /// <summary>
    /// Decodes <paramref name="inputPath"/> to raw 16-bit stereo 44.1 kHz PCM via
    /// ffmpeg, then wraps it in a sector-aligned CD-DA WAV at <paramref name="outputWavPath"/>.
    /// </summary>
    public static async Task ToCdAudioWavAsync(string ffmpegPath, string inputPath, string outputWavPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(ffmpegPath);
        ArgumentException.ThrowIfNullOrEmpty(inputPath);
        ArgumentException.ThrowIfNullOrEmpty(outputWavPath);

        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("Burn source missing.", inputPath);
        }

        // ffmpeg emits raw little-endian signed 16-bit PCM to a scratch file; we
        // then build the WAV header ourselves so the data chunk length is exactly a
        // sector multiple. Writing raw PCM (not WAV) keeps us in full control of the
        // header - ffmpeg's own WAV muxer can prepend LIST/INFO chunks and won't pad.
        var pcmPath = outputWavPath + ".pcm";

        var psi = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-nostdin");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(inputPath);
        psi.ArgumentList.Add("-vn");                  // drop embedded cover art
        psi.ArgumentList.Add("-ar");
        psi.ArgumentList.Add(RedbookSampleRate.ToString());
        psi.ArgumentList.Add("-ac");
        psi.ArgumentList.Add(RedbookChannels.ToString());
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("s16le");                // raw signed 16-bit LE PCM
        psi.ArgumentList.Add(pcmPath);

        _log.Information("Transcoding to CD-DA PCM: {Input} -> {Output}", inputPath, outputWavPath);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
            try
            {
                // Output goes to a file, so only stderr can back up the pipe - drain it.
                var stderr = await proc.StandardError.ReadToEndAsync(ct);
                await proc.WaitForExitAsync(ct);
                if (proc.ExitCode != 0)
                {
                    var tail = stderr.Length > 800 ? stderr[^800..] : stderr;
                    throw new InvalidOperationException($"ffmpeg exited {proc.ExitCode}: {tail}");
                }
            }
            catch (OperationCanceledException)
            {
                // Don't leave a detached ffmpeg holding the scratch file open.
                try
                {
                    if (!proc.HasExited)
                    {
                        proc.Kill(entireProcessTree: true);
                    }
                }
                catch (Exception killEx)
                {
                    _log.Debug(killEx, "Failed to kill cancelled ffmpeg for {Input}", inputPath);
                }

                throw;
            }

            var pcmLength = new FileInfo(pcmPath).Length;
            if (pcmLength == 0)
            {
                throw new InvalidDataException($"ffmpeg produced no PCM for '{inputPath}'.");
            }

            using var pcm = new FileStream(pcmPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var wav = new FileStream(outputWavPath, FileMode.Create, FileAccess.Write, FileShare.None);
            WriteCdAudioWav(pcm, pcmLength, wav);
        }
        finally
        {
            try
            {
                if (File.Exists(pcmPath))
                {
                    File.Delete(pcmPath);
                }
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Failed to delete scratch PCM {PcmPath}", pcmPath);
            }
        }
    }

    /// <summary>
    /// Copies <paramref name="pcmLength"/> bytes of CD-DA PCM from <paramref name="pcm"/>
    /// into <paramref name="wav"/>, framed by a canonical 44-byte RIFF/WAVE header whose
    /// data chunk is rounded up to a whole CD sector and padded with silence.
    /// </summary>
    internal static void WriteCdAudioWav(Stream pcm, long pcmLength, Stream wav)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(wav);

        // PCM frames are 4 bytes (16-bit stereo) and a sector is a whole number of
        // frames, so zero-padding to the next sector keeps frame alignment intact.
        long dataLength = ((pcmLength + BytesPerSector - 1) / BytesPerSector) * BytesPerSector;

        Span<byte> header = stackalloc byte[44];
        WriteFourCc(header[..4], "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)(36 + dataLength));
        WriteFourCc(header.Slice(8, 4), "WAVE");

        WriteFourCc(header.Slice(12, 4), "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);                  // fmt chunk size
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1);                   // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), RedbookChannels);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), RedbookSampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), RedbookByteRate);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), RedbookBlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), RedbookBitsPerSample);

        WriteFourCc(header.Slice(36, 4), "data");
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), (uint)dataLength);

        wav.Write(header);

        // Stream the PCM payload across - never buffer a whole track in memory.
        var buffer = new byte[81920];
        long remaining = pcmLength;
        while (remaining > 0)
        {
            int want = (int)Math.Min(buffer.Length, remaining);
            int got = pcm.Read(buffer, 0, want);
            if (got <= 0)
            {
                throw new EndOfStreamException($"PCM stream ended {remaining} bytes early.");
            }

            wav.Write(buffer, 0, got);
            remaining -= got;
        }

        long pad = dataLength - pcmLength;
        if (pad > 0)
        {
            Array.Clear(buffer);
            while (pad > 0)
            {
                int chunk = (int)Math.Min(buffer.Length, pad);
                wav.Write(buffer, 0, chunk);
                pad -= chunk;
            }
        }
    }

    private static void WriteFourCc(Span<byte> dest, string fourCc)
    {
        for (int i = 0; i < 4; i++)
        {
            dest[i] = (byte)fourCc[i];
        }
    }
}
