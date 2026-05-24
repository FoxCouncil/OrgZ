// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using FsChangeKind = OrgZ.Services.MusicFolderWatcher.FsChangeKind;
using FsEvent = OrgZ.Services.MusicFolderWatcher.FsEvent;

namespace OrgZ.Tests;

public class MusicFolderWatcherTests
{
    private static Dictionary<string, FsChangeKind> NewPending()
        => new(StringComparer.OrdinalIgnoreCase);

    // -- Coalesce: the FS-event state machine --

    [Fact]
    public void Coalesce_FirstEventForPath_StoredAsIs()
    {
        var pending = NewPending();

        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Created, "a.mp3"));

        Assert.Equal(FsChangeKind.Created, pending["a.mp3"]);
    }

    [Fact]
    public void Coalesce_DeletedThenCreated_BecomesChanged()
    {
        // File replaced in place - deleted then re-created collapses to a content change.
        var pending = NewPending();

        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Deleted, "a.mp3"));
        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Created, "a.mp3"));

        Assert.Equal(FsChangeKind.Changed, pending["a.mp3"]);
    }

    [Fact]
    public void Coalesce_CreatedThenDeleted_CancelsOut()
    {
        // A temp file that appeared and vanished within the window is a net no-op.
        var pending = NewPending();

        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Created, "a.mp3"));
        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Deleted, "a.mp3"));

        Assert.False(pending.ContainsKey("a.mp3"));
    }

    // FsChangeKind is internal, so a public [Theory] can't take it directly as a parameter -
    // pass the names and parse them in the body.
    [Theory]
    [InlineData("Created", "Created", "Created")]
    [InlineData("Created", "Changed", "Changed")]
    [InlineData("Changed", "Changed", "Changed")]
    [InlineData("Changed", "Created", "Created")]
    [InlineData("Changed", "Deleted", "Deleted")]
    [InlineData("Deleted", "Deleted", "Deleted")]
    [InlineData("Deleted", "Changed", "Changed")]
    public void Coalesce_LatestWinsExceptForSpecialCases(string first, string second, string expected)
    {
        var pending = NewPending();

        MusicFolderWatcher.Coalesce(pending, new FsEvent(Kind(first), "a.mp3"));
        MusicFolderWatcher.Coalesce(pending, new FsEvent(Kind(second), "a.mp3"));

        Assert.Equal(Kind(expected), pending["a.mp3"]);
    }

    private static FsChangeKind Kind(string name) => Enum.Parse<FsChangeKind>(name);

    [Fact]
    public void Coalesce_DifferentPaths_TrackedIndependently()
    {
        var pending = NewPending();

        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Created, "a.mp3"));
        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Deleted, "b.mp3"));

        Assert.Equal(FsChangeKind.Created, pending["a.mp3"]);
        Assert.Equal(FsChangeKind.Deleted, pending["b.mp3"]);
        Assert.Equal(2, pending.Count);
    }

    [Fact]
    public void Coalesce_PathMatchingIsCaseInsensitive()
    {
        // The consumer builds its pending map with an OrdinalIgnoreCase comparer so that
        // a case-only re-report folds into the existing entry rather than duplicating it.
        var pending = NewPending();

        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Deleted, "Song.mp3"));
        MusicFolderWatcher.Coalesce(pending, new FsEvent(FsChangeKind.Created, "song.mp3"));

        Assert.Single(pending);
        Assert.Equal(FsChangeKind.Changed, pending.Values.Single());
    }

    // -- IsTempFile --

    [Theory]
    [InlineData("song.mp3", false)]
    [InlineData("song.flac", false)]
    [InlineData("My Song (Live).m4a", false)]
    public void IsTempFile_RealMediaFiles_AreNotTemp(string name, bool expected)
    {
        Assert.Equal(expected, MusicFolderWatcher.IsTempFile(MakePath(name)));
    }

    [Theory]
    [InlineData("download.tmp")]
    [InlineData("download.part")]
    [InlineData("download.crdownload")]
    [InlineData("download.partial")]
    [InlineData("DOWNLOAD.TMP")]      // suffix match is case-insensitive
    [InlineData("track.Part")]
    public void IsTempFile_KnownTempSuffixes_AreTemp(string name)
    {
        Assert.True(MusicFolderWatcher.IsTempFile(MakePath(name)));
    }

    [Theory]
    [InlineData(".hidden")]           // dotfile
    [InlineData(".DS_Store")]
    [InlineData("~lock.mp3")]         // editor/lock prefix
    public void IsTempFile_HiddenAndLockPrefixes_AreTemp(string name)
    {
        Assert.True(MusicFolderWatcher.IsTempFile(MakePath(name)));
    }

    [Fact]
    public void IsTempFile_EmptyFileName_IsTemp()
    {
        // A directory-style path with no file component can't be a real media file.
        var dirPath = Path.Combine(Path.GetTempPath(), "subdir") + Path.DirectorySeparatorChar;

        Assert.True(MusicFolderWatcher.IsTempFile(dirPath));
    }

    private static string MakePath(string fileName)
        => Path.Combine(Path.GetTempPath(), "OrgZWatcherTests", fileName);
}
