// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;
using OrgZ.Models;

namespace OrgZ.Tests;

public class PodcastDtoTests
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    [Fact]
    public void Episode_Duration_Field_Maps_To_DurationSec()
    {
        var json = """
        { "status": "true", "items": [
            { "id": 55723561284, "title": "x", "duration": 1475, "feedId": 586839 }
        ] }
        """;
        var resp = JsonSerializer.Deserialize<PodcastEpisodesResponse>(json, Opts);
        Assert.NotNull(resp);
        Assert.NotNull(resp!.Items);
        Assert.Single(resp.Items);
        Assert.Equal(1475, resp.Items[0].DurationSec);
    }

    [Fact]
    public void Episode_Mixed_Episode_Field_Does_Not_Crash()
    {
        // PodcastIndex returns "episode" as either int or string ("E1", "S01E01")
        // depending on the feed. We dropped the field from the DTO so the
        // deserializer ignores it (UnmappedMemberHandling = Skip by default).
        var json = """
        { "status": "true", "items": [
            { "id": 1, "title": "a", "duration": 100, "episode": "E1", "season": "S01" },
            { "id": 2, "title": "b", "duration": 200, "episode": 5,    "season": 0 }
        ] }
        """;
        var resp = JsonSerializer.Deserialize<PodcastEpisodesResponse>(json, Opts);
        Assert.NotNull(resp!.Items);
        Assert.Equal(2, resp.Items!.Count);
        Assert.Equal(100, resp.Items[0].DurationSec);
        Assert.Equal(200, resp.Items[1].DurationSec);
    }
}
