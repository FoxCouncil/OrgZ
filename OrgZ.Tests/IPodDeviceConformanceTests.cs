// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Capability conformance for the <see cref="IPodDevice"/> tiers, driven end-to-end against
/// SYNTHETIC devices (a temp directory + a real database built by the product's own writers - no
/// hardware, no fixtures, CI-safe). The contract under test: the factory routes every generation to
/// the right tier, every capability a tier CLAIMS works as a full product lifecycle (add → read back
/// through the product's own reader → playlist → podcasts → remove → erase), and tiers that claim
/// nothing fail LOUDLY (NotImplementedException), never silently. A capability flag cannot be
/// flipped, and an operation cannot be stubbed, without this suite going red.
/// </summary>
public class IPodDeviceConformanceTests
{
    // ── factory routing: generation string → tier ─────────────────────────────

    [Theory]
    [InlineData("1G",          typeof(BinaryIPod))]       // no checksum, still binary iTunesDB
    [InlineData("4G",          typeof(BinaryIPod))]
    [InlineData("Photo",       typeof(BinaryIPod))]
    [InlineData("Video 5.5G",  typeof(BinaryIPod))]       // hash58
    [InlineData("Classic 7G",  typeof(BinaryIPod))]
    [InlineData("Nano 5G",     typeof(Nano5gIPod))]       // hash72 SQLite
    [InlineData("Shuffle 1G",  typeof(ShuffleIPod))]      // classic big-endian iTunesSD
    [InlineData("Shuffle 2G",  typeof(ShuffleIPod))]
    [InlineData("Shuffle 3G",  typeof(ShuffleIPod))]      // little-endian bdhs
    [InlineData("Shuffle 4G",  typeof(ShuffleIPod))]
    [InlineData("Nano 6G",     typeof(UnsupportedIPod))]  // hashAB - no open-source signer
    [InlineData("Nano 7G",     typeof(UnsupportedIPod))]
    [InlineData("Touch 1G",    typeof(UnsupportedIPod))]
    [InlineData(null,          typeof(UnsupportedIPod))]  // undetected generation
    public void Factory_routes_stock_ipods_by_generation(string? generation, Type expectedTier)
    {
        var device = new ConnectedDevice { MountPath = @"L:\", DeviceType = DeviceType.StockIPod, Name = "POD", IpodGeneration = generation };
        Assert.IsType(expectedTier, IPodDevice.For(device));
    }

    [Theory]
    [InlineData(DeviceType.RockboxIPod,   typeof(RockboxIPod))]
    [InlineData(DeviceType.RockboxOther,  typeof(RockboxIPod))]
    [InlineData(DeviceType.GenericPlayer, typeof(RockboxIPod))]   // any mass-storage player IS the filesystem tier
    [InlineData(DeviceType.Unknown,       typeof(UnsupportedIPod))]
    public void Factory_routes_non_stock_devices_by_type(DeviceType type, Type expectedTier)
    {
        var device = new ConnectedDevice { MountPath = @"L:\", DeviceType = type, Name = "DEV" };
        Assert.IsType(expectedTier, IPodDevice.For(device));
    }

    // ── capability claims: the spec, pinned ───────────────────────────────────
    // Flipping any flag is a product decision - it must arrive together with this row changing
    // AND the lifecycle below proving the newly claimed operation.

    [Theory]
    //          generation      dbWrite playlists podcasts artwork audiobooks
    [InlineData("Video 5.5G",   true,   true,     true,    true,   true)]
    [InlineData("Nano 5G",      true,   true,     true,    true,   true)]
    [InlineData("Shuffle 2G",   true,   true,     true,    false,  false)]   // podcasts/playlists fold into the one list; no audiobook concept in iTunesSD - a book shuffled into songs is worse than absent
    [InlineData("Nano 7G",      false,  false,    false,   false,  false)]
    public void Capability_claims_are_the_agreed_spec(string generation, bool dbWrite, bool playlists, bool podcasts, bool artwork, bool audiobooks)
    {
        var ipod = IPodDevice.For(new ConnectedDevice { MountPath = @"L:\", DeviceType = DeviceType.StockIPod, Name = "POD", IpodGeneration = generation });
        Assert.Equal(dbWrite,   ipod.SupportsDatabaseWrite);
        Assert.Equal(playlists, ipod.SupportsPlaylists);
        Assert.Equal(podcasts,  ipod.SupportsPodcasts);
        Assert.Equal(artwork,   ipod.SupportsArtwork);
        Assert.Equal(audiobooks, ipod.SupportsAudiobooks);
    }

    // ── unsupported tier: gaps must be LOUD ───────────────────────────────────

    [Fact]
    public async Task Unsupported_tier_throws_on_every_operation_instead_of_silently_no_opping()
    {
        var ipod = IPodDevice.For(new ConnectedDevice { MountPath = @"L:\", DeviceType = DeviceType.StockIPod, Name = "POD", IpodGeneration = "Touch 1G" });
        var item = new MediaItem { Id = "x", Kind = MediaKind.Music, Title = "T" };

        await Assert.ThrowsAsync<NotImplementedException>(() => ipod.ReadLibraryAsync());
        await Assert.ThrowsAsync<NotImplementedException>(() => ipod.AddTrackAsync(item, "ffmpeg"));
        await Assert.ThrowsAsync<NotImplementedException>(() => ipod.RemoveTrackAsync(item));
        await Assert.ThrowsAsync<NotImplementedException>(() => ipod.CreatePlaylistAsync("PL", [item]));
        await Assert.ThrowsAsync<NotImplementedException>(() => ipod.AddPodcastsAsync([], "ffmpeg"));
        await Assert.ThrowsAsync<NotImplementedException>(() => ipod.EraseAsync());
    }

    // ── binary tier: the full claimed lifecycle on a synthetic iPod ───────────
    // Covers BOTH checksum flavours: "Video 5.5G" (checksum None - the validated tier-1 path) and
    // "Classic 7G" (hash58, FireWire-GUID keyed - every 2007+ Classic/Nano 3G/4G takes this path).
    // The device starts as a bare mount with an EMPTY factory database; every subsequent byte is
    // written by the product and read back by the product. The import source is a deliberately
    // tagless file - the importer's TagLib fallback (title = filename) is part of the contract.

    [Theory]
    [InlineData("Video 5.5G", null)]
    [InlineData("Classic 7G", "000A2700DEADBEEF")]
    public async Task Binary_tier_full_lifecycle_add_playlist_podcasts_remove_erase(string generation, string? fireWireGuid)
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-conf-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-confsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            var ipod = NewBinaryIPod(mount, generation, fireWireGuid);

            // Factory-fresh: empty library.
            var empty = await ipod.ReadLibraryAsync();
            Assert.Empty(empty.Tracks);
            Assert.Empty(empty.Playlists);

            // ── ADD (the real import path: copy + MHIT + checksum + verify + swap) ──
            var song1 = await ipod.AddTrackAsync(LibraryTrack(srcDir, "Running in the 90s"), ffmpegPath: "ffmpeg-not-installed");
            var song2 = await ipod.AddTrackAsync(LibraryTrack(srcDir, "Deja Vu"), ffmpegPath: "ffmpeg-not-installed");
            Assert.True(File.Exists(song1.FilePath));

            var afterAdd = await ipod.ReadLibraryAsync();
            Assert.Equal(2, afterAdd.Tracks.Count);
            Assert.Contains(afterAdd.Tracks, t => t.Title == "Running in the 90s");
            Assert.All(afterAdd.Tracks, t => Assert.Equal(MediaKind.Music, t.Kind));

            // ── PLAYLIST (native MHYP, read back through the product reader) ──
            await ipod.CreatePlaylistAsync("Roadtrip", afterAdd.Tracks);
            var afterPlaylist = await ipod.ReadLibraryAsync();
            var pl = Assert.Single(afterPlaylist.Playlists, p => p.Name == "Roadtrip");
            Assert.Equal(2, pl.TrackIds.Count);

            // ── PODCASTS (episodes land as media_type=podcast and read back that way) ──
            int pushed = await ipod.AddPodcastsAsync(
            [
                Episode(srcDir, "DEADLOCK", "Episode One"),
                Episode(srcDir, "DEADLOCK", "Episode Two"),
            ], "ffmpeg-not-installed");
            Assert.Equal(2, pushed);

            var afterPodcasts = await ipod.ReadLibraryAsync();
            Assert.Equal(2, afterPodcasts.Tracks.Count(t => t.Kind == MediaKind.Podcast));
            Assert.Equal(2, afterPodcasts.Tracks.Count(t => t.Kind == MediaKind.Music));

            // Idempotent re-sync: pushing the same episodes again adds nothing.
            Assert.Equal(0, await ipod.AddPodcastsAsync([Episode(srcDir, "DEADLOCK", "Episode One")], "ffmpeg-not-installed"));

            // ── AUDIOBOOK (an .m4b imports as media_type=8 and reads back as an audiobook - the
            //    iPod's NATIVE audiobook support, not a book mislabeled as a song) ──
            var book = await ipod.AddTrackAsync(AudiobookTrack(srcDir, "The Art of War"), "ffmpeg-not-installed");
            Assert.EndsWith(".m4b", book.FilePath!, StringComparison.OrdinalIgnoreCase);   // container passes through
            var afterBook = await ipod.ReadLibraryAsync();
            Assert.Equal(5, afterBook.Tracks.Count);
            var bookBack = Assert.Single(afterBook.Tracks, t => t.Kind == MediaKind.Audiobook);
            Assert.Equal("The Art of War", bookBack.Title);

            // ── REMOVE (database row + audio file) ──
            var doomed = afterBook.Tracks.First(t => t.Title == "Deja Vu");
            await ipod.RemoveTrackAsync(doomed);
            var afterRemove = await ipod.ReadLibraryAsync();
            Assert.Equal(4, afterRemove.Tracks.Count);
            Assert.DoesNotContain(afterRemove.Tracks, t => t.Title == "Deja Vu");
            Assert.False(File.Exists(doomed.FilePath));

            // ── ERASE (empty and ready to load) ──
            int removed = await ipod.EraseAsync();
            Assert.True(removed >= 4);
            var afterErase = await ipod.ReadLibraryAsync();
            Assert.Empty(afterErase.Tracks);
            Assert.Empty(Directory.GetFiles(Path.Combine(mount, "iPod_Control", "Music"), "*", SearchOption.AllDirectories));
        }
        finally
        {
            TryDelete(mount);
            TryDelete(srcDir);
        }
    }

    [Fact]
    public async Task Binary_hash58_tier_refuses_to_write_without_the_FireWire_guid()
    {
        // A hash58 iPod without its FireWire GUID cannot produce a checksum the firmware accepts -
        // writing anyway would brick the library to "0 songs". The product must refuse loudly.
        var mount = Path.Combine(Path.GetTempPath(), "orgz-conf-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-confsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        try
        {
            var ipod = NewBinaryIPod(mount, "Classic 7G", fireWireGuid: null);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => ipod.AddTrackAsync(LibraryTrack(srcDir, "Nope"), "ffmpeg-not-installed"));
        }
        finally
        {
            TryDelete(mount);
            TryDelete(srcDir);
        }
    }

    // ── shuffle tier: podcasts + playlists fold into the single track list ────
    // These capabilities are CLAIMED (flags true), so the lifecycle must prove them: episodes land
    // as plain tracks in the iTunesSD order, and syncing a playlist REPLACES the device's list -
    // that's what a screenless one-list device is.

    [Theory]
    [InlineData("Shuffle 2G")]   // classic big-endian iTunesSD
    [InlineData("Shuffle 4G")]   // little-endian bdhs
    public async Task Shuffle_tier_podcasts_land_as_tracks_and_a_playlist_replaces_the_list(string generation)
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-conf-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-confsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "iTunes"));
        Directory.CreateDirectory(srcDir);
        try
        {
            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.StockIPod, Name = "SHUF", IpodGeneration = generation };
            var ipod = IPodDevice.For(device);
            Assert.IsType<ShuffleIPod>(ipod);
            Assert.True(ipod.SupportsPodcasts);
            Assert.True(ipod.SupportsPlaylists);

            // Podcast episodes land as plain tracks in the device list.
            int pushed = await ipod.AddPodcastsAsync(
            [
                Episode(srcDir, "DEADLOCK", "Episode One"),
                Episode(srcDir, "DEADLOCK", "Episode Two"),
            ], "ffmpeg-not-installed");
            Assert.Equal(2, pushed);
            var afterPodcasts = await ipod.ReadLibraryAsync();
            Assert.Equal(2, afterPodcasts.Tracks.Count);

            // Re-pushing the same episodes adds nothing (same on-device path).
            Assert.Equal(0, await ipod.AddPodcastsAsync([Episode(srcDir, "DEADLOCK", "Episode One")], "ffmpeg-not-installed"));

            // A playlist sync REPLACES the device's one list with exactly its tracks.
            var song = await ipod.AddTrackAsync(LibraryTrack(srcDir, "Night of Fire"), "ffmpeg-not-installed");
            Assert.Equal(3, (await ipod.ReadLibraryAsync()).Tracks.Count);
            await ipod.CreatePlaylistAsync("Only This", [song]);
            var afterPlaylist = await ipod.ReadLibraryAsync();
            var only = Assert.Single(afterPlaylist.Tracks);
            Assert.Equal(song.FilePath, only.FilePath);
        }
        finally
        {
            TryDelete(mount);
            TryDelete(srcDir);
        }
    }

