// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
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
/// Detects audio CDs in optical drives and reads the table of contents via Win32 IOCTL.
/// Computes MusicBrainz DiscID and enriches tracks with metadata from MusicBrainz + Cover Art Archive.
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
    private const uint IOCTL_CDROM_READ_TOC = 0x00024000;

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACK_DATA
    {
        public byte Reserved;
        public byte ControlAdr;
        public byte TrackNumber;
        public byte Reserved1;
        public byte Address0; // reserved
        public byte Address1; // minutes
        public byte Address2; // seconds
        public byte Address3; // frames
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct CDROM_TOC
    {
        public ushort Length;
        public byte FirstTrack;
        public byte LastTrack;
        public fixed byte TrackDataRaw[800]; // 100 tracks × 8 bytes each
    }
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
    /// Reads the TOC from an audio CD. On Windows, uses IOCTL_CDROM_READ_TOC for instant
    /// reliable track enumeration. Builds cdda:// URIs for LibVLC playback.
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

#if WINDOWS
        var tocResult = ReadTocWin32(drivePath);
        if (tocResult == null)
        {
            return info;
        }

        info.FirstTrack = tocResult.Value.FirstTrack;
        info.LastTrack = tocResult.Value.LastTrack;
        info.TrackOffsets = tocResult.Value.TrackOffsets;
        info.LeadOutOffset = tocResult.Value.LeadOutOffset;


        // Compute MusicBrainz DiscID
        info.DiscId = DiscIdService.ComputeDiscId(info.FirstTrack, info.LastTrack, info.TrackOffsets, info.LeadOutOffset);
        info.TocString = DiscIdService.BuildTocString(info.FirstTrack, info.LastTrack, info.TrackOffsets, info.LeadOutOffset);
        _log.Debug("DiscID computed: {DiscId} for {DrivePath}", info.DiscId, info.DrivePath);

        var label = string.IsNullOrWhiteSpace(volumeLabel)
            ? $"Audio CD ({drivePath})"
            : $"{volumeLabel} ({drivePath})";

        foreach (var (trackNum, duration) in tocResult.Value.Tracks)
        {
            info.Tracks.Add(new MediaItem
            {
                Id = $"cd:{drivePath}:{trackNum}",
                Kind = MediaKind.Music,
                Title = $"Track {trackNum}",
                Album = label,
                Track = (uint)trackNum,
                Duration = duration,
                StreamUrl = $"cdda:///{drivePath}/",
                Source = "cdda",
            });
        }

        // Look up metadata via MusicBrainz
        await EnrichFromMusicBrainzAsync(info);
#endif

        return info;
    }

    private static async Task EnrichFromMusicBrainzAsync(CdDiscInfo info)
    {
        if (string.IsNullOrEmpty(info.DiscId))
        {
            return;
        }

        // Check cache first
        var cached = MediaCache.GetCdMetadata(info.DiscId);
        if (cached != null)
        {

            ApplyCachedMetadata(info, cached);
            return;
        }

        // Query MusicBrainz

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

        // Fetch cover art
        byte[]? coverArt = null;
        if (!string.IsNullOrEmpty(result.ReleaseMbid))
        {
            coverArt = await MusicBrainzService.FetchCoverArtAsync(result.ReleaseMbid);
        }

        // Cache the result
        var tracksJson = System.Text.Json.JsonSerializer.Serialize(result.Tracks);
        MediaCache.SaveCdMetadata(new CachedCdMetadata
        {
            DiscId = info.DiscId,
            ReleaseMbid = result.ReleaseMbid,
            Artist = result.Artist,
            Album = result.Title,
            Year = result.Year,
            TracksJson = tracksJson,
            CoverArt = coverArt,
        });

        // Apply to tracks
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
        albumLabel += $" ({info.DrivePath})";

        foreach (var track in info.Tracks)
        {
            track.Album = albumLabel;
            track.Artist = cached.Artist;
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
        albumLabel += $" ({info.DrivePath})";

        foreach (var track in info.Tracks)
        {
            track.Album = albumLabel;
            track.Artist = result.Artist;
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

#if WINDOWS
    private record struct TocResult(int FirstTrack, int LastTrack, int[] TrackOffsets, int LeadOutOffset, List<(int TrackNum, TimeSpan Duration)> Tracks);

    /// <summary>
    /// Reads the CD table of contents via IOCTL_CDROM_READ_TOC.
    /// Returns track info + raw LBA offsets for DiscID computation.
    /// </summary>
    private static unsafe TocResult? ReadTocWin32(string drivePath)
    {
        var devicePath = $@"\\.\{drivePath}";

        var handle = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            _log.Warning("CreateFile failed for {DevicePath} (Win32 error {Error})", devicePath, Marshal.GetLastWin32Error());
            return null;
        }

        try
        {
            var toc = new CDROM_TOC();
            var tocSize = (uint)Marshal.SizeOf<CDROM_TOC>();
            var tocPtr = Marshal.AllocHGlobal((int)tocSize);

            try
            {
                var ok = DeviceIoControl(handle, IOCTL_CDROM_READ_TOC, IntPtr.Zero, 0, tocPtr, tocSize, out _, IntPtr.Zero);
                if (!ok)
                {
                    _log.Warning("IOCTL_CDROM_READ_TOC failed for {DevicePath} (Win32 error {Error})", devicePath, Marshal.GetLastWin32Error());
                    return null;
                }

                toc = Marshal.PtrToStructure<CDROM_TOC>(tocPtr);


                int totalEntries = toc.LastTrack - toc.FirstTrack + 1;
                var trackList = new List<(int, TimeSpan)>();
                var audioOffsets = new List<int>();
                var allOffsets = new int[totalEntries];
                int firstAudioTrack = toc.LastTrack;
                int lastAudioTrack = toc.FirstTrack;

                // First pass: collect offsets and identify audio vs data tracks
                // Control byte bit 2 (0x04): 0 = audio, 1 = data
                for (int i = 0; i < totalEntries; i++)
                {
                    var byteOffset = i * 8;
                    byte control = toc.TrackDataRaw[byteOffset + 1];
                    byte min = toc.TrackDataRaw[byteOffset + 5];
                    byte sec = toc.TrackDataRaw[byteOffset + 6];
                    byte frm = toc.TrackDataRaw[byteOffset + 7];
                    int lba = DiscIdService.MsfToLba(min, sec, frm);
                    allOffsets[i] = lba;

                    bool isData = (control & 0x04) != 0;
                    int trackNum = toc.FirstTrack + i;


                    if (!isData)
                    {
                        audioOffsets.Add(lba);
                        if (trackNum < firstAudioTrack) firstAudioTrack = trackNum;
                        if (trackNum > lastAudioTrack) lastAudioTrack = trackNum;
                    }
                }

                // Lead-out is the entry after the last track (track 0xAA)
                var leadOutByte = totalEntries * 8;
                byte loTrackNum = toc.TrackDataRaw[leadOutByte + 2];
                byte loMin = toc.TrackDataRaw[leadOutByte + 5];
                byte loSec = toc.TrackDataRaw[leadOutByte + 6];
                byte loFrm = toc.TrackDataRaw[leadOutByte + 7];
                int leadOut = DiscIdService.MsfToLba(loMin, loSec, loFrm);

                if (audioOffsets.Count == 0)
                {
                    _log.Debug("No audio tracks found in TOC (data-only disc) for {DevicePath}", devicePath);
                    return null;
                }

                // For mixed-mode CDs, the DiscID lead-out is the end of the audio session,
                // NOT the data track start or the full disc lead-out.
                // Standard multi-session gap = 11400 frames (6750 lead-out + 4500 lead-in + 150 pre-gap).
                // audio_session_leadout = first_data_track_start - 11400
                const int MultiSessionGap = 11400;
                int discIdLeadOut = leadOut;
                for (int i = 0; i < totalEntries; i++)
                {
                    byte control = toc.TrackDataRaw[i * 8 + 1];
                    if ((control & 0x04) != 0)
                    {
                        discIdLeadOut = allOffsets[i] - MultiSessionGap;

                        break;
                    }
                }

                // Compute durations for audio tracks only
                for (int i = 0; i < totalEntries; i++)
                {
                    byte control = toc.TrackDataRaw[i * 8 + 1];
                    if ((control & 0x04) != 0)
                    {
                        continue;
                    }

                    int trackNum = toc.FirstTrack + i;
                    int endOffset = (i < totalEntries - 1) ? allOffsets[i + 1] : discIdLeadOut;
                    int durationFrames = endOffset - allOffsets[i];
                    var duration = TimeSpan.FromSeconds((double)durationFrames / 75.0);
                    trackList.Add((trackNum, duration));
                }


                return new TocResult(firstAudioTrack, lastAudioTrack, [.. audioOffsets], discIdLeadOut, trackList);
            }
            finally
            {
                Marshal.FreeHGlobal(tocPtr);
            }
        }
        finally
        {
            CloseHandle(handle);
        }
    }
#endif
}
