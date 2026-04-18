// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class FileScannerTests : IDisposable
{
    private readonly string _tempDir;

    public FileScannerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "OrgZ-FileScanner-tests-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string Touch(string relativePath, long sizeBytes = 0)
    {
        var fullPath = Path.Combine(_tempDir, relativePath);
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);

        if (sizeBytes > 0)
        {
            File.WriteAllBytes(fullPath, new byte[sizeBytes]);
        }
        else
        {
            File.WriteAllText(fullPath, "");
        }

        return fullPath;
    }

    // ===== IsSupportedExtension =====

    [Theory]
    [InlineData("track.mp3",   true)]
    [InlineData("track.flac",  true)]
    [InlineData("track.m4a",   true)]
    [InlineData("track.aac",   true)]
    [InlineData("track.ogg",   true)]
    [InlineData("track.wav",   true)]
    [InlineData("track.wma",   true)]
    [InlineData("track.ape",   true)]
    [InlineData("track.opus",  true)]
    [InlineData("TRACK.MP3",   true)]    // case-insensitive
    [InlineData("track.Flac",  true)]
    [InlineData("track.txt",   false)]
    [InlineData("track.jpg",   false)]
    [InlineData("track",       false)]   // no extension
    [InlineData("",            false)]
    [InlineData("track.mp4",   false)]   // mp4 video, not audio
    public void IsSupportedExtension_recognizes_audio_extensions(string filename, bool expected)
    {
        Assert.Equal(expected, FileScanner.IsSupportedExtension(filename));
    }

    [Fact]
    public void IsSupportedExtension_works_with_full_paths()
    {
        Assert.True(FileScanner.IsSupportedExtension(@"C:\Music\Album\track.mp3"));
        Assert.False(FileScanner.IsSupportedExtension(@"C:\Music\Album\notes.txt"));
    }

    // ===== CreateMediaItemFromPath =====

    [Fact]
    public void CreateMediaItemFromPath_returns_null_for_unsupported_extension()
    {
        var path = Touch("notes.txt");
        Assert.Null(FileScanner.CreateMediaItemFromPath(path));
    }

    [Fact]
    public void CreateMediaItemFromPath_returns_null_when_file_does_not_exist()
    {
        var path = Path.Combine(_tempDir, "does-not-exist.mp3");
        Assert.Null(FileScanner.CreateMediaItemFromPath(path));
    }

    [Fact]
    public void CreateMediaItemFromPath_populates_basic_metadata()
    {
        var path = Touch("song.mp3", sizeBytes: 1234);
        var item = FileScanner.CreateMediaItemFromPath(path);

        Assert.NotNull(item);
        Assert.Equal(path, item!.Id);
        Assert.Equal(MediaKind.Music, item.Kind);
        Assert.Equal(path, item.FilePath);
        Assert.Equal("song.mp3", item.FileName);
        Assert.Equal(".mp3", item.Extension);
        Assert.Equal(1234, item.FileSize);
        Assert.NotNull(item.LastModified);
    }

    [Fact]
    public void CreateMediaItemFromPath_handles_zero_byte_file()
    {
        var path = Touch("empty.flac");
        var item = FileScanner.CreateMediaItemFromPath(path);

        Assert.NotNull(item);
        Assert.Equal(0, item!.FileSize);
    }

    // ===== ScanDirectoryAsync =====

    [Fact]
    public async Task ScanDirectoryAsync_returns_empty_for_missing_directory()
    {
        var nonExistent = Path.Combine(_tempDir, "does-not-exist");
        var items = await FileScanner.ScanDirectoryAsync(nonExistent);
        Assert.Empty(items);
    }

    [Fact]
    public async Task ScanDirectoryAsync_returns_empty_for_null_or_empty_path()
    {
        Assert.Empty(await FileScanner.ScanDirectoryAsync(""));
        Assert.Empty(await FileScanner.ScanDirectoryAsync(null!));
    }

    [Fact]
    public async Task ScanDirectoryAsync_finds_audio_files_skips_others()
    {
        Touch("a.mp3");
        Touch("b.flac");
        Touch("notes.txt");
        Touch("cover.jpg");

        var items = await FileScanner.ScanDirectoryAsync(_tempDir);

        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.FileName == "a.mp3");
        Assert.Contains(items, i => i.FileName == "b.flac");
    }

    [Fact]
    public async Task ScanDirectoryAsync_recursive_finds_files_in_subdirectories()
    {
        Touch("top.mp3");
        Touch(Path.Combine("sub", "deep.flac"));
        Touch(Path.Combine("sub", "deeper", "ogg-track.ogg"));

        var items = await FileScanner.ScanDirectoryAsync(_tempDir, recursive: true);

        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.FileName == "top.mp3");
        Assert.Contains(items, i => i.FileName == "deep.flac");
        Assert.Contains(items, i => i.FileName == "ogg-track.ogg");
    }

    [Fact]
    public async Task ScanDirectoryAsync_non_recursive_skips_subdirectories()
    {
        Touch("top.mp3");
        Touch(Path.Combine("sub", "deep.flac"));

        var items = await FileScanner.ScanDirectoryAsync(_tempDir, recursive: false);

        Assert.Single(items);
        Assert.Equal("top.mp3", items[0].FileName);
    }

    [Fact]
    public async Task ScanDirectoryAsync_respects_cancellation()
    {
        for (int i = 0; i < 50; i++)
        {
            Touch($"track-{i:D3}.mp3");
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();   // pre-cancel

        // Task.Run(_, ct) with a pre-cancelled token throws TaskCanceledException before
        // the work runs. If the token is cancelled mid-scan, the inner loop bails on the
        // next iteration and returns a partial list. Either is correct cancellation behavior.
        try
        {
            var items = await FileScanner.ScanDirectoryAsync(_tempDir, recursive: true, cts.Token);
            Assert.True(items.Count < 50, $"Expected partial result, got {items.Count} items");
        }
        catch (TaskCanceledException)
        {
            // Acceptable — the runtime cancelled the task before our loop got a chance to run
        }
    }

    [Fact]
    public async Task ScanDirectoryAsync_returns_empty_for_directory_with_no_audio_files()
    {
        Touch("notes.txt");
        Touch("cover.jpg");
        Touch(Path.Combine("sub", "doc.pdf"));

        var items = await FileScanner.ScanDirectoryAsync(_tempDir);
        Assert.Empty(items);
    }
}
