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
                FileSize: 6_900_000, LocationRelative: "F00/ORGZ.mp3", ExtensionFourCc: 0x4D503320, KindId: 1));

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
                FileSize: 1_000_000, LocationRelative: "F00/RMVE.mp3", ExtensionFourCc: 0x4D503320, KindId: 1));

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
}
