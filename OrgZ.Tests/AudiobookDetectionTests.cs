// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;
using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

/// <summary>
/// Library-side audiobook detection. The contract: .m4b IS an audiobook by container (decided at
/// scan time with no file IO), and a Music item whose tags self-identify (iTunes stik atom or an
/// audiobook genre) is promoted during analysis - never demoted. Also pins the library-scan
/// predicate (<see cref="MainWindowViewModel.IsLocalLibraryFile"/>) whose Source==null guard keeps
/// the folder-scan reconciliation from sweeping connected-device rows out of the library, and the
/// kind's round-trip through the on-device filesystem cache via the real scanner pipeline.
/// </summary>
public class AudiobookDetectionTests
{
    // ===== AudiobookDetector: the extension layer (no IO) =====

    [Theory]
    [InlineData(".m4b", true)]
    [InlineData(".M4B", true)]     // extension comparisons are case-insensitive everywhere else too
    [InlineData(".mp3", false)]
    [InlineData(".m4a", false)]    // plain m4a is music unless its TAGS say otherwise
    [InlineData("", false)]
    [InlineData(null, false)]
    public void M4b_is_the_audiobook_container(string? extension, bool expected)
    {
        Assert.Equal(expected, AudiobookDetector.IsAudiobookExtension(extension));
    }

    [Theory]
    [InlineData(@"C:\Library\Hyperion.m4b", MediaKind.Audiobook)]
    [InlineData(@"C:\Library\Subdivisions.mp3", MediaKind.Music)]
    [InlineData("/library/book.M4B", MediaKind.Audiobook)]
    public void KindForPath_classifies_by_extension_only(string path, MediaKind expected)
    {
        Assert.Equal(expected, AudiobookDetector.KindForPath(path));
    }

    // ===== AudiobookDetector: the tag layer's pure pieces =====

    [Theory]
    [InlineData(new byte[] { 2 }, true)]              // 1-byte payload
    [InlineData(new byte[] { 0, 0, 0, 2 }, true)]     // big-endian int payload - value is the last byte
    [InlineData(new byte[] { 1 }, false)]             // stik 1 = normal music
    [InlineData(new byte[] { 0, 0, 0, 10 }, false)]   // stik 10 = TV show
    [InlineData(new byte[0], false)]
    [InlineData(null, false)]
    public void Stik_atom_value_2_means_audiobook(byte[]? data, bool expected)
    {
        Assert.Equal(expected, AudiobookDetector.IsAudiobookStik(data));
    }

    [Theory]
    [InlineData("Audiobook", true)]
    [InlineData("Audiobooks", true)]
    [InlineData("audiobook; Science Fiction", true)]
    [InlineData("Rock", false)]
    [InlineData("Spoken Word", false)]   // comedy albums and poetry live here - too broad to auto-promote
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Genre_containing_audiobook_promotes(string? genres, bool expected)
    {
        Assert.Equal(expected, AudiobookDetector.IsAudiobookGenre(genres));
    }

    // ===== FileScanner: .m4b is a supported, correctly-kinded library file =====

    [Fact]
    public void FileScanner_supports_m4b()
    {
        Assert.True(FileScanner.IsSupportedExtension(@"C:\Library\Hyperion.m4b"));
    }

