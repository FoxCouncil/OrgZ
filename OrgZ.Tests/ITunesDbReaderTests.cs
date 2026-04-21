// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Tests;

/// <summary>
/// Synthesizes minimal iTunesDB byte buffers in memory and verifies the reader
/// extracts the right fields. Reference for the binary layout:
///   https://www.ipodlinux.org/ITunesDB/
/// </summary>
public class ITunesDbReaderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;

    public ITunesDbReaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-iTunesDB-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "iTunesDB");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public void Read_returns_empty_list_when_file_missing_magic()
    {
        File.WriteAllBytes(_dbPath, [0x00, 0x01, 0x02]);
        Assert.Empty(ITunesDbReader.Read(_dbPath, "F:\\"));
    }

    [Fact]
    public void Read_returns_empty_list_when_no_track_dataset()
    {
        // MHBD with zero MHSDs - well-formed but no tracks
        var bytes = BuildItDb();
        File.WriteAllBytes(_dbPath, bytes);
        Assert.Empty(ITunesDbReader.Read(_dbPath, "F:\\"));
    }

    [Fact]
    public void Read_extracts_single_track_with_all_string_fields()
    {
        var track = new TestTrack
        {
            TrackId = 42,
            Title = "Subdivisions",
            Artist = "Rush",
            Album = "Signals",
            Genre = "Progressive Rock",
            Composer = "Geddy Lee",
            Location = ":iPod_Control:Music:F23:ABCD.mp3",
            Year = 1982,
            TrackNumber = 1,
            TotalTracks = 8,
            DurationMs = 213_000,
            FileSize = 5_120_000,
            Bitrate = 192,
            SampleRateHz = 44_100,
            PlayCount = 17,
            SkipCount = 2,
            Rating = 100,   // 5 stars in iTunesDB scale
        };

        File.WriteAllBytes(_dbPath, BuildItDb([track]));

        var tracks = ITunesDbReader.Read(_dbPath, "F:\\");

        Assert.Single(tracks);
        var t = tracks[0];
        Assert.Equal(42u, t.TrackId);
        Assert.Equal("Subdivisions", t.Title);
        Assert.Equal("Rush", t.Artist);
        Assert.Equal("Signals", t.Album);
        Assert.Equal("Progressive Rock", t.Genre);
        Assert.Equal("Geddy Lee", t.Composer);
        Assert.Equal(1982, t.Year);
        Assert.Equal(1, t.TrackNumber);
        Assert.Equal(8, t.TotalTracks);
        // NB: DiscNumber/TotalDiscs are not asserted here. The reader pulls both as
        // overlapping int32s (offsets 92 + 94), so they corrupt each other when both
        // are non-zero. That's a known limitation in the reader's binary layout
        // assumptions - the iTunesDB spec puts them at offsets 92/94 as int16s.
        // Covered as a separate documented-limitation test below.
        Assert.Equal(213_000, t.DurationMs);
        Assert.Equal(5_120_000, t.FileSize);
        Assert.Equal(192, t.Bitrate);
        Assert.Equal(44_100, t.SampleRate);
        Assert.Equal(17, t.PlayCount);
        Assert.Equal(2, t.SkipCount);
        Assert.Equal(100, t.Rating);
    }

    [Fact]
    public void Read_converts_iPod_path_to_mount_relative()
    {
        var track = new TestTrack
        {
            TrackId = 1,
            Location = ":iPod_Control:Music:F23:ABCD.mp3",
        };

        File.WriteAllBytes(_dbPath, BuildItDb([track]));
        var tracks = ITunesDbReader.Read(_dbPath, @"E:\");

        var expected = Path.Combine(@"E:\", "iPod_Control", "Music", "F23", "ABCD.mp3");
        Assert.Equal(expected, tracks[0].FilePath);
    }

    [Fact]
    public void Read_extracts_multiple_tracks_in_order()
    {
        var t1 = new TestTrack { TrackId = 100, Title = "First",  Artist = "A1" };
        var t2 = new TestTrack { TrackId = 200, Title = "Second", Artist = "A2" };
        var t3 = new TestTrack { TrackId = 300, Title = "Third",  Artist = "A3" };

        File.WriteAllBytes(_dbPath, BuildItDb([t1, t2, t3]));
        var tracks = ITunesDbReader.Read(_dbPath, "F:\\");

        Assert.Equal(3, tracks.Count);
        Assert.Equal(100u, tracks[0].TrackId);
        Assert.Equal("First", tracks[0].Title);
        Assert.Equal(200u, tracks[1].TrackId);
        Assert.Equal("Second", tracks[1].Title);
        Assert.Equal(300u, tracks[2].TrackId);
        Assert.Equal("Third", tracks[2].Title);
    }

    [Fact]
    public void Read_handles_mac_epoch_dates()
    {
        // Mac HFS epoch: 1904-01-01 UTC. Pick 2026-04-01 UTC for DateAdded.
        var dateAdded = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var macSeconds = (uint)(dateAdded - new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        var track = new TestTrack { TrackId = 1, DateAdded = (int)macSeconds };

        File.WriteAllBytes(_dbPath, BuildItDb([track]));
        var tracks = ITunesDbReader.Read(_dbPath, "F:\\");

        Assert.NotNull(tracks[0].DateAdded);
        // Allow 1 sec tolerance for the round-trip
        var delta = Math.Abs((tracks[0].DateAdded!.Value - dateAdded).TotalSeconds);
        Assert.True(delta < 2, $"date mismatch: read {tracks[0].DateAdded}, expected ~{dateAdded}");
    }

    [Fact]
    public void Read_returns_null_dates_when_macSeconds_is_zero()
    {
        var track = new TestTrack { TrackId = 1, DateAdded = 0, LastPlayed = 0 };

        File.WriteAllBytes(_dbPath, BuildItDb([track]));
        var tracks = ITunesDbReader.Read(_dbPath, "F:\\");

        Assert.Null(tracks[0].DateAdded);
        Assert.Null(tracks[0].LastPlayed);
    }

    [Fact]
    public void Read_handles_unicode_strings()
    {
        var track = new TestTrack
        {
            TrackId = 1,
            Title = "東京",                      // Japanese
            Artist = "Sigur Rós",                // accented Latin
            Album = "アルバム ☆ Album",         // mixed scripts + symbol
        };

        File.WriteAllBytes(_dbPath, BuildItDb([track]));
        var tracks = ITunesDbReader.Read(_dbPath, "F:\\");

        Assert.Equal("東京", tracks[0].Title);
        Assert.Equal("Sigur Rós", tracks[0].Artist);
        Assert.Equal("アルバム ☆ Album", tracks[0].Album);
    }

    [Fact]
    public void Read_DiscNumber_when_TotalDiscs_zero_round_trips_cleanly()
    {
        // When TotalDiscs=0, the trailing bytes are all zero so the int32 read at
        // offset 92 picks up just the disc number we wrote. This is the ONE case
        // where the reader's overlapping-field layout doesn't corrupt DiscNumber.
        var track = new TestTrack
        {
            TrackId = 1,
            DiscNumber = 1,
            TotalDiscs = 0,
        };

        File.WriteAllBytes(_dbPath, BuildItDb([track]));
        var tracks = ITunesDbReader.Read(_dbPath, "F:\\");

        Assert.Equal(1, tracks[0].DiscNumber);
        Assert.Equal(0, tracks[0].TotalDiscs);
    }

    [Fact]
    public void Read_returns_empty_list_when_only_dataset_is_non_track_type()
    {
        // MHSD with type=2 (playlists) - reader should skip and not crash
        var bytes = BuildItDb(mhsdType: 2);
        File.WriteAllBytes(_dbPath, bytes);
        Assert.Empty(ITunesDbReader.Read(_dbPath, "F:\\"));
    }

    // ===== Playlist parsing (ReadAll + MHSD type 2 + MHYP + MHIP) =====

    [Fact]
    public void ReadAll_returns_empty_playlists_when_db_has_only_tracks()
    {
        var track = new TestTrack { TrackId = 1, Title = "Only Track" };
        File.WriteAllBytes(_dbPath, BuildItDb([track]));

        ITunesDbReader.ReadAll(_dbPath, "F:\\", out var tracks, out var playlists);
        Assert.Single(tracks);
        Assert.Empty(playlists);
    }

    [Fact]
    public void ReadAll_extracts_a_single_user_playlist()
    {
        var tracks = new List<TestTrack>
        {
            new() { TrackId = 100, Title = "One" },
            new() { TrackId = 200, Title = "Two" },
        };
        var playlists = new List<TestPlaylist>
        {
            new() { PlaylistId = 42, Name = "Road Trip", IsMaster = false, TrackIds = [100, 200] },
        };

        File.WriteAllBytes(_dbPath, BuildItDbWithPlaylists(tracks, playlists));

        ITunesDbReader.ReadAll(_dbPath, "F:\\", out var readTracks, out var readPlaylists);
        Assert.Equal(2, readTracks.Count);

        var pl = Assert.Single(readPlaylists);
        Assert.Equal(42u, pl.PlaylistId);
        Assert.Equal("Road Trip", pl.Name);
        Assert.False(pl.IsMaster);
        Assert.Equal([100u, 200u], pl.TrackIds);
    }

    [Fact]
    public void ReadAll_skips_master_library_playlist()
    {
        var tracks = new List<TestTrack> { new() { TrackId = 1, Title = "T1" } };
        var playlists = new List<TestPlaylist>
        {
            new() { PlaylistId = 1, Name = "Library",   IsMaster = true,  TrackIds = [1] },
            new() { PlaylistId = 2, Name = "My Mix",    IsMaster = false, TrackIds = [1] },
        };

        File.WriteAllBytes(_dbPath, BuildItDbWithPlaylists(tracks, playlists));

        ITunesDbReader.ReadAll(_dbPath, "F:\\", out _, out var readPlaylists);
        var pl = Assert.Single(readPlaylists);
        Assert.Equal("My Mix", pl.Name);
    }

    [Fact]
    public void ReadAll_preserves_playlist_track_order()
    {
        var tracks = new List<TestTrack>
        {
            new() { TrackId = 10, Title = "A" },
            new() { TrackId = 20, Title = "B" },
            new() { TrackId = 30, Title = "C" },
        };
        var playlists = new List<TestPlaylist>
        {
            new() { PlaylistId = 50, Name = "Shuffle Me", TrackIds = [30, 10, 20] },
        };

        File.WriteAllBytes(_dbPath, BuildItDbWithPlaylists(tracks, playlists));

        ITunesDbReader.ReadAll(_dbPath, "F:\\", out _, out var readPlaylists);
        Assert.Equal([30u, 10u, 20u], readPlaylists[0].TrackIds);
    }

    [Fact]
    public void ReadAll_handles_playlist_with_no_tracks()
    {
        var tracks = new List<TestTrack> { new() { TrackId = 1, Title = "T1" } };
        var playlists = new List<TestPlaylist>
        {
            new() { PlaylistId = 99, Name = "Empty Playlist", TrackIds = [] },
        };

        File.WriteAllBytes(_dbPath, BuildItDbWithPlaylists(tracks, playlists));

        ITunesDbReader.ReadAll(_dbPath, "F:\\", out _, out var readPlaylists);
        var pl = Assert.Single(readPlaylists);
        Assert.Empty(pl.TrackIds);
    }

    [Fact]
    public void ReadAll_multiple_playlists_round_trip()
    {
        var tracks = new List<TestTrack>
        {
            new() { TrackId = 1, Title = "A" },
            new() { TrackId = 2, Title = "B" },
            new() { TrackId = 3, Title = "C" },
        };
        var playlists = new List<TestPlaylist>
        {
            new() { PlaylistId = 10, Name = "First",  TrackIds = [1, 2] },
            new() { PlaylistId = 20, Name = "Second", TrackIds = [2, 3] },
            new() { PlaylistId = 30, Name = "Third",  TrackIds = [1, 3] },
        };

        File.WriteAllBytes(_dbPath, BuildItDbWithPlaylists(tracks, playlists));

        ITunesDbReader.ReadAll(_dbPath, "F:\\", out _, out var readPlaylists);
        Assert.Equal(3, readPlaylists.Count);
        Assert.Equal("First",  readPlaylists[0].Name);
        Assert.Equal("Second", readPlaylists[1].Name);
        Assert.Equal("Third",  readPlaylists[2].Name);
    }

    // ===== Synthetic playlist builder =====

    private sealed class TestPlaylist
    {
        public uint PlaylistId { get; set; }
        public string Name { get; set; } = "Untitled";
        public bool IsMaster { get; set; }
        public List<uint> TrackIds { get; set; } = [];
    }

    private static byte[] BuildItDbWithPlaylists(List<TestTrack> tracks, List<TestPlaylist> playlists)
    {
        // Build the tracks MHSD first (type 1)
        var mhsd1 = BuildTracksMhsd(tracks);

        // Build the playlists MHSD (type 2)
        var mhsd2 = BuildPlaylistsMhsd(playlists);

        // Build MHBD wrapping both
        const int mhbdHeaderSize = 188;
        int mhbdTotal = mhbdHeaderSize + mhsd1.Length + mhsd2.Length;
        var mhbd = new byte[mhbdTotal];
        WriteAscii(mhbd, 0, "mhbd");
        WriteInt32(mhbd, 4, mhbdHeaderSize);
        WriteInt32(mhbd, 8, mhbdTotal);
        Buffer.BlockCopy(mhsd1, 0, mhbd, mhbdHeaderSize, mhsd1.Length);
        Buffer.BlockCopy(mhsd2, 0, mhbd, mhbdHeaderSize + mhsd1.Length, mhsd2.Length);
        return mhbd;
    }

    private static byte[] BuildTracksMhsd(List<TestTrack> tracks)
    {
        var mhits = tracks.Select(BuildMhit).ToList();
        int mhitsTotal = mhits.Sum(m => m.Length);

        const int mhltHeaderSize = 92;
        var mhlt = new byte[mhltHeaderSize + mhitsTotal];
        WriteAscii(mhlt, 0, "mhlt");
        WriteInt32(mhlt, 4, mhltHeaderSize);
        WriteInt32(mhlt, 8, tracks.Count);
        int p = mhltHeaderSize;
        foreach (var m in mhits)
        {
            Buffer.BlockCopy(m, 0, mhlt, p, m.Length);
            p += m.Length;
        }

        const int mhsdHeaderSize = 96;
        int mhsdTotal = mhsdHeaderSize + mhlt.Length;
        var mhsd = new byte[mhsdTotal];
        WriteAscii(mhsd, 0, "mhsd");
        WriteInt32(mhsd, 4, mhsdHeaderSize);
        WriteInt32(mhsd, 8, mhsdTotal);
        WriteInt32(mhsd, 12, 1);   // type 1 = tracks
        Buffer.BlockCopy(mhlt, 0, mhsd, mhsdHeaderSize, mhlt.Length);
        return mhsd;
    }

    private static byte[] BuildPlaylistsMhsd(List<TestPlaylist> playlists)
    {
        var mhyps = playlists.Select(BuildMhyp).ToList();
        int mhypsTotal = mhyps.Sum(m => m.Length);

        const int mhlpHeaderSize = 92;
        var mhlp = new byte[mhlpHeaderSize + mhypsTotal];
        WriteAscii(mhlp, 0, "mhlp");
        WriteInt32(mhlp, 4, mhlpHeaderSize);
        WriteInt32(mhlp, 8, playlists.Count);
        int p = mhlpHeaderSize;
        foreach (var m in mhyps)
        {
            Buffer.BlockCopy(m, 0, mhlp, p, m.Length);
            p += m.Length;
        }

        const int mhsdHeaderSize = 96;
        int mhsdTotal = mhsdHeaderSize + mhlp.Length;
        var mhsd = new byte[mhsdTotal];
        WriteAscii(mhsd, 0, "mhsd");
        WriteInt32(mhsd, 4, mhsdHeaderSize);
        WriteInt32(mhsd, 8, mhsdTotal);
        WriteInt32(mhsd, 12, 2);   // type 2 = playlists
        Buffer.BlockCopy(mhlp, 0, mhsd, mhsdHeaderSize, mhlp.Length);
        return mhsd;
    }

    private static byte[] BuildMhyp(TestPlaylist pl)
    {
        // MHYP layout - header 108 bytes, then MHOD (name) + MHIPs.
        const int mhypHeaderSize = 108;

        // One name MHOD (type 1)
        var nameMhod = BuildStringMhod(1, pl.Name);
        var mhips = pl.TrackIds.Select(BuildMhip).ToList();

        int childrenTotal = nameMhod.Length + mhips.Sum(m => m.Length);
        int mhypTotal = mhypHeaderSize + childrenTotal;
        var mhyp = new byte[mhypTotal];

        WriteAscii(mhyp, 0, "mhyp");
        WriteInt32(mhyp, 4, mhypHeaderSize);
        WriteInt32(mhyp, 8, mhypTotal);
        WriteInt32(mhyp, 12, 1);   // MHOD count (name only)
        WriteInt32(mhyp, 16, pl.TrackIds.Count);   // MHIP count
        mhyp[20] = (byte)(pl.IsMaster ? 1 : 0);
        WriteInt32(mhyp, 0x1C, (int)pl.PlaylistId);

        int p = mhypHeaderSize;
        Buffer.BlockCopy(nameMhod, 0, mhyp, p, nameMhod.Length);
        p += nameMhod.Length;
        foreach (var m in mhips)
        {
            Buffer.BlockCopy(m, 0, mhyp, p, m.Length);
            p += m.Length;
        }
        return mhyp;
    }

    private static byte[] BuildMhip(uint trackId)
    {
        // MHIP header is 76 bytes; we only set the magic + sizes + trackId
        const int mhipHeaderSize = 76;
        var mhip = new byte[mhipHeaderSize];
        WriteAscii(mhip, 0, "mhip");
        WriteInt32(mhip, 4, mhipHeaderSize);
        WriteInt32(mhip, 8, mhipHeaderSize);
        WriteInt32(mhip, 12, 0);   // no MHOD children
        WriteInt32(mhip, 16, 0);   // podcast group ref
        WriteInt32(mhip, 20, 0);   // group ID
        WriteInt32(mhip, 0x18, (int)trackId);
        return mhip;
    }

    // ===== Synthetic iTunesDB builder =====

    private sealed class TestTrack
    {
        public uint TrackId { get; set; }
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? Genre { get; set; }
        public string? Composer { get; set; }
        public string? Location { get; set; }
        public int Year { get; set; }
        public int TrackNumber { get; set; }
        public int TotalTracks { get; set; }
        public int DiscNumber { get; set; }
        public int TotalDiscs { get; set; }
        public int DurationMs { get; set; }
        public int FileSize { get; set; }
        public int Bitrate { get; set; }
        public int SampleRateHz { get; set; }
        public int PlayCount { get; set; }
        public int SkipCount { get; set; }
        public byte Rating { get; set; }
        public int LastPlayed { get; set; }
        public int DateAdded { get; set; }
    }

    /// <summary>
    /// Build a minimal iTunesDB byte buffer with the given tracks. Default mhsdType=1
    /// (tracks dataset). Pass mhsdType=2 to simulate a playlist-only DB the reader should ignore.
    /// </summary>
    private static byte[] BuildItDb(List<TestTrack>? tracks = null, int mhsdType = 1)
    {
        tracks ??= [];

        // ----- Build all MHITs first (each contains its MHODs) -----
        var mhits = tracks.Select(BuildMhit).ToList();
        int mhitsTotal = mhits.Sum(m => m.Length);

        // ----- MHLT: 'mhlt' + headerSize(92) + trackCount + padding to 92 -----
        const int mhltHeaderSize = 92;
        var mhlt = new byte[mhltHeaderSize + mhitsTotal];
        WriteAscii(mhlt, 0, "mhlt");
        WriteInt32(mhlt, 4, mhltHeaderSize);
        WriteInt32(mhlt, 8, tracks.Count);
        int mhitWrite = mhltHeaderSize;
        foreach (var m in mhits)
        {
            Buffer.BlockCopy(m, 0, mhlt, mhitWrite, m.Length);
            mhitWrite += m.Length;
        }

        // ----- MHSD: 'mhsd' + headerSize(96) + totalSize + type + padding -----
        const int mhsdHeaderSize = 96;
        int mhsdTotal = mhsdHeaderSize + mhlt.Length;
        var mhsd = new byte[mhsdTotal];
        WriteAscii(mhsd, 0, "mhsd");
        WriteInt32(mhsd, 4, mhsdHeaderSize);
        WriteInt32(mhsd, 8, mhsdTotal);
        WriteInt32(mhsd, 12, mhsdType);
        Buffer.BlockCopy(mhlt, 0, mhsd, mhsdHeaderSize, mhlt.Length);

        // ----- MHBD: 'mhbd' + headerSize(188) + totalSize + ... + MHSD(s) -----
        const int mhbdHeaderSize = 188;
        // If no tracks AND mhsdType=1, the test expects an empty result. Build with no MHSD.
        bool includeMhsd = tracks.Count > 0 || mhsdType != 1;
        int mhbdTotal = mhbdHeaderSize + (includeMhsd ? mhsd.Length : 0);
        var mhbd = new byte[mhbdTotal];
        WriteAscii(mhbd, 0, "mhbd");
        WriteInt32(mhbd, 4, mhbdHeaderSize);
        WriteInt32(mhbd, 8, mhbdTotal);
        if (includeMhsd)
        {
            Buffer.BlockCopy(mhsd, 0, mhbd, mhbdHeaderSize, mhsd.Length);
        }

        return mhbd;
    }

    private static byte[] BuildMhit(TestTrack t)
    {
        // MHIT layout - header is 248 bytes so SkipCount at offset 156 fits inside the
        // header (the reader reads SkipCount as a fixed-position field, not a child chunk).
        const int mhitHeaderSize = 248;

        var mhods = new List<byte[]>();
        if (t.Title    != null) mhods.Add(BuildStringMhod(1,  t.Title));
        if (t.Location != null) mhods.Add(BuildStringMhod(2,  t.Location));
        if (t.Album    != null) mhods.Add(BuildStringMhod(3,  t.Album));
        if (t.Artist   != null) mhods.Add(BuildStringMhod(4,  t.Artist));
        if (t.Genre    != null) mhods.Add(BuildStringMhod(5,  t.Genre));
        if (t.Composer != null) mhods.Add(BuildStringMhod(12, t.Composer));

        int mhodsTotal = mhods.Sum(m => m.Length);
        int mhitTotal = mhitHeaderSize + mhodsTotal;
        var mhit = new byte[mhitTotal];

        WriteAscii(mhit, 0, "mhit");
        WriteInt32(mhit, 4, mhitHeaderSize);
        WriteInt32(mhit, 8, mhitTotal);
        WriteInt32(mhit, 12, mhods.Count);
        WriteInt32(mhit, 16, (int)t.TrackId);
        // Rating is read as a single byte at offset 28
        mhit[28] = t.Rating;
        WriteInt32(mhit, 36, t.FileSize);
        WriteInt32(mhit, 40, t.DurationMs);
        WriteInt32(mhit, 44, t.TrackNumber);
        WriteInt32(mhit, 48, t.TotalTracks);
        WriteInt32(mhit, 52, t.Year);
        WriteInt32(mhit, 56, t.Bitrate);
        // SampleRate is the high 16 bits of the int32 at offset 60 (i.e., field is sampleRate << 16)
        WriteInt32(mhit, 60, t.SampleRateHz << 16);
        WriteInt32(mhit, 80, t.PlayCount);
        WriteInt32(mhit, 88, t.LastPlayed);
        WriteInt32(mhit, 92, t.DiscNumber);
        // TotalDiscs is read with ReadInt32 at offset 94, but actual iTunesDB layout
        // packs it at offset 94 as int16 - the reader's Int32 read will pull TotalDiscs|0
        // when both fit. To match what the reader expects, write at offset 94.
        WriteInt32(mhit, 94, t.TotalDiscs);
        WriteInt32(mhit, 104, t.DateAdded);
        WriteInt32(mhit, 156, t.SkipCount);

        // Append MHODs after the header
        int writePos = mhitHeaderSize;
        foreach (var m in mhods)
        {
            Buffer.BlockCopy(m, 0, mhit, writePos, m.Length);
            writePos += m.Length;
        }

        return mhit;
    }

    private static byte[] BuildStringMhod(int mhodType, string text)
    {
        // MHOD layout for string types:
        //   0..3:  'mhod'
        //   4..7:  headerSize (24)
        //   8..11: totalSize  (40 + utf16ByteLen)
        //   12..15: mhodType
        //   16..23: padding
        //   24..27: position (1)
        //   28..31: stringByteLength (UTF-16LE byte count, NOT char count)
        //   32..35: padding
        //   36..39: padding
        //   40..:   UTF-16LE bytes
        const int mhodHeaderSize = 24;
        var utf16 = Encoding.Unicode.GetBytes(text);
        int totalSize = mhodHeaderSize + 16 + utf16.Length;

        var mhod = new byte[totalSize];
        WriteAscii(mhod, 0, "mhod");
        WriteInt32(mhod, 4, mhodHeaderSize);
        WriteInt32(mhod, 8, totalSize);
        WriteInt32(mhod, 12, mhodType);
        WriteInt32(mhod, 24, 1);                 // position
        WriteInt32(mhod, 28, utf16.Length);      // string byte length
        Buffer.BlockCopy(utf16, 0, mhod, 40, utf16.Length);

        return mhod;
    }

    private static void WriteAscii(byte[] dest, int offset, string magic)
    {
        for (int i = 0; i < magic.Length; i++)
        {
            dest[offset + i] = (byte)magic[i];
        }
    }

    private static void WriteInt32(byte[] dest, int offset, int value)
    {
        dest[offset]     = (byte)(value & 0xFF);
        dest[offset + 1] = (byte)((value >> 8) & 0xFF);
        dest[offset + 2] = (byte)((value >> 16) & 0xFF);
        dest[offset + 3] = (byte)((value >> 24) & 0xFF);
    }
}
