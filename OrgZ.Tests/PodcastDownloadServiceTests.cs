// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;
using OrgZ.Services.Podcast;

namespace OrgZ.Tests;

/// <summary>
/// Filesystem-level tests for the podcast download service: where files land, how on-disk
/// state is reported (notably that a finished file is trusted regardless of the feed's
/// mis-reported enclosureLength — the regression we just fixed), and removal.
/// </summary>
public class PodcastDownloadServiceTests : IDisposable
{
    private readonly string _root;

    public PodcastDownloadServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "OrgZ-PodcastDownload-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) { Directory.Delete(_root, recursive: true); } } catch { }
    }

    private static PodcastFeed Feed(long id = 100) => new() { Id = id, Title = "Test Feed" };

    private static PodcastEpisode Episode(long id = 555, string? type = "audio/mpeg", long len = 0)
        => new() { Id = id, Title = "Ep", EnclosureType = type, EnclosureLength = len };

    private void WriteEpisodeFile(PodcastFeed feed, PodcastEpisode ep, int bytes)
    {
        var path = PodcastDownloadService.GetLocalPath(feed, ep, _root)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
    }

    [Theory]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("audio/mp4", ".m4a")]
    [InlineData("audio/x-m4a", ".m4a")]
    [InlineData("audio/ogg", ".ogg")]
    [InlineData("audio/flac", ".flac")]
    [InlineData("", ".mp3")]
    [InlineData(null, ".mp3")]
    public void GetLocalPath_picks_extension_from_enclosure_type(string? type, string expectedExt)
    {
        var path = PodcastDownloadService.GetLocalPath(Feed(), Episode(type: type), _root);
        Assert.NotNull(path);
        Assert.EndsWith(expectedExt, path);
        Assert.Contains(Path.Combine(".podcasts", "100"), path);
    }

    [Fact]
    public void GetLocalPath_is_null_without_root()
        => Assert.Null(PodcastDownloadService.GetLocalPath(Feed(), Episode(), null));

    [Fact]
    public void GetState_is_NotDownloaded_when_no_file()
        => Assert.Equal(PodcastDownloadState.NotDownloaded, PodcastDownloadService.GetState(Feed(), Episode(), _root));

    [Fact]
    public void GetState_is_NotDownloaded_without_root()
        => Assert.Equal(PodcastDownloadState.NotDownloaded, PodcastDownloadService.GetState(Feed(), Episode(), null));

    [Fact]
    public void GetState_is_Incomplete_when_only_a_partial_is_present()
    {
        var path = PodcastDownloadService.GetLocalPath(Feed(), Episode(), _root)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".partial", new byte[10]);

        Assert.Equal(PodcastDownloadState.Incomplete, PodcastDownloadService.GetState(Feed(), Episode(), _root));
    }

    [Fact]
    public void GetState_is_Downloaded_for_a_final_file()
    {
        var feed = Feed();
        var ep = Episode();
        WriteEpisodeFile(feed, ep, 2048);

        Assert.Equal(PodcastDownloadState.Downloaded, PodcastDownloadService.GetState(feed, ep, _root));
    }

    [Fact]
    public void GetState_trusts_final_file_even_when_smaller_than_reported_length()
    {
        // Regression guard: hosts mis-report enclosureLength, but the .partial -> final
        // rename only happens after a full read, so a final file is complete by construction.
        var feed = Feed();
        var ep = Episode(len: 50_000_000);   // feed claims ~50 MB
        WriteEpisodeFile(feed, ep, 4096);    // only 4 KB on disk

        Assert.Equal(PodcastDownloadState.Downloaded, PodcastDownloadService.GetState(feed, ep, _root));
    }

    [Fact]
    public void DeleteDownload_removes_the_file_and_reports_true()
    {
        var feed = Feed();
        var ep = Episode();
        WriteEpisodeFile(feed, ep, 1024);

        Assert.True(PodcastDownloadService.DeleteDownload(feed, ep, _root));
        Assert.Equal(PodcastDownloadState.NotDownloaded, PodcastDownloadService.GetState(feed, ep, _root));
    }

    [Fact]
    public void DeleteDownload_also_clears_a_stray_partial()
    {
        var feed = Feed();
        var ep = Episode();
        var path = PodcastDownloadService.GetLocalPath(feed, ep, _root)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".partial", new byte[16]);

        Assert.True(PodcastDownloadService.DeleteDownload(feed, ep, _root));
        Assert.False(File.Exists(path + ".partial"));
    }

    [Fact]
    public void DeleteDownload_is_false_when_nothing_to_remove()
        => Assert.False(PodcastDownloadService.DeleteDownload(Feed(), Episode(), _root));
}