    [Fact]
    public void Scanned_m4b_arrives_as_an_audiobook_and_mp3_as_music()
    {
        var dir = MakeTempDir("orgz-abscan");
        try
        {
            var m4b = Path.Combine(dir, "Hyperion.m4b");
            var mp3 = Path.Combine(dir, "Subdivisions.mp3");
            File.WriteAllBytes(m4b, new byte[16]);
            File.WriteAllBytes(mp3, new byte[16]);

            Assert.Equal(MediaKind.Audiobook, FileScanner.CreateMediaItemFromPath(m4b)!.Kind);
            Assert.Equal(MediaKind.Music, FileScanner.CreateMediaItemFromPath(mp3)!.Kind);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ===== Analyzer promotion: genre tag flips Music → Audiobook during analysis =====

    [Fact]
    public void Analysis_promotes_a_music_item_whose_genre_says_audiobook()
    {
        var dir = MakeTempDir("orgz-abgenre");
        try
        {
            var wav = Path.Combine(dir, "book.wav");
            WriteMinimalWav(wav);
            WriteGenre(wav, "Audiobook");

            var item = FileScanner.CreateMediaItemFromPath(wav)!;
            Assert.Equal(MediaKind.Music, item.Kind);   // extension alone says music

            AudioFileAnalyzer.AnalyzeFile(item);

            Assert.Equal(MediaKind.Audiobook, item.Kind);
            Assert.True(item.IsAnalyzed);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Analysis_leaves_ordinary_music_alone()
    {
        var dir = MakeTempDir("orgz-abrock");
        try
        {
            var wav = Path.Combine(dir, "song.wav");
            WriteMinimalWav(wav);
            WriteGenre(wav, "Rock");

            var item = FileScanner.CreateMediaItemFromPath(wav)!;
            AudioFileAnalyzer.AnalyzeFile(item);

            Assert.Equal(MediaKind.Music, item.Kind);
            Assert.True(item.IsAnalyzed);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Analysis_survives_an_unreadable_m4b_without_demoting_it()
    {
        // A corrupt .m4b must stay an audiobook (kind came from the container) and must not
        // throw - the analyzer's failure contract is an Issue + IsAnalyzed, same as music.
        var dir = MakeTempDir("orgz-abbad");
        try
        {
            var m4b = Path.Combine(dir, "corrupt.m4b");
            File.WriteAllBytes(m4b, new byte[32]);

            var item = FileScanner.CreateMediaItemFromPath(m4b)!;
            AudioFileAnalyzer.AnalyzeFile(item);

            Assert.Equal(MediaKind.Audiobook, item.Kind);
            Assert.True(item.IsAnalyzed);
            Assert.Contains(item.Issues, i => i.StartsWith("Failed to analyze", StringComparison.Ordinal));
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ===== The library-scan predicate: what the folder reconciliation may touch =====

    [Fact]
    public void Local_music_and_audiobooks_are_library_files()
    {
        Assert.True(MainWindowViewModel.IsLocalLibraryFile(LocalFile(MediaKind.Music)));
        Assert.True(MainWindowViewModel.IsLocalLibraryFile(LocalFile(MediaKind.Audiobook)));
    }

    [Fact]
    public void Device_and_cd_rows_are_not_library_files()
    {
        // THE guard: device tracks are Kind=Music with real FilePaths - without the Source check
        // a library-folder rescan while an iPod is connected would sweep them out of _allItems.
        Assert.False(MainWindowViewModel.IsLocalLibraryFile(LocalFile(MediaKind.Music, source: @"device:E:\")));
        Assert.False(MainWindowViewModel.IsLocalLibraryFile(LocalFile(MediaKind.Music, source: "cdda")));
    }

    [Fact]
    public void Radio_and_pathless_rows_are_not_library_files()
    {
        Assert.False(MainWindowViewModel.IsLocalLibraryFile(Radio("r1")));
        Assert.False(MainWindowViewModel.IsLocalLibraryFile(Music("no-path")));   // FilePath null
    }

    // ===== FilesystemLibraryScanner: kind assignment + cache round-trip on a fake device =====

    [Fact]
    public void Device_walk_kinds_m4b_as_audiobook_and_the_cache_preserves_it()
    {
        var mount = MakeTempDir("orgz-abmount");
        try
        {
            var wav = Path.Combine(mount, "Music", "song.wav");
            var m4b = Path.Combine(mount, "Audiobooks", "book.m4b");
            Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
            Directory.CreateDirectory(Path.GetDirectoryName(m4b)!);
            WriteMinimalWav(wav);
            File.WriteAllBytes(m4b, new byte[32]);   // analysis fails safe; kind is by extension

            var device = new ConnectedDevice { MountPath = mount, DeviceType = DeviceType.RockboxOther, Name = "Rockbox" };

            // First scan: fresh analysis assigns kinds and persists them to /.orgz/library.db.
            var first = FilesystemLibraryScanner.Scan(device);
            Assert.Equal(MediaKind.Music, first.Tracks.Single(t => t.FilePath == wav).Kind);
            Assert.Equal(MediaKind.Audiobook, first.Tracks.Single(t => t.FilePath == m4b).Kind);

            // Second scan: size+mtime unchanged, so both rows come straight from the device cache -
            // the audiobook kind must survive the round-trip (it used to flatten to Music).
            var second = FilesystemLibraryScanner.Scan(device);
            Assert.Equal(MediaKind.Audiobook, second.Tracks.Single(t => t.FilePath == m4b).Kind);
        }
        finally
        {
            TryDelete(mount);
        }
    }

    // ===== plumbing =====

    private static MediaItem LocalFile(MediaKind kind, string? source = null)
        => new() { Id = "x", Kind = kind, FilePath = @"C:\Library\x.mp3", Source = source };

    private static string MakeTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A tiny but valid silent PCM WAV - enough for TagLib to analyze and tag.</summary>
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

    private static void WriteGenre(string path, string genre)
    {
        using var file = TagLib.File.Create(path);
        var id3 = (TagLib.Id3v2.Tag)file.GetTag(TagLib.TagTypes.Id3v2, create: true);
        id3.Genres = [genre];
        file.Save();
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) { Directory.Delete(dir, recursive: true); } } catch { /* temp cleanup */ }
    }
}

/// <summary>
/// Audiobook rows through the library cache - needs the shared MediaCache DB override, hence the
/// collection (same isolation pattern as the other MediaCache test classes).
/// </summary>
[Collection("MediaCache")]
public class AudiobookMediaCacheTests : IDisposable
{
    private readonly string _tempDbPath;

    public AudiobookMediaCacheTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"orgz-test-{Guid.NewGuid():N}.db");
        MediaCache.OverrideCachePath(_tempDbPath);
        MediaCache.EnsureCreated();
    }

    public void Dispose()
    {
        MediaCache.OverrideCachePath(null);
        try { if (File.Exists(_tempDbPath)) File.Delete(_tempDbPath); } catch { }
    }

    private static MediaItem Audiobook(string id) => new()
    {
        Id = id,
        Kind = MediaKind.Audiobook,
        FilePath = $@"C:\Library\{id}.m4b",
        FileName = $"{id}.m4b",
        Extension = ".m4b",
        Title = id,
    };

    [Fact]
    public void Audiobook_rows_round_trip_as_analyzed()
    {
        MediaCache.UpsertMusic(Audiobook("hyperion"));

        var loaded = MediaCache.LoadAll().Single(i => i.Id == "hyperion");
        Assert.Equal(MediaKind.Audiobook, loaded.Kind);
        // Cached local files were analyzed before being saved - audiobooks included; without this
        // the scan's delta logic would re-run TagLib on every unchanged audiobook at startup.
        Assert.True(loaded.IsAnalyzed);
    }

    [Fact]
    public void RemoveLibraryFiles_removes_audiobook_rows()
    {
        MediaCache.UpsertMusic(Audiobook("hyperion"));

        MediaCache.RemoveLibraryFiles(["hyperion"]);

        Assert.DoesNotContain(MediaCache.LoadAll(), i => i.Id == "hyperion");
    }
}
