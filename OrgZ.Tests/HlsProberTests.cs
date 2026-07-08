// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Tests;

/// <summary>
/// Conformance tests for the HLS segment sniffer against synthetic segments built to spec:
/// packed-audio (ID3v2 tag + ADTS frames), MPEG-TS (PAT → PMT with a stream_type 0x15 timed
/// metadata stream + ID3 PES), and fMP4 (styp + ID3-bearing emsg box).
/// </summary>
public class HlsProberTests
{
    // -- ID3 tag builder (v2.4, syncsafe frame sizes) --

    private static byte[] BuildId3(string? title, string? artist)
    {
        var frames = new List<byte>();
        void AddText(string id, string value)
        {
            var text = Encoding.UTF8.GetBytes(value);
            frames.AddRange(Encoding.ASCII.GetBytes(id));
            var size = text.Length + 1; // +1 encoding byte
            frames.AddRange([(byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
            frames.AddRange([0, 0]);   // frame flags
            frames.Add(3);             // UTF-8
            frames.AddRange(text);
        }
        if (title != null)
        {
            AddText("TIT2", title);
        }
        if (artist != null)
        {
            AddText("TPE1", artist);
        }

        var body = frames.ToArray();
        var tag = new List<byte>();
        tag.AddRange("ID3"u8.ToArray());
        tag.AddRange([4, 0, 0]); // v2.4, no flags
        tag.AddRange([(byte)((body.Length >> 21) & 0x7F), (byte)((body.Length >> 14) & 0x7F), (byte)((body.Length >> 7) & 0x7F), (byte)(body.Length & 0x7F)]);
        tag.AddRange(body);
        return tag.ToArray();
    }

    // One valid ADTS AAC frame (44.1kHz, header + filler payload), repeated to give the sniffer a chain.
    private static byte[] BuildAdtsFrames(int count, int frameLength = 400)
    {
        var stream = new List<byte>();
        for (var i = 0; i < count; i++)
        {
            var frame = new byte[frameLength];
            frame[0] = 0xFF;
            frame[1] = 0xF1; // sync + MPEG-4, layer 00, no CRC
            frame[2] = 0x50; // profile AAC-LC, sampling index 4 (44100), channel cfg hi
            frame[3] = (byte)(0x80 | ((frameLength >> 11) & 0x03));
            frame[4] = (byte)((frameLength >> 3) & 0xFF);
            frame[5] = (byte)(((frameLength & 0x07) << 5) | 0x1F);
            frame[6] = 0xFC;
            stream.AddRange(frame);
        }
        return stream.ToArray();
    }

    [Fact]
    public void PackedAudioSegmentYieldsSegKindTitleAndCodec()
    {
        var segment = BuildId3("A Future Of Ultraviolence", "DEADLIFE").Concat(BuildAdtsFrames(6)).ToArray();

        var info = HlsProber.SniffSegment(segment);

        Assert.Equal("seg", info.MetaKind);
        Assert.Equal("DEADLIFE - A Future Of Ultraviolence", info.Title);
        Assert.Equal("aac", info.Codec);
    }

    [Fact]
    public void PlainAdtsSegmentSniffsCodecWithoutMetadata()
    {
        var info = HlsProber.SniffSegment(BuildAdtsFrames(6));

        Assert.Null(info.MetaKind);
        Assert.False(info.HasLeadingId3);
        Assert.Equal("aac", info.Codec);
    }

    [Fact]
    public void MandatoryTimestampTagAloneIsNotAMetadataChannel()
    {
        // Every packed-audio segment must lead with a PRIV timestamp tag - plumbing, not metadata.
        var priv = new List<byte>();
        priv.AddRange("PRIV"u8.ToArray());
        var owner = Encoding.ASCII.GetBytes("com.apple.streaming.transportStreamTimestamp\0");
        var size = owner.Length + 8;
        priv.AddRange([(byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
        priv.AddRange([0, 0]);
        priv.AddRange(owner);
        priv.AddRange(new byte[8]);

        var body = priv.ToArray();
        var tag = new List<byte>();
        tag.AddRange("ID3"u8.ToArray());
        tag.AddRange([4, 0, 0]);
        tag.AddRange([(byte)((body.Length >> 21) & 0x7F), (byte)((body.Length >> 14) & 0x7F), (byte)((body.Length >> 7) & 0x7F), (byte)(body.Length & 0x7F)]);
        tag.AddRange(body);

        var segment = tag.Concat(BuildAdtsFrames(6)).ToArray();
        var info = HlsProber.SniffSegment(segment);

        Assert.Null(info.MetaKind);
        Assert.True(info.HasLeadingId3);
        Assert.Equal("aac", info.Codec);
    }

    // -- MPEG-TS builder --

    private static byte[] TsPacket(int pid, bool pusi, byte[] payload)
    {
        var packet = new byte[188];
        packet[0] = 0x47;
        packet[1] = (byte)(((pusi ? 1 : 0) << 6) | ((pid >> 8) & 0x1F));
        packet[2] = (byte)(pid & 0xFF);
        packet[3] = 0x10; // payload only, cc 0
        Array.Copy(payload, 0, packet, 4, Math.Min(payload.Length, 184));
        for (var i = 4 + payload.Length; i < 188; i++)
        {
            packet[i] = 0xFF;
        }
        return packet;
    }

    private static byte[] BuildTsSegment(bool withTimedId3, string? title = null, string? artist = null)
    {
        const int pmtPid = 0x100, audioPid = 0x101, id3Pid = 0x102;

        // PAT: program 1 → PMT PID.
        var pat = new List<byte> { 0x00 /* pointer */, 0x00, 0xB0, 0x0D, 0x00, 0x01, 0xC1, 0x00, 0x00 };
        pat.AddRange([0x00, 0x01, (byte)(0xE0 | (pmtPid >> 8)), (byte)(pmtPid & 0xFF)]);
        pat.AddRange([0xDE, 0xAD, 0xBE, 0xEF]); // CRC (unchecked)

        // PMT: ADTS AAC ES + optional timed-ID3 (0x15) ES.
        var es = new List<byte> { 0x0F, (byte)(0xE0 | (audioPid >> 8)), (byte)(audioPid & 0xFF), 0xF0, 0x00 };
        if (withTimedId3)
        {
            es.AddRange([0x15, (byte)(0xE0 | (id3Pid >> 8)), (byte)(id3Pid & 0xFF), 0xF0, 0x00]);
        }
        var sectionLength = 9 + es.Count + 4; // after-length header + ES loop + CRC
        var pmt = new List<byte> { 0x00 /* pointer */, 0x02, (byte)(0xB0 | (sectionLength >> 8)), (byte)(sectionLength & 0xFF), 0x00, 0x01, 0xC1, 0x00, 0x00, (byte)(0xE0 | (audioPid >> 8)), (byte)(audioPid & 0xFF), 0xF0, 0x00 };
        pmt.AddRange(es);
        pmt.AddRange([0xDE, 0xAD, 0xBE, 0xEF]);

        var segment = new List<byte>();
        segment.AddRange(TsPacket(0, pusi: true, pat.ToArray()));
        segment.AddRange(TsPacket(pmtPid, pusi: true, pmt.ToArray()));

        if (withTimedId3 && title != null)
        {
            var tag = BuildId3(title, artist);
            var pes = new List<byte> { 0x00, 0x00, 0x01, 0xBD /* private_stream_1 */ };
            var pesLength = 3 + tag.Length; // flags(2) + header_length(1) + payload
            pes.AddRange([(byte)(pesLength >> 8), (byte)(pesLength & 0xFF), 0x80, 0x00, 0x00]);
            pes.AddRange(tag);
            segment.AddRange(TsPacket(id3Pid, pusi: true, pes.ToArray()));
        }

        // Pad with audio-PID packets so the sync check sees a real stream.
        for (var i = 0; i < 4; i++)
        {
            segment.AddRange(TsPacket(audioPid, pusi: false, [0xAA, 0xBB]));
        }
        return segment.ToArray();
    }

    [Fact]
    public void TsSegmentWithTimedId3YieldsTsKindTitleAndCodec()
    {
        var segment = BuildTsSegment(withTimedId3: true, title: "All Summer Long", artist: "Kid Rock");

        var info = HlsProber.SniffSegment(segment);

        Assert.Equal("ts", info.MetaKind);
        Assert.Equal("Kid Rock - All Summer Long", info.Title);
        Assert.Equal("aac", info.Codec);
    }

    [Fact]
    public void TsSegmentWithoutMetadataStreamYieldsNoKind()
    {
        var info = HlsProber.SniffSegment(BuildTsSegment(withTimedId3: false));

        Assert.Null(info.MetaKind);
        Assert.Equal("aac", info.Codec);
    }

    // -- fMP4 --

    private static byte[] Box(string type, byte[] payload)
    {
        var box = new List<byte>();
        var size = 8 + payload.Length;
        box.AddRange([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size]);
        box.AddRange(Encoding.ASCII.GetBytes(type));
        box.AddRange(payload);
        return box.ToArray();
    }

    [Fact]
    public void Fmp4SegmentWithId3EmsgYieldsEmsgKindAndTitle()
    {
        var scheme = Encoding.ASCII.GetBytes("https://aomedia.org/emsg/ID3\0\0"); // scheme + empty value
        var emsgPayload = new byte[] { 0x00, 0x00, 0x00, 0x00 } // version 0 + flags
            .Concat(scheme)
            .Concat(new byte[16])                               // timescale/presentation/duration/id
            .Concat(BuildId3("Blue Velvet", "The Clovers"))
            .ToArray();

        var segment = Box("styp", Encoding.ASCII.GetBytes("msdh\0\0\0\0msdh"))
            .Concat(Box("emsg", emsgPayload))
            .Concat(Box("mdat", new byte[64]))
            .ToArray();

        var info = HlsProber.SniffSegment(segment);

        Assert.Equal("emsg", info.MetaKind);
        Assert.Equal("The Clovers - Blue Velvet", info.Title);
    }

    [Fact]
    public void Fmp4SegmentWithoutEmsgYieldsNoKind()
    {
        var segment = Box("styp", Encoding.ASCII.GetBytes("msdh\0\0\0\0msdh"))
            .Concat(Box("mdat", new byte[64]))
            .ToArray();

        var info = HlsProber.SniffSegment(segment);

        Assert.Null(info.MetaKind);
    }

    // -- Playlist walking --

    [Fact]
    public void PickVariantChoosesHighestBandwidth()
    {
        const string master = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=64000,CODECS="mp4a.40.2"
            low/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=256000,CODECS="mp4a.40.2"
            high/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=128000,CODECS="mp4a.40.2"
            mid/index.m3u8
            """;

        Assert.Equal("https://radio.example/hls/high/index.m3u8", HlsProber.PickVariant(master, "https://radio.example/hls/master.m3u8"));
    }

    [Fact]
    public void LastSegmentReturnsNewestUriWithDuration()
    {
        const string media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:9.98,
            seg1001.aac
            #EXTINF:10.02,
            seg1002.aac
            """;

        var (url, duration) = HlsProber.LastSegment(media, "https://radio.example/hls/chunklist.m3u8");

        Assert.Equal("https://radio.example/hls/seg1002.aac", url);
        Assert.Equal(10.02, duration!.Value, precision: 2);
    }

    // -- StreamTitle cleanup --

    [Theory]
    [InlineData("SURFARIS - text=\"Wipe Out\" song_spot=\"M\" MediaBaseId=\"1098209\" amgArtworkURL=\"http://x\"", "SURFARIS - Wipe Out")]
    [InlineData("text=\"Wipe Out\" song_spot=\"M\"", "Wipe Out")]
    [InlineData("DEADLIFE - A Future Of Ultraviolence", "DEADLIFE - A Future Of Ultraviolence")]
    [InlineData("Plain Title", "Plain Title")]
    // Tilde-delimited playout payload (United Music, captured verbatim off ITALIA Italia 60):
    // title~artist~album~year~ISRC~seconds~start~end~channel~...~event-uuid.
    [InlineData("Dove Non So (Tema Di Lara)~Orietta Berti~~1966~ITR008900478~169~2026-07-04T18:51:13~2026-07-04T18:53:35~United Music Hits Italia 60~142.17~3eadf5f3-0e75-4d1e-94de-8bf5a97e2425", "Orietta Berti - Dove Non So (Tema Di Lara)")]
    [InlineData("Instrumental Break~~~1970~XYZ~120", "Instrumental Break")]
    // One or two tildes are punctuation/separators, not a payload - untouched.
    [InlineData("Artist ~ Title", "Artist ~ Title")]
    [InlineData("Wave~Form ~ Redux", "Wave~Form ~ Redux")]
    public void CleanStreamTitleStripsAutomationJunk(string raw, string expected)
    {
        Assert.Equal(expected, IcyMetadata.CleanStreamTitle(raw));
    }

    // -- ID3 text encodings --

    [Fact]
    public void Id3ParsesUtf16TitleWithBom()
    {
        var text = Encoding.Unicode.GetBytes("Café del Mar");
        var frame = new List<byte>();
        frame.AddRange("TIT2"u8.ToArray());
        var size = text.Length + 3; // encoding byte + BOM
        frame.AddRange([(byte)((size >> 21) & 0x7F), (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F)]);
        frame.AddRange([0, 0]);
        frame.AddRange([1, 0xFF, 0xFE]); // UTF-16 + little-endian BOM
        frame.AddRange(text);

        var body = frame.ToArray();
        var tag = new List<byte>();
        tag.AddRange("ID3"u8.ToArray());
        tag.AddRange([4, 0, 0]);
        tag.AddRange([(byte)((body.Length >> 21) & 0x7F), (byte)((body.Length >> 14) & 0x7F), (byte)((body.Length >> 7) & 0x7F), (byte)(body.Length & 0x7F)]);
        tag.AddRange(body);

        Assert.Equal("Café del Mar", Id3.Parse(tag.ToArray(), 0).Title);
    }
}
