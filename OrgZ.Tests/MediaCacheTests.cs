// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

/// <summary>
/// MediaCache tests use a fresh temp DB file per test class instance.
/// xUnit creates one instance per test method, so each test gets an isolated DB.
/// </summary>
[Collection("MediaCache")]
public class MediaCacheTests : IDisposable
{
    private readonly string _tempDbPath;

    public MediaCacheTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"orgz-test-{Guid.NewGuid():N}.db");
        MediaCache.OverrideCachePath(_tempDbPath);
        MediaCache.EnsureCreated();
    }

    public void Dispose()
    {
        // Restore default before deleting so subsequent tests don't accidentally hit a missing path
        MediaCache.OverrideCachePath(null);

        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch
        {
            // best effort - Windows may still hold a handle for a moment
        }
    }

    /// <summary>
    /// PlaylistTracks has a FK on Media(Id), so a Media row must exist before we can reference its Id.
    /// This helper inserts minimal music rows so the FK is satisfied.
    /// </summary>
    private static void SeedMedia(params string[] ids)
    {
        foreach (var id in ids)
        {
            MediaCache.UpsertMusic(Music(id, title: $"Title {id}"));
        }
    }

    // -- Playlist CRUD --

    [Fact]
    public void CreatePlaylist_ReturnsAutoIncrementId()
    {
        var id1 = MediaCache.CreatePlaylist("Chill");
        var id2 = MediaCache.CreatePlaylist("Workout");

        Assert.True(id1 > 0);
        Assert.True(id2 > id1);
    }

    [Fact]
    public void LoadAllPlaylists_ReturnsAllOrderedByName()
    {
        MediaCache.CreatePlaylist("Zeta");
        MediaCache.CreatePlaylist("Alpha");
        MediaCache.CreatePlaylist("Mike");

        var all = MediaCache.LoadAllPlaylists();

        Assert.Equal(3, all.Count);
        Assert.Equal("Alpha", all[0].Name);
        Assert.Equal("Mike", all[1].Name);
        Assert.Equal("Zeta", all[2].Name);
    }

    [Fact]
    public void RenamePlaylist_UpdatesName()
    {
        var id = MediaCache.CreatePlaylist("Old Name");
        MediaCache.RenamePlaylist(id, "New Name");

        var all = MediaCache.LoadAllPlaylists();
        var renamed = all.Single(p => p.Id == id);
        Assert.Equal("New Name", renamed.Name);
    }

    [Fact]
    public void DeletePlaylist_RemovesIt()
    {
        var keep = MediaCache.CreatePlaylist("Keep");
        var doomed = MediaCache.CreatePlaylist("Delete Me");

        MediaCache.DeletePlaylist(doomed);

        var all = MediaCache.LoadAllPlaylists();
        Assert.Single(all);
        Assert.Equal(keep, all[0].Id);
    }

    // -- Track membership --

    [Fact]
    public void AddTrackToPlaylist_AppendsWithIncreasingSortOrder()
    {
        SeedMedia("track-a", "track-b", "track-c");
        var id = MediaCache.CreatePlaylist("My List");
        MediaCache.AddTrackToPlaylist(id, "track-a");
        MediaCache.AddTrackToPlaylist(id, "track-b");
        MediaCache.AddTrackToPlaylist(id, "track-c");

        var ids = MediaCache.GetPlaylistTrackIds(id);

        Assert.Equal(3, ids.Count);
        Assert.Equal("track-a", ids[0]);
        Assert.Equal("track-b", ids[1]);
        Assert.Equal("track-c", ids[2]);
    }

    [Fact]
    public void AddTrackToPlaylist_DuplicateIsIgnored()
    {
        SeedMedia("track-a");
        var id = MediaCache.CreatePlaylist("My List");
        MediaCache.AddTrackToPlaylist(id, "track-a");
        MediaCache.AddTrackToPlaylist(id, "track-a"); // duplicate

        var ids = MediaCache.GetPlaylistTrackIds(id);
        Assert.Single(ids);
    }

    [Fact]
    public void RemoveTrackFromPlaylist_RemovesIt()
    {
        SeedMedia("track-a", "track-b", "track-c");
        var id = MediaCache.CreatePlaylist("My List");
        MediaCache.AddTrackToPlaylist(id, "track-a");
        MediaCache.AddTrackToPlaylist(id, "track-b");
        MediaCache.AddTrackToPlaylist(id, "track-c");

        MediaCache.RemoveTrackFromPlaylist(id, "track-b");

        var ids = MediaCache.GetPlaylistTrackIds(id);
        Assert.Equal(2, ids.Count);
        Assert.Equal("track-a", ids[0]);
        Assert.Equal("track-c", ids[1]);
    }

    [Fact]
    public void GetPlaylistTrackIds_EmptyPlaylist_ReturnsEmpty()
    {
        var id = MediaCache.CreatePlaylist("Empty");
        Assert.Empty(MediaCache.GetPlaylistTrackIds(id));
    }

    [Fact]
    public void GetPlaylistTrackIds_NonexistentPlaylist_ReturnsEmpty()
    {
        Assert.Empty(MediaCache.GetPlaylistTrackIds(99999));
    }

    // -- Reordering --

    [Fact]
    public void ReorderPlaylistTracks_PersistsNewOrder()
    {
        SeedMedia("a", "b", "c", "d");
        var id = MediaCache.CreatePlaylist("My List");
        MediaCache.AddTrackToPlaylist(id, "a");
        MediaCache.AddTrackToPlaylist(id, "b");
        MediaCache.AddTrackToPlaylist(id, "c");
        MediaCache.AddTrackToPlaylist(id, "d");

        // Reverse it
        MediaCache.ReorderPlaylistTracks(id, ["d", "c", "b", "a"]);

        var ids = MediaCache.GetPlaylistTrackIds(id);
        Assert.Equal(["d", "c", "b", "a"], ids);
    }

    [Fact]
    public void ReorderPlaylistTracks_PartialReorder()
    {
        SeedMedia("a", "b", "c");
        var id = MediaCache.CreatePlaylist("My List");
        MediaCache.AddTrackToPlaylist(id, "a");
        MediaCache.AddTrackToPlaylist(id, "b");
        MediaCache.AddTrackToPlaylist(id, "c");

        // Move "a" to position 2 (between b and c → result: b, a, c)
        MediaCache.ReorderPlaylistTracks(id, ["b", "a", "c"]);

        Assert.Equal(["b", "a", "c"], MediaCache.GetPlaylistTrackIds(id));
    }

    [Fact]
    public void ReorderPlaylistTracks_DoesNotAffectOtherPlaylists()
    {
        SeedMedia("x", "y");
        var p1 = MediaCache.CreatePlaylist("One");
        var p2 = MediaCache.CreatePlaylist("Two");

        MediaCache.AddTrackToPlaylist(p1, "x");
        MediaCache.AddTrackToPlaylist(p1, "y");
        MediaCache.AddTrackToPlaylist(p2, "x");
        MediaCache.AddTrackToPlaylist(p2, "y");

        MediaCache.ReorderPlaylistTracks(p1, ["y", "x"]);

        Assert.Equal(["y", "x"], MediaCache.GetPlaylistTrackIds(p1));
        Assert.Equal(["x", "y"], MediaCache.GetPlaylistTrackIds(p2));
    }

    // -- Cascade --

    [Fact]
    public void DeletePlaylist_CascadesPlaylistTracks()
    {
        SeedMedia("a", "b");
        var id = MediaCache.CreatePlaylist("Doomed");
        MediaCache.AddTrackToPlaylist(id, "a");
        MediaCache.AddTrackToPlaylist(id, "b");

        MediaCache.DeletePlaylist(id);

        // Track entries should be gone
        Assert.Empty(MediaCache.GetPlaylistTrackIds(id));
    }

    // -- Isolation --

    [Fact]
    public void TwoPlaylists_TracksAreIndependent()
    {
        SeedMedia("song-pop-1", "song-pop-2", "song-rock-1");
        var p1 = MediaCache.CreatePlaylist("Pop");
        var p2 = MediaCache.CreatePlaylist("Rock");

        MediaCache.AddTrackToPlaylist(p1, "song-pop-1");
        MediaCache.AddTrackToPlaylist(p1, "song-pop-2");
        MediaCache.AddTrackToPlaylist(p2, "song-rock-1");

        Assert.Equal(2, MediaCache.GetPlaylistTrackIds(p1).Count);
        Assert.Single(MediaCache.GetPlaylistTrackIds(p2));
    }

    // -- The row tick (IsIgnored) --
    // The old IgnoreMedia / RestoreMedia pair went with the Ignored view in 0.9.16; what
    // remains is SetIgnored, whose contract is that playlists are left ALONE.

    [Fact]
    public void SetIgnored_toggles_the_flag_and_leaves_playlists_alone()
    {
        SeedMedia("shared", "a");
        var p1 = MediaCache.CreatePlaylist("One");
        MediaCache.AddTrackToPlaylist(p1, "shared");
        MediaCache.AddTrackToPlaylist(p1, "a");

        MediaCache.SetIgnored("shared", true);

        Assert.True(MediaCache.LoadAll().Single(m => m.Id == "shared").IsIgnored);
        // Unticking a row must never cost the user their playlist membership - that was
        // exactly the old Ignore behaviour, and why it couldn't be allowed to linger.
        Assert.Contains("shared", MediaCache.GetPlaylistTrackIds(p1));
        Assert.Contains("a", MediaCache.GetPlaylistTrackIds(p1));

        MediaCache.SetIgnored("shared", false);
        Assert.False(MediaCache.LoadAll().Single(m => m.Id == "shared").IsIgnored);
    }

    [Fact]
    public void UpsertMusic_PreservesIsIgnoredOnConflict()
    {
        // Initial insert
        var item = Music("x", title: "Original Title");
        MediaCache.UpsertMusic(item);

        // User unticks the row
        MediaCache.SetIgnored("x", true);

        // Scanner re-sees the file and upserts with updated metadata (this mirrors a real rescan)
        var rescanned = Music("x", title: "New Title");
        MediaCache.UpsertMusic(rescanned);

        // IsIgnored should persist even though Title got updated
        var loaded = MediaCache.LoadAll().Single(m => m.Id == "x");
        Assert.True(loaded.IsIgnored);
        Assert.Equal("New Title", loaded.Title);
    }

    [Fact]
    public void UpsertMusic_PreservesPlayCountOnConflict()
    {
        var item = Music("x", title: "Original Title");
        MediaCache.UpsertMusic(item);

        // The user plays it twice
        MediaCache.IncrementPlayCount("x");
        MediaCache.IncrementPlayCount("x");

        // Scanner re-sees the file and upserts fresh metadata; its item carries PlayCount 0.
        // The count is the user's history - the rescan must not zero it.
        var rescanned = Music("x", title: "New Title");
        MediaCache.UpsertMusic(rescanned);

        var loaded = MediaCache.LoadAll().Single(m => m.Id == "x");
        Assert.Equal(2, loaded.PlayCount);
        Assert.Equal("New Title", loaded.Title);
    }

    [Fact]
    public void RemoveLibraryFiles_CleansPlaylistMemberships()
    {
        // The schema's ON DELETE CASCADE never fires (foreign_keys is off per connection),
        // so the delete path must clean memberships itself - ghosts used to pile up forever.
        SeedMedia("x");
        var p = MediaCache.CreatePlaylist("MyList");
        MediaCache.AddTrackToPlaylist(p, "x");

        MediaCache.RemoveLibraryFiles(["x"]);

        Assert.Empty(MediaCache.GetPlaylistTrackIds(p));
    }

}
