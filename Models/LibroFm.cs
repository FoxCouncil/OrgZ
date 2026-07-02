// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json.Serialization;

namespace OrgZ.Models;

// DTOs for the (unofficial) Libro.fm app API - the shapes community clients have proven
// (burntcookie90/librofm-downloader, jedwards1230/libro-client). All content is the USER'S OWN
// DRM-free purchases; the store's checkout stays on libro.fm - there is no purchase API.

public class LibroLoginRequest
{
    [JsonPropertyName("grant_type")]
    public string GrantType { get; set; } = "password";

    [JsonPropertyName("username")]
    public string Username { get; set; } = "";

    [JsonPropertyName("password")]
    public string Password { get; set; } = "";
}

public class LibroTokenResponse
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }
}

public class LibroLibraryPage
{
    [JsonPropertyName("audiobooks")]
    public List<LibroBook> Audiobooks { get; set; } = [];

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; set; }
}

public class LibroBook
{
    [JsonPropertyName("isbn")]
    public string Isbn { get; set; } = "";

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("authors")]
    public List<string> Authors { get; set; } = [];

    [JsonPropertyName("cover_url")]
    public string? CoverUrl { get; set; }

    [JsonPropertyName("audiobook_info")]
    public LibroBookInfo? AudiobookInfo { get; set; }

    public string AuthorDisplay => Authors.Count > 0 ? string.Join(", ", Authors) : "Unknown Author";
}

public class LibroBookInfo
{
    [JsonPropertyName("narrators")]
    public List<string> Narrators { get; set; } = [];

    [JsonPropertyName("duration")]
    public long Duration { get; set; }

    [JsonPropertyName("track_count")]
    public int TrackCount { get; set; }
}

/// <summary>The packaged single-file m4b, when Libro.fm has one for the title.</summary>
public class LibroM4bMetadata
{
    [JsonPropertyName("m4b_url")]
    public string? M4bUrl { get; set; }
}

/// <summary>The MP3 fallback: one or more pre-signed zip parts holding the chapter files.</summary>
public class LibroMp3Manifest
{
    [JsonPropertyName("parts")]
    public List<LibroDownloadPart> Parts { get; set; } = [];
}

public class LibroDownloadPart
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("size_bytes")]
    public long SizeBytes { get; set; }
}
