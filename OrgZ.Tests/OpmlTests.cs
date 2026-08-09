// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Podcast;

namespace OrgZ.Tests;

public class OpmlTests
{
    [Fact]
    public void ParseFeedUrls_reads_flat_and_nested_outlines()
    {
        const string opml = """
            <?xml version="1.0" encoding="utf-8"?>
            <opml version="2.0">
              <head><title>subs</title></head>
              <body>
                <outline type="rss" text="Show A" xmlUrl="https://a.example/feed.xml" />
                <outline text="Folder">
                  <outline type="rss" text="Show B" xmlUrl="https://b.example/rss" htmlUrl="https://b.example" />
                </outline>
                <outline text="No url here" />
              </body>
            </opml>
            """;

        var urls = Opml.ParseFeedUrls(opml);

        Assert.Equal(["https://a.example/feed.xml", "https://b.example/rss"], urls);
    }

    [Fact]
    public void ParseFeedUrls_dedupes_case_insensitively()
    {
        const string opml = """
            <opml version="2.0"><body>
              <outline xmlUrl="https://a.example/FEED" />
              <outline xmlUrl="https://a.example/feed" />
            </body></opml>
            """;

        Assert.Single(Opml.ParseFeedUrls(opml));
    }

    [Fact]
    public void ParseFeedUrls_throws_on_malformed_xml()
    {
        Assert.ThrowsAny<System.Xml.XmlException>(() => Opml.ParseFeedUrls("<opml><body>"));
    }

    [Fact]
    public void Export_round_trips_through_parse()
    {
        var subs = new List<PodcastSubscription>
        {
            new() { FeedId = 1, Title = "Show \"A\" & Friends", FeedUrl = "https://a.example/feed.xml" },
            new() { FeedId = 2, Title = null, FeedUrl = "https://b.example/rss" },
            new() { FeedId = 3, Title = "No url - skipped", FeedUrl = null },
        };

        var opml = Opml.Export(subs);
        var urls = Opml.ParseFeedUrls(opml);

        Assert.Equal(["https://a.example/feed.xml", "https://b.example/rss"], urls);
        Assert.Contains("OrgZ Podcast Subscriptions", opml);
        Assert.StartsWith("<?xml", opml);
    }
}