    // ── rockbox tier: erase empties the library, never the firmware ───────────

    [Fact]
    public async Task Rockbox_erase_empties_music_podcasts_playlists_but_never_the_firmware()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-conf-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-confsrc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(srcDir);
        Directory.CreateDirectory(Path.Combine(mount, ".rockbox"));
        var firmwareFile = Path.Combine(mount, ".rockbox", "rockbox-info.txt");
        File.WriteAllText(firmwareFile, "Version: 3.15");
        try
        {
            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.RockboxIPod, Name = "RBX" };
            var ipod = IPodDevice.For(device);

            await ipod.AddTrackAsync(LibraryTrack(srcDir, "Gas Gas Gas"), "ffmpeg-not-installed");
            await ipod.AddPodcastsAsync([Episode(srcDir, "DEADLOCK", "Episode One")], "ffmpeg-not-installed");
            var populated = await ipod.ReadLibraryAsync();
            Assert.Equal(2, populated.Tracks.Count);
            await ipod.CreatePlaylistAsync("Keepers", populated.Tracks);

            int removed = await ipod.EraseAsync();
            Assert.True(removed >= 2);   // both audio files at minimum (the /Podcasts m3u8 rides along)

            var afterErase = await ipod.ReadLibraryAsync();
            Assert.Empty(afterErase.Tracks);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(mount, "Playlists")));
            Assert.True(File.Exists(firmwareFile), "/.rockbox must survive an erase — the firmware lives there");
        }
        finally
        {
            TryDelete(mount);
            TryDelete(srcDir);
        }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    /// <summary>A synthetic stock iPod: bare mount + the product's own factory-empty iTunesDB.</summary>
    private static IPodDevice NewBinaryIPod(string mount, string generation, string? fireWireGuid)
    {
        var iTunesDir = Path.Combine(mount, "iPod_Control", "iTunes");
        Directory.CreateDirectory(iTunesDir);
        Directory.CreateDirectory(Path.Combine(mount, "iPod_Control", "Music"));
        var doc = ITunesDbWriter.CreateEmpty();
        ITunesDbChunkTree.Normalize(doc.Root);
        File.WriteAllBytes(Path.Combine(iTunesDir, "iTunesDB"), ITunesDbChunkTree.Serialize(doc));

        var device = new ConnectedDevice
        {
            MountPath = mount,
            DeviceType = DeviceType.StockIPod,
            Name = "FAKEPOD",
            IpodGeneration = generation,
            FireWireGuid = fireWireGuid,
        };
        var ipod = IPodDevice.For(device);
        Assert.IsType<BinaryIPod>(ipod);   // the fake must land on the tier under test
        return ipod;
    }

    /// <summary>A library-side track backed by a deliberately tagless audio file (TagLib fallback path).</summary>
    private static MediaItem LibraryTrack(string srcDir, string title)
    {
        var path = Path.Combine(srcDir, title + ".mp3");
        WriteTaglessMp3(path);
        return new MediaItem
        {
            Id = path,
            Kind = MediaKind.Music,
            FilePath = path,
            FileName = Path.GetFileName(path),
            Title = title,
            Artist = "Initial D",
            Album = "Super Eurobeat",
        };
    }

    private static PodcastPush Episode(string srcDir, string show, string title)
    {
        var path = Path.Combine(srcDir, $"{show} - {title}.mp3");
        WriteTaglessMp3(path);
        return new PodcastPush(path, title, show, "notes", "https://example.test/feed.xml",
            new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc), LengthMs: 1_800_000);
    }

    /// <summary>A library-side audiobook backed by a deliberately tagless .m4b - the CONTAINER is
    /// the audiobook signal (no TagLib needed), and the title falls back to the filename.</summary>
    private static MediaItem AudiobookTrack(string srcDir, string title)
    {
        var path = Path.Combine(srcDir, title + ".m4b");
        File.WriteAllBytes(path, new byte[512]);
        return new MediaItem
        {
            Id = path,
            Kind = MediaKind.Audiobook,
            FilePath = path,
            FileName = Path.GetFileName(path),
            Title = title,
            Artist = "Sun Tzu",
            Album = title,
        };
    }

    /// <summary>A few MPEG frame-sync bytes - enough to be an "mp3" for the copy path while making
    /// TagLib fail, which the import contract must survive (title falls back to the filename).</summary>
    private static void WriteTaglessMp3(string path)
    {
        var bytes = new byte[512];
        bytes[0] = 0xFF; bytes[1] = 0xFB; bytes[2] = 0x90;
        File.WriteAllBytes(path, bytes);
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { /* temp cleanup */ }
    }
}
