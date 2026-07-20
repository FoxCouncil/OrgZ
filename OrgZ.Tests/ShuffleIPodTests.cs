// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The iPod Shuffle 1G/2G tier: the classic big-endian <c>iTunesSD</c> writer/reader and full CRUD through
/// the <see cref="IPodDevice"/> interface. Everything runs on a temp directory standing in for the mount -
/// no hardware, no fixture. Byte layout follows libgpod's <c>itdb_shuffle_write_file</c>.
/// </summary>
public class ShuffleIPodTests
{
    [Fact]
    public void ITunesSD_layout_is_correct_and_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-sd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ShuffleSdWriter.Write(dir,
            [
                new ShuffleSdTrack("/iPod_Control/Music/F00/Song.mp3", 1),
                new ShuffleSdTrack("/iPod_Control/Music/F00/Two.wav", 4),
            ]);

            var b = File.ReadAllBytes(Path.Combine(dir, "iTunesSD"));
            Assert.Equal(18 + 2 * 558, b.Length);                 // 18-byte header + two 558-byte entries
            Assert.Equal(2, Be24(b, 0));                          // num_songs (big-endian)
            Assert.Equal(0x010800, Be24(b, 3));                   // magic (what iTunes writes - see Fixtures/itunessd)
            Assert.Equal(0x12, Be24(b, 6));                       // header size
            Assert.Equal(0x22E, Be24(b, 18));                     // first entry size = 558
            Assert.Equal(0x5AA501, Be24(b, 21));                  // entry magic

            var read = ShuffleSdWriter.Read(dir);
            Assert.Equal(2, read.Count);
            Assert.Equal("/iPod_Control/Music/F00/Song.mp3", read[0].IpodPath);
            Assert.Equal(1, read[0].FileType);
            Assert.Equal("/iPod_Control/Music/F00/Two.wav", read[1].IpodPath);
            Assert.Equal(4, read[1].FileType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("Shuffle 2G")]   // classic big-endian iTunesSD
    [InlineData("Shuffle 4G")]   // newer little-endian "bdhs" iTunesSD
    public async Task Add_read_remove_and_erase_through_the_interface(string generation)
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-shuf-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-shufsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(srcDir);
        try
        {
            var songSrc = Path.Combine(srcDir, "song.mp3");
            File.WriteAllBytes(songSrc, new byte[500]);

            // "Shuffle 2G" → iTunesSD tier → ShuffleIPod (via the real factory).
            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, IpodGeneration = generation, Name = "Shuffle" };
            var ipod = IPodDevice.For(device);
            Assert.IsType<ShuffleIPod>(ipod);
            Assert.False(ipod.HasKindSubViews);   // one flat list - no Podcasts/Audiobooks sidebar children

            var source = new MediaItem { Id = songSrc, FilePath = songSrc, FileName = "song.mp3", Title = "Song", Kind = MediaKind.Music };

            // ── ADD: copies to Music/F00 (iTunes-style 4-caps name) and lists it in iTunesSD ──
            var item = await ipod.AddTrackAsync(source, "ffmpeg");
            Assert.Equal(Path.Combine(mount, "iPod_Control", "Music", "F00"), Path.GetDirectoryName(item.FilePath));
            Assert.Matches(@"^[A-Z]{4}\.mp3$", Path.GetFileName(item.FilePath)!);
            Assert.True(File.Exists(item.FilePath));

            // ── READ ──
            var lib = await ipod.ReadLibraryAsync();
            Assert.Single(lib.Tracks);
            Assert.Equal(item.FilePath, lib.Tracks[0].FilePath);

            // ── DELETE: gone from iTunesSD and from disk ──
            await ipod.RemoveTrackAsync(item);
            Assert.False(File.Exists(item.FilePath));
            Assert.Empty((await ipod.ReadLibraryAsync()).Tracks);

