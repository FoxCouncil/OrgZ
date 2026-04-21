// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Tests the pure-function helpers inside <see cref="MainWindowViewModel"/> that
/// power the "Send to Device" feature. Exercising the full async method would need
/// a live VM with LibVLC and Avalonia; the helpers are the part that has real logic
/// to get wrong, so we isolate and cover them.
/// </summary>
public class SendPlaylistToDeviceHelperTests
{
    // ===== NormalizeMatchKey =====

    [Theory]
    [InlineData("Rush",      "Subdivisions",    "Rush|Subdivisions")]
    [InlineData("  rush  ",  "  subdivisions ", "rush|subdivisions")]
    [InlineData(null,        "Solo Work",       "|Solo Work")]
    [InlineData("",          "Solo Work",       "|Solo Work")]
    public void NormalizeMatchKey_combines_trimmed_artist_and_title(string? artist, string title, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.NormalizeMatchKey(artist, title));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "")]
    [InlineData(null, "   ")]
    [InlineData("Rush", "")]
    public void NormalizeMatchKey_returns_empty_when_title_missing(string? artist, string? title)
    {
        Assert.Equal(string.Empty, MainWindowViewModel.NormalizeMatchKey(artist, title));
    }

    // ===== ToDeviceRelativePath =====

    [Theory]
    [InlineData(@"L:\Music\Rush\Signals\01.mp3", @"L:\",                  "/Music/Rush/Signals/01.mp3")]
    [InlineData("/run/media/fox/FOXPOD/Music/01.mp3", "/run/media/fox/FOXPOD/", "/Music/01.mp3")]
    [InlineData("/run/media/fox/FOXPOD/Music/01.mp3", "/run/media/fox/FOXPOD",  "/Music/01.mp3")]
    public void ToDeviceRelativePath_strips_mount_and_normalizes_separator(string absolute, string mount, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.ToDeviceRelativePath(absolute, mount));
    }

    [Fact]
    public void ToDeviceRelativePath_returns_normalized_input_when_not_under_mount()
    {
        // If the absolute path isn't under the mount (shouldn't happen in practice, but
        // defensive), return it with forward slashes rather than fabricating a prefix.
        var result = MainWindowViewModel.ToDeviceRelativePath(@"C:\OtherDrive\track.mp3", @"L:\");
        Assert.Equal("C:/OtherDrive/track.mp3", result);
    }

    // ===== ToMountAbsolute =====

    [Fact]
    public void ToMountAbsolute_resolves_device_rel_path_against_mount()
    {
        var mount = Path.Combine(Path.GetTempPath(), "mount-" + Path.GetRandomFileName());
        Directory.CreateDirectory(mount);
        try
        {
            var result = MainWindowViewModel.ToMountAbsolute("/Music/track.mp3", mount);
            var expected = Path.GetFullPath(Path.Combine(mount, "Music", "track.mp3"));
            Assert.Equal(expected, result);
        }
        finally
        {
            Directory.Delete(mount, true);
        }
    }

    // ===== Round-trip: ToDeviceRelativePath → ToMountAbsolute =====

    [Fact]
    public void DevicePath_round_trips_through_relative_then_absolute()
    {
        var mount = Path.Combine(Path.GetTempPath(), "mount-" + Path.GetRandomFileName());
        Directory.CreateDirectory(mount);
        try
        {
            var original = Path.Combine(mount, "Music", "Rush", "01.mp3");
            var rel = MainWindowViewModel.ToDeviceRelativePath(original, mount);
            var roundTripped = MainWindowViewModel.ToMountAbsolute(rel, mount);
            Assert.Equal(Path.GetFullPath(original), roundTripped);
        }
        finally
        {
            Directory.Delete(mount, true);
        }
    }

    // ===== SanitizeFileName =====

    [Theory]
    [InlineData("Road Trip",            "Road Trip")]
    [InlineData("normal-name_123",      "normal-name_123")]
    [InlineData("Feat. AC/DC",          "Feat. AC_DC")]       // forward slash on all platforms is invalid
    public void SanitizeFileName_passes_safe_names(string input, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.SanitizeFileName(input));
    }

    [Fact]
    public void SanitizeFileName_replaces_invalid_chars_with_underscore()
    {
        // Windows considers these all invalid: < > : " | ? * and control chars
        var input = "bad<name>:one\"|two?three*";
        var result = MainWindowViewModel.SanitizeFileName(input);

        // Whatever got replaced, the result must not contain any invalid filename char
        var invalid = Path.GetInvalidFileNameChars();
        Assert.DoesNotContain(result, c => invalid.Contains(c));
    }

    [Fact]
    public void SanitizeFileName_trims_whitespace()
    {
        Assert.Equal("trimmed", MainWindowViewModel.SanitizeFileName("  trimmed  "));
    }
}

/// <summary>
/// End-to-end test of the M3U write-then-read cycle that the Send-to-Device feature
/// performs. Simulates the exact bytes the VM would write and verifies the Rockbox
/// reader parses them back to the expected track list.
/// </summary>
public class SendPlaylistM3URoundTripTests : IDisposable
{
    private readonly string _mountPath;
    private readonly string _playlistsDir;

    public SendPlaylistM3URoundTripTests()
    {
        _mountPath = Path.Combine(Path.GetTempPath(), "OrgZ-send-pl-" + Path.GetRandomFileName());
        _playlistsDir = Path.Combine(_mountPath, "Playlists");
        Directory.CreateDirectory(_playlistsDir);

        // Also create the target track directories so path resolution in
        // M3UPlaylistReader.ResolvePath produces stable output.
        Directory.CreateDirectory(Path.Combine(_mountPath, "Music", "Rush", "Signals"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_mountPath)) Directory.Delete(_mountPath, recursive: true); } catch { }
    }

    [Fact]
    public void WriteM3U_then_read_back_recovers_track_list()
    {
        // This mirrors the VM's SendPlaylistToDevice format
        var path = Path.Combine(_playlistsDir, "Road Trip.m3u");
        var m3u = """
            #EXTM3U
            #PLAYLIST:Road Trip
            /Music/Rush/Signals/01.mp3
            /Music/Rush/Signals/02.mp3
            """;
        File.WriteAllText(path, m3u);

        var read = M3UPlaylistReader.ReadOne(path, _mountPath);
        Assert.NotNull(read);
        Assert.Equal("Road Trip", read!.Name);   // #PLAYLIST: directive wins
        Assert.Equal(2, read.TrackIds.Count);

        var expected1 = Path.GetFullPath(Path.Combine(_mountPath, "Music", "Rush", "Signals", "01.mp3"));
        var expected2 = Path.GetFullPath(Path.Combine(_mountPath, "Music", "Rush", "Signals", "02.mp3"));
        Assert.Equal(expected1, read.TrackIds[0]);
        Assert.Equal(expected2, read.TrackIds[1]);
    }
}
