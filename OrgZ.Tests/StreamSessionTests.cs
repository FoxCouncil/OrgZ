// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Tests;

/// <summary>
/// The single-connection engine's parsing primitives: the incremental ICY de-interleaver
/// (must survive metadata blocks straddling ANY read boundary), the HLS media-playlist
/// parser the playback pump drives, and the packed-audio ID3 strip.
/// </summary>
public class StreamSessionTests
{
    // -- Incremental ICY de-interleave --

    /// <summary>Builds an interleaved ICY body: [metaint audio][len][metadata]... with the given titles (null = zero-length block).</summary>
    private static byte[] BuildIcyBody(int metaint, int blocks, Func<int, string?> titleFor, out byte[] expectedAudio)
    {
        var body = new List<byte>();
        var audio = new List<byte>();
        byte next = 0;
        for (var block = 0; block < blocks; block++)
        {
            for (var i = 0; i < metaint; i++)
            {
                body.Add(next);
                audio.Add(next);
                next++;
            }
            var title = titleFor(block);
            if (title == null)
            {
                body.Add(0);
            }
            else
            {
                var text = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
                var padded = (text.Length + 15) / 16 * 16;
                body.Add((byte)(padded / 16));
                body.AddRange(text);
                body.AddRange(new byte[padded - text.Length]);
            }
        }
        expectedAudio = [.. audio];
        return [.. body];
    }

    [Theory]
    [InlineData(1)]     // pathological: every byte its own feed - every state boundary crossed
    [InlineData(7)]     // prime-sized chunks land mid-length-byte and mid-metadata
    [InlineData(4096)]  // realistic read size
    public void DeinterleaverSurvivesAnyChunkSize(int chunkSize)
    {
        var body = BuildIcyBody(1000, 4, b => b switch { 0 => "Song A", 2 => "Song B", _ => null }, out var expectedAudio);

        var deinterleaver = new IcyDeinterleaver(1000);
        var audio = new List<byte>();
        var titles = new List<string>();
        for (var pos = 0; pos < body.Length; pos += chunkSize)
        {
            var count = Math.Min(chunkSize, body.Length - pos);
            deinterleaver.Feed(body, pos, count,
                onAudio: (buffer, offset, length) => audio.AddRange(buffer.Skip(offset).Take(length)),
                onTitle: titles.Add);
        }

        Assert.Equal(expectedAudio, audio);
        Assert.Equal(new[] { "Song A", "Song B" }, titles);
    }

    [Fact]
    public void DeinterleaverWithoutMetaintPassesAudioThrough()
    {
        var body = new byte[] { 1, 2, 3, 4, 5 };
        var deinterleaver = new IcyDeinterleaver(0);
        var audio = new List<byte>();
        deinterleaver.Feed(body, 0, body.Length, (buffer, offset, length) => audio.AddRange(buffer.Skip(offset).Take(length)), _ => { });
        Assert.Equal(body, audio);
    }

    [Fact]
    public void DeinterleaverMatchesBatchParser()
    {
        var body = BuildIcyBody(500, 6, b => b == 1 ? "Batch Vs Stream" : null, out var expectedAudio);

        var (batchAudio, batchTitle) = IcyMetadata.Deinterleave(body, 500);

        var deinterleaver = new IcyDeinterleaver(500);
        var streamAudio = new List<byte>();
        string? streamTitle = null;
        deinterleaver.Feed(body, 0, body.Length, (buffer, offset, length) => streamAudio.AddRange(buffer.Skip(offset).Take(length)), t => streamTitle ??= t);

        Assert.Equal(batchAudio, streamAudio);
        Assert.Equal(batchTitle, streamTitle);
        Assert.Equal(expectedAudio, streamAudio);
    }

    // -- HLS media playlist parsing (what the playback pump drives) --

    [Fact]
    public void MediaPlaylistParsesSequencesDurationsAndCadence()
    {
        const string body = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:1042
            #EXTINF:6.016,
            seg1042.ts
            #EXTINF:5.984,
            seg1043.ts
            #EXTINF:6.016,
            https://cdn.example.com/abs/seg1044.ts
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "https://host.example.com/radio/chunklist.m3u8");

