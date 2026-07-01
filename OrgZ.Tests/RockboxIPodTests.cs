// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// End-to-end CRUD for the Rockbox tier through the <see cref="IPodDevice"/> interface - music,
/// podcasts, and playlists - the tier the audit found had zero coverage. Runs entirely on a temp
/// directory standing in for the device mount; no hardware, no fixture. Rockbox is pure filesystem:
/// files under /Music and /Podcasts, playlists as .m3u8 in /Playlists (the Playlist Catalog folder),
/// so everything here is real File I/O we can assert on.
/// </summary>
public class RockboxIPodTests
{
    [Fact]
    public async Task Music_podcast_and_playlist_round_trip_through_the_interface()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-rbx-" + Guid.NewGuid().ToString("N"));
        var srcDir = Path.Combine(Path.GetTempPath(), "orgz-rbxsrc-" + Guid.NewGuid().ToString("N")); // outside the mount
        Directory.CreateDirectory(mount);
        Directory.CreateDirectory(srcDir);
        try
        {
            var songSrc = Path.Combine(srcDir, "song.wav"); WriteMinimalWav(songSrc);
            var epSrc = Path.Combine(srcDir, "56571756629.wav"); WriteMinimalWav(epSrc);

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.RockboxOther, Name = "Rockbox" };
            var ipod = IPodDevice.For(device);
            Assert.IsType<RockboxIPod>(ipod);

            var libraryTrack = new MediaItem
            {
                Id = songSrc, FilePath = songSrc, FileName = "song.wav", Title = "Song One",
                Artist = "The Artist", Album = "The Album", Kind = MediaKind.Music,
            };

            // ── CREATE: music → /Music/{Artist}/{Album}/ ──────────────────────────────
            var deviceTrack = await ipod.AddTrackAsync(libraryTrack, "ffmpeg");
            Assert.True(File.Exists(deviceTrack.FilePath));
            Assert.Equal(Path.Combine(mount, "Music", "The Artist", "The Album", "song.wav"), deviceTrack.FilePath);

            // ── CREATE: podcast → /Podcasts/{Show}/ + /Podcasts/Podcasts.m3u8 ─────────
            int pushed = await ipod.AddPodcastsAsync(
                [new PodcastPush(epSrc, "Revisiting WWE ECW 2006", "DEADLOCK", null, null, new DateTime(2026, 6, 22, 0, 0, 0, DateTimeKind.Utc), 1000)],
                "ffmpeg");
            Assert.Equal(1, pushed);
            Assert.True(File.Exists(Path.Combine(mount, "Podcasts", "DEADLOCK", "Revisiting WWE ECW 2006.wav")));
            var podM3u = Path.Combine(mount, "Podcasts", "Podcasts.m3u8");
            Assert.True(File.Exists(podM3u));
            Assert.Contains("/Podcasts/DEADLOCK/Revisiting WWE ECW 2006.wav", await File.ReadAllTextAsync(podM3u));

            // ── CREATE: playlist → /Playlists/{name}.m3u8 (Rockbox's own extension) ───
            await ipod.CreatePlaylistAsync("Road Trip", [deviceTrack]);
            var plPath = Path.Combine(mount, "Playlists", "Road Trip.m3u8");
            Assert.True(File.Exists(plPath));
            var plText = await File.ReadAllTextAsync(plPath);
            Assert.Contains("#EXTM3U", plText);
            Assert.Contains("/Music/The Artist/The Album/song.wav", plText.Replace('\\', '/'));

            // ── READ: the filesystem walk surfaces the tracks + the .m3u8 playlists ───
            var lib = await ipod.ReadLibraryAsync();
            Assert.Contains(lib.Tracks, t => t.FilePath == deviceTrack.FilePath);
            Assert.Contains(lib.Playlists, p => p.Name.Equals("Road Trip", StringComparison.OrdinalIgnoreCase) || p.Key.Contains("Road Trip"));

            // ── DELETE: remove the track → file gone + pruned from the playlist ───────
            await ipod.RemoveTrackAsync(deviceTrack);
            Assert.False(File.Exists(deviceTrack.FilePath));
            Assert.DoesNotContain("song.wav", await File.ReadAllTextAsync(plPath));
        }
        finally
        {
            try { Directory.Delete(mount, recursive: true); } catch { }
            try { Directory.Delete(srcDir, recursive: true); } catch { }
        }
    }

    /// <summary>A tiny but valid silent PCM WAV (0.1 s, 44.1 kHz mono 16-bit) - enough for the scanner's
    /// TagLib analysis to accept it as an audio file so it comes back from ReadLibraryAsync.</summary>
    private static void WriteMinimalWav(string path)
    {
        const int sampleRate = 44100, channels = 1, bits = 16, samples = 4410;
        int dataSize = samples * channels * bits / 8;
        using var w = new BinaryWriter(new FileStream(path, FileMode.Create));
        void Str(string s) => w.Write(System.Text.Encoding.ASCII.GetBytes(s));
        Str("RIFF"); w.Write(36 + dataSize); Str("WAVE");
        Str("fmt "); w.Write(16); w.Write((short)1); w.Write((short)channels);
        w.Write(sampleRate); w.Write(sampleRate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
        Str("data"); w.Write(dataSize);
        w.Write(new byte[dataSize]);
    }
}
