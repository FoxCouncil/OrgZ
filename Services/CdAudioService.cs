// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using FoxRedbook;
using LibVLCSharp.Shared;
using Serilog;

namespace OrgZ.Services;

public class CdDiscInfo
{
    public string DrivePath { get; set; } = "";
    public int FirstTrack { get; set; }
    public int LastTrack { get; set; }
    public int[] TrackOffsets { get; set; } = [];
    public int LeadOutOffset { get; set; }
    public string? DiscId { get; set; }
    public string? TocString { get; set; }
    public string? ReleaseMbid { get; set; }
    public byte[]? CoverArtBytes { get; set; }
    public List<MediaItem> Tracks { get; set; } = [];
}

/// <summary>
/// Detects audio CDs in optical drives and reads the table of contents via
/// FoxRedbook's cross-platform SCSI passthrough.  Computes MusicBrainz DiscID and
/// enriches tracks with metadata from MusicBrainz + Cover Art Archive.
/// </summary>
public static class CdAudioService
{
    private static readonly ILogger _log = Logging.For("CdAudio");

#if WINDOWS
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr secAttr, uint creationDisposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint ioControlCode, IntPtr inBuffer, uint inBufferSize, IntPtr outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint IOCTL_STORAGE_CHECK_VERIFY = 0x002D4800;
#endif

    /// <summary>
    /// Checks if a specific CD-ROM drive has media inserted.
    /// Uses IOCTL_STORAGE_CHECK_VERIFY on Windows (instant, works for audio + data CDs).
    /// </summary>
    public static bool DriveHasMedia(DriveInfo drive)
    {
#if WINDOWS
        var devicePath = $@"\\.\{drive.Name.TrimEnd('\\')}";
        var handle = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return false;
        }