        Assert.Equal(6, playlist.TargetDuration);
        Assert.Equal(1042, playlist.MediaSequence);
        Assert.False(playlist.EndList);
        Assert.Equal(3, playlist.Segments.Count);
        Assert.Equal(1042, playlist.Segments[0].Sequence);
        Assert.Equal(1044, playlist.Segments[2].Sequence);
        Assert.Equal("https://host.example.com/radio/seg1042.ts", playlist.Segments[0].Url);
        Assert.Equal("https://cdn.example.com/abs/seg1044.ts", playlist.Segments[2].Url);
        Assert.Equal(6.016, playlist.Segments[0].Duration!.Value, 3);
    }

    [Fact]
    public void MediaPlaylistCarriesInitSegmentKeysAndEndList()
    {
        const string body = """
            #EXTM3U
            #EXT-X-TARGETDURATION:4
            #EXT-X-MEDIA-SEQUENCE:7
            #EXT-X-MAP:URI="init.mp4"
            #EXT-X-KEY:METHOD=AES-128,URI="key.bin",IV=0x000102030405060708090A0B0C0D0E0F
            #EXTINF:4.0,
            frag7.m4s
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:4.0,
            frag8.m4s
            #EXT-X-ENDLIST
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "https://host/live/list.m3u8");

        Assert.Equal("https://host/live/init.mp4", playlist.InitSegmentUrl);
        Assert.True(playlist.EndList);
        Assert.Null(playlist.UnsupportedKeyMethod);

        Assert.Equal("https://host/live/key.bin", playlist.Segments[0].KeyUrl);
        Assert.Equal(Convert.FromHexString("000102030405060708090A0B0C0D0E0F"), playlist.Segments[0].KeyIv);
        Assert.Null(playlist.Segments[1].KeyUrl); // METHOD=NONE turns encryption back off
    }

    [Fact]
    public void MediaPlaylistParsesIHeartExtInfNowPlaying()
    {
        // Real ihrhls.com shape: title/artist as plaintext EXTINF attributes, with a nested
        // \"-escaped attribute blob inside url="..." carrying the spot type and album art.
        const string body = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:5000
            #EXTINF:10,title="Sunflower",artist="Post Malone / Swae Lee",url="song_spot=\"M\" spotInstanceId=\"-1\" length=\"00:02:34\" MediaBaseId=\"2440595\" TAID=\"0\" TPID=\"79727416\" amgArtworkURL=\"https://i.iheart.com/v3/catalog/track/79727416?ops=fit(400,400)\" spEventID=\"2ac04ff8\""
            seg5000.aac
            #EXTINF:10,title="Thousands In Free Prizes!",artist="",url="song_spot=\"F\" spotInstanceId=\"55193\" length=\"00:00:30\" amgArtworkURL=\"null\""
            seg5001.aac
            #EXTINF:10,
            seg5002.aac
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "https://n33b-e2.revma.ihrhls.com/zc3949/x/playlist.m3u8");

        var music = playlist.Segments[0];
        Assert.Equal("Sunflower", music.Title);
        Assert.Equal("Post Malone / Swae Lee", music.Artist);
        Assert.Equal("M", music.SpotType);
        Assert.Equal("https://i.iheart.com/v3/catalog/track/79727416?ops=fit(400,400)", music.ArtUrl);
        Assert.Equal("Post Malone / Swae Lee - Sunflower", music.NowPlaying);

        // The nested escaped blob must not shred segment URL resolution or durations.
        Assert.Equal("https://n33b-e2.revma.ihrhls.com/zc3949/x/seg5000.aac", music.Url);
        Assert.Equal(10, music.Duration!.Value, 3);

        var adSpot = playlist.Segments[1];
        Assert.Equal("F", adSpot.SpotType);
        Assert.Null(adSpot.NowPlaying);   // ad/talk spots carry junk titles - filtered
        Assert.Null(adSpot.ArtUrl);       // "null" placeholder is not a URL

        var bare = playlist.Segments[2];
        Assert.Null(bare.Title);
        Assert.Null(bare.NowPlaying);
    }

    [Fact]
    public void MediaPlaylistFiltersSpotBreaksButKeepsMislabeledMusic()
    {
        // First entry is a VERBATIM live capture from zc6882 (July 2026): a station sweeper
        // tagged song_spot="T" with a junk internal title, blank artist, and empty catalog
        // IDs. Second entry is the known station quirk: a real song mislabeled "F" but
        // still carrying catalog IDs (MediaBaseId/TPID > 0) - catalog wins over the label.
        const string body = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:178600401
            #EXTINF:10,title="iH50s ComFree Sweepers - 06",artist=" ",url="song_spot=\"T\" spotInstanceId=\"-1\" length=\"00:00:04\" MediaBaseId=\"\" TAID=\"0\" TPID=\"0\" cartcutId=\"\" amgArtworkURL=\"\" spEventID=\"97881034-6474-f111-8380-0242c86e7629\" "
            seg178600401.aac
            #EXTINF:10,title="When Will I Be Loved",artist="Everly Brothers",url="song_spot=\"F\" spotInstanceId=\"-1\" length=\"00:01:56\" MediaBaseId=\"1088340\" TAID=\"0\" TPID=\"239691734\" cartcutId=\"701645\" amgArtworkURL=\"http://image.iheart.com/content/x.jpg\" spEventID=\"98881034-6474-f111-8380-0242c86e7629\" "
            seg178600402.aac
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "http://n11b-e2.revma.ihrhls.com/zc6882/x/playlist.m3u8");

        var sweeper = playlist.Segments[0];
        Assert.Equal("T", sweeper.SpotType);
        Assert.False(sweeper.CatalogBacked);
        Assert.Null(sweeper.NowPlaying);   // junk stays off the toolbar
        Assert.True(sweeper.IsSpotBreak);  // and the display flips to station branding

        var mislabeled = playlist.Segments[1];
        Assert.Equal("F", mislabeled.SpotType);
        Assert.True(mislabeled.CatalogBacked);
        Assert.Equal("Everly Brothers - When Will I Be Loved", mislabeled.NowPlaying);
        Assert.Equal("http://image.iheart.com/content/x.jpg", mislabeled.ArtUrl);
        Assert.False(mislabeled.IsSpotBreak);   // catalog-backed "F" is music, not a break
    }

    [Fact]
    public void SegmentsWithoutSpotMarkersAreNeverBreaks()
    {
        // "No metadata" must not clear the display - only a POSITIVE spot marker is a break.
        const string body = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXTINF:6.0,
            plain.ts
            #EXTINF:10,title="Sunflower",artist="Post Malone",url="song_spot=\"M\" MediaBaseId=\"1\" TPID=\"2\""
            music.aac
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "https://host/x.m3u8");

        Assert.False(playlist.Segments[0].IsSpotBreak);
        Assert.False(playlist.Segments[1].IsSpotBreak);
    }

    [Fact]
    public void MediaPlaylistIgnoresPlainCommentTitles()
    {
        const string body = """
            #EXTM3U
            #EXT-X-TARGETDURATION:6
            #EXTINF:6.0,Some Show Name - Live
            seg1.ts
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "https://host/x.m3u8");

        // A classic display-title comment is not name="value" metadata.
        Assert.Null(playlist.Segments[0].Title);
        Assert.Null(playlist.Segments[0].NowPlaying);
    }

    [Fact]
    public void MediaPlaylistFlagsUndecryptableEncryption()
    {
        const string body = """
            #EXTM3U
            #EXT-X-KEY:METHOD=SAMPLE-AES,URI="skd://key"
            #EXTINF:6.0,
            seg.ts
            """;

        var playlist = HlsMediaPlaylist.Parse(body, "https://host/x.m3u8");

        Assert.Equal("SAMPLE-AES", playlist.UnsupportedKeyMethod);
    }

    // -- Packed-audio ID3 strip: VLC gets the bare elementary stream --

    [Fact]
    public void StripLeadingId3RemovesChainedTagsOnly()
    {
        static byte[] Tag(byte marker)
        {
            // Minimal ID3v2.4 tag with a 16-byte body of `marker` padding.
            byte[] tag = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 16, .. Enumerable.Repeat(marker, 16)];
            return tag;
        }

        byte[] adts = [0xFF, 0xF1, 0x50, 0x80, 0x00, 0x1F, 0xFC, 0xAA, 0xBB];
        byte[] segment = [.. Tag(1), .. Tag(2), .. adts];

        var stripped = StreamSession.StripLeadingId3(segment);

        Assert.Equal(adts, stripped);
    }

    [Fact]
    public void StripLeadingId3LeavesBareStreamsAlone()
    {
        byte[] adts = [0xFF, 0xF1, 0x50, 0x80, 0x00, 0x1F, 0xFC];
        Assert.Same(adts, StreamSession.StripLeadingId3(adts));
    }

    // -- AudioPipe: the VLC-facing conduit --

    [Fact]
    public void AudioPipeDrainsThenReportsEof()
    {
        var pipe = new AudioPipe();
        Assert.True(pipe.Write([1, 2, 3], CancellationToken.None));
        Assert.True(pipe.Write([4], CancellationToken.None));
        pipe.Complete();

        Assert.False(pipe.Write([5], CancellationToken.None)); // completed pipe rejects writes

        Assert.True(pipe.TryRead(out var first, 100));
        Assert.Equal(new byte[] { 1, 2, 3 }, first);
        Assert.True(pipe.TryRead(out var second, 100));
        Assert.Equal(new byte[] { 4 }, second);

        Assert.False(pipe.TryRead(out _, 10));
        Assert.True(pipe.IsCompleted);
        Assert.Equal(4, pipe.TotalWritten);
    }
}
