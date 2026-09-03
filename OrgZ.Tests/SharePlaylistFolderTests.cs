// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Sharing;

namespace OrgZ.Tests;

/// <summary>
/// Playlist folders over library sharing: the server sends each playlist's folder, the client
/// reads it back, and a server from before folders existed still parses - its playlists just
/// sit at the root.
/// </summary>
public class SharePlaylistFolderTests
{
    private static readonly DiscoveredShare Share = new("Den", "den.local", 7391, "192.168.1.50");

    [Fact]
    public void Folders_travel_over_the_wire_and_come_back_normalised()
    {
        var json = LibraryShareServer.BuildPlaylistsJson(
        [
            new LibraryShareServer.ServedPlaylist("Favorites", ["a"], "favorites"),
            new LibraryShareServer.ServedPlaylist("Summer", ["a", "b"], Folder: "Road Trips/2026"),
            new LibraryShareServer.ServedPlaylist("Loose", ["b"]),
        ]);

        var parsed = ShareDiscovery.ParsePlaylists(json, Share);

        Assert.Equal(3, parsed.Count);
        Assert.Equal("", parsed[0].Folder);
        Assert.True(parsed[0].IsFavorites);
        Assert.Equal("Road Trips/2026", parsed[1].Folder);
        Assert.Equal("", parsed[2].Folder);
        Assert.Equal([$"share:{Share.Key}:a", $"share:{Share.Key}:b"], parsed[1].TrackIds);
    }

    [Fact]
    public void A_server_that_predates_folders_still_parses()
    {
        const string legacy = """{"playlists":[{"name":"Old","trackIds":["x"],"type":"playlist"}]}""";

        var parsed = ShareDiscovery.ParsePlaylists(legacy, Share);

        var only = Assert.Single(parsed);
        Assert.Equal("Old", only.Name);
        Assert.Equal("", only.Folder);
    }

    [Fact]
    public void A_hand_written_folder_is_normalised_on_the_way_in()
    {
        const string odd = """{"playlists":[{"name":"P","trackIds":[],"type":"playlist","folder":" Chill \\ Deep / "}]}""";

        var only = Assert.Single(ShareDiscovery.ParsePlaylists(odd, Share));
        Assert.Equal("Chill/Deep", only.Folder);
    }
}
