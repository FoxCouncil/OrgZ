// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// Thumbnail file rollover: the naming, the ceiling rule, the file index surviving a trip through
/// the ArtworkDB, and a real on-disk compaction that has to split one format across two files.
/// </summary>
public class IPodArtworkFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"orgz-ithmb-{Guid.NewGuid():N}");

    public IPodArtworkFilesTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "iPod_Control", "Artwork"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string ArtDir => Path.Combine(_root, "iPod_Control", "Artwork");

    [Theory]
    [InlineData(1060, 1, "F1060_1.ithmb")]
    [InlineData(1060, 2, "F1060_2.ithmb")]
    [InlineData(1055, 17, "F1055_17.ithmb")]
    public void File_names_follow_iTunes(int format, int index, string expected)
    {
        Assert.Equal(expected, IPodArtworkFiles.FileName(format, index));
        Assert.Equal(expected, new ArtThumb(format, 1, 1, 0, 1, index).FileName);
    }

    [Fact]
    public void An_empty_file_always_takes_its_first_thumbnail()
    {
        Assert.False(IPodArtworkFiles.WouldOverflow(0, long.MaxValue / 2));
    }

    [Fact]
    public void Newest_file_index_is_the_highest_present_or_one()
    {
        Assert.Equal(1, IPodArtworkFiles.NewestFileIndex(ArtDir, 1060));

        File.WriteAllBytes(Path.Combine(ArtDir, "F1060_1.ithmb"), [1]);
        File.WriteAllBytes(Path.Combine(ArtDir, "F1060_3.ithmb"), [1]);
        File.WriteAllBytes(Path.Combine(ArtDir, "F1055_9.ithmb"), [1]);   // another format: ignored

        Assert.Equal(3, IPodArtworkFiles.NewestFileIndex(ArtDir, 1060));
        Assert.Equal(9, IPodArtworkFiles.NewestFileIndex(ArtDir, 1055));
        Assert.Equal(1, IPodArtworkFiles.NewestFileIndex(Path.Combine(_root, "nowhere"), 1060));
    }

    [Fact]
    public void The_file_index_survives_a_round_trip_through_the_ArtworkDB()
    {
        var image = new ArtImage(0xABCDUL, 101,
        [
            new ArtThumb(1060, 320, 320, 4096, 204800, FileIndex: 2),
            new ArtThumb(1061, 56, 56, 0, 6272),   // default index 1
        ], 9000);

        var doc = ArtworkDbWriter.BuildFromImages([image]);
        ITunesDbChunkTree.Normalize(doc.Root);
        var back = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(ITunesDbChunkTree.Serialize(doc)));

        var thumbs = Assert.Single(back).Thumbs;
        Assert.Equal(2, thumbs.Single(t => t.FormatId == 1060).FileIndex);
        Assert.Equal(1, thumbs.Single(t => t.FormatId == 1061).FileIndex);
        Assert.Equal(4096, thumbs.Single(t => t.FormatId == 1060).IthmbOffset);
    }

    [Fact]
    public async Task Compaction_splits_a_format_across_files_at_the_ceiling()
    {
        // Six entries, three distinct covers, each cover 100 bytes at one format. With a 250-byte
        // ceiling the three distinct covers must land as two in _1 and one in _2.
        const int format = 1060;
        const int size = 100;
        var raw = new byte[6 * size];
        var covers = new[] { 1, 1, 2, 2, 3, 3 };
        for (int i = 0; i < 6; i++)
        {
            Array.Fill(raw, (byte)covers[i], i * size, size);
        }
        File.WriteAllBytes(Path.Combine(ArtDir, "F1060_1.ithmb"), raw);

        var images = Enumerable.Range(0, 6)
            .Select(i => new ArtImage((ulong)(0x100 + i), 100 + i, [new ArtThumb(format, 10, 5, i * size, size)], 500))
            .ToList();
        var doc = ArtworkDbWriter.BuildFromImages(images);
        ITunesDbChunkTree.Normalize(doc.Root);
        File.WriteAllBytes(Path.Combine(ArtDir, "ArtworkDB"), ITunesDbChunkTree.Serialize(doc));

        var result = await IPodArtworkCompactor.CompactAsync(_root, fileLimit: 250);

        Assert.Equal(6, result.Images);
        Assert.Equal(3, result.DistinctCovers);
        Assert.Equal(600, result.BytesBefore);
        Assert.Equal(300, result.BytesAfter);

        // On disk: two covers fit the first file, the third opened the second.
        Assert.Equal(200, new FileInfo(Path.Combine(ArtDir, "F1060_1.ithmb")).Length);
        Assert.Equal(100, new FileInfo(Path.Combine(ArtDir, "F1060_2.ithmb")).Length);

        // The database agrees, and every entry still points at its own cover's bytes.
        var back = ArtworkDbWriter.ReadImages(ITunesDbChunkTree.Parse(File.ReadAllBytes(Path.Combine(ArtDir, "ArtworkDB"))));
        Assert.Equal(6, back.Count);
        foreach (var (entry, expectedCover) in back.Zip(covers))
        {
            var thumb = Assert.Single(entry.Thumbs);
            var bytes = File.ReadAllBytes(Path.Combine(ArtDir, thumb.FileName));
            Assert.All(bytes.Skip(thumb.IthmbOffset).Take(thumb.ImageSize), b => Assert.Equal((byte)expectedCover, b));
        }
        Assert.Equal(2, back.Where(e => e.Dbid >= 0x104).Select(e => e.Thumbs[0].FileIndex).Distinct().Single());
    }
}
