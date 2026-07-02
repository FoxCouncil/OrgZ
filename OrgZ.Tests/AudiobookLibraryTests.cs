// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Audiobooks;
using OrgZ.Services.Media;

namespace OrgZ.Tests;

/// <summary>
/// The record layer that makes an audiobook behave like a podcast: a store download is remembered
/// even after its file is deleted (so it can be re-downloaded), while a file the user drops in is
/// adopted as its own record and truly forgotten once removed. Redirects the acquisition DB to a
/// temp directory and works entirely off a temp library root - no network.
/// </summary>
[Collection("AcquisitionStore")]
public class AudiobookLibraryTests : IDisposable
{
    private readonly string _dbDir;
    private readonly string _root;

    public AudiobookLibraryTests()
    {
        _dbDir = Path.Combine(Path.GetTempPath(), "OrgZ-ABLib-db-" + Path.GetRandomFileName());
        _root  = Path.Combine(Path.GetTempPath(), "OrgZ-ABLib-lib-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_dbDir);
        Directory.CreateDirectory(_root);
        AcquisitionStore.OverrideCacheDirectory(_dbDir);
        AcquisitionStore.EnsureCreated();
    }

    public void Dispose()
    {
        AcquisitionStore.OverrideCacheDirectory(null);
        try { Directory.Delete(_dbDir, recursive: true); } catch { }
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static AudiobookListing Archive(string id = "mobydick_librivox", string title = "Moby Dick", string author = "Herman Melville")
        => new() { Identifier = id, Title = title, Creator = author };

    private string WriteAudioFileFor(AudiobookListing book, string name = "chapter01.mp3")
    {
        var dir = AudiobookDownloadService.TargetDirectoryFor(_root, book);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    // ===== the headline contract: a store book survives its file being deleted =====

    [Fact]
    public void A_downloaded_store_book_is_remembered_and_becomes_redownloadable_after_its_file_is_deleted()
    {
        var book = Archive();
        AudiobookLibrary.RecordArchiveAcquisition(book);
        var file = WriteAudioFileFor(book);

        // On disk → not in the re-downloadable set.
        Assert.DoesNotContain(AudiobookLibrary.ReDownloadable(_root), a => a.SourceKey == book.Identifier);

        // File gone, but the record remains and is now offered for re-download.
        AudiobookDownloadService.DeleteFromDisk(file);
        var redownloadable = AudiobookLibrary.ReDownloadable(_root);
        var remembered = Assert.Single(redownloadable, a => a.SourceKey == book.Identifier);
        Assert.False(remembered.IsUserProvided);
        Assert.Equal("Moby Dick", remembered.Title);
        Assert.Equal(("archive", "mobydick_librivox"), AudiobookLibrary.SourceOf(remembered));
    }

    [Fact]
    public void Recording_a_libro_purchase_keeps_the_isbn_source_and_the_shelf_identity()
    {
        var libro = new LibroBook { Isbn = "9781400031702", Title = "Cloud Atlas", Authors = ["David Mitchell"], CoverUrl = "https://covers.libro.fm/x.jpg" };
        AudiobookLibrary.RecordLibroAcquisition(libro);

        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, "libro:9781400031702")!;
        Assert.Equal("Cloud Atlas", got.Title);
        Assert.Equal("David Mitchell", got.Creator);
        Assert.Equal("https://covers.libro.fm/x.jpg", got.ImageUrl);
        Assert.Equal(("libro", "9781400031702"), AudiobookLibrary.SourceOf(got));
    }

    // ===== user-dropped files: adopt on sight, forget on removal =====

    [Fact]
    public void A_user_dropped_file_is_adopted_as_a_user_provided_acquisition()
    {
        // No record yet - just a file the user placed under .audiobooks/{Author}/{Book}/.
        var bookDir = Path.Combine(_root, AudiobookDetector.AudiobooksFolderName, "Cory Doctorow", "Little Brother");
        Directory.CreateDirectory(bookDir);
        var file = Path.Combine(bookDir, "part1.mp3");
        File.WriteAllBytes(file, new byte[16]);

        AudiobookLibrary.ReconcileUserFiles(_root, [file]);

        var books = AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook);
        var adopted = Assert.Single(books);
        Assert.True(adopted.IsUserProvided);
        Assert.Equal("Little Brother", adopted.Title);
        Assert.Equal("Cory Doctorow", adopted.Creator);
        Assert.Null(adopted.SourceRefJson);

        // No source → never offered for re-download.
        Assert.Empty(AudiobookLibrary.ReDownloadable(_root));
    }

    [Fact]
    public void A_user_provided_record_is_forgotten_once_its_files_disappear()
    {
        var bookDir = Path.Combine(_root, AudiobookDetector.AudiobooksFolderName, "Cory Doctorow", "Little Brother");
        Directory.CreateDirectory(bookDir);
        var file = Path.Combine(bookDir, "part1.mp3");
        File.WriteAllBytes(file, new byte[16]);
        AudiobookLibrary.ReconcileUserFiles(_root, [file]);
        Assert.Single(AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook));

        // The user deletes the file; the next reconcile sees no files and forgets the record.
        File.Delete(file);
        AudiobookLibrary.ReconcileUserFiles(_root, []);
        Assert.Empty(AcquisitionStore.GetAll(AcquiredMediaKind.Audiobook));
    }

