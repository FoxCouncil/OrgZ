// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Microsoft.Data.Sqlite;
using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// End-to-end CRUD for the Nano 5G (Hash72 / SQLite) tier through the <see cref="IPodDevice"/> interface -
/// the stock-iPod equivalent of <see cref="RockboxIPodTests"/>. Builds a fake device mount from a fixture
/// (its <c>iTunes Library.itlp</c> stack), seeds a track with the writer, then exercises
/// Read → CreatePlaylist → Remove → Erase *through the interface* so the polymorphic wiring - not just the
/// underlying writer - is covered. Runs against the committed synthetic fixture everywhere, plus the
/// private real-device capture when this machine has it. No device is touched.
/// </summary>
public class Nano5gIPodTests
{
    [Theory]
    [MemberData(nameof(Nano5gLibraryWriterTests.FixtureDirs), MemberType = typeof(Nano5gLibraryWriterTests))]
    public async Task Read_create_playlist_remove_and_erase_through_the_interface(string src)
    {

        var mount = Path.Combine(Path.GetTempPath(), "orgz-n5gdev-" + Guid.NewGuid().ToString("N"));
        var itlp = Path.Combine(mount, "iPod_Control", "iTunes", "iTunes Library.itlp");
        var musicF00 = Path.Combine(mount, "iPod_Control", "Music", "F00");
        Directory.CreateDirectory(itlp);
        Directory.CreateDirectory(musicF00);
        try
        {
            foreach (var f in new[] { "Library.itdb", "Locations.itdb", "Locations.itdb.cbk", "Dynamic.itdb" })
            {
                File.Copy(Path.Combine(src, f), Path.Combine(itlp, f));
            }

            // Seed a track via the writer + a matching on-device file (so removal can delete it).
            var writer = new Nano5gLibraryWriter(itlp);
            writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: "Iface Track", Artist: "Iface Artist", Album: "Iface Album", AlbumArtist: null, Genre: "X",
                DurationMs: 60_000, TrackNumber: 1, DiscNumber: 1, Year: 2026, AudioFormat: 301, BitRate: 128,
                SampleRate: 44100, Channels: 2, FileSize: 1_000, LocationRelative: "F00/IFACE.mp3",
                ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));
            File.WriteAllBytes(Path.Combine(musicF00, "IFACE.mp3"), new byte[1_000]);

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, Name = "Nano 5G" };
            var ipod = new Nano5gIPod(device);

            // ── READ: the seeded track surfaces as a device MediaItem with a resolvable on-device path ──
            var lib = await ipod.ReadLibraryAsync();
            var item = lib.Tracks.Single(t => t.Title == "Iface Track");
            Assert.EndsWith(Path.Combine("F00", "IFACE.mp3"), item.FilePath);

            // ── CREATE PLAYLIST: an ordinary, visible container referencing the read-back item ─────────
            await ipod.CreatePlaylistAsync("Iface PL", [item]);
            using (var c = OpenRo(Path.Combine(itlp, "Library.itdb")))
            {
                Assert.Equal(1, Count(c, "container", "name='Iface PL' AND distinguished_kind=0"));
            }

            // ── DELETE: removing through the interface drops the rows and the on-device file ──────────
            await ipod.RemoveTrackAsync(item);
            using (var c = OpenRo(Path.Combine(itlp, "Library.itdb")))
            {
                Assert.Equal(0, Count(c, "item", "title='Iface Track'"));
            }
            Assert.False(File.Exists(Path.Combine(musicF00, "IFACE.mp3")));

            // ── ERASE: re-seed a file, wipe the device, and confirm both the db and Music go empty ────
            writer.AddTrack(new Nano5gLibraryWriter.TrackInsert(
                Title: "Erase Me", Artist: "E", Album: "E", AlbumArtist: null, Genre: "X",
                DurationMs: 1_000, TrackNumber: 1, DiscNumber: 1, Year: 2026, AudioFormat: 301, BitRate: 128,
                SampleRate: 44100, Channels: 2, FileSize: 10, LocationRelative: "F00/ERASE.mp3",
                ExtensionFourCc: 0x4D503320, KindString: "MPEG audio file"));
            File.WriteAllBytes(Path.Combine(musicF00, "ERASE.mp3"), new byte[10]);

            int removed = await ipod.EraseAsync();
            Assert.True(removed >= 1);
            using (var c = OpenRo(Path.Combine(itlp, "Library.itdb")))
            {
                Assert.Equal(0, Count(c, "item"));
            }
            Assert.Empty(Directory.GetFiles(Path.Combine(mount, "iPod_Control", "Music"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(mount, recursive: true);
        }
    }

    private static SqliteConnection OpenRo(string path)
    {
        var c = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        c.Open();
        return c;
    }
    private static long Count(SqliteConnection c, string table, string? where = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}" + (where is null ? "" : $" WHERE {where}");
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
