// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json.Serialization;

namespace OrgZ.Models;

/// <summary>
/// PodcastIndex feed DTO. Shape mirrors the upstream <c>feeds[]</c> array element
/// from /api/search/byterm, /api/podcasts/trending, /api/recent/feeds, etc.
/// Only fields we actually use are kept here - the upstream payload has ~30 more.
/// </summary>
public sealed record PodcastFeed
{
    [JsonPropertyName("id")]                     public long   Id              { get; init; }
    [JsonPropertyName("podcastGuid")]            public string? PodcastGuid    { get; init; }
    [JsonPropertyName("title")]                  public string? Title          { get; init; }
    [JsonPropertyName("url")]                    public string? FeedUrl        { get; init; }
    [JsonPropertyName("link")]                   public string? HomepageUrl    { get; init; }
    [JsonPropertyName("description")]            public string? Description    { get; init; }
    [JsonPropertyName("author")]                 public string? Author         { get; init; }
    [JsonPropertyName("ownerName")]              public string? OwnerName      { get; init; }
    [JsonPropertyName("image")]                  public string? Image          { get; init; }
    [JsonPropertyName("artwork")]                public string? Artwork        { get; init; }
    [JsonPropertyName("language")]               public string? Language       { get; init; }
    [JsonPropertyName("explicit")]               public bool   Explicit        { get; init; }
    [JsonPropertyName("episodeCount")]           public int    EpisodeCount    { get; init; }
    [JsonPropertyName("newestItemPubdate")]      public long   NewestItemEpoch { get; init; }
    [JsonPropertyName("newestItemPublishTime")]  public long   NewestItemEpoch2 { get; init; }
    [JsonPropertyName("categories")]             public Dictionary<string, string>? Categories { get; init; }

    public string DisplayImage => !string.IsNullOrWhiteSpace(Artwork) ? Artwork! : (Image ?? "");
    public long DisplayNewest => NewestItemEpoch != 0 ? NewestItemEpoch : NewestItemEpoch2;
}

/// <summary>
/// PodcastIndex episode DTO from /api/episodes/byfeedid items[].
/// The enclosureUrl is the playable mp3; enclosureLength is bytes for download progress.
/// </summary>
public sealed record PodcastEpisode
{
    [JsonPropertyName("id")]                public long    Id              { get; init; }
    [JsonPropertyName("title")]             public string? Title           { get; init; }
    [JsonPropertyName("description")]       public string? Description     { get; init; }
    [JsonPropertyName("guid")]              public string? Guid            { get; init; }
    [JsonPropertyName("datePublished")]     public long    DatePublishedEpoch { get; init; }
    [JsonPropertyName("datePublishedPretty")] public string? DatePublishedPretty { get; init; }
    [JsonPropertyName("enclosureUrl")]      public string? EnclosureUrl    { get; init; }
    [JsonPropertyName("enclosureType")]     public string? EnclosureType   { get; init; }
    [JsonPropertyName("enclosureLength")]   public long    EnclosureLength { get; init; }
    [JsonPropertyName("duration")]          public int     DurationSec     { get; init; }
    [JsonPropertyName("explicit")]          public int     Explicit        { get; init; }

    /// <summary>
    /// Friendly duration label for direct binding from the episode DataGrid.
    /// Standard <c>m:ss</c> / <c>h:mm:ss</c> shape -- same as the LCD.
    /// </summary>
    public string DurationLabel
        => DurationSec <= 0 ? "" : OrgZ.Helpers.FormatHelper.FormatDurationCompact(DurationSec);
    // episode/season fields intentionally omitted: PodcastIndex returns them
    // as either int OR free-form strings ("E1", "S01E01") depending on the
    // upstream feed, and we don't display them anywhere -- pulling them in
    // would just be another way for one weird feed to break the whole list.
    [JsonPropertyName("image")]             public string? Image           { get; init; }
    [JsonPropertyName("feedId")]            public long    FeedId          { get; init; }
    [JsonPropertyName("feedTitle")]         public string? FeedTitle       { get; init; }
    [JsonPropertyName("feedImage")]         public string? FeedImage       { get; init; }
}

/// <summary>
/// PodcastIndex category DTO from /api/categories/list.
/// </summary>
public sealed record PodcastCategory
{
    [JsonPropertyName("id")]   public int    Id   { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>Upstream response wrapper for endpoints that return feeds[].</summary>
public sealed record PodcastFeedsResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("feeds")]  public List<PodcastFeed>? Feeds { get; init; }
    [JsonPropertyName("count")]  public int    Count   { get; init; }
}

/// <summary>Upstream response wrapper for /api/episodes/byfeedid.</summary>
public sealed record PodcastEpisodesResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("items")]  public List<PodcastEpisode>? Items { get; init; }
    [JsonPropertyName("count")]  public int Count { get; init; }
}

/// <summary>Upstream response wrapper for /api/categories/list.</summary>
public sealed record PodcastCategoriesResponse
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("feeds")]  public List<PodcastCategory>? Feeds { get; init; }
    [JsonPropertyName("count")]  public int Count { get; init; }
}

/// <summary>
/// Local subscription record (SQLite-persisted). Identity is <see cref="FeedId"/>.
/// </summary>
public sealed record PodcastSubscription
{
    public long      FeedId         { get; init; }
    public string?   PodcastGuid    { get; init; }
    public string?   Title          { get; init; }
    public string?   Author         { get; init; }
    public string?   Description    { get; init; }
    public string?   HomepageUrl    { get; init; }
    public string?   FeedUrl        { get; init; }
    public string?   ImageUrl       { get; init; }
    public DateTime  SubscribedAt   { get; init; }
    public DateTime? LastCheckedAt  { get; init; }
}

/// <summary>
/// Persisted listen history entry. Distinct from <see cref="PodcastDownload"/> -
/// every episode the user has ever played (downloaded or streamed) gets a row;
/// downloads are a separate concept. Used to populate the "recently played" UI.
/// </summary>
public sealed record PodcastListenEntry
{
    public long      EpisodeId          { get; init; }
    public long      FeedId             { get; init; }
    public string?   Title              { get; init; }
    public string?   FeedTitle          { get; init; }
    public string?   EnclosureUrl       { get; init; }
    public string?   ImageUrl           { get; init; }
    public int       DurationSec        { get; init; }
    public long      DatePublishedEpoch { get; init; }
    public DateTime  FirstPlayedAt      { get; init; }
    public DateTime  LastPlayedAt       { get; init; }
    public long?     LastPositionMs     { get; init; }
    public int       PlayCount          { get; init; }
    public bool      Completed          { get; init; }
}

/// <summary>
/// Local download record (SQLite-persisted). One row per downloaded or in-progress
/// episode. <see cref="LocalPath"/> is set once the file is on disk.
/// </summary>
public sealed record PodcastDownload
{
    public long      EpisodeId      { get; init; }
    public long      FeedId         { get; init; }
    public string?   Title          { get; init; }
    public string?   Description    { get; init; }
    public string?   EnclosureUrl   { get; init; }
    public long      EnclosureBytes { get; init; }
    public int       DurationSec    { get; init; }
    public long      DatePublishedEpoch { get; init; }
    public string?   LocalPath      { get; init; }
    public DateTime  AddedAt        { get; init; }
    public DateTime? CompletedAt    { get; init; }
    public long?     LastPositionMs { get; init; }
}
