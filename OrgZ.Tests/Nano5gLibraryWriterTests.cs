// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Microsoft.Data.Sqlite;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Offline Stage 2 test for <see cref="Nano5gLibraryWriter"/> - gated on the device fixture
/// (Library/Locations/Dynamic.itdb + cbk) at <c>%LOCALAPPDATA%\OrgZ\nano5g-fixture</c>. Copies
/// the set to a temp dir, inserts a track, and asserts the rows plus a self-consistent re-signed
/// cbk. No device is touched. No-ops when the fixture is absent (CI).
/// </summary>
public class Nano5gLibraryWriterTests
{
    private static string? FixtureDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgZ", "nano5g-fixture");
        return File.Exists(Path.Combine(dir, "Library.itdb")) && File.Exists(Path.Combine(dir, "Locations.itdb.cbk")) ? dir : null;
    }

    [Fact]
    public void AddTrack_inserts_rows_and_resigns_cbk()
    {
        var src = FixtureDir();
        if (src is null) { return; } // device fixture absent - not run here

        var tmp = Path.Combine(Path.GetTempPath(), "orgz-nano5g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(tmp, f));
            }

            long before = CountIn(Path.Combine(tmp, "Library.itdb"), "item");

            var writer = new Nano5gLibraryWriter(tmp);
            long pid = writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: "OrgZ Test Track", Artist: "OrgZ Test Artist", Album: "OrgZ Test Album",
                AlbumArtist: null, Genre: "Eurobeat", DurationMs: 210_000, TrackNumber: 1, DiscNumber: 1,
                Year: 2026, AudioFormat: 301, BitRate: 256, SampleRate: 44100, Channels: 2,
                FileSize: 6_900_000, LocationRelative: "F00/ORGZ.mp3", ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(before + 1, Count(c, "item"));
                Assert.Equal("OrgZ Test Track", ScalarStr(c, $"SELECT title FROM item WHERE pid={pid}"));
                Assert.Equal(1, Count(c, "item_to_container", $"item_pid={pid}"));
                Assert.Equal(1, Count(c, "avformat_info", $"item_pid={pid}"));
                Assert.Equal(1, Count(c, "artist", "name='OrgZ Test Artist'"));
                Assert.Equal(1, Count(c, "album", "name='OrgZ Test Album'"));
            }
            using (var c = OpenRo(Path.Combine(tmp, "Locations.itdb")))
            {
                Assert.Equal("F00/ORGZ.mp3", ScalarStr(c, $"SELECT location FROM location WHERE item_pid={pid}"));
            }
            using (var c = OpenRo(Path.Combine(tmp, "Dynamic.itdb")))
            {
                Assert.Equal(1, Count(c, "item_stats", $"item_pid={pid}"));
            }

            // The re-signed cbk must be self-consistent for the updated Locations.itdb.
            var loc = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb"));
            var cbk = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb.cbk"));
            Assert.True(ITunesLocationsCbk.TryExtractSeed(loc, cbk, out var iv, out var rnd));
            Assert.Equal(cbk, ITunesLocationsCbk.Build(loc, iv, rnd));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void BeginCdbBatch_defers_the_CDB_rebuild_until_dispose()
    {
        var src = FixtureDir();
        if (src is null) { return; } // device fixture absent

        // Real on-device layout (...\iPod_Control\iTunes\iTunes Library.itlp) so the regenerated
        // iTunesCDB + zeroed legacy iTunesDB land inside the temp root, where we can observe them.
        var root = Path.Combine(Path.GetTempPath(), "orgz-nano5g-" + Guid.NewGuid().ToString("N"));
        var iTunesDir = Path.Combine(root, "iPod_Control", "iTunes");
        var itlp = Path.Combine(iTunesDir, "iTunes Library.itlp");
        Directory.CreateDirectory(itlp);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(itlp, f));
            }

            var cdbPath = Path.Combine(iTunesDir, "iTunesCDB");
            var writer = new Nano5gLibraryWriter(itlp);

            static Nano5gLibraryWriter.TrackInsert Track(int n) => new(
                Title: $"OrgZ Batch Track {n}", Artist: "OrgZ Batch Artist", Album: "OrgZ Batch Album",
                AlbumArtist: null, Genre: "Eurobeat", DurationMs: 200_000 + n, TrackNumber: n, DiscNumber: 1,
                Year: 2026, AudioFormat: 301, BitRate: 256, SampleRate: 44100, Channels: 2,
                FileSize: 5_000_000 + n, LocationRelative: $"F00/BAT{n}.mp3", ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file");

            using (writer.BeginCdbBatch())
            {
                writer.AddTrack(Track(1));
                writer.AddTrack(Track(2));

                // Both SQLite inserts landed, but the CDB rebuild is deferred - nothing written yet.
                Assert.False(File.Exists(cdbPath));
            }

            // Disposing the scope runs the ONE regeneration: a non-empty signed CDB plus the zeroed
            // legacy iTunesDB the firmware requires next to it, reflecting both tracks.
            Assert.True(File.Exists(cdbPath));
            Assert.True(new FileInfo(cdbPath).Length > 0);
            var legacyDb = Path.Combine(iTunesDir, "iTunesDB");
            Assert.True(File.Exists(legacyDb));
            Assert.Equal(0, new FileInfo(legacyDb).Length);

            // Un-scoped mutations regenerate immediately again (the defer depth fully unwound).
            var cdbAfterBatch = File.ReadAllBytes(cdbPath);
            writer.AddTrack(Track(3));
            Assert.False(cdbAfterBatch.AsSpan().SequenceEqual(File.ReadAllBytes(cdbPath)));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddTrack_then_RemoveTrack_returns_to_baseline()
    {
        var src = FixtureDir();
        if (src is null) { return; } // device fixture absent

        var tmp = Path.Combine(Path.GetTempPath(), "orgz-nano5g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(tmp, f));
            }

            long beforeItems = CountIn(Path.Combine(tmp, "Library.itdb"), "item");
            long beforeArtists = CountIn(Path.Combine(tmp, "Library.itdb"), "artist");

            var writer = new Nano5gLibraryWriter(tmp);
            long pid = writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: "Remove Me", Artist: "Orphan Artist XYZ", Album: "Orphan Album XYZ", AlbumArtist: null,
                Genre: "OrphanGenreXYZ", DurationMs: 120_000, TrackNumber: 1, DiscNumber: 1, Year: 2026,
                AudioFormat: 301, BitRate: 256, SampleRate: 44100, Channels: 2,
                FileSize: 1_000_000, LocationRelative: "F00/RMVE.mp3", ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(beforeItems + 1, Count(c, "item"));
            }

            writer.RemoveTrack(pid, Path.Combine(tmp, "Music")); // no real file → delete is skipped

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(beforeItems, Count(c, "item"));
                Assert.Equal(0, Count(c, "item", $"pid={pid}"));
                Assert.Equal(beforeArtists, Count(c, "artist"));          // orphan artist pruned
                Assert.Equal(0, Count(c, "artist", "name='Orphan Artist XYZ'"));
                Assert.Equal(0, Count(c, "genre_map", "genre='OrphanGenreXYZ'"));
            }
            using (var c = OpenRo(Path.Combine(tmp, "Locations.itdb")))
            {
                Assert.Equal(0, Count(c, "location", $"item_pid={pid}"));
            }

            var loc = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb"));
            var cbk = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb.cbk"));
            Assert.True(ITunesLocationsCbk.TryExtractSeed(loc, cbk, out var iv, out var rnd));
            Assert.Equal(cbk, ITunesLocationsCbk.Build(loc, iv, rnd));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void AddPodcast_then_RemovePodcast_round_trips_with_dedup_release_date_and_no_orphans()
    {
        var src = FixtureDir();
        if (src is null) { return; } // device fixture absent

        var tmp = Path.Combine(Path.GetTempPath(), "orgz-nano5g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(tmp, f));
            }

            long beforeItems = CountIn(Path.Combine(tmp, "Library.itdb"), "item");
            var writer = new Nano5gLibraryWriter(tmp);

            Assert.False(writer.PodcastEpisodeExists("DEADLOCK", "Revisiting WWE ECW 2006"));

            long pid = writer.AddPodcastEpisode(new Nano5gLibraryWriter.PodcastInsert(
                Title: "Revisiting WWE ECW 2006", ShowName: "DEADLOCK", Description: "notes",
                FeedUrl: "https://example.test/feed", ExternalGuid: null,
                ReleasedUtc: new DateTime(2026, 6, 22, 12, 0, 0, DateTimeKind.Utc), DurationMs: 90_000,
                AudioFormat: 301, BitRate: 128, SampleRate: 44100, Channels: 2, FileSize: 2_000_000,
                LocationRelative: "F00/POD1.mp3", ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(beforeItems + 1, Count(c, "item"));
                Assert.Equal(1, Count(c, "item", $"pid={pid} AND media_kind=4"));   // catalogued as a podcast, not music
                Assert.Equal(1, Count(c, "podcast_info", $"item_pid={pid}"));
                Assert.Equal("Revisiting WWE ECW 2006", ScalarStr(c, $"SELECT title FROM item WHERE pid={pid}"));
                // The release date is persisted (podcast_info.date_released is non-zero, not left at epoch).
                Assert.NotEqual("0", ScalarStr(c, $"SELECT date_released FROM podcast_info WHERE item_pid={pid}"));
            }

            // Dedup sees it now (same Show + Title) - this is what stops a re-sync duplicating episodes.
            Assert.True(writer.PodcastEpisodeExists("DEADLOCK", "Revisiting WWE ECW 2006"));

            writer.RemoveTrack(pid, Path.Combine(tmp, "Music")); // no real file → delete is skipped

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(beforeItems, Count(c, "item"));
                Assert.Equal(0, Count(c, "item", $"pid={pid}"));
                Assert.Equal(0, Count(c, "podcast_info", $"item_pid={pid}"));   // no orphan left behind
            }
            using (var c = OpenRo(Path.Combine(tmp, "Locations.itdb")))
            {
                Assert.Equal(0, Count(c, "location", $"item_pid={pid}"));
            }
            Assert.False(writer.PodcastEpisodeExists("DEADLOCK", "Revisiting WWE ECW 2006"));

            // The re-signed cbk stays self-consistent after a podcast delete.
            var loc = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb"));
            var cbk = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb.cbk"));
            Assert.True(ITunesLocationsCbk.TryExtractSeed(loc, cbk, out var iv, out var rnd));
            Assert.Equal(cbk, ITunesLocationsCbk.Build(loc, iv, rnd));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void CreatePlaylist_is_visible_categorised_and_idempotent()
    {
        var src = FixtureDir();
        if (src is null) { return; } // device fixture absent

        var tmp = Path.Combine(Path.GetTempPath(), "orgz-nano5g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(tmp, f));
            }

            var writer = new Nano5gLibraryWriter(tmp);
            long musicPid = writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: "PL Music", Artist: "PL Artist", Album: "PL Album", AlbumArtist: null, Genre: "Eurobeat",
                DurationMs: 180_000, TrackNumber: 1, DiscNumber: 1, Year: 2026, AudioFormat: 301, BitRate: 256,
                SampleRate: 44100, Channels: 2, FileSize: 5_000_000, LocationRelative: "F00/PLM.mp3",
                ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));
            long podcastPid = writer.AddPodcastEpisode(new Nano5gLibraryWriter.PodcastInsert(
                Title: "PL Ep", ShowName: "PL Show", Description: null, FeedUrl: null, ExternalGuid: null,
                ReleasedUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), DurationMs: 60_000, AudioFormat: 301,
                BitRate: 128, SampleRate: 44100, Channels: 2, FileSize: 1_000_000, LocationRelative: "F00/PLP.mp3",
                ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));

            long plPid = writer.CreatePlaylist("OrgZ Test Playlist", new[] { musicPid, podcastPid });

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                // A user playlist the firmware will actually surface: named, ordinary (distinguished_kind=0),
                // not hidden, and categorised (media_kinds != 0 - a 0 mask makes it silently invisible).
                Assert.Equal("OrgZ Test Playlist", ScalarStr(c, $"SELECT name FROM container WHERE pid={plPid}"));
                Assert.Equal(0L, ScalarLong(c, $"SELECT distinguished_kind FROM container WHERE pid={plPid}"));
                Assert.Equal(0L, ScalarLong(c, $"SELECT is_hidden FROM container WHERE pid={plPid}"));
                Assert.Equal(1L | 4L, ScalarLong(c, $"SELECT media_kinds FROM container WHERE pid={plPid}")); // music|podcast
                Assert.Equal(2, Count(c, "item_to_container", $"container_pid={plPid}"));
            }

            // Idempotent re-sync: the same name again (fewer tracks) replaces in place - one playlist,
            // membership + category updated - so re-syncing a playlist never stacks duplicates.
            long plPid2 = writer.CreatePlaylist("OrgZ Test Playlist", new[] { musicPid });
            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(1, Count(c, "container", "name='OrgZ Test Playlist' AND distinguished_kind=0"));
                Assert.Equal(1, Count(c, "item_to_container", $"container_pid={plPid2}"));
                Assert.Equal(1L, ScalarLong(c, $"SELECT media_kinds FROM container WHERE pid={plPid2}")); // now music-only
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void WipeLibrary_empties_tracks_podcasts_locations_and_resigns_cbk()
    {
        var src = FixtureDir();
        if (src is null) { return; } // device fixture absent

        var tmp = Path.Combine(Path.GetTempPath(), "orgz-nano5g-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(tmp, f));
            }

            var writer = new Nano5gLibraryWriter(tmp);
            // Seed a track + a podcast so "empty afterwards" is a real assertion, not a vacuous one.
            writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: "Wipe Me", Artist: "Wipe Artist", Album: "Wipe Album", AlbumArtist: null, Genre: "X",
                DurationMs: 60_000, TrackNumber: 1, DiscNumber: 1, Year: 2026, AudioFormat: 301, BitRate: 128,
                SampleRate: 44100, Channels: 2, FileSize: 1_000_000, LocationRelative: "F00/WIPE.mp3",
                ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));
            writer.AddPodcastEpisode(new Nano5gLibraryWriter.PodcastInsert(
                Title: "Wipe Ep", ShowName: "Wipe Show", Description: null, FeedUrl: null, ExternalGuid: null,
                ReleasedUtc: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), DurationMs: 60_000, AudioFormat: 301,
                BitRate: 128, SampleRate: 44100, Channels: 2, FileSize: 500_000, LocationRelative: "F00/WIPEP.mp3",
                ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));
            Assert.True(CountIn(Path.Combine(tmp, "Library.itdb"), "item") > 0);

            writer.WipeLibrary();

            using (var c = OpenRo(Path.Combine(tmp, "Library.itdb")))
            {
                Assert.Equal(0, Count(c, "item"));           // every track + episode gone
                Assert.Equal(0, Count(c, "podcast_info"));   // no orphaned podcast rows
            }
            using (var c = OpenRo(Path.Combine(tmp, "Locations.itdb")))
            {
                Assert.Equal(0, Count(c, "location"));        // no dangling file locations
            }

            // The re-signed cbk must stay self-consistent so the firmware still trusts the now-empty db.
            var loc = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb"));
            var cbk = File.ReadAllBytes(Path.Combine(tmp, "Locations.itdb.cbk"));
            Assert.True(ITunesLocationsCbk.TryExtractSeed(loc, cbk, out var iv, out var rnd));
            Assert.Equal(cbk, ITunesLocationsCbk.Build(loc, iv, rnd));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(tmp, recursive: true);
        }
    }

    private static SqliteConnection OpenRo(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        return c;
    }
    private static long CountIn(string dbPath, string table)
    {
        using var c = OpenRo(dbPath);
        return Count(c, table);
    }
    private static long Count(SqliteConnection c, string table, string? where = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where is null ? "" : $" WHERE {where}");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    private static string? ScalarStr(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar()?.ToString();
    }
    private static long ScalarLong(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
