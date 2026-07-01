// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// End-to-end CRUD for the binary-iTunesDB tier through the <see cref="IPodDevice"/> interface - every
/// pre-Nano-5G stock iPod (1st-gen FireWire → Classic). Builds a real iTunesDB in a temp mount with
/// <see cref="ITunesDbWriter.CreateEmpty"/> + <see cref="ITunesDbWriter.AddTrack"/>, then drives
/// Read → CreatePlaylist → Remove → Erase through the interface. Uses generation "1G" (no checksum) so
/// no hash58 signing is needed - CI-safe, no fixture, no hardware.
/// </summary>
public class BinaryIPodTests
{
    [Fact]
    public async Task Read_create_playlist_remove_and_erase_through_the_interface()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-bin-" + Guid.NewGuid().ToString("N"));
        var iTunesDir = Path.Combine(mount, "iPod_Control", "iTunes");
        var musicF00 = Path.Combine(mount, "iPod_Control", "Music", "F00");
        Directory.CreateDirectory(iTunesDir);
        Directory.CreateDirectory(musicF00);
        var dbPath = Path.Combine(iTunesDir, "iTunesDB");
        try
        {
            // Build a real binary iTunesDB with two tracks, plus their on-device audio files.
            var doc = ITunesDbWriter.CreateEmpty();
            ITunesDbWriter.AddTrack(doc, Track(1));
            ITunesDbWriter.AddTrack(doc, Track(2));
            ITunesDbChunkTree.Normalize(doc.Root);
            File.WriteAllBytes(dbPath, ITunesDbChunkTree.Serialize(doc));
            File.WriteAllBytes(Path.Combine(musicF00, "T1.m4a"), new byte[100]);
            File.WriteAllBytes(Path.Combine(musicF00, "T2.m4a"), new byte[100]);

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, IpodGeneration = "1G", Name = "iPod 1G" };
            var ipod = new BinaryIPod(device);

            // ── READ ──────────────────────────────────────────────────────────────────
            var lib = await ipod.ReadLibraryAsync();
            Assert.Equal(2, lib.Tracks.Count);
            var t1 = lib.Tracks.Single(t => t.Title == "Track 1");
            Assert.EndsWith(Path.Combine("F00", "T1.m4a"), t1.FilePath);

            // ── CREATE PLAYLIST ──────────────────────────────────────────────────────
            await ipod.CreatePlaylistAsync("Bin PL", [.. lib.Tracks]);
            ITunesDbReader.ReadAll(dbPath, mount, out _, out var playlists);
            Assert.Contains(playlists, p => p.Name == "Bin PL");

            // ── DELETE ───────────────────────────────────────────────────────────────
            await ipod.RemoveTrackAsync(t1);
            ITunesDbReader.ReadAll(dbPath, mount, out var afterRemove, out _);
            Assert.Single(afterRemove);                               // one track left
            Assert.False(File.Exists(Path.Combine(musicF00, "T1.m4a"))); // its audio deleted

            // ── ERASE (the reset that previously threw NotImplemented on this tier) ───
            int removed = await ipod.EraseAsync();
            Assert.True(removed >= 1);
            ITunesDbReader.ReadAll(dbPath, mount, out var afterErase, out _);
            Assert.Empty(afterErase);                                 // library empty
            Assert.Empty(Directory.GetFiles(Path.Combine(mount, "iPod_Control", "Music"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }

    private static NewTrack Track(uint id) => new()
    {
        TrackId = id,
        IpodPath = $":iPod_Control:Music:F00:T{id}.m4a",
        Title = $"Track {id}",
        Artist = "Artist",
        Album = "Album",
        Genre = "Genre",
        Year = 2024,
        TrackNumber = (int)id,
        FileSize = 100 + id,
        LengthMs = 200_000,
        Bitrate = 256,
        SampleRate = 44100,
        DateAddedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        Dbid = 0x1000UL + id,
    };
}
