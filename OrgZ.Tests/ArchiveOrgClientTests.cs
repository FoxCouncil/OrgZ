// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Audiobooks;

namespace OrgZ.Tests;

/// <summary>
/// The audiobook store's archive.org backend, exercised against CAPTURED live responses (the
/// fixtures are actual advancedsearch/metadata payloads, not hand-written JSON) plus the pure
/// helpers: URL construction, the m4b-preferred download-file picker, and the two duration
/// shapes the metadata API serves.
/// </summary>
public class ArchiveOrgClientTests
{
    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    // ===== advancedsearch response (captured live) =====

    [Fact]
    public void Search_fixture_parses_into_listings()
    {
        var resp = ArchiveOrgClient.ParseJson<ArchiveSearchResponse>(Fixture("archiveorg_search_librivox.json"));

        Assert.NotNull(resp?.Response);
        var docs = resp!.Response!.Docs;
        Assert.Equal(5, docs.Count);
        Assert.All(docs, d => Assert.False(string.IsNullOrEmpty(d.Identifier)));
        Assert.All(docs, d => Assert.Contains("Sherlock", d.Title));
        Assert.All(docs, d => Assert.Equal("Sir Arthur Conan Doyle", d.Creator));
        Assert.All(docs, d => Assert.True(d.Downloads > 0, "downloads is the popularity signal — must parse"));
        Assert.All(docs, d => Assert.Matches(@"^\d+:\d{2}:\d{2}$", d.Runtime!));
    }

    [Fact]
    public void Cover_url_is_the_item_image_service()
    {
        var listing = new AudiobookListing { Identifier = "memoirs_sherlock_holmes_1007_librivox" };
        Assert.Equal("https://archive.org/services/img/memoirs_sherlock_holmes_1007_librivox", listing.CoverUrl);
    }

    // ===== item metadata response (captured live) =====

    [Fact]
    public void Metadata_fixture_parses_files_and_fields()
    {
        var item = ArchiveOrgClient.ParseJson<ArchiveItemResponse>(Fixture("archiveorg_metadata_item.json"));

        Assert.NotNull(item);
        Assert.True(item!.Files.Count > 0);
        Assert.Contains(item.Files, f => f.Format == "64Kbps MP3");
        Assert.Equal("The Adventures of Sherlock Holmes (version 4)", item.Metadata?.Title);
        Assert.Equal("Sir Arthur Conan Doyle", item.Metadata?.Creator);
        Assert.False(string.IsNullOrWhiteSpace(item.Metadata?.Description));
    }

    [Fact]
    public void Download_picker_prefers_the_real_items_chaptered_m4bs()
    {
        // The captured item carries the two-part .m4b set LibriVox publishes (IA format
        // "Audiobook") alongside its per-chapter MP3s - the picker must take the m4bs, in order.
        var item = ArchiveOrgClient.ParseJson<ArchiveItemResponse>(Fixture("archiveorg_metadata_item.json"))!;

        var picked = ArchiveOrgClient.PickDownloadFiles(item.Files);

        Assert.Equal(2, picked.Count);
        Assert.All(picked, f => Assert.EndsWith(".m4b", f.Name!, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(picked.Select(f => f.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase), picked.Select(f => f.Name));
    }

    [Fact]
    public void Download_picker_falls_back_to_the_mp3_chapter_set()
    {
        List<ArchiveItemFile> files =
        [
            new() { Name = "book_02_64kb.mp3", Format = "64Kbps MP3" },
            new() { Name = "book_01_64kb.mp3", Format = "64Kbps MP3" },
            new() { Name = "book_01.mp3", Format = "128Kbps MP3" },
            new() { Name = "book.png", Format = "PNG" },
        ];

        var picked = ArchiveOrgClient.PickDownloadFiles(files);

        // 64Kbps is LibriVox's canonical chapter set; sorted = section order (zero-padded names).
        Assert.Equal(["book_01_64kb.mp3", "book_02_64kb.mp3"], picked.Select(f => f.Name));
    }

    [Fact]
    public void Download_picker_returns_empty_when_an_item_has_no_audio()
    {
        Assert.Empty(ArchiveOrgClient.PickDownloadFiles([new ArchiveItemFile { Name = "cover.jpg", Format = "JPEG" }]));
    }

    // ===== duration parsing - both shapes the metadata API serves =====

    [Theory]
    [InlineData("55:12", 0, 55, 12)]
    [InlineData("1:02:03", 1, 2, 3)]
    [InlineData("10:56:13", 10, 56, 13)]
    public void Clock_style_lengths_parse(string input, int h, int m, int s)
    {
        Assert.Equal(new TimeSpan(h, m, s), ArchiveOrgClient.ParseFileLength(input));
    }

    [Fact]
    public void Decimal_seconds_lengths_parse()
    {
        Assert.Equal(TimeSpan.FromSeconds(3068.71), ArchiveOrgClient.ParseFileLength("3068.71"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4")]
    public void Garbage_lengths_parse_to_null(string? input)
    {
        Assert.Null(ArchiveOrgClient.ParseFileLength(input));
    }

    // ===== URL construction =====

    [Fact]
    public void Search_url_scopes_to_librivox_and_matches_title_or_author()
    {
        var url = ArchiveOrgClient.BuildSearchUrl("sherlock holmes", rows: 25);

        Assert.StartsWith("https://archive.org/advancedsearch.php?q=", url);
        Assert.Contains(Uri.EscapeDataString("collection:librivoxaudio"), url);
        Assert.Contains(Uri.EscapeDataString("title:(sherlock holmes)"), url);
        Assert.Contains(Uri.EscapeDataString("creator:(sherlock holmes)"), url);
        Assert.Contains("rows=25", url);
        Assert.Contains("output=json", url);
    }

    [Fact]
    public void List_urls_sort_by_popularity_and_recency()
    {
        Assert.Contains(Uri.EscapeDataString("downloads desc"), ArchiveOrgClient.BuildListUrl("downloads desc", 25));
        Assert.Contains(Uri.EscapeDataString("publicdate desc"), ArchiveOrgClient.BuildListUrl("publicdate desc", 25));
    }

    [Fact]
    public void Download_url_uses_the_canonical_redirector()
    {
        Assert.Equal(
            "https://archive.org/download/some_item/chapter%2001.mp3",
            ArchiveOrgClient.DownloadUrlFor("some_item", "chapter 01.mp3"));
    }

    // ===== the string-or-array metadata quirk =====

    [Fact]
    public void Creator_serialized_as_an_array_reads_as_its_first_value()
    {
        var json = """{"metadata":{"title":"T","creator":["Jane Author","Second Author"],"description":"D"},"files":[]}""";
        var item = ArchiveOrgClient.ParseJson<ArchiveItemResponse>(json);
        Assert.Equal("Jane Author", item?.Metadata?.Creator);
    }
}