            // ── ERASE: re-add, then wipe the device empty ──
            await ipod.AddTrackAsync(source, "ffmpeg");
            int removed = await ipod.EraseAsync();
            Assert.True(removed >= 1);
            Assert.Empty((await ipod.ReadLibraryAsync()).Tracks);
            Assert.Empty(Directory.GetFiles(Path.Combine(mount, "iPod_Control", "Music"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>
    /// The known-answer test against real Apple output: <c>Fixtures/itunessd/shuffle2g-itunes.itunessd</c>
    /// is a verbatim capture of the iTunesSD Mac iTunes wrote to a real Shuffle 2G (A1204). Our reader must
    /// parse it, and our writer must reproduce it byte-for-byte from the parsed list - any drift from
    /// Apple's real bytes fails here before it ever reaches hardware.
    /// </summary>
    [Fact]
    public void Real_iTunes_shuffle2g_iTunesSD_parses_and_round_trips_byte_identically()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunessd", "shuffle2g-itunes.itunessd");
        var dir = Path.Combine(Path.GetTempPath(), "orgz-sdkat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var original = File.ReadAllBytes(fixture);
            File.WriteAllBytes(Path.Combine(dir, "iTunesSD"), original);

            var tracks = ShuffleSdWriter.Read(dir);
            Assert.Equal(21, tracks.Count);
            Assert.Equal("/iPod_Control/Music/F02/RLED.m4a", tracks[0].IpodPath);
            Assert.All(tracks, t =>
            {
                Assert.Equal(2, t.FileType);       // every synced file is AAC
                Assert.Equal(0, t.Volume);         // iTunes writes 0, not the spec's "100 = neutral"
                Assert.Equal(0, t.StartTimeMs);
                Assert.Equal(0, t.StopTimeMs);
                Assert.True(t.PlayInShuffle);
                Assert.False(t.Bookmarkable);
                Assert.StartsWith("/iPod_Control/Music/F0", t.IpodPath);
            });

            var outDir = Path.Combine(dir, "rewrite");
            ShuffleSdWriter.Write(outDir, tracks);
            Assert.Equal(original, File.ReadAllBytes(Path.Combine(outDir, "iTunesSD")));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// iTunes writes a full iTunesDB beside the iTunesSD on every Shuffle it syncs - that's where titles and
    /// artists live. ReadLibrary must join SD entries to DB rows by device path and fall back to the file
    /// itself for entries the DB doesn't know (OrgZ-copied tracks).
    /// </summary>
    [Fact]
    public async Task ReadLibrary_joins_iTunesDB_metadata_and_falls_back_without_a_row()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-shufdb-" + Guid.NewGuid().ToString("N"));
        var iTunesDir = Path.Combine(mount, "iPod_Control", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        try
        {
            ShuffleSdWriter.Write(iTunesDir,
            [
                new ShuffleSdTrack("/iPod_Control/Music/F00/AAAA.m4a", 2),
                new ShuffleSdTrack("/iPod_Control/Music/F00/BBBB.mp3", 1),
            ]);

            var doc = ITunesDbWriter.CreateEmpty();
            ITunesDbWriter.AddTrack(doc, new NewTrack
            {
                TrackId = 1,
                IpodPath = ":iPod_Control:Music:F00:AAAA.m4a",
                Title = "Real Title",
                Artist = "Real Artist",
                Album = "Real Album",
                LengthMs = 123000,
            });
            ITunesDbChunkTree.Normalize(doc.Root);
            File.WriteAllBytes(Path.Combine(iTunesDir, "iTunesDB"), ITunesDbChunkTree.Serialize(doc));

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, IpodGeneration = "Shuffle 2G", Name = "Shuffle" };
            var lib = await IPodDevice.For(device).ReadLibraryAsync();

            Assert.Equal(2, lib.Tracks.Count);

            // SD order is the device's play order - the DB join must not reorder it.
            var joined = lib.Tracks[0];
            Assert.Equal("Real Title", joined.Title);
            Assert.Equal("Real Artist", joined.Artist);
            Assert.Equal("Real Album", joined.Album);
            Assert.Equal($"device:{mount}:1", joined.Id);
            Assert.Equal(Path.Combine(mount, "iPod_Control", "Music", "F00", "AAAA.m4a"), joined.FilePath);

            var fallback = lib.Tracks[1];
            Assert.Equal("BBBB", fallback.Title);   // no DB row, no readable file - filename is all we have
            Assert.True(fallback.IsAnalyzed);
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }

    /// <summary>
    /// Music lands under iTunes-style random 4-caps names - the only alphabet the 2006 firmware has
    /// ever parsed. Hardware-confirmed necessity: one U+2019 apostrophe in an iTunesSD path made the
    /// 2G silently skip the track (the same bytes played once ASCII-renamed).
    /// </summary>
    [Fact]
    public async Task Music_lands_with_itunes_style_four_caps_names()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-shufascii-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-shufasciisrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(srcDir);
        try
        {
            var songSrc = Path.Combine(srcDir, "08 - Where It’s Ät — Béck ♥.mp3");
            File.WriteAllBytes(songSrc, new byte[500]);

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, IpodGeneration = "Shuffle 2G", Name = "Shuffle" };
            var item = await IPodDevice.For(device).AddTrackAsync(new MediaItem { Id = songSrc, FilePath = songSrc, FileName = Path.GetFileName(songSrc), Title = "Where It’s At", Kind = MediaKind.Music }, "ffmpeg");

            Assert.Matches(@"^[A-Z]{4}\.mp3$", Path.GetFileName(item.FilePath)!);
            Assert.True(File.Exists(item.FilePath));
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>
    /// A stock Shuffle can't decode FLAC - hardware-confirmed on a real 2G, where a verbatim-copied
    /// FLAC is silently skipped by the firmware. The add path must transcode it, so with no ffmpeg
    /// available the add must FAIL LOUDLY and leave the device untouched - never fall back to copying
    /// the raw file.
    /// </summary>
    [Fact]
    public async Task Adding_a_flac_transcodes_never_copies_verbatim()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-shufflac-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-shufflacsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(srcDir);
        try
        {
            var flacSrc = Path.Combine(srcDir, "song.flac");
            File.WriteAllBytes(flacSrc, new byte[500]);

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, IpodGeneration = "Shuffle 2G", Name = "Shuffle" };
            var ipod = IPodDevice.For(device);
            var source = new MediaItem { Id = flacSrc, FilePath = flacSrc, FileName = "song.flac", Title = "Song", Kind = MediaKind.Music };

            await Assert.ThrowsAnyAsync<Exception>(() => ipod.AddTrackAsync(source, "ffmpeg-not-installed"));

            Assert.Empty((await ipod.ReadLibraryAsync()).Tracks);   // nothing listed in iTunesSD
            var musicDir = Path.Combine(mount, "iPod_Control", "Music");
            Assert.True(!Directory.Exists(musicDir) || !Directory.EnumerateFiles(musicDir, "*", SearchOption.AllDirectories).Any());
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
            Directory.Delete(srcDir, recursive: true);
        }
    }

    /// <summary>
    /// Reorder rewrites the iTunesSD in the requested order while each entry keeps its own fields
    /// (volume, start/stop, flags) - and entries the caller doesn't mention (e.g. podcasts hidden from
    /// the music view) keep their old relative order after the mentioned ones.
    /// </summary>
    [Fact]
    public async Task Reorder_rewrites_play_order_and_preserves_entry_fields()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-shufreord-" + Guid.NewGuid().ToString("N"));
        var iTunesDir = Path.Combine(mount, "iPod_Control", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        try
        {
            ShuffleSdWriter.Write(iTunesDir,
            [
                new ShuffleSdTrack("/iPod_Control/Music/F00/A.mp3", 1, Volume: 42, StartTimeMs: 2560, StopTimeMs: 0, PlayInShuffle: false, Bookmarkable: true),
                new ShuffleSdTrack("/iPod_Control/Music/F00/B.mp3", 1),
                new ShuffleSdTrack("/iPod_Control/Music/F00/C.wav", 4),
            ]);

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, IpodGeneration = "Shuffle 2G", Name = "Shuffle" };
            var ipod = IPodDevice.For(device);
            Assert.True(ipod.SupportsReorder);

            MediaItem M(string file) => new() { Id = file, FilePath = Path.Combine(mount, "iPod_Control", "Music", "F00", file), Kind = MediaKind.Music };

            // Mention only C and A - B is "hidden" and must trail in its old relative order.
            await ipod.ReorderAsync([M("C.wav"), M("A.mp3")]);

            var read = ShuffleSdWriter.Read(iTunesDir);
            Assert.Equal(
                ["/iPod_Control/Music/F00/C.wav", "/iPod_Control/Music/F00/A.mp3", "/iPod_Control/Music/F00/B.mp3"],
                read.Select(t => t.IpodPath).ToArray());

            var a = read[1];
            Assert.Equal(42, a.Volume);
            Assert.Equal(2560, a.StartTimeMs);
            Assert.False(a.PlayInShuffle);
            Assert.True(a.Bookmarkable);
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }

    [Fact]
    public void Bdhs_iTunesSD_layout_is_correct_and_round_trips()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-bdhs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            ShuffleBdhsWriter.Write(dir,
            [
                new ShuffleSdTrack("/iPod_Control/Music/F00/A.mp3", 1),
                new ShuffleSdTrack("/iPod_Control/Music/F00/B.m4a", 2),
                new ShuffleSdTrack("/iPod_Control/Music/F00/C.wav", 4),
            ]);

            var b = File.ReadAllBytes(Path.Combine(dir, "iTunesSD"));
            Assert.Equal("bdhs", System.Text.Encoding.ASCII.GetString(b, 0, 4));
            Assert.Equal(3, Le32(b, 12));                                               // track count (little-endian)
            Assert.Equal("hths", System.Text.Encoding.ASCII.GetString(b, Le32(b, 36), 4)); // track-header offset -> hths

            var read = ShuffleBdhsWriter.Read(dir);
            Assert.Equal(3, read.Count);
            Assert.Equal("/iPod_Control/Music/F00/A.mp3", read[0].IpodPath);
            Assert.Equal(1, read[0].FileType);
            Assert.Equal("/iPod_Control/Music/F00/C.wav", read[2].IpodPath);
            Assert.Equal(4, read[2].FileType);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static int Be24(byte[] b, int off) => (b[off] << 16) | (b[off + 1] << 8) | b[off + 2];
    private static int Le32(byte[] b, int off) => b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
}