    [Fact]
    public void Reconcile_does_not_adopt_or_prune_a_store_record_whose_download_is_missing()
    {
        // A store acquisition with no file on disk must be left alone by the user-file reconcile -
        // it belongs to the re-downloadable set, not the forget-on-removal path.
        var book = Archive();
        AudiobookLibrary.RecordArchiveAcquisition(book);

        AudiobookLibrary.ReconcileUserFiles(_root, []);   // no files at all

        var got = AcquisitionStore.Get(AcquiredMediaKind.Audiobook, book.Identifier);
        Assert.NotNull(got);
        Assert.False(got!.IsUserProvided);
        Assert.Contains(AudiobookLibrary.ReDownloadable(_root), a => a.SourceKey == book.Identifier);
    }

    // ===== the owned-books shelf: whole books, not files =====

    private string BookDir(string author, string title) => Path.Combine(_root, AudiobookDetector.AudiobooksFolderName, author, title);

    private static MediaItem Chapter(string bookDir, string file, string title, string author, int track, int minutes)
    {
        var path = Path.Combine(bookDir, file);
        return new MediaItem
        {
            Id = path, Kind = MediaKind.Audiobook, FilePath = path, FileName = file,
            Album = title, Artist = author, Track = (uint)track, Duration = TimeSpan.FromMinutes(minutes),
        };
    }

    [Fact]
    public void Owned_shelf_collapses_a_multi_chapter_book_into_one_entry_in_play_order()
    {
        var dir = BookDir("Herman Melville", "Moby Dick");
        var items = new[]
        {
            Chapter(dir, "ch02.mp3", "Moby Dick", "Herman Melville", 2, 20),
            Chapter(dir, "ch01.mp3", "Moby Dick", "Herman Melville", 1, 30),
            Chapter(dir, "ch03.mp3", "Moby Dick", "Herman Melville", 3, 10),
        };

        var book = Assert.Single(AudiobookLibrary.AssembleOwned(_root, items));
        Assert.Equal("Moby Dick", book.Title);
        Assert.Equal("Herman Melville", book.Author);
        Assert.True(book.IsDownloaded);
        Assert.Equal(3, book.ChapterCount);
        Assert.Equal(TimeSpan.FromMinutes(60), book.TotalDuration);
        Assert.Equal(new[] { "ch01.mp3", "ch02.mp3", "ch03.mp3" }, book.Chapters.Select(c => c.FileName));
        Assert.False(book.CanReDownload);
    }

    [Fact]
    public void Owned_shelf_shows_an_acquired_book_whose_download_is_gone_as_redownloadable()
    {
        AudiobookLibrary.RecordArchiveAcquisition(Archive());   // recorded, no files on disk

        var entry = Assert.Single(AudiobookLibrary.AssembleOwned(_root, Array.Empty<MediaItem>()));
        Assert.Equal("Moby Dick", entry.Title);
        Assert.False(entry.IsDownloaded);
        Assert.Equal(0, entry.ChapterCount);
        Assert.True(entry.CanReDownload);
    }

    [Fact]
    public void Owned_shelf_marks_a_downloaded_user_book_as_not_redownloadable()
    {
        var dir = BookDir("Cory Doctorow", "Little Brother");
        var item = Chapter(dir, "part1.mp3", "Little Brother", "Cory Doctorow", 1, 45);
        AudiobookLibrary.ReconcileUserFiles(_root, [item.FilePath!]);   // adopted as user-provided

        var book = Assert.Single(AudiobookLibrary.AssembleOwned(_root, [item]));
        Assert.True(book.IsDownloaded);
        Assert.True(book.IsUserProvided);
        Assert.False(book.CanReDownload);
    }

    [Fact]
    public void A_downloaded_store_book_appears_once_and_is_enriched_from_its_record()
    {
        var book = Archive();
        AudiobookLibrary.RecordArchiveAcquisition(book);
        var dir = AudiobookDownloadService.TargetDirectoryFor(_root, book);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "book.m4b"), new byte[16]);
        var item = Chapter(dir, "book.m4b", book.Title!, book.Creator!, 1, 200);

        var entry = Assert.Single(AudiobookLibrary.AssembleOwned(_root, [item]));   // once, not twice
        Assert.True(entry.IsDownloaded);
        Assert.Equal(book.Identifier, entry.SourceKey);   // carried over from the acquisition record
    }
}
