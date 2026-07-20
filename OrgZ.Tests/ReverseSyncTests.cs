// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The reverse-sync (device → library) protection gate: FairPlay files must be refused. The .m4p
/// extension is the cheap tell; a 'drms' atom inside an .m4a needs the container opened, and an
/// unreadable container reads as NOT protected (the copy is allowed rather than blocked blind).
/// </summary>
public class ReverseSyncTests
{
    [Theory]
    [InlineData("track.m4p", true)]
    [InlineData("track.M4P", true)]
    [InlineData("track.mp3", false)]
    [InlineData("track.flac", false)]
    [InlineData("track.wav", false)]
    public void Protected_detection_by_extension(string file, bool expected)
    {
        var path = Path.Combine(Path.GetTempPath(), "orgz-prot-" + Guid.NewGuid().ToString("N") + "-" + file);
        File.WriteAllBytes(path, new byte[64]);
        try
        {
            Assert.Equal(expected, MainWindowViewModel.IsProtectedTrack(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Unreadable_m4a_container_reads_as_not_protected()
    {
        var path = Path.Combine(Path.GetTempPath(), "orgz-prot-" + Guid.NewGuid().ToString("N") + ".m4a");
        File.WriteAllBytes(path, new byte[64]);   // not a real MP4 - TagLib throws, gate must not block
        try
        {
            Assert.False(MainWindowViewModel.IsProtectedTrack(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
