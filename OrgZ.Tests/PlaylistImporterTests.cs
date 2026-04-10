// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Xml.Linq;

namespace OrgZ.Tests;

public class PlaylistImporterTests : IDisposable
{
    private readonly string _tempDir;

    public PlaylistImporterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"orgz-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // -- M3U --

    [Fact]
    public void ParseM3U_BasicFile_ExtractsPathsAndName()
    {
        var path = Path.Combine(_tempDir, "test.m3u");
        File.WriteAllText(path, "#EXTM3U\n#PLAYLIST:My Tunes\n#EXTINF:180,Artist - Song\nC:\\Music\\song.flac\nC:\\Music\\other.mp3\n");

        var result = PlaylistImporter.Import(path);

        Assert.Equal("My Tunes", result.Name);
        Assert.Equal(2, result.TrackPaths.Count);
        Assert.Equal(@"C:\Music\song.flac", result.TrackPaths[0]);
        Assert.Equal(@"C:\Music\other.mp3", result.TrackPaths[1]);
    }

    [Fact]
    public void ParseM3U_NoPlaylistTag_FallsBackToFilename()
    {
        var path = Path.Combine(_tempDir, "cool-mix.m3u8");
        File.WriteAllText(path, "#EXTM3U\nC:\\Music\\song.flac\n");

        var result = PlaylistImporter.Import(path);

        Assert.Equal("cool-mix", result.Name);
    }

    [Fact]
    public void ParseM3U_SkipsCommentAndEmptyLines()
    {
        var path = Path.Combine(_tempDir, "test.m3u");
        File.WriteAllText(path, "#EXTM3U\n# a comment\n\n#EXTINF:60,Song\nC:\\Music\\song.flac\n\n");

        var result = PlaylistImporter.Import(path);

        Assert.Single(result.TrackPaths);
    }

    [Fact]
    public void ParseM3U_RelativePaths_ResolvedAgainstPlaylistDir()
    {
        var path = Path.Combine(_tempDir, "test.m3u");
        File.WriteAllText(path, "subfolder/song.flac\n");

        var result = PlaylistImporter.Import(path);

        var expected = Path.Combine(_tempDir, "subfolder", "song.flac");
        Assert.Equal(expected, result.TrackPaths[0]);
    }

    // -- PLS --

    [Fact]
    public void ParsePLS_BasicFile_ExtractsPathsInOrder()
    {
        var path = Path.Combine(_tempDir, "test.pls");
        File.WriteAllText(path, "[playlist]\nNumberOfEntries=2\nFile1=C:\\Music\\first.flac\nTitle1=First\nFile2=C:\\Music\\second.mp3\nTitle2=Second\nVersion=2\n");

        var result = PlaylistImporter.Import(path);

        Assert.Equal(2, result.TrackPaths.Count);
        Assert.Equal(@"C:\Music\first.flac", result.TrackPaths[0]);
        Assert.Equal(@"C:\Music\second.mp3", result.TrackPaths[1]);
    }

    [Fact]
    public void ParsePLS_OutOfOrderEntries_SortedByNumber()
    {
        var path = Path.Combine(_tempDir, "test.pls");
        File.WriteAllText(path, "[playlist]\nFile3=C:\\c.flac\nFile1=C:\\a.flac\nFile2=C:\\b.flac\n");

        var result = PlaylistImporter.Import(path);

        Assert.Equal(@"C:\a.flac", result.TrackPaths[0]);
        Assert.Equal(@"C:\b.flac", result.TrackPaths[1]);
        Assert.Equal(@"C:\c.flac", result.TrackPaths[2]);
    }

    [Fact]
    public void ParsePLS_NameFallsBackToFilename()
    {
        var path = Path.Combine(_tempDir, "my-playlist.pls");
        File.WriteAllText(path, "[playlist]\nFile1=C:\\song.flac\n");

        var result = PlaylistImporter.Import(path);

        Assert.Equal("my-playlist", result.Name);
    }

    // -- XSPF --

    [Fact]
    public void ParseXSPF_BasicFile_ExtractsNameAndPaths()
    {
        var path = Path.Combine(_tempDir, "test.xspf");
        XNamespace ns = "http://xspf.org/ns/0/";
        var doc = new XDocument(
            new XElement(ns + "playlist",
                new XAttribute("version", "1"),
                new XElement(ns + "title", "XML Playlist"),
                new XElement(ns + "trackList",
                    new XElement(ns + "track",
                        new XElement(ns + "location", "file:///C:/Music/song.flac")),
                    new XElement(ns + "track",
                        new XElement(ns + "location", "file:///C:/Music/other.mp3")))));
        doc.Save(path);

        var result = PlaylistImporter.Import(path);

        Assert.Equal("XML Playlist", result.Name);
        Assert.Equal(2, result.TrackPaths.Count);
        Assert.Equal(@"C:\Music\song.flac", result.TrackPaths[0]);
        Assert.Equal(@"C:\Music\other.mp3", result.TrackPaths[1]);
    }

