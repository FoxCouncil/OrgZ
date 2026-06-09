// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Tests;

/// <summary>
/// Round-trip tests for <see cref="ITunesDbChunkTree"/>: parsing the iTunesDB
/// chunk tree and re-serializing it must be byte-identical, and Normalize must be
/// a no-op on a well-formed unmutated DB. Synthetic DBs mirror the layout the
/// reader tests use (header 188/96/92/248/24, list headers with the item count at
/// offset 8). A guarded test round-trips a real device DB when ORGZ_REAL_ITUNESDB
/// points at one.
///
/// Reference: https://www.ipodlinux.org/ITunesDB/
/// </summary>
public class ITunesDbChunkTreeTests
{
    [Fact]
    public void RoundTrip_is_byte_identical_for_tracks_db()
    {
        var original = BuildTracksDb(
        [
            new() { TrackId = 1, Title = "One",   Artist = "A", Album = "X", Location = ":iPod_Control:Music:F00:AAAA.mp3" },
            new() { TrackId = 2, Title = "Two",   Artist = "B", Location = ":iPod_Control:Music:F01:BBBB.m4a" },
            new() { TrackId = 3, Title = "東京",  Artist = "Sigur Rós" },
        ]);

        var doc = ITunesDbChunkTree.Parse(original);
        var rewritten = ITunesDbChunkTree.Serialize(doc);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void RoundTrip_is_byte_identical_with_playlists()
    {
        var original = BuildFullDb(
            tracks:
            [
                new() { TrackId = 10, Title = "A" },
                new() { TrackId = 20, Title = "B" },
            ],
            playlists:
            [
                new() { PlaylistId = 1, Name = "Library", IsMaster = true,  TrackIds = [10, 20] },
                new() { PlaylistId = 2, Name = "Mix",     IsMaster = false, TrackIds = [20] },
            ]);

        var doc = ITunesDbChunkTree.Parse(original);
        var rewritten = ITunesDbChunkTree.Serialize(doc);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void Normalize_is_a_noop_on_wellformed_db()
    {
        var original = BuildFullDb(
            tracks:
            [
                new() { TrackId = 10, Title = "A", Album = "Al" },
                new() { TrackId = 20, Title = "B", Artist = "Ar" },
                new() { TrackId = 30, Title = "C" },
            ],
            playlists:
            [
                new() { PlaylistId = 1, Name = "Library", IsMaster = true, TrackIds = [10, 20, 30] },
                new() { PlaylistId = 2, Name = "Road",    TrackIds = [30, 10] },
            ]);

        var doc = ITunesDbChunkTree.Parse(original);
        ITunesDbChunkTree.Normalize(doc.Root);
        var rewritten = ITunesDbChunkTree.Serialize(doc);

        Assert.Equal(original, rewritten);
    }

    [Fact]
    public void Parsed_then_reserialized_db_still_reads_back_identically()
    {
        var original = BuildFullDb(
            tracks:
            [
                new() { TrackId = 100, Title = "First",  Artist = "A1", Location = ":iPod_Control:Music:F12:ZZZZ.mp3" },
                new() { TrackId = 200, Title = "Second", Artist = "A2" },
            ],
            playlists:
            [
                new() { PlaylistId = 1, Name = "Library", IsMaster = true, TrackIds = [100, 200] },
                new() { PlaylistId = 7, Name = "Faves",   TrackIds = [200, 100] },
            ]);

        var dir = Path.Combine(Path.GetTempPath(), "OrgZ-itdb-tree-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "iTunesDB");
            var doc = ITunesDbChunkTree.Parse(original);
            ITunesDbChunkTree.Normalize(doc.Root);
            File.WriteAllBytes(path, ITunesDbChunkTree.Serialize(doc));

            ITunesDbReader.ReadAll(path, @"E:\", out var tracks, out var playlists);

            Assert.Equal([100u, 200u], tracks.Select(t => t.TrackId));
            Assert.Equal("First", tracks[0].Title);
            var faves = Assert.Single(playlists);          // master is skipped by the reader
            Assert.Equal("Faves", faves.Name);
            Assert.Equal([200u, 100u], faves.TrackIds);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Opaque_body_that_starts_like_a_chunk_is_preserved()
    {
        // An MHOD whose payload happens to begin with "mh.." but is NOT a valid
        // nested chunk (bogus header size) must be kept as an opaque body, not
        // mis-parsed as children. Proves the tiling guard.
        var fakey = new byte[20];
        WriteAscii(fakey, 0, "mhqq");   // looks like a chunk magic...
        WriteInt32(fakey, 4, 0);        // ...but header size 0 => invalid => opaque
        for (int i = 8; i < fakey.Length; i++) fakey[i] = (byte)(i * 7);

        var mhod = BuildRawMhod(mhodType: 999, body: fakey);
        var original = BuildTracksDb([new() { TrackId = 1, Title = "T" }], extraTrackMhods: [mhod]);

        var doc = ITunesDbChunkTree.Parse(original);
        Assert.Equal(original, ITunesDbChunkTree.Serialize(doc));

        // And the dodgy MHOD round-trips through Normalize too.
        ITunesDbChunkTree.Normalize(doc.Root);
        Assert.Equal(original, ITunesDbChunkTree.Serialize(doc));
    }

    [Fact]
    public void Parse_rejects_non_itunesdb_bytes()
    {
        Assert.Throws<InvalidDataException>(() => ITunesDbChunkTree.Parse([0x00, 0x01, 0x02, 0x03, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    /// <summary>
    /// Round-trips a real device iTunesDB when ORGZ_REAL_ITUNESDB points at one
    /// (e.g. a copy of E:\iPod_Control\iTunes\iTunesDB). Skipped otherwise so CI
    /// stays hermetic and no personal library data is committed.
    /// </summary>
    [Fact]
    public void RoundTrip_and_normalize_are_byte_identical_for_real_db()
    {
        var path = Environment.GetEnvironmentVariable("ORGZ_REAL_ITUNESDB");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;   // not provided - nothing to validate against
        }

        var original = File.ReadAllBytes(path);

        var doc = ITunesDbChunkTree.Parse(original);
        Assert.Equal(original, ITunesDbChunkTree.Serialize(doc));

        var doc2 = ITunesDbChunkTree.Parse(original);
        ITunesDbChunkTree.Normalize(doc2.Root);
        Assert.Equal(original, ITunesDbChunkTree.Serialize(doc2));
    }

    // ===== synthetic builders (mirror ITunesDbReaderTests' layout) =====

    private sealed class TestTrack
    {
        public uint TrackId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Location { get; set; }
    }

    private sealed class TestPlaylist
    {
        public uint PlaylistId { get; set; }
        public string Name { get; set; } = "Untitled";
        public bool IsMaster { get; set; }
        public List<uint> TrackIds { get; set; } = [];
    }

    private static byte[] BuildTracksDb(List<TestTrack> tracks, List<byte[]>? extraTrackMhods = null)
        => WrapMhbd([BuildTracksMhsd(tracks, extraTrackMhods)]);

    private static byte[] BuildFullDb(List<TestTrack> tracks, List<TestPlaylist> playlists)
        => WrapMhbd([BuildTracksMhsd(tracks, null), BuildPlaylistsMhsd(playlists)]);

    private static byte[] WrapMhbd(List<byte[]> mhsds)
    {
        const int headerSize = 188;
        int total = headerSize + mhsds.Sum(m => m.Length);
        var mhbd = new byte[total];
        WriteAscii(mhbd, 0, "mhbd");
        WriteInt32(mhbd, 4, headerSize);
        WriteInt32(mhbd, 8, total);
        int p = headerSize;
        foreach (var m in mhsds) { Buffer.BlockCopy(m, 0, mhbd, p, m.Length); p += m.Length; }
        return mhbd;
    }

    private static byte[] BuildTracksMhsd(List<TestTrack> tracks, List<byte[]>? extraTrackMhods)
    {
        var mhits = tracks.Select(t => BuildMhit(t, extraTrackMhods)).ToList();
        return BuildMhsd(type: 1, listMagic: "mhlt", itemCount: tracks.Count, items: mhits);
    }

    private static byte[] BuildPlaylistsMhsd(List<TestPlaylist> playlists)
    {
        var mhyps = playlists.Select(BuildMhyp).ToList();
        return BuildMhsd(type: 2, listMagic: "mhlp", itemCount: playlists.Count, items: mhyps);
    }

    private static byte[] BuildMhsd(int type, string listMagic, int itemCount, List<byte[]> items)
    {
        const int listHeaderSize = 92;
        const int mhsdHeaderSize = 96;
        int itemsTotal = items.Sum(i => i.Length);
        int total = mhsdHeaderSize + listHeaderSize + itemsTotal;

        var mhsd = new byte[total];
        WriteAscii(mhsd, 0, "mhsd");
        WriteInt32(mhsd, 4, mhsdHeaderSize);
        WriteInt32(mhsd, 8, total);
        WriteInt32(mhsd, 12, type);

        WriteAscii(mhsd, mhsdHeaderSize, listMagic);
        WriteInt32(mhsd, mhsdHeaderSize + 4, listHeaderSize);
        WriteInt32(mhsd, mhsdHeaderSize + 8, itemCount);

        int p = mhsdHeaderSize + listHeaderSize;
        foreach (var i in items) { Buffer.BlockCopy(i, 0, mhsd, p, i.Length); p += i.Length; }
        return mhsd;
    }

    private static byte[] BuildMhit(TestTrack t, List<byte[]>? extraMhods)
    {
        const int headerSize = 248;
        var mhods = new List<byte[]>();
        if (t.Title    != null) mhods.Add(BuildStringMhod(1, t.Title));
        if (t.Location != null) mhods.Add(BuildStringMhod(2, t.Location));
        if (t.Album    != null) mhods.Add(BuildStringMhod(3, t.Album));
        if (t.Artist   != null) mhods.Add(BuildStringMhod(4, t.Artist));
        if (extraMhods != null) mhods.AddRange(extraMhods);

        int total = headerSize + mhods.Sum(m => m.Length);
        var mhit = new byte[total];
        WriteAscii(mhit, 0, "mhit");
        WriteInt32(mhit, 4, headerSize);
        WriteInt32(mhit, 8, total);
        WriteInt32(mhit, 12, mhods.Count);
        WriteInt32(mhit, 16, (int)t.TrackId);

        int p = headerSize;
        foreach (var m in mhods) { Buffer.BlockCopy(m, 0, mhit, p, m.Length); p += m.Length; }
        return mhit;
    }

    private static byte[] BuildMhyp(TestPlaylist pl)
    {
        const int headerSize = 108;
        var name = BuildStringMhod(1, pl.Name);
        var mhips = pl.TrackIds.Select(BuildMhip).ToList();

        int total = headerSize + name.Length + mhips.Sum(m => m.Length);
        var mhyp = new byte[total];
        WriteAscii(mhyp, 0, "mhyp");
        WriteInt32(mhyp, 4, headerSize);
        WriteInt32(mhyp, 8, total);
        WriteInt32(mhyp, 12, 1);                    // MHOD count
        WriteInt32(mhyp, 16, pl.TrackIds.Count);    // MHIP count
        mhyp[20] = (byte)(pl.IsMaster ? 1 : 0);
        WriteInt32(mhyp, 0x1C, (int)pl.PlaylistId);

        int p = headerSize;
        Buffer.BlockCopy(name, 0, mhyp, p, name.Length); p += name.Length;
        foreach (var m in mhips) { Buffer.BlockCopy(m, 0, mhyp, p, m.Length); p += m.Length; }
        return mhyp;
    }

    private static byte[] BuildMhip(uint trackId)
    {
        const int headerSize = 76;
        var mhip = new byte[headerSize];
        WriteAscii(mhip, 0, "mhip");
        WriteInt32(mhip, 4, headerSize);
        WriteInt32(mhip, 8, headerSize);
        WriteInt32(mhip, 12, 0);
        WriteInt32(mhip, 0x18, (int)trackId);
        return mhip;
    }

    private static byte[] BuildStringMhod(int mhodType, string text)
    {
        const int headerSize = 24;
        var utf16 = Encoding.Unicode.GetBytes(text);
        int total = headerSize + 16 + utf16.Length;
        var mhod = new byte[total];
        WriteAscii(mhod, 0, "mhod");
        WriteInt32(mhod, 4, headerSize);
        WriteInt32(mhod, 8, total);
        WriteInt32(mhod, 12, mhodType);
        WriteInt32(mhod, 24, 1);                // position
        WriteInt32(mhod, 28, utf16.Length);     // byte length
        Buffer.BlockCopy(utf16, 0, mhod, 40, utf16.Length);
        return mhod;
    }

    private static byte[] BuildRawMhod(int mhodType, byte[] body)
    {
        const int headerSize = 24;
        int total = headerSize + body.Length;
        var mhod = new byte[total];
        WriteAscii(mhod, 0, "mhod");
        WriteInt32(mhod, 4, headerSize);
        WriteInt32(mhod, 8, total);
        WriteInt32(mhod, 12, mhodType);
        Buffer.BlockCopy(body, 0, mhod, headerSize, body.Length);
        return mhod;
    }

    private static void WriteAscii(byte[] dest, int offset, string magic)
    {
        for (int i = 0; i < magic.Length; i++) dest[offset + i] = (byte)magic[i];
    }

    private static void WriteInt32(byte[] dest, int offset, int value)
    {
        dest[offset]     = (byte)(value & 0xFF);
        dest[offset + 1] = (byte)((value >> 8) & 0xFF);
        dest[offset + 2] = (byte)((value >> 16) & 0xFF);
        dest[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
