// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Tests;

/// <summary>
/// Opt-in live test that imports a real file onto a connected iPod and verifies
/// the iTunesDB re-reads with the new track. Skipped unless ORGZ_DEVICE_MOUNT,
/// ORGZ_IMPORT_SOURCE and ORGZ_FFMPEG are all set — it WRITES to the device, so
/// it never runs in normal/CI test passes.
/// </summary>
public class IPodLiveImportTests
{
    [Fact]
    public async Task Import_source_onto_device_and_verify_db()
    {
        var mount  = Environment.GetEnvironmentVariable("ORGZ_DEVICE_MOUNT");
        var source = Environment.GetEnvironmentVariable("ORGZ_IMPORT_SOURCE");
        var ffmpeg = Environment.GetEnvironmentVariable("ORGZ_FFMPEG");
        var gen    = Environment.GetEnvironmentVariable("ORGZ_DEVICE_GEN");
        var fwid   = Environment.GetEnvironmentVariable("ORGZ_FW_GUID");   // needed for hash58 (Classic / Nano 3G+)

        if (string.IsNullOrWhiteSpace(mount) || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(ffmpeg))
        {
            return;   // not opted in
        }

        var result = await OrgZ.Services.IPodTrackImporter.ImportAsync(mount, source, ffmpeg, gen, fwid);

        Assert.True(File.Exists(result.DestFile), $"copied file missing: {result.DestFile}");

        var dbPath = Path.Combine(mount, "iPod_Control", "iTunes", "iTunesDB");
        OrgZ.Services.ITunesDbReader.ReadAll(dbPath, mount, out var tracks, out var playlists);

        var added = Assert.Single(tracks, t => t.TrackId == result.TrackId);
        Assert.Equal(result.Title, added.Title);

        // The track must be reachable from a playlist reference too (we add it to
        // the master; the reader hides the master but the id must still resolve).
        Assert.False(string.IsNullOrEmpty(added.FilePath));
    }
}
