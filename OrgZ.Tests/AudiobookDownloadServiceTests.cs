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

    [Fact]
    public void A_legit_album_folder_starting_with_dots_is_scanned_not_skipped()
    {
        // Regression: the old blanket "any dotted folder is hidden" rule dropped real albums like
        // "...Baby One More Time" and "...And Justice for All" from the library.
        var root = Path.Combine(Path.GetTempPath(), "orgz-dotalbum-" + Guid.NewGuid().ToString("N"));
        try
        {
            var track = Path.Combine(root, "Britney Spears", "...Baby One More Time", "01 - Baby One More Time.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(track)!);
            File.WriteAllBytes(track, new byte[16]);

            var items = FileScanner.ScanDirectoryAsync(root).GetAwaiter().GetResult();

            var found = Assert.Single(items);
            Assert.Equal(track, found.FilePath);
            Assert.Equal(MediaKind.Music, found.Kind);   // a dotted ALBUM folder is not audiobook-by-location
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

    // ===== The catalog-metadata stamp =====

    private static readonly ArchiveItemMetadataFields Meta = new()
    {
        Title = "The Book",
        Creator = "Jane Author",
        Description = "Recording of The Book. Read by A Narrator.<br /><i>A story.</i><br />Second line.",
        Year = "1902",
    };

    [Fact]
    public void Stamp_writes_the_catalog_onto_a_bare_file()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-abtag-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "chapter.wav");
            WriteMinimalWav(wav);
            var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 };   // JPEG-ish payload - TagLib stores what it's given

            AudiobookDownloadService.StampMetadata(wav, Book(), Meta, cover, trackNumber: 2, trackCount: 12);

            using var file = TagLib.File.Create(wav);
            Assert.Equal("Jane Author", file.Tag.FirstPerformer);
            Assert.Equal("A Narrator", file.Tag.FirstComposer);   // Narrator column, parsed from "Read by ..."
            Assert.Equal("The Book", file.Tag.Album);
            Assert.Equal("The Book — Part 2", file.Tag.Title);   // multi-file sets number their parts
            Assert.Equal(2u, file.Tag.Track);
            Assert.Equal(12u, file.Tag.TrackCount);
            Assert.Equal(1902u, file.Tag.Year);
            Assert.Equal("Recording of The Book. Read by A Narrator.\nA story.\nSecond line.", file.Tag.Comment);   // HTML-stripped description
            Assert.True(AudiobookDetector.TagsSayAudiobook(file));
            Assert.Single(file.Tag.Pictures);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Stamp_respects_what_the_file_already_carries_and_never_stacks_genres()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-abtag2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "chapter.wav");
            WriteMinimalWav(wav);
            using (var file = TagLib.File.Create(wav))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
                id3.Title = "Chapter One — Down the Rabbit-Hole";
                id3.Year = 1865;
                file.Save();
            }

            AudiobookDownloadService.StampMetadata(wav, Book(), Meta, coverBytes: null, trackNumber: 1, trackCount: 12);
            AudiobookDownloadService.StampMetadata(wav, Book(), Meta, coverBytes: null, trackNumber: 1, trackCount: 12);   // idempotent

            using var stamped = TagLib.File.Create(wav);
            Assert.Equal("Chapter One — Down the Rabbit-Hole", stamped.Tag.Title);   // the file's own title wins
            Assert.Equal(1865u, stamped.Tag.Year);                                    // as does its year
            Assert.Equal("The Book", stamped.Tag.Album);                              // the catalog is authoritative for the book fields
            Assert.Single(stamped.Tag.Genres, g => g.Contains("audiobook", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ===== Deletion - a managed book deletes as a whole, a loose file deletes alone =====

    [Theory]
    [InlineData(@"C:\Music\.audiobooks\Author\Book\01.mp3", @"C:\Music\.audiobooks\Author\Book")]
    [InlineData(@"C:\Music\.audiobooks\Author\Book\Disc 1\01.mp3", @"C:\Music\.audiobooks\Author\Book")]   // deeper nesting still scopes to the book
    [InlineData(@"C:\Music\.audiobooks\Author\loose.m4b", null)]   // too shallow - file-scope delete
    [InlineData(@"C:\Music\.audiobooks\loose.m4b", null)]
    [InlineData(@"C:\Music\Rush\Signals\01.mp3", null)]            // not managed at all
    public void Book_folder_scope_resolves_the_title_directory(string path, string? expected)
    {
        Assert.Equal(expected, AudiobookDetector.BookFolderFor(path));
    }

    [Fact]
    public void Deleting_a_managed_book_removes_its_folder_and_prunes_an_emptied_author()
    {
        var root = Path.Combine(Path.GetTempPath(), "orgz-abdel-" + Guid.NewGuid().ToString("N"));
        try
        {
            var bookDir = Path.Combine(root, ".audiobooks", "Sun Tzu", "The Art of War");
            Directory.CreateDirectory(bookDir);
            File.WriteAllBytes(Path.Combine(bookDir, "part1.m4b"), new byte[8]);
            File.WriteAllBytes(Path.Combine(bookDir, "part2.m4b"), new byte[8]);
            File.WriteAllBytes(Path.Combine(bookDir, "cover.jpg"), new byte[8]);   // not audio - deleted with the folder, not reported

            var deleted = AudiobookDownloadService.DeleteFromDisk(Path.Combine(bookDir, "part2.m4b"));

            Assert.Equal(2, deleted.Count);   // the audio files, for row cleanup
            Assert.All(deleted, p => Assert.EndsWith(".m4b", p));
            Assert.False(Directory.Exists(bookDir));
            Assert.False(Directory.Exists(Path.Combine(root, ".audiobooks", "Sun Tzu")));   // author emptied → pruned
            Assert.True(Directory.Exists(Path.Combine(root, ".audiobooks")));               // the root never goes
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Deleting_one_book_leaves_the_authors_other_books_alone()
    {
        var root = Path.Combine(Path.GetTempPath(), "orgz-abdel2-" + Guid.NewGuid().ToString("N"));
        try
        {
            var authorDir = Path.Combine(root, ".audiobooks", "Mark Twain");
            var sawyer = Path.Combine(authorDir, "Tom Sawyer");
            var finn = Path.Combine(authorDir, "Huckleberry Finn");
            Directory.CreateDirectory(sawyer);
            Directory.CreateDirectory(finn);
            File.WriteAllBytes(Path.Combine(sawyer, "book.m4b"), new byte[8]);
            File.WriteAllBytes(Path.Combine(finn, "book.m4b"), new byte[8]);

            AudiobookDownloadService.DeleteFromDisk(Path.Combine(sawyer, "book.m4b"));

            Assert.False(Directory.Exists(sawyer));
            Assert.True(File.Exists(Path.Combine(finn, "book.m4b")));   // the sibling book survives
            Assert.True(Directory.Exists(authorDir));                    // author still has a book → kept
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Deleting_an_unmanaged_file_removes_exactly_that_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "orgz-abdel3-" + Guid.NewGuid().ToString("N"));
        try
        {
            var dir = Path.Combine(root, "Library", "Books");
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, "book.m4b");
            var neighbor = Path.Combine(dir, "other.m4b");
            File.WriteAllBytes(target, new byte[8]);
            File.WriteAllBytes(neighbor, new byte[8]);

            var deleted = AudiobookDownloadService.DeleteFromDisk(target);

            Assert.Equal([target], deleted);
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(neighbor));
            Assert.True(Directory.Exists(dir));   // no folder-level deletion outside .audiobooks
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ===== Own-file import - tags pick the shelf, filenames are the fallback =====

    [Fact]
    public void Import_destination_comes_from_the_files_tags()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-abimp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var wav = Path.Combine(dir, "chapter 01.wav");
            WriteMinimalWav(wav);
            using (var file = TagLib.File.Create(wav))
            {
                var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
                id3.Performers = ["Dan Simmons"];
                id3.Album = "Hyperion";
                file.Save();
            }

            Assert.Equal(@"C:\Music\.audiobooks\Dan Simmons\Hyperion\chapter 01.wav",
                AudiobookDownloadService.ImportDestinationFor(@"C:\Music", wav));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Import_destination_falls_back_to_the_filename_for_untagged_files()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orgz-abimp2-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var m4b = Path.Combine(dir, "Some Book.m4b");
            File.WriteAllBytes(m4b, new byte[16]);   // TagLib can't read it - fallbacks stand

            Assert.Equal(@"C:\Music\.audiobooks\Unknown Author\Some Book\Some Book.m4b",
                AudiobookDownloadService.ImportDestinationFor(@"C:\Music", m4b));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    // ===== Cover picking - the real cover file beats the image-service thumb =====

    [Fact]
    public void Cover_picker_takes_the_largest_nonthumb_image()
    {
        List<ArchiveItemFile> files =
        [
            new() { Name = "book_thumb.jpg", Format = "JPEG Thumb", Size = "9000000" },
            new() { Name = "book_cover.jpg", Format = "JPEG", Size = "500000" },
            new() { Name = "book_small.png", Format = "PNG", Size = "20000" },
            new() { Name = "chapter.mp3", Format = "64Kbps MP3", Size = "26502144" },
        ];

        Assert.Equal("book_cover.jpg", ArchiveOrgClient.PickCoverFile(files)?.Name);
        Assert.Null(ArchiveOrgClient.PickCoverFile([new ArchiveItemFile { Name = "chapter.mp3", Format = "64Kbps MP3" }]));
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
