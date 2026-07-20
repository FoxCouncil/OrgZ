// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.Tests;

/// <summary>
/// The iTunes DeviceInfo name file (u16 LE char count + UTF-16LE name, zero-padded to 0x600) and the
/// FAT32 volume-label mirror. The DeviceInfo name is authoritative and unclipped - it's how
/// "DEBBIE'S IPOD" survives the label's 11-char FAT32 truncation.
/// </summary>
public class IPodRenameTests
{
    [Theory]
    [InlineData("FoxPod")]
    [InlineData("DEBBIE'S IPOD")]                       // longer than a FAT32 label can hold
    [InlineData("Ünïcodé — iPod ♥")]                    // UTF-16 payload, no OEM-charset squashing
    public void DeviceInfo_name_round_trips(string name)
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-ren-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        try
        {
            IPodRename.WriteName(mount, name);

            var file = new FileInfo(IPodRename.DeviceInfoPath(mount));
            Assert.Equal(0x600, file.Length);           // iTunes's exact layout size
            Assert.Equal(name, IPodRename.TryReadName(mount));
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }

    [Fact]
    public void Missing_or_garbled_DeviceInfo_reads_as_null()
    {
        var mount = Path.Combine(Path.GetTempPath(), "orgz-ren-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(mount);
        try
        {
            Assert.Null(IPodRename.TryReadName(mount));                             // absent

            var path = IPodRename.DeviceInfoPath(mount);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, [0xFF, 0xFF, 0x00]);                           // claims 65535 chars in 3 bytes
            Assert.Null(IPodRename.TryReadName(mount));
        }
        finally
        {
            Directory.Delete(mount, recursive: true);
        }
    }

    [Fact]
    public void Master_playlist_name_reads_without_parsing_tracks()
    {
        // iTunes names the hidden master playlist after the iPod; our own writer stamps "OrgZ".
        var path = Path.Combine(Path.GetTempPath(), "orgz-mpl-" + Guid.NewGuid().ToString("N") + ".itunesdb");
        try
        {
            var doc = ITunesDbWriter.CreateEmpty();
            ITunesDbChunkTree.Normalize(doc.Root);
            File.WriteAllBytes(path, ITunesDbChunkTree.Serialize(doc));
            Assert.Equal("OrgZ", ITunesDbReader.TryReadDeviceName(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Track_only_database_reads_as_no_device_name()
    {
        // The bripod fixture is a tracks-only envelope (mhsd type 1, no playlist dataset).
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "itunesdb", "bripod-3tracks.itunesdb");
        Assert.Null(ITunesDbReader.TryReadDeviceName(fixture));
    }

    [Theory]
    [InlineData("FoxPod", "FoxPod")]
    [InlineData("DEBBIE'S IPOD", "DEBBIE'S IP")]        // FAT32's 11-char ceiling, trailing space trimmed
    [InlineData("A/B\\C:D*E?F", "ABCDEF")]              // FAT-invalid characters stripped
    [InlineData("***", "")]                             // nothing label-safe left
    public void Volume_label_sanitizes_to_fat32_rules(string name, string expected)
    {
        Assert.Equal(expected, IPodRename.SanitizeVolumeLabel(name));
    }
}
