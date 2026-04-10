// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Runtime.InteropServices;
using LibVLCSharp.Shared;

namespace OrgZ.Services;

/// <summary>
/// Detects audio CDs in optical drives and reads the table of contents via LibVLC.
/// Each track becomes a MediaItem with a cdda:// URI as the StreamUrl.
/// </summary>
public static class CdAudioService
{
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
        var all = GetAllCdDrives();
        System.Diagnostics.Debug.WriteLine($"CdAudio: {all.Count} CD-ROM drive(s): {string.Join(", ", all.Select(d => d.Name))}");
        foreach (var d in all)
        {
            var has = DriveHasMedia(d);
            System.Diagnostics.Debug.WriteLine($"CdAudio:   {d.Name} → HasMedia={has}");
        }
        return all.Where(DriveHasMedia).ToList();
    }

    /// <summary>
    /// Reads the TOC from an audio CD. On Windows, uses IOCTL_CDROM_READ_TOC for instant
    /// reliable track enumeration. Builds cdda:// URIs for LibVLC playback.
    /// </summary>
    public static Task<List<MediaItem>> ReadDiscAsync(LibVLC vlc, DriveInfo drive)
    {
        var tracks = new List<MediaItem>();
        var drivePath = drive.Name.TrimEnd('\\', '/');

        string label;
        try
        {
            label = drive.VolumeLabel;
        }
        catch
        {
            label = "";
        }

        label = string.IsNullOrWhiteSpace(label)
            ? $"Audio CD ({drivePath})"
            : $"{label} ({drivePath})";

#if WINDOWS
        var tocTracks = ReadTocWin32(drivePath);
        System.Diagnostics.Debug.WriteLine($"CdAudio: TOC read from {drivePath}: {tocTracks.Count} track(s)");

        foreach (var (trackNum, duration) in tocTracks)
        {
            tracks.Add(new MediaItem
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
#endif

        return Task.FromResult(tracks);
    }

#if WINDOWS
    /// <summary>
    /// Reads the CD table of contents via IOCTL_CDROM_READ_TOC.
    /// Returns (trackNumber, duration) pairs. Red Book: 75 frames/second.
    /// </summary>
    private static unsafe List<(int TrackNum, TimeSpan Duration)> ReadTocWin32(string drivePath)
    {
        var result = new List<(int, TimeSpan)>();
        var devicePath = $@"\\.\{drivePath}";

        var handle = CreateFile(devicePath, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            System.Diagnostics.Debug.WriteLine($"CdAudio: CreateFile failed for {devicePath}");
            return result;
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
                    System.Diagnostics.Debug.WriteLine($"CdAudio: IOCTL_CDROM_READ_TOC failed");
                    return result;
                }

                toc = Marshal.PtrToStructure<CDROM_TOC>(tocPtr);
                System.Diagnostics.Debug.WriteLine($"CdAudio: TOC first={toc.FirstTrack} last={toc.LastTrack}");

                int trackCount = toc.LastTrack - toc.FirstTrack + 1;

                for (int i = 0; i < trackCount; i++)
                {
                    // Each TRACK_DATA is 8 bytes
                    var offset = i * 8;
                    var nextOffset = (i + 1) * 8;

                    byte min1 = toc.TrackDataRaw[offset + 5];
                    byte sec1 = toc.TrackDataRaw[offset + 6];
                    byte frm1 = toc.TrackDataRaw[offset + 7];

                    byte min2 = toc.TrackDataRaw[nextOffset + 5];
                    byte sec2 = toc.TrackDataRaw[nextOffset + 6];
                    byte frm2 = toc.TrackDataRaw[nextOffset + 7];

                    int startFrames = min1 * 60 * 75 + sec1 * 75 + frm1;
                    int endFrames = min2 * 60 * 75 + sec2 * 75 + frm2;
                    int durationFrames = endFrames - startFrames;

                    var duration = TimeSpan.FromSeconds((double)durationFrames / 75.0);
                    result.Add((toc.FirstTrack + i, duration));
                }
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

        return result;
    }
#endif
}
