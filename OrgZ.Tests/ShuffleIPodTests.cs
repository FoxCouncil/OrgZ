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
            Assert.Equal(0x010600, Be24(b, 3));                   // magic
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

            var source = new MediaItem { Id = songSrc, FilePath = songSrc, FileName = "song.mp3", Title = "Song", Kind = MediaKind.Music };

            // ── ADD: copies to Music/F00 and lists it in iTunesSD ──
            var item = await ipod.AddTrackAsync(source, "ffmpeg");
            Assert.Equal(Path.Combine(mount, "iPod_Control", "Music", "F00", "song.mp3"), item.FilePath);
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
