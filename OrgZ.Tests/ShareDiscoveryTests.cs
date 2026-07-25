// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.Sharing;

namespace OrgZ.Tests;

/// <summary>
/// The sharing client: how a discovered instance becomes a sidebar-mountable share and
/// how a remote catalogue maps to playable, read-only MediaItems. Adversarial cases
/// attack malformed catalogues and hostile ids - a share is another machine's output,
/// so nothing it sends is trusted.
/// </summary>
public class ShareDiscoveryTests
{
    private static readonly DiscoveredShare Share = new("Fox Library", "foxbox.local", 7391, "192.168.1.50");

    // ── Identity ──────────────────────────────────────────────

    [Fact]
    public void Share_identity_is_address_and_port_not_the_display_name()
    {
        Assert.Equal("192.168.1.50:7391", Share.Key);
        Assert.Equal("http://192.168.1.50:7391", Share.BaseUrl);

        // Renaming the share must not change its identity.
        Assert.Equal(Share.Key, (Share with { Name = "Renamed" }).Key);

        // Without an A record the hostname carries it.
        Assert.Equal("foxbox.local:7391", new DiscoveredShare("n", "foxbox.local", 7391, null).Key);
    }

    [Fact]
    public void Display_name_prefers_the_txt_record_over_the_mdns_label()
    {
        var instance = new MdnsWire.ServiceInstance("Fox-s Library", "h.local", 1, ["version=1", "name=Fox.s Library"]);
        Assert.Equal("Fox.s Library", ShareDiscovery.DisplayNameFor(instance));

        // No name= TXT: fall back to the label.
        Assert.Equal("Plain", ShareDiscovery.DisplayNameFor(new MdnsWire.ServiceInstance("Plain", "h.local", 1, [])));
    }

    // ── Catalogue mapping ─────────────────────────────────────

