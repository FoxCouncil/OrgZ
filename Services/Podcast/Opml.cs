// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Xml.Linq;
using OrgZ.Models;

namespace OrgZ.Services.Podcast;

/// <summary>
/// Minimal OPML 2.0 in/out for podcast subscriptions - the interchange format every
/// podcatcher speaks. Import collects every outline's <c>xmlUrl</c> (nested folders
/// included); export writes one flat outline per subscription.
/// </summary>
public static class Opml
{
    /// <summary>Every distinct feed URL in the document, in order. Throws on malformed XML.</summary>
    public static List<string> ParseFeedUrls(string opmlXml)
    {
        var doc = XDocument.Parse(opmlXml);
        return doc.Descendants("outline")
            .Select(o => o.Attribute("xmlUrl")?.Value)
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string Export(IEnumerable<PodcastSubscription> subscriptions)
    {
        var body = new XElement("body");
        foreach (var sub in subscriptions)
        {
            if (string.IsNullOrWhiteSpace(sub.FeedUrl))
            {
                continue;
            }

            body.Add(new XElement("outline",
                new XAttribute("type", "rss"),
                new XAttribute("text", sub.Title ?? sub.FeedUrl),
                new XAttribute("title", sub.Title ?? sub.FeedUrl),
                new XAttribute("xmlUrl", sub.FeedUrl)));
        }

        var doc = new XDocument(
            new XElement("opml",
                new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", "OrgZ Podcast Subscriptions")),
                body));

        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + doc;
    }
}
