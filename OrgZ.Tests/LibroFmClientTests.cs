// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Audiobooks;

namespace OrgZ.Tests;

/// <summary>
/// The Libro.fm client's pure pieces. The DTO field names mirror the community-proven client
/// (grant_type/access_token/audiobooks/total_pages/parts/size_bytes/m4b_url) - these tests pin
/// OUR serialization against those shapes; the live round-trip needs credentials and belongs to
/// a signed-in session, not CI.
/// </summary>
public class LibroFmClientTests
{
    // ===== Pre-signed URL filenames =====

    [Fact]
    public void Filename_comes_from_the_response_content_disposition_parameter()
    {
        var url = "https://cdn.example/abc.m4b?X-Sig=1&response-content-disposition=attachment%3B%20filename%3D%22The%2BArt%2Bof%2BWar.m4b%22";
        Assert.Equal("The Art of War.m4b", LibroFmClient.FileNameFromPresignedUrl(url));
    }

    [Theory]
    [InlineData("https://cdn.example/abc.m4b")]              // no disposition parameter
    [InlineData("not a url at all")]
    public void Missing_or_malformed_urls_yield_no_filename(string url)
    {
        Assert.Null(LibroFmClient.FileNameFromPresignedUrl(url));
    }

    // ===== DTO shapes =====

    [Fact]
    public void Library_page_parses_the_proven_field_names()
    {
        var json = """
            {"audiobooks":[{"isbn":"9781234567890","title":"The Book","authors":["Jane Author","Co Writer"],
              "cover_url":"https://covers.example/x.jpg","audiobook_info":{"narrators":["A Voice"],"duration":41400,"track_count":12}}],
             "total_pages":3}
            """;
        var page = System.Text.Json.JsonSerializer.Deserialize<LibroLibraryPage>(json, LibroFmClient.JsonOpts)!;

        Assert.Equal(3, page.TotalPages);
        var book = Assert.Single(page.Audiobooks);
        Assert.Equal("9781234567890", book.Isbn);
        Assert.Equal("The Book", book.Title);
        Assert.Equal("Jane Author, Co Writer", book.AuthorDisplay);
        Assert.Equal(12, book.AudiobookInfo?.TrackCount);
    }

    [Fact]
    public void Token_and_manifest_shapes_parse()
    {
        Assert.Equal("tok123", System.Text.Json.JsonSerializer.Deserialize<LibroTokenResponse>(
            """{"access_token":"tok123"}""", LibroFmClient.JsonOpts)!.AccessToken);

        var manifest = System.Text.Json.JsonSerializer.Deserialize<LibroMp3Manifest>(
            """{"parts":[{"url":"https://cdn.example/part1.zip","size_bytes":"123456"}]}""", LibroFmClient.JsonOpts)!;
        var part = Assert.Single(manifest.Parts);
        Assert.Equal(123456, part.SizeBytes);   // numbers-from-strings tolerated, like the other clients
    }

    // ===== The .audiobooks identity of a purchase =====

    [Fact]
    public void A_purchase_lands_on_the_same_shelf_layout_as_everything_else()
    {
        var book = new LibroBook { Isbn = "9781234567890", Title = "The Book", Authors = ["Jane Author"] };
        var listing = AudiobookDownloadService.ListingFor(book);

        Assert.Equal("libro:9781234567890", listing.Identifier);
        Assert.Equal(@"C:\Music\.audiobooks\Jane Author\The Book",
            AudiobookDownloadService.TargetDirectoryFor(@"C:\Music", listing));
    }
}
