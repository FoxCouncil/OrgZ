// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Identifies what a stream ACTUALLY carries by parsing the audio bytes themselves, because
/// Content-Type and icy-br are just claims. MPEG audio (mp3/mp2/mp1) and ADTS AAC are walked
/// frame-by-frame - a real stream chains valid frames, which also yields the true bitrate
/// (averaged, so VBR reports its running rate). Ogg and FLAC are matched by container magic,
/// with the Vorbis identification header supplying its nominal bitrate when present.
/// </summary>
public static class AudioSniffer
{
    public sealed record Result(string Format, int? Bitrate, int Frames);

    public static Result? Sniff(byte[] data)
    {
        if (data.Length < 16)
        {
            return null;
        }

        // Some streams front-load an ID3v2 tag; the audio starts behind it.
        var offset = 0;
        if (data.Length > 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
        {
            var size = ((data[6] & 0x7F) << 21) | ((data[7] & 0x7F) << 14) | ((data[8] & 0x7F) << 7) | (data[9] & 0x7F);
            offset = Math.Min(10 + size, data.Length);
        }

        if (data.AsSpan(offset).StartsWith("fLaC"u8))
        {
            return new Result("flac", null, 1);
        }

        // Ogg pages recur every ~4-64KB, so the magic shows up even when we joined mid-stream.
        if (data.AsSpan(offset).IndexOf("OggS"u8) >= 0)
        {
            return new Result("ogg", VorbisNominalBitrate(data), 1);
        }

        // Frame walkers: both start from a 0xFF sync byte, so run both and let the one that
        // chains the most consecutive valid frames win (the loser stalls after a false sync).
        var mpeg = ScanFrames(data, offset, WalkMpeg);
        var adts = ScanFrames(data, offset, WalkAdts);
        if (mpeg != null && adts != null)
        {
            return adts.Frames > mpeg.Frames ? adts : mpeg;
        }
        return mpeg ?? adts;
    }

    private static Result? ScanFrames(byte[] data, int offset, Func<byte[], int, Result?> walker)
    {
        for (var i = offset; i < data.Length - 8; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0)
            {
                continue;
            }
            var result = walker(data, i);
            if (result is { Frames: >= 3 })
            {
                return result;
            }
        }
        return null;
    }

    // -- MPEG audio (mp1/mp2/mp3) --

    private static readonly int[][] MpegSampleRates =
    [
        [11025, 12000, 8000],   // version bits 00 = MPEG 2.5
        [],                     // 01 = reserved
        [22050, 24000, 16000],  // 10 = MPEG 2
        [44100, 48000, 32000],  // 11 = MPEG 1
    ];

    private static readonly int[] V1L1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
    private static readonly int[] V1L2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
    private static readonly int[] V1L3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] V2L1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
    private static readonly int[] V2L23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    private static Result? WalkMpeg(byte[] d, int pos)
    {
        var bitrates = new List<int>();
        string? format = null;
        int? lockedRate = null;

        while (pos + 4 <= d.Length && bitrates.Count < 12)
        {
            if (d[pos] != 0xFF || (d[pos + 1] & 0xE0) != 0xE0)
            {
                break;
            }

            var version = (d[pos + 1] >> 3) & 0x3;  // 3=MPEG1, 2=MPEG2, 0=MPEG2.5
            var layer = (d[pos + 1] >> 1) & 0x3;    // 3=Layer I, 2=Layer II, 1=Layer III
            var brIdx = (d[pos + 2] >> 4) & 0xF;
            var srIdx = (d[pos + 2] >> 2) & 0x3;
            var padding = (d[pos + 2] >> 1) & 0x1;
            if (version == 1 || layer == 0 || brIdx is 0 or 15 || srIdx == 3)
            {
                break;
            }

            var sampleRate = MpegSampleRates[version][srIdx];
            var table = version == 3
                ? layer switch { 3 => V1L1, 2 => V1L2, _ => V1L3 }
                : layer == 3 ? V2L1 : V2L23;
            var bitrate = table[brIdx];
            if (bitrate <= 0)
            {
                break;
            }

            if (lockedRate == null)
            {
                lockedRate = sampleRate;
            }
            else if (sampleRate != lockedRate)
            {
                break;
            }

            var thisFormat = layer switch { 3 => "mp1", 2 => "mp2", _ => "mp3" };
            if (format == null)
            {
                format = thisFormat;
            }
            else if (format != thisFormat)
            {
                break;
            }

            bitrates.Add(bitrate);
            var frameLength = layer == 3
                ? (12 * bitrate * 1000 / sampleRate + padding) * 4
                : layer == 2
                    ? 144 * bitrate * 1000 / sampleRate + padding
                    : (version == 3 ? 144 : 72) * bitrate * 1000 / sampleRate + padding;
            if (frameLength <= 4)
            {
                break;
            }
            pos += frameLength;
        }

        return format == null || bitrates.Count == 0 ? null : new Result(format, (int)Math.Round(bitrates.Average()), bitrates.Count);
    }

    // -- ADTS AAC --

    private static readonly int[] AdtsSampleRates = [96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350];

    private static Result? WalkAdts(byte[] d, int pos)
    {
        var frames = 0;
        long bytes = 0;
        int? lockedRate = null;

        while (pos + 7 <= d.Length && frames < 32)
        {
            if (d[pos] != 0xFF || (d[pos + 1] & 0xF6) != 0xF0)
            {
                break;
            }

            var srIdx = (d[pos + 2] >> 2) & 0xF;
            if (srIdx >= AdtsSampleRates.Length)
            {
                break;
            }
            var sampleRate = AdtsSampleRates[srIdx];
            if (lockedRate == null)
            {
                lockedRate = sampleRate;
            }
            else if (sampleRate != lockedRate)
            {
                break;
            }

            var frameLength = ((d[pos + 3] & 0x03) << 11) | (d[pos + 4] << 3) | ((d[pos + 5] >> 5) & 0x7);
            if (frameLength < 7)
            {
                break;
            }

            frames++;
            bytes += frameLength;
            pos += frameLength;
        }

        if (frames == 0 || lockedRate == null)
        {
            return null;
        }

        // 1024 samples per AAC frame → average bitrate over the frames we saw.
        var kbps = (int)Math.Round(bytes * 8.0 * lockedRate.Value / (frames * 1024.0) / 1000.0);
        return new Result("aac", kbps, frames);
    }

    // -- Ogg Vorbis --

    private static int? VorbisNominalBitrate(byte[] data)
    {
        ReadOnlySpan<byte> magic = [0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s'];
        var idx = data.AsSpan().IndexOf(magic);
        if (idx < 0 || idx + 24 > data.Length)
        {
            return null;
        }

        // Identification header: type+'vorbis'(7) version(4) channels(1) rate(4) brMax(4) brNominal(4).
        var nominal = BitConverter.ToInt32(data, idx + 20);
        return nominal is > 0 and < 10_000_000 ? (int)Math.Round(nominal / 1000.0) : null;
    }
}
