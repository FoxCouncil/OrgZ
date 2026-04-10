// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Xml.Linq;
using static OrgZ.Tests.TestHelpers;

namespace OrgZ.Tests;

public class PlaylistExporterTests : IDisposable
{
    private readonly string _tempDir;

    public PlaylistExporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orgz-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static List<MediaItem> SampleTracks() =>
    [
        new MediaItem { Id = "1", Kind = MediaKind.Music, Title = "First Song", Artist = "Artist A", Album = "Album X", FilePath = @"C:\Music\first.flac", Duration = TimeSpan.FromSeconds(180) },
        new MediaItem { Id = "2", Kind = MediaKind.Music, Title = "Second Song", Artist = "Artist B", Album = "Album Y", FilePath = @"C:\Music\second.mp3", Duration = TimeSpan.FromSeconds(240) },
        new MediaItem { Id = "3", Kind = MediaKind.Music, Title = "No Artist", FilePath = @"C:\Music\noartist.wav", FileName = "noartist.wav", Duration = TimeSpan.FromSeconds(60) },
    ];

    // -- M3U8 --

    [Fact]
    public void ExportM3U8_WritesValidFile()
    {
        var path = Path.Combine(_tempDir, "test.m3u8");
        PlaylistExporter.ExportM3U8(path, "My Playlist", SampleTracks());

        var lines = File.ReadAllLines(path);
        Assert.Equal("#EXTM3U", lines[0]);
        Assert.Equal("#PLAYLIST:My Playlist", lines[1]);
    }

    [Fact]
    public void ExportM3U8_ContainsExtinfAndPaths()
    {
        var path = Path.Combine(_tempDir, "test.m3u8");
        PlaylistExporter.ExportM3U8(path, "Test", SampleTracks());

        var content = File.ReadAllText(path);
        Assert.Contains("#EXTINF:180,Artist A - First Song", content);
        Assert.Contains(@"C:\Music\first.flac", content);
        Assert.Contains("#EXTINF:240,Artist B - Second Song", content);
        Assert.Contains(@"C:\Music\second.mp3", content);
    }

    [Fact]
    public void ExportM3U8_FallsBackToTitleWhenNoArtist()
    {
        var path = Path.Combine(_tempDir, "test.m3u8");
        PlaylistExporter.ExportM3U8(path, "Test", SampleTracks());

        var content = File.ReadAllText(path);
        Assert.Contains("#EXTINF:60,No Artist", content);
    }

    [Fact]
    public void ExportM3U8_SkipsTracksWithoutFilePath()
    {
        var tracks = new List<MediaItem>
        {
            new() { Id = "1", Kind = MediaKind.Music, Title = "Has Path", FilePath = @"C:\Music\song.flac" },
            new() { Id = "2", Kind = MediaKind.Music, Title = "No Path" },
        };

        var path = Path.Combine(_tempDir, "test.m3u8");
        PlaylistExporter.ExportM3U8(path, "Test", tracks);

        var content = File.ReadAllText(path);
        Assert.Contains("Has Path", content);
        Assert.DoesNotContain("No Path", content);
    }

    // -- PLS --

    [Fact]
    public void ExportPLS_WritesValidFile()
    {
        var path = Path.Combine(_tempDir, "test.pls");
        PlaylistExporter.ExportPLS(path, "My PLS", SampleTracks());

        var content = File.ReadAllText(path);
        Assert.StartsWith("[playlist]", content);
        Assert.Contains("NumberOfEntries=3", content);
        Assert.Contains("Version=2", content);
    }

    [Fact]
    public void ExportPLS_ContainsNumberedEntries()
    {
        var path = Path.Combine(_tempDir, "test.pls");
        PlaylistExporter.ExportPLS(path, "Test", SampleTracks());

        var content = File.ReadAllText(path);
        Assert.Contains(@"File1=C:\Music\first.flac", content);
        Assert.Contains("Title1=Artist A - First Song", content);
        Assert.Contains("Length1=180", content);
        Assert.Contains(@"File2=C:\Music\second.mp3", content);
        Assert.Contains(@"File3=C:\Music\noartist.wav", content);
    }

    // -- XSPF --

    [Fact]
    public void ExportXSPF_WritesValidXml()
    {
        var path = Path.Combine(_tempDir, "test.xspf");
        PlaylistExporter.ExportXSPF(path, "My XSPF", SampleTracks());

        var doc = XDocument.Load(path);
        XNamespace ns = "http://xspf.org/ns/0/";
        Assert.Equal("playlist", doc.Root!.Name.LocalName);
        Assert.Equal("My XSPF", doc.Root.Element(ns + "title")!.Value);
    }

    [Fact]
    public void ExportXSPF_ContainsTrackElements()
    {
        var path = Path.Combine(_tempDir, "test.xspf");
        PlaylistExporter.ExportXSPF(path, "Test", SampleTracks());

        var doc = XDocument.Load(path);
        XNamespace ns = "http://xspf.org/ns/0/";
        var tracks = doc.Descendants(ns + "track").ToList();

        Assert.Equal(3, tracks.Count);
        Assert.Equal("First Song", tracks[0].Element(ns + "title")!.Value);
        Assert.Equal("Artist A", tracks[0].Element(ns + "creator")!.Value);
        Assert.Equal("Album X", tracks[0].Element(ns + "album")!.Value);
        Assert.Equal("180000", tracks[0].Element(ns + "duration")!.Value);
    }

    [Fact]
    public void ExportXSPF_LocationIsFileUri()
    {
        var path = Path.Combine(_tempDir, "test.xspf");
        PlaylistExporter.ExportXSPF(path, "Test", SampleTracks());

        var doc = XDocument.Load(path);
        XNamespace ns = "http://xspf.org/ns/0/";
        var location = doc.Descendants(ns + "location").First().Value;

        Assert.StartsWith("file:///", location);
    }
}