    [Fact]
    public void Catalogue_tracks_become_streamable_read_only_items()
    {
        const string json = """
        {"share":"Fox Library","version":1,"count":1,"tracks":[
          {"id":"abc","title":"Stop","artist":"Spice Girls","album":"Spiceworld","kind":"Music","durationTicks":2040000000,"track":2,"year":1997}
        ]}
        """;

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));

        Assert.Equal("share:192.168.1.50:7391:abc", item.Id);   // namespaced: no collisions
        Assert.Equal("Stop", item.Title);
        Assert.Equal("Spice Girls", item.Artist);
        Assert.Equal(MediaKind.Music, item.Kind);
        Assert.Equal(2u, item.Track);
        Assert.Equal(1997u, item.Year);
        Assert.Equal(TimeSpan.FromTicks(2040000000), item.Duration);
        Assert.Equal("http://192.168.1.50:7391/stream/abc", item.StreamUrl);
        Assert.Equal("share:192.168.1.50:7391", item.Source);

        // Read-only: no local file means nothing can try to edit or delete it.
        Assert.Null(item.FilePath);
    }

    [Fact]
    public void Ids_are_url_escaped_so_odd_characters_still_resolve()
    {
        const string json = """{"tracks":[{"id":"a b/c?d","title":"T","kind":"Music"}]}""";

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));

        Assert.Equal("http://192.168.1.50:7391/stream/a%20b%2Fc%3Fd", item.StreamUrl);
    }

    [Fact]
    public void Two_shares_serving_the_same_id_stay_distinct()
    {
        const string json = """{"tracks":[{"id":"1","title":"T","kind":"Music"}]}""";
        var other = new DiscoveredShare("Other", "other.local", 7391, "192.168.1.51");

        var a = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));
        var b = Assert.Single(ShareDiscovery.ParseCatalogue(json, other));

        Assert.NotEqual(a.Id, b.Id);
        Assert.NotEqual(a.Source, b.Source);
    }

    [Fact]
    public void The_extension_rides_along_on_the_stream_url()
    {
        const string json = """{"tracks":[{"id":"abc","title":"T","kind":"Music","ext":".flac"}]}""";

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));

        // libvlc picks a demuxer far more reliably when the location looks like a file.
        Assert.Equal("http://192.168.1.50:7391/stream/abc.flac", item.StreamUrl);
        Assert.Equal(".flac", item.Extension);
    }

    [Fact]
    public void A_catalogue_without_extensions_still_produces_a_usable_stream_url()
    {
        // An older sharing host, or a track whose format we don't serve.
        const string json = """{"tracks":[{"id":"abc","title":"T","kind":"Music"},{"id":"d","title":"T","kind":"Music","ext":""}]}""";

        var items = ShareDiscovery.ParseCatalogue(json, Share);

        Assert.Equal("http://192.168.1.50:7391/stream/abc", items[0].StreamUrl);
        Assert.Equal("http://192.168.1.50:7391/stream/d", items[1].StreamUrl);
        Assert.All(items, i => Assert.Null(i.Extension));
    }

    // ── Art URLs ──────────────────────────────────────────────

    [Fact]
    public void Art_urls_are_rebuilt_from_the_namespaced_id_alone()
    {
        const string json = """{"tracks":[{"id":"a b/c","title":"T","kind":"Music","ext":".mp3"}]}""";

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));

        Assert.True(ShareDiscovery.IsShareItem(item));
        Assert.Equal("http://192.168.1.50:7391/art/a%20b%2Fc", ShareDiscovery.ArtUrlFor(item));
    }

    [Fact]
    public void Nothing_but_a_share_item_gets_an_art_url()
    {
        Assert.Null(ShareDiscovery.ArtUrlFor(new MediaItem { Id = "1", Kind = MediaKind.Music, FilePath = @"C:\a.mp3" }));
        Assert.Null(ShareDiscovery.ArtUrlFor(new MediaItem { Id = "cd:D::3", Kind = MediaKind.Music, Source = "cdda" }));
        Assert.Null(ShareDiscovery.ArtUrlFor(new MediaItem { Id = "x", Kind = MediaKind.Music, Source = "device:/mnt/ipod" }));

        // A malformed share item (id not actually namespaced under its source) yields
        // nothing rather than a URL pointing at the wrong thing.
        Assert.Null(ShareDiscovery.ArtUrlFor(new MediaItem { Id = "share:h:1", Kind = MediaKind.Music, Source = "share:h:1" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"tracks":"nope"}""")]
    [InlineData("""{"tracks":[]}""")]
    public void Malformed_catalogues_yield_nothing_instead_of_throwing(string json)
    {
        Assert.Empty(ShareDiscovery.ParseCatalogue(json, Share));
    }

    [Fact]
    public void Tracks_missing_an_id_are_skipped_and_the_rest_survive()
    {
        const string json = """
        {"tracks":[
          {"title":"No Id","kind":"Music"},
          {"id":"","title":"Blank Id","kind":"Music"},
          {"id":"ok","title":"Good","kind":"Music"}
        ]}
        """;

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));
        Assert.Equal("Good", item.Title);
    }

    [Fact]
    public void Unknown_kinds_and_absent_fields_fall_back_to_safe_defaults()
    {
        const string json = """{"tracks":[{"id":"x","kind":"Hologram"}]}""";

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));

        Assert.Equal(MediaKind.Music, item.Kind);   // unknown kind -> Music
        Assert.Equal("Unknown", item.Title);
        Assert.Null(item.Duration);
        Assert.Null(item.Track);
        Assert.Null(item.Year);
    }

    [Fact]
    public void Wrongly_typed_fields_do_not_throw()
    {
        // A hostile/buggy server sending numbers-as-strings and vice versa.
        const string json = """{"tracks":[{"id":"x","title":42,"durationTicks":"lots","track":"two"}]}""";

        var item = Assert.Single(ShareDiscovery.ParseCatalogue(json, Share));

        Assert.Equal("Unknown", item.Title);
        Assert.Null(item.Duration);
        Assert.Null(item.Track);
    }
}
