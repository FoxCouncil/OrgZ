// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Tests;

public class PlaylistFolderSyncTests : IDisposable
{
    private readonly string _root;

    public PlaylistFolderSyncTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"orgz-plsync-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Touch(string relativePath)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, string.Empty);
        return full;
    }

    private static MediaItem Track(string path, string artist, string title) => new()
    {
        Id = path,
        Kind = MediaKind.Music,
        FilePath = path,
        Artist = artist,
        Title = title,
        Duration = TimeSpan.FromSeconds(200),
    };

    [Theory]
    [InlineData("Road Trip", "Road Trip")]
    [InlineData("AC/DC", "AC-DC")]
    [InlineData("what:now?", "what-now-")]
    [InlineData("  ", "Playlist")]
    [InlineData("trailing. ", "trailing")]
    public void SanitizeFileName_ProducesAnOpenableName(string input, string expected)
    {
        Assert.Equal(expected, PlaylistFolderSync.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_KeepsDistinctNamesDistinct()
    {
        Assert.NotEqual(
            PlaylistFolderSync.SanitizeFileName("AC/DC"),
            PlaylistFolderSync.SanitizeFileName("ACDC"));
    }

    [Fact]
    public void Write_UsesPathsRelativeToTheMusicFolder()
    {
        var track = Touch(Path.Combine("Nine Inch Nails", "Year Zero", "07 - Capital G.flac"));

        PlaylistFolderSync.Write(_root, "Angry", [Track(track, "Nine Inch Nails", "Capital G")]);

        var written = File.ReadAllText(Path.Combine(_root, "Angry.m3u8"));

        Assert.Contains(Path.Combine("Nine Inch Nails", "Year Zero", "07 - Capital G.flac"), written);
        Assert.DoesNotContain(_root, written);
    }

    [Fact]
    public void Write_KeepsAbsolutePathsForTracksOutsideTheMusicFolder()
    {
        var outside = Path.Combine(Path.GetTempPath(), $"orgz-outside-{Guid.NewGuid():N}.flac");

        PlaylistFolderSync.Write(_root, "Mixed", [Track(outside, "Someone", "Elsewhere")]);

        var written = File.ReadAllText(Path.Combine(_root, "Mixed.m3u8"));

        Assert.Contains(outside, written);
        Assert.DoesNotContain("..", written);
    }

    [Fact]
    public void Write_RoundTripsThroughTheImporter()
    {
        var first = Touch(Path.Combine("A", "one.flac"));
        var second = Touch(Path.Combine("B", "two.flac"));

        PlaylistFolderSync.Write(_root, "Both", [Track(first, "A", "One"), Track(second, "B", "Two")]);

        var result = PlaylistImporter.Import(Path.Combine(_root, "Both.m3u8"));

        Assert.Equal("Both", result.Name);
        Assert.Equal([first, second], result.TrackPaths.Select(Path.GetFullPath));
    }

    [Fact]
    public void Write_LeavesNoTemporaryFileBehind()
    {
        PlaylistFolderSync.Write(_root, "Clean", [Track(Touch("a.flac"), "A", "One")]);

        Assert.Empty(Directory.GetFiles(_root, "*.orgztmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Discover_FindsPlaylistsAtEveryDepthButExcludesFavorites()
    {
        Touch("Road Trip.m3u8");
        Touch(Path.Combine("Soundtracks", "Nested.m3u8"));
        Touch("Favorites.m3u8");
        Touch("notes.txt");

        var found = PlaylistFolderSync.Discover(_root).Select(Path.GetFileName).ToList();

        Assert.Contains("Road Trip.m3u8", found);
        Assert.Contains("Nested.m3u8", found);
        Assert.DoesNotContain("Favorites.m3u8", found);
        Assert.DoesNotContain("notes.txt", found);
    }

    [Fact]
    public void Discover_SkipsOtherSubsystemsStorage()
    {
        Touch(Path.Combine(".podcasts", "feed.m3u8"));
        Touch(Path.Combine(".audiobooks", "book.m3u8"));
        Touch("Keep.m3u8");

        var found = PlaylistFolderSync.Discover(_root).Select(Path.GetFileName).ToList();

        Assert.Equal(["Keep.m3u8"], found);
    }

    [Fact]
    public void Delete_RemovesThePlaylistFileAndToleratesAMissingOne()
    {
        PlaylistFolderSync.Write(_root, "Temp", [Track(Touch("a.flac"), "A", "One")]);
        var path = Path.Combine(_root, "Temp.m3u8");
        Assert.True(File.Exists(path));

        PlaylistFolderSync.Delete(_root, "Temp");
        Assert.False(File.Exists(path));

        PlaylistFolderSync.Delete(_root, "Temp");
    }

    [Fact]
    public void Rename_LeavesOnlyTheNewFile()
    {
        var track = Track(Touch("a.flac"), "A", "One");

        PlaylistFolderSync.Write(_root, "Before", [track]);
        Assert.True(File.Exists(Path.Combine(_root, "Before.m3u8")));

        // What the rename path does: drop the old name, write the new one.
        PlaylistFolderSync.Delete(_root, "Before");
        PlaylistFolderSync.Write(_root, "After", [track]);

        Assert.False(File.Exists(Path.Combine(_root, "Before.m3u8")));
        Assert.True(File.Exists(Path.Combine(_root, "After.m3u8")));
        Assert.Equal("After", PlaylistImporter.Import(Path.Combine(_root, "After.m3u8")).Name);
    }

    [Fact]
    public void Rename_ToANameThatSanitizesTheSameRewritesInPlace()
    {
        var track = Track(Touch("a.flac"), "A", "One");

        PlaylistFolderSync.Write(_root, "AC/DC", [track]);
        PlaylistFolderSync.Write(_root, "AC:DC", [track]);

        Assert.Single(Directory.GetFiles(_root, "*.m3u8"));
        Assert.True(File.Exists(Path.Combine(_root, "AC-DC.m3u8")));
    }

    [Fact]
    public void Favorites_IsWrittenButNeverDiscovered()
    {
        PlaylistFolderSync.Write(_root, PlaylistFolderSync.FavoritesName, [Track(Touch("a.flac"), "A", "One")]);

        var path = Path.Combine(_root, "Favorites.m3u8");
        Assert.True(File.Exists(path));
        Assert.Single(PlaylistImporter.Import(path).TrackPaths);
        Assert.Empty(PlaylistFolderSync.Discover(_root));
    }

    [Fact]
    public void Favorites_RewriteReplacesTheWholeListRatherThanAppending()
    {
        var a = Track(Touch("a.flac"), "A", "One");
        var b = Track(Touch("b.flac"), "B", "Two");

        PlaylistFolderSync.Write(_root, PlaylistFolderSync.FavoritesName, [a, b]);
        PlaylistFolderSync.Write(_root, PlaylistFolderSync.FavoritesName, [a]);

        Assert.Single(PlaylistImporter.Import(Path.Combine(_root, "Favorites.m3u8")).TrackPaths);
    }

    [Fact]
    public void Write_UnchangedContentDoesNotTouchTheFile()
    {
        var track = Track(Touch("a.flac"), "A", "One");
        var path = Path.Combine(_root, "Stable.m3u8");

        PlaylistFolderSync.Write(_root, "Stable", [track]);
        var stamp = File.GetLastWriteTimeUtc(path);

        File.SetLastWriteTimeUtc(path, stamp.AddMinutes(-5));
        var moved = File.GetLastWriteTimeUtc(path);

        PlaylistFolderSync.Write(_root, "Stable", [track]);

        // Rewriting identical content is what fed the watcher/scanner loop.
        Assert.Equal(moved, File.GetLastWriteTimeUtc(path));
        Assert.Empty(Directory.GetFiles(_root, "*.orgztmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Write_ChangedContentDoesRewrite()
    {
        var a = Track(Touch("a.flac"), "A", "One");
        var b = Track(Touch("b.flac"), "B", "Two");
        var path = Path.Combine(_root, "Moving.m3u8");

        PlaylistFolderSync.Write(_root, "Moving", [a]);
        PlaylistFolderSync.Write(_root, "Moving", [a, b]);

        Assert.Equal(2, PlaylistImporter.Import(path).TrackPaths.Count);
    }

    [Fact]
    public void WasSelfWritten_SurvivesMoreThanOneWatcherEvent()
    {
        PlaylistFolderSync.Write(_root, "Echo", [Track(Touch("a.flac"), "A", "One")]);
        var path = Path.Combine(_root, "Echo.m3u8");

        // One write raises several events; every one of them must be suppressed.
        Assert.True(PlaylistFolderSync.WasSelfWritten(path));
        Assert.True(PlaylistFolderSync.WasSelfWritten(path));
        Assert.True(PlaylistFolderSync.WasSelfWritten(path));
    }

    [Fact]
    public void WriteTo_RewritesADiscoveredPlaylistWhereItWasFound()
    {
        var nested = Touch(Path.Combine("Soundtracks", "Nested.m3u8"));
        var track = Touch(Path.Combine("Soundtracks", "song.flac"));

        PlaylistFolderSync.WriteTo(nested, _root, "Nested", [Track(track, "A", "One")]);

        Assert.True(File.Exists(nested));
        Assert.False(File.Exists(Path.Combine(_root, "Nested.m3u8")));
        Assert.Contains(Path.Combine("Soundtracks", "song.flac"), File.ReadAllText(nested));
    }
}
