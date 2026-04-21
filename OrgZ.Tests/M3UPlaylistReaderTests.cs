// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

public class M3UPlaylistReaderTests : IDisposable
{
    private readonly string _mountPath;
    private readonly string _playlistsDir;

    public M3UPlaylistReaderTests()
    {
        _mountPath = Path.Combine(Path.GetTempPath(), "OrgZ-M3U-" + Path.GetRandomFileName());
        _playlistsDir = Path.Combine(_mountPath, "Playlists");
        Directory.CreateDirectory(_playlistsDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_mountPath)) Directory.Delete(_mountPath, recursive: true); } catch { }
    }

    private string WritePlaylist(string filename, string content)
    {
        var path = Path.Combine(_playlistsDir, filename);
        File.WriteAllText(path, content);
        return path;
    }

    // ===== Read (whole folder) =====

    [Fact]
    public void Read_returns_empty_when_Playlists_folder_missing()
    {
        // Fresh mount path with no Playlists subfolder
        var freshMount = Path.Combine(Path.GetTempPath(), "OrgZ-M3U-nofolder-" + Path.GetRandomFileName());
        Directory.CreateDirectory(freshMount);
        try
        {
            Assert.Empty(M3UPlaylistReader.Read(freshMount));
        }
        finally
        {
            Directory.Delete(freshMount, true);
        }
    }

    [Fact]
    public void Read_returns_empty_when_Playlists_folder_empty()
    {
        Assert.Empty(M3UPlaylistReader.Read(_mountPath));
    }

    [Fact]
    public void Read_picks_up_both_m3u_and_m3u8_extensions()
    {
        WritePlaylist("a.m3u", "/Music/track1.mp3");
        WritePlaylist("b.m3u8", "/Music/track2.mp3");
        WritePlaylist("ignored.txt", "not a playlist");

        var pls = M3UPlaylistReader.Read(_mountPath);
        Assert.Equal(2, pls.Count);
        Assert.Contains(pls, p => p.Key == "a");
        Assert.Contains(pls, p => p.Key == "b");
    }

    [Fact]
    public void Read_is_case_insensitive_on_extension()
    {
        WritePlaylist("upper.M3U", "/Music/a.mp3");
        WritePlaylist("mixed.M3u8", "/Music/b.mp3");

        var pls = M3UPlaylistReader.Read(_mountPath);
        Assert.Equal(2, pls.Count);
    }

    // ===== ReadOne =====

    [Fact]
    public void ReadOne_uses_filename_stem_as_name_when_no_directive()
    {
        var path = WritePlaylist("Road Trip.m3u", "/Music/a.mp3\n/Music/b.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        Assert.Equal("Road Trip", pl!.Name);
        Assert.Equal("Road Trip", pl.Key);
    }

    [Fact]
    public void ReadOne_honors_PLAYLIST_directive_for_name()
    {
        var path = WritePlaylist("generic.m3u8", "#PLAYLIST:My Workout Mix\n/Music/a.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        Assert.Equal("My Workout Mix", pl!.Name);
        // Key still derived from filename — it's the stable identity
        Assert.Equal("generic", pl.Key);
    }

    [Fact]
    public void ReadOne_skips_EXTINF_and_EXTM3U_directives()
    {
        var path = WritePlaylist("mix.m3u", """
            #EXTM3U
            #EXTINF:213,Rush - Subdivisions
            /Music/subdivisions.mp3
            #EXTINF:180,Rush - YYZ
            /Music/yyz.mp3
            """);

        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);
        Assert.NotNull(pl);
        Assert.Equal(2, pl!.TrackIds.Count);
    }

    [Fact]
    public void ReadOne_skips_blank_lines()
    {
        var path = WritePlaylist("sparse.m3u", "\n\n/Music/a.mp3\n\n/Music/b.mp3\n\n\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        Assert.Equal(2, pl!.TrackIds.Count);
    }

    // ===== Path resolution =====

    [Fact]
    public void ReadOne_resolves_rockbox_absolute_path_against_mount_root()
    {
        // Rockbox convention: "/Music/..." means relative to the mount root
        var path = WritePlaylist("abs.m3u", "/Music/Rush/Signals/01.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        var expected = Path.GetFullPath(Path.Combine(_mountPath, "Music", "Rush", "Signals", "01.mp3"));
        Assert.Equal(expected, pl!.TrackIds[0]);
    }

    [Fact]
    public void ReadOne_resolves_relative_path_against_playlist_directory()
    {
        var path = WritePlaylist("rel.m3u", "../Music/track.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        // ../Music/ from the Playlists dir = {mount}/Music/track.mp3
        var expected = Path.GetFullPath(Path.Combine(_mountPath, "Music", "track.mp3"));
        Assert.Equal(expected, pl!.TrackIds[0]);
    }

    [Fact]
    public void ReadOne_normalizes_backslashes_to_native_separators()
    {
        var path = WritePlaylist("winsep.m3u", "/Music\\Rush\\Signals\\01.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        var expected = Path.GetFullPath(Path.Combine(_mountPath, "Music", "Rush", "Signals", "01.mp3"));
        Assert.Equal(expected, pl!.TrackIds[0]);
    }

    [Fact]
    public void ReadOne_preserves_track_order()
    {
        var path = WritePlaylist("order.m3u", "/Music/1.mp3\n/Music/2.mp3\n/Music/3.mp3\n/Music/4.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        Assert.Equal(4, pl!.TrackIds.Count);
        Assert.EndsWith("1.mp3", pl.TrackIds[0]);
        Assert.EndsWith("2.mp3", pl.TrackIds[1]);
        Assert.EndsWith("3.mp3", pl.TrackIds[2]);
        Assert.EndsWith("4.mp3", pl.TrackIds[3]);
    }

    [Fact]
    public void ReadOne_empty_PLAYLIST_directive_falls_back_to_filename()
    {
        var path = WritePlaylist("fallback.m3u", "#PLAYLIST:\n/Music/a.mp3\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        Assert.Equal("fallback", pl!.Name);
    }

    [Fact]
    public void ReadOne_playlist_with_only_comments_returns_empty_tracks()
    {
        var path = WritePlaylist("all-comments.m3u", "#EXTM3U\n#EXTINF:100,Nobody\n");
        var pl = M3UPlaylistReader.ReadOne(path, _mountPath);

        Assert.NotNull(pl);
        Assert.Empty(pl!.TrackIds);
    }

    [Fact]
    public void ReadOne_nonexistent_file_returns_null()
    {
        var path = Path.Combine(_playlistsDir, "does-not-exist.m3u");
        Assert.Null(M3UPlaylistReader.ReadOne(path, _mountPath));
    }
}
