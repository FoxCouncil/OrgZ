// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Audiobooks;

namespace OrgZ.Tests;

/// <summary>
/// The download pipeline's pure pieces: the .audiobooks target-path contract (including the
/// scanner exemption + kind-by-location that make the dot-folder work as LIBRARY content),
/// filesystem-derived download state, and the genre stamp that lets detection promote MP3 chapters.
/// </summary>
public class AudiobookDownloadServiceTests
{
    private static AudiobookListing Book(string id = "some_item_librivox", string? creator = "Jane Author", string? title = "The Book")
        => new() { Identifier = id, Creator = creator, Title = title };

    // ===== Target paths - {library}/.audiobooks/{Author}/{Title}/ =====

    [Fact]
    public void Books_live_under_the_dot_audiobooks_folder_by_author_and_title()
    {
        var dir = AudiobookDownloadService.TargetDirectoryFor(@"C:\Music", Book());
        Assert.Equal(@"C:\Music\.audiobooks\Jane Author\The Book", dir);
    }

    [Fact]
    public void Path_segments_sanitize_and_never_go_empty()
    {
        // Each invalid char becomes one underscore; trailing dots trim (Windows dislikes them).
        Assert.Equal(@"C:\Music\.audiobooks\AC_DC_ Author\Book_ The _Sequel_",
            AudiobookDownloadService.TargetDirectoryFor(@"C:\Music", Book(creator: "AC/DC: Author", title: "Book: The \"Sequel\"...")));

        Assert.Equal("Unknown", AudiobookDownloadService.SanitizeSegment("..."));
        Assert.Equal("Unknown", AudiobookDownloadService.SanitizeSegment("   "));
    }

    [Fact]
    public void File_names_keep_their_extension_through_sanitizing()
    {
        Assert.Equal("chapter_ 01.mp3", AudiobookDownloadService.SanitizeFileName("chapter: 01.mp3"));
    }

    // ===== The dot-folder contract: scanner walks it, contents are audiobooks by location =====

    [Fact]
    public void The_scanner_walks_dot_audiobooks_but_still_skips_other_dot_folders()
    {
        var root = Path.Combine(Path.GetTempPath(), "orgz-abdl-" + Guid.NewGuid().ToString("N"));
        try
        {
            var inBooks = Path.Combine(root, ".audiobooks", "Author", "Book", "01.mp3");
            var inPodcasts = Path.Combine(root, ".podcasts", "99", "ep.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(inBooks)!);
            Directory.CreateDirectory(Path.GetDirectoryName(inPodcasts)!);
            File.WriteAllBytes(inBooks, new byte[16]);
            File.WriteAllBytes(inPodcasts, new byte[16]);

            var items = FileScanner.ScanDirectoryAsync(root).GetAwaiter().GetResult();

            var book = Assert.Single(items);   // the podcast stays hidden
            Assert.Equal(inBooks, book.FilePath);
            Assert.Equal(MediaKind.Audiobook, book.Kind);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(@"C:\Music\.audiobooks\Author\Book\01.mp3", true)]    // location marks it, no tags needed
    [InlineData(@"C:\Music\.AUDIOBOOKS\book.mp3", true)]              // case-insensitive
    [InlineData(@"C:\Music\Rush\Signals\01.mp3", false)]
    [InlineData(@"C:\Music\audiobooks\book.mp3", false)]              // no dot - an ordinary folder
    public void Anything_under_dot_audiobooks_is_an_audiobook_by_location(string path, bool expected)
    {
        Assert.Equal(expected, AudiobookDetector.IsInAudiobooksFolder(path));
        if (expected)
        {
            Assert.Equal(MediaKind.Audiobook, AudiobookDetector.KindForPath(path));
        }
    }

    // ===== ParseSize =====

    [Theory]
    [InlineData("26502144", 26502144)]
    [InlineData("0", 0)]
    [InlineData("-5", 0)]
    [InlineData("abc", 0)]
    [InlineData(null, 0)]
    public void Item_file_sizes_parse_defensively(string? size, long expected)
    {
        Assert.Equal(expected, AudiobookDownloadService.ParseSize(size));
    }

    // ===== Download state from the filesystem =====

    [Fact]
    public void State_reads_from_disk_missing_partial_and_complete()
    {
        var root = Path.Combine(Path.GetTempPath(), "orgz-abst-" + Guid.NewGuid().ToString("N"));
        var book = Book();
        try
        {
            Assert.Equal(AudiobookDownloadState.NotDownloaded, AudiobookDownloadService.Instance.GetState(book, root));

            var dir = AudiobookDownloadService.TargetDirectoryFor(root, book);
            Directory.CreateDirectory(dir);
            Assert.Equal(AudiobookDownloadState.NotDownloaded, AudiobookDownloadService.Instance.GetState(book, root));

            // A .partial marks an interrupted set - not downloaded, even beside a finished file.
            File.WriteAllBytes(Path.Combine(dir, "book.m4b"), new byte[16]);
            File.WriteAllBytes(Path.Combine(dir, "part2.m4b.partial"), new byte[16]);
            Assert.Equal(AudiobookDownloadState.NotDownloaded, AudiobookDownloadService.Instance.GetState(book, root));

            File.Delete(Path.Combine(dir, "part2.m4b.partial"));
            Assert.Equal(AudiobookDownloadState.Downloaded, AudiobookDownloadService.Instance.GetState(book, root));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ===== The genre stamp =====

    [Fact]
    public void Genre_stamp_marks_untagged_files_and_leaves_selfidentified_ones_alone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-abtag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "chapter.wav");
            WriteMinimalWav(wav);

            AudiobookDownloadService.EnsureAudiobookGenre(wav);
            using (var file = TagLib.File.Create(wav))
            {
                Assert.True(AudiobookDetector.TagsSayAudiobook(file));
            }

            // Idempotent: a second stamp must not stack another genre entry.
            AudiobookDownloadService.EnsureAudiobookGenre(wav);
            using (var file = TagLib.File.Create(wav))
            {
                Assert.Single(file.Tag.Genres, g => g.Contains("audiobook", StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

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