    [Fact]
    public void ParseXSPF_NoTitle_FallsBackToFilename()
    {
        var path = Path.Combine(_tempDir, "untitled.xspf");
        XNamespace ns = "http://xspf.org/ns/0/";
        var doc = new XDocument(
            new XElement(ns + "playlist",
                new XAttribute("version", "1"),
                new XElement(ns + "trackList",
                    new XElement(ns + "track",
                        new XElement(ns + "location", "file:///C:/Music/song.flac")))));
        doc.Save(path);

        var result = PlaylistImporter.Import(path);

        Assert.Equal("untitled", result.Name);
    }

    [Fact]
    public void ParseXSPF_SkipsEmptyLocations()
    {
        var path = Path.Combine(_tempDir, "test.xspf");
        XNamespace ns = "http://xspf.org/ns/0/";
        var doc = new XDocument(
            new XElement(ns + "playlist",
                new XAttribute("version", "1"),
                new XElement(ns + "trackList",
                    new XElement(ns + "track",
                        new XElement(ns + "location", "file:///C:/Music/song.flac")),
                    new XElement(ns + "track",
                        new XElement(ns + "location", "")))));
        doc.Save(path);

        var result = PlaylistImporter.Import(path);

        Assert.Single(result.TrackPaths);
    }

    // -- Round-trip --

    [Fact]
    public void RoundTrip_M3U8_ExportThenImport_PreservesPaths()
    {
        var tracks = new List<MediaItem>
        {
            new() { Id = "1", Kind = MediaKind.Music, Title = "Song A", Artist = "Artist", FilePath = @"C:\Music\a.flac", Duration = TimeSpan.FromSeconds(100) },
            new() { Id = "2", Kind = MediaKind.Music, Title = "Song B", Artist = "Artist", FilePath = @"C:\Music\b.mp3", Duration = TimeSpan.FromSeconds(200) },
        };

        var exportPath = Path.Combine(_tempDir, "roundtrip.m3u8");
        PlaylistExporter.ExportM3U8(exportPath, "Round Trip", tracks);

        var imported = PlaylistImporter.Import(exportPath);

        Assert.Equal("Round Trip", imported.Name);
        Assert.Equal(2, imported.TrackPaths.Count);
        Assert.Equal(@"C:\Music\a.flac", imported.TrackPaths[0]);
        Assert.Equal(@"C:\Music\b.mp3", imported.TrackPaths[1]);
    }

    [Fact]
    public void RoundTrip_PLS_ExportThenImport_PreservesPaths()
    {
        var tracks = new List<MediaItem>
        {
            new() { Id = "1", Kind = MediaKind.Music, Title = "Song", FilePath = @"C:\Music\song.flac" },
        };

        var exportPath = Path.Combine(_tempDir, "roundtrip.pls");
        PlaylistExporter.ExportPLS(exportPath, "PLS Test", tracks);

        var imported = PlaylistImporter.Import(exportPath);

        Assert.Single(imported.TrackPaths);
        Assert.Equal(@"C:\Music\song.flac", imported.TrackPaths[0]);
    }

    [Fact]
    public void RoundTrip_XSPF_ExportThenImport_PreservesPaths()
    {
        var tracks = new List<MediaItem>
        {
            new() { Id = "1", Kind = MediaKind.Music, Title = "Song", Artist = "Art", Album = "Alb", FilePath = @"C:\Music\song.flac", Duration = TimeSpan.FromSeconds(300) },
        };

        var exportPath = Path.Combine(_tempDir, "roundtrip.xspf");
        PlaylistExporter.ExportXSPF(exportPath, "XSPF Test", tracks);

        var imported = PlaylistImporter.Import(exportPath);

        Assert.Equal("XSPF Test", imported.Name);
        Assert.Single(imported.TrackPaths);
        Assert.Equal(@"C:\Music\song.flac", imported.TrackPaths[0]);
    }

    // -- Unknown format --

    [Fact]
    public void Import_UnknownExtension_ReturnsEmptyResult()
    {
        var path = Path.Combine(_tempDir, "test.txt");
        File.WriteAllText(path, "just some text");

        var result = PlaylistImporter.Import(path);

        Assert.Empty(result.TrackPaths);
    }
}