        var hasMedia = DeviceIoControl(handle, IOCTL_STORAGE_CHECK_VERIFY, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero);
        CloseHandle(handle);
        return hasMedia;
#else
        return drive.IsReady;
#endif
    }

    /// <summary>
    /// Returns all CD-ROM drives on the system (regardless of media state).
    /// </summary>
    public static List<DriveInfo> GetAllCdDrives()
    {
        return DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.CDRom)
            .ToList();
    }

    /// <summary>
    /// Returns only CD-ROM drives that currently have a disc inserted.
    /// </summary>
    public static List<DriveInfo> GetCdDrivesWithMedia()
    {
        return GetAllCdDrives().Where(DriveHasMedia).ToList();
    }

    /// <summary>
    /// Reads the TOC from an audio CD via FoxRedbook (cross-platform SCSI passthrough).
    /// Builds cdda:// URIs for LibVLC playback and enriches via MusicBrainz + Cover Art Archive.
    /// </summary>
    public static async Task<CdDiscInfo> ReadDiscAsync(LibVLC vlc, DriveInfo drive)
    {
        var drivePath = drive.Name.TrimEnd('\\', '/');

        string volumeLabel;
        try
        {
            volumeLabel = drive.VolumeLabel;
        }
        catch
        {
            volumeLabel = "";
        }

        var info = new CdDiscInfo { DrivePath = drivePath };

        // FoxRedbook wants a bare BSD name (disk4) or /dev/ path on macOS; .NET hands us
        // the mount point (/Volumes/Audio CD). Resolve mount → device before opening.
        var openPath = OperatingSystem.IsMacOS() ? ResolveMacBsdDevice(drivePath) ?? drivePath : drivePath;

        // MacOpticalDrive ctor runs DA + IOKit work that doesn't belong on the
        // UI thread (it shells diskutil, walks the IOKit tree, claims exclusive
        // SCSI access). Run it on the thread pool.
        //
        // macOS dev note: SCSITaskUserClient access is gated on the calling
        // binary's code signature. The .NET-generated apphost (`bin/.../OrgZ`)
        // is ad-hoc signed and gets kIOReturnUnsupported. Launch via
        // `scripts/run-mac.sh` (which uses `dotnet exec OrgZ.dll`) so the host
        // process is Microsoft-signed `dotnet` and the kernel grants access.
        // Once Developer ID signing is wired into Velopack pack this becomes
        // a non-issue for released builds.
        DiscInfo? discInfo;
        try
        {
            discInfo = await Task.Run(() =>
            {
                using var optical = OpticalDrive.Open(openPath);
                return optical.ReadDiscInfoAsync().GetAwaiter().GetResult();
            });
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "FoxRedbook ReadDiscInfo failed for {DrivePath} (open={OpenPath})", drivePath, openPath);
            return info;
        }

        var toc = discInfo.Toc;
        var audioTracks = toc.Tracks.Where(t => t.Type == FoxRedbook.TrackType.Audio).ToList();
        if (audioTracks.Count == 0)
        {
            _log.Debug("No audio tracks on disc in {DrivePath}", drivePath);
            return info;
        }

        info.FirstTrack = audioTracks[0].Number;
        info.LastTrack = audioTracks[^1].Number;
        info.TrackOffsets = audioTracks.Select(t => (int)t.StartLba).ToArray();
        info.LeadOutOffset = (int)toc.LeadOutLba;
        info.DiscId = discInfo.MusicBrainzDiscId;
        info.TocString = DiscIdService.BuildTocString(info.FirstTrack, info.LastTrack, info.TrackOffsets, info.LeadOutOffset);
        _log.Debug("DiscID computed: {DiscId} for {DrivePath}", info.DiscId, info.DrivePath);

        var label = string.IsNullOrWhiteSpace(volumeLabel) ? "Audio CD" : volumeLabel;

        foreach (var track in audioTracks)
        {
            var duration = TimeSpan.FromSeconds(track.SectorCount / 75.0);
            // On macOS libvlc's cdda:// access module asserts inside CoreFoundation
            // and kills the host process. cddafs exposes each CDDA track as a
            // synthetic AIFF file at the mount point, which libvlc plays cleanly.
            // Until we move playback to FoxRedbook streaming, route through the
            // AIFF on macOS only; Windows/Linux keep using libvlc's cdda module.
            var streamUrl = OperatingSystem.IsMacOS()
                ? new Uri(Path.Combine(drivePath, $"{track.Number} Audio Track.aiff")).AbsoluteUri
                : $"cdda:///{drivePath}/";
            info.Tracks.Add(new MediaItem
            {
                Id = $"cd:{drivePath}:{track.Number}",
                Kind = MediaKind.Music,
                Title = $"Track {track.Number}",
                Album = label,
                Track = (uint)track.Number,
                Duration = duration,
                StreamUrl = streamUrl,
                Source = "cdda",
            });
        }

        await EnrichFromMusicBrainzAsync(info);

        return info;
    }

    private static async Task EnrichFromMusicBrainzAsync(CdDiscInfo info)
    {
        if (string.IsNullOrEmpty(info.DiscId))
        {
            return;
        }

        var cached = MediaCache.GetCdMetadata(info.DiscId);
        // Force a fresh MusicBrainz hit when the cached row is missing data
        // that a newer version of OrgZ now fetches — cover art via the
        // release-group CAA fallback, and genre from MB's voted genres.
        // Won't loop: the next save either fills the gap or persists a real
        // null, and we don't retry until the row is invalidated.
        bool needsRefetch = cached != null && (cached.CoverArt == null || string.IsNullOrEmpty(cached.Genre));
        if (cached != null && !needsRefetch)
        {
            ApplyCachedMetadata(info, cached);
            return;
        }
        if (cached != null)
        {
            _log.Information("Cached CD metadata for disc {DiscId} missing {Missing} — re-running MusicBrainz lookup", info.DiscId,
                cached.CoverArt == null && string.IsNullOrEmpty(cached.Genre) ? "cover art and genre"
                : cached.CoverArt == null ? "cover art"
                : "genre");
        }

        var result = await MusicBrainzService.LookupByDiscIdAsync(info.DiscId);

        if (result == null && !string.IsNullOrEmpty(info.TocString))
        {
            result = await MusicBrainzService.LookupByTocAsync(info.TocString);
        }

        if (result == null)
        {
            return;
        }

        _log.Information("MusicBrainz match: Artist={Artist} Title={Title} Year={Year}", result.Artist, result.Title, result.Year);
        info.ReleaseMbid = result.ReleaseMbid;

        byte[]? coverArt = null;
        if (!string.IsNullOrEmpty(result.ReleaseMbid))
        {
            coverArt = await MusicBrainzService.FetchCoverArtAsync(result.ReleaseMbid, result.ReleaseGroupMbid);
        }

        var tracksJson = System.Text.Json.JsonSerializer.Serialize(result.Tracks);
        MediaCache.SaveCdMetadata(new CachedCdMetadata
        {
            DiscId = info.DiscId,
            ReleaseMbid = result.ReleaseMbid,
            Artist = result.Artist,
            Album = result.Title,
            Year = result.Year,
            Genre = result.Genre,
            TracksJson = tracksJson,
            CoverArt = coverArt,
        });

        ApplyLookupResult(info, result, coverArt);
    }

    private static void ApplyCachedMetadata(CdDiscInfo info, CachedCdMetadata cached)
    {
        info.ReleaseMbid = cached.ReleaseMbid;
        info.CoverArtBytes = cached.CoverArt;

        if (string.IsNullOrEmpty(cached.Album))
        {
            return;
        }

        List<TrackInfo>? trackInfos = null;
        if (!string.IsNullOrEmpty(cached.TracksJson))
        {
            trackInfos = System.Text.Json.JsonSerializer.Deserialize<List<TrackInfo>>(cached.TracksJson);
        }

        var albumLabel = cached.Album;
        if (cached.Year.HasValue)
        {
            albumLabel += $" ({cached.Year})";
        }

        foreach (var track in info.Tracks)
        {
            track.Album = albumLabel;
            track.Artist = cached.Artist;
            track.Genre = cached.Genre;
            if (cached.Year.HasValue)
            {
                track.Year = cached.Year;
            }

            var mbTrack = trackInfos?.FirstOrDefault(t => t.Position == track.Track);
            if (mbTrack != null)
            {
                track.Title = mbTrack.Title;
                if (!string.IsNullOrWhiteSpace(mbTrack.Artist))
                {
                    track.Artist = mbTrack.Artist;
                }
            }
        }
    }

    private static void ApplyLookupResult(CdDiscInfo info, DiscLookupResult result, byte[]? coverArt)
    {
        info.CoverArtBytes = coverArt;

        var albumLabel = result.Title;
        if (result.Year.HasValue)
        {
            albumLabel += $" ({result.Year})";
        }

        foreach (var track in info.Tracks)
        {
            track.Album = albumLabel;
            track.Artist = result.Artist;
            track.Genre = result.Genre;
            if (result.Year.HasValue)
            {
                track.Year = result.Year;
            }

            var mbTrack = result.Tracks.FirstOrDefault(t => t.Position == track.Track);
            if (mbTrack != null)
            {
                track.Title = mbTrack.Title;
                if (!string.IsNullOrWhiteSpace(mbTrack.Artist))
                {
                    track.Artist = mbTrack.Artist;
                }
            }
        }
    }

    // Translates a macOS mount point like "/Volumes/Audio CD" to the underlying BSD device
    // ("/dev/disk4") by parsing `df -P` output. FoxRedbook's IOKit lookup needs the device,
    // not the mount path. Shelling out keeps us off the moving target of statfs struct
    // layouts across macOS ABIs; a single fork on disc-insert is fine.
    internal static string? ResolveMacBsdDevice(string mountPath)
    {
        try
        {
            using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "/bin/df",
                ArgumentList = { "-P", mountPath },
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return null;
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);

            // POSIX df output: header line, then "<filesystem> <blocks> <used> ... <mount>".
            // First whitespace-separated token on line 2 is the device path.
            var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                return null;
            }

            var firstToken = lines[1].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return string.IsNullOrEmpty(firstToken) || !firstToken.StartsWith("/dev/", StringComparison.Ordinal)
                ? null
                : firstToken;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "df failed to resolve BSD device for {MountPath}", mountPath);
            return null;
        }
    }
}
