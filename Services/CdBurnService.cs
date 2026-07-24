// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using FoxOrangebook;
using FoxOrangebook.FileSystem;
using FoxRedbook;
using Serilog;

namespace OrgZ.Services;

public sealed record CdBurnTrack
{
    public required string WavFilePath { get; init; }
    public string? Title { get; init; }
    public string? Performer { get; init; }
}

/// <summary>One file on a data disc: where it lands on the disc and where its bytes come from.</summary>
public sealed record DataBurnFile
{
    /// <summary>Destination path on the disc, e.g. <c>"Artist/Album/01 Song.mp3"</c>.</summary>
    public required string DiscPath { get; init; }
    public required string SourcePath { get; init; }
}

public readonly record struct CdBurnProgress
{
    public required int TrackNumber { get; init; }
    public required int TrackCount { get; init; }
    public required int TrackSectors { get; init; }
    public required int SectorsWritten { get; init; }
    public required long TotalDiscSectors { get; init; }
    public required long TotalSectorsWritten { get; init; }
    public double DiscPercent => TotalDiscSectors == 0 ? 0 : (double)TotalSectorsWritten / TotalDiscSectors;
}

/// <summary>
/// Disc-At-Once audio burning via FoxOrangebook.  Accepts WAV files containing
/// 16-bit stereo 44.1 kHz PCM (the CD-DA native format) and programs the drive
/// with a full cue sheet before streaming sectors.
/// </summary>
/// <remarks>
/// Transcoding from lossy/lossless sources (MP3, FLAC, etc.) is not done here -
/// callers should rip via <see cref="CdRipService"/> or supply already-encoded
/// WAVs.  Non-CD-DA WAV formats are rejected up front so we never program a
/// coaster-inducing cue sheet.
/// </remarks>
public static class CdBurnService
{
    private const int BytesPerSector = 2352;
    private const int RedbookSampleRate = 44100;
    private const int RedbookChannels = 2;
    private const int RedbookBitsPerSample = 16;

    /// <summary>Red Book caps a disc at 99 tracks.</summary>
    public const int MaxRedbookTracks = 99;

    /// <summary>Red Book minimum track length: 4 seconds = 300 sectors.</summary>
    public const int MinRedbookTrackSectors = 4 * 75;

    private static readonly ILogger _log = Logging.For("CdBurn");

    /// <summary>Result of <see cref="CheckBurnMedia"/>.</summary>
    public enum BurnMediaStatus
    {
        /// <summary>A blank, writable disc is loaded - ready to burn.</summary>
        Ready,
        /// <summary>The drive is a recorder but no disc is loaded.</summary>
        NoMedia,
        /// <summary>A disc is loaded but it already has content (not blank).</summary>
        NotBlank,
        /// <summary>The drive can't write discs (DAO unsupported).</summary>
        NotWritable,
        /// <summary>The drive answered NOT READY - busy finishing a prior operation, or still
        /// spinning up. Common after an aborted burn leaves a half-written disc (sense 02/04/07).
        /// Ejecting and reinserting the disc clears it.</summary>
        Busy,
        /// <summary>The drive couldn't be opened or queried.</summary>
        DriveError,
    }

    /// <summary>Media pre-flight result: status plus the blank disc's writable capacity when the drive reports one.</summary>
    public readonly record struct BurnMediaInfo
    {
        public required BurnMediaStatus Status { get; init; }

        /// <summary>Writable capacity in CD sectors (1/75 s each), from READ DISC INFORMATION's
        /// Last Possible Lead-Out Start Address. Null when the drive doesn't report it.</summary>
        public long? CapacitySectors { get; init; }

        /// <summary>True when the loaded disc is rewritable (READ DISC INFORMATION's Erasable
        /// bit) - a <see cref="BurnMediaStatus.NotBlank"/> CD-RW can be blanked and reused.</summary>
        public bool Erasable { get; init; }

        /// <summary>MMC media profile from GET CONFIGURATION (0x0009 CD-R, 0x000A CD-RW,
        /// 0x001A DVD+RW, ...). 0 when no disc is loaded or the drive couldn't say.</summary>
        public ushort Profile { get; init; }

        /// <summary>Human-readable media name derived from <see cref="Profile"/>.</summary>
        public string MediaLabel => MediaLabelForProfile(Profile);

        /// <summary>CD-class recordable media (CD-R / CD-RW) - eligible for Audio CD and Data CD burns.</summary>
        public bool IsCdRecordable => Profile is ProfileCdR or ProfileCdRw;

        /// <summary>Rewritable media. Also means no simulated (test) writes - MMC prohibits them on high-speed RW.</summary>
        public bool IsRewritable => Profile is ProfileCdRw or ProfileDvdPlusRw or ProfileDvdMinusRwRo or ProfileDvdMinusRwSeq or ProfileDvdRam or ProfileBdRe;

        /// <summary>DVD+RW - the one DVD profile FoxOrangebook data burns support today.</summary>
        public bool IsDataDvdCapable => Profile == ProfileDvdPlusRw;
    }

    // MMC media profiles (GET CONFIGURATION header bytes 6-7).
    public const ushort ProfileCdR = 0x0009;
    public const ushort ProfileCdRw = 0x000A;
    public const ushort ProfileDvdRam = 0x0012;
    public const ushort ProfileDvdMinusRwRo = 0x0013;
    public const ushort ProfileDvdMinusRwSeq = 0x0014;
    public const ushort ProfileDvdPlusRw = 0x001A;
    public const ushort ProfileBdRe = 0x0043;

    /// <summary>Friendly name for an MMC media profile ("CD-RW", "DVD+RW", ...).</summary>
    public static string MediaLabelForProfile(ushort profile) => profile switch
    {
        0x0000 => "No disc",
        0x0008 => "CD-ROM",
        ProfileCdR => "CD-R",
        ProfileCdRw => "CD-RW",
        0x0010 => "DVD-ROM",
        0x0011 => "DVD-R",
        ProfileDvdRam => "DVD-RAM",
        ProfileDvdMinusRwRo or ProfileDvdMinusRwSeq => "DVD-RW",
        0x0015 or 0x0016 => "DVD-R DL",
        ProfileDvdPlusRw => "DVD+RW",
        0x001B => "DVD+R",
        0x002B => "DVD+R DL",
        0x0040 => "BD-ROM",
        0x0041 or 0x0042 => "BD-R",
        ProfileBdRe => "BD-RE",
        _ => $"Unknown media (0x{profile:X4})",
    };

    /// <summary>
    /// Un-elevated pre-flight before a burn: opens <paramref name="drivePath"/> and checks a
    /// blank, writable disc is loaded - the same SCSI passthrough as the recorder probe, so no
    /// UAC. Lets the GUI fail fast with a clear message instead of transcoding and prompting
    /// for elevation only to have the drive reject the burn.
    /// </summary>
    public static BurnMediaInfo CheckBurnMedia(string drivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(drivePath);

        try
        {
            using var drive = OpticalDrive.Open(drivePath);
            if (drive is not IScsiTransport transport)
            {
                return new BurnMediaInfo { Status = BurnMediaStatus.DriveError };
            }

            var session = new BurnSession(transport);

            // Media profile answers even with no disc loaded (profile 0) - it feeds the
            // dialog's media line and its Audio/Data/DVD mode gating.
            ushort profile = 0;
            try
            {
                profile = new DataBurnSession(transport).GetCurrentProfile();
            }
            catch (Exception ex)
            {
                _log.Debug(ex, "GET CONFIGURATION profile probe failed for {Drive}", drivePath);
            }

            if (!session.SupportsDaoBurn())
            {
                return new BurnMediaInfo { Status = BurnMediaStatus.NotWritable, Profile = profile };
            }

            try
            {
                var info = session.ReadDiscInfo();
                if (info.Status != DiscStatus.Blank)
                {
                    // A formatted DVD+RW never reads "blank" - data burns overwrite it in
                    // place, so it's Ready as-is. Its READ DISC INFORMATION lead-out field
                    // is CD-MSF-based and meaningless here, so no capacity is reported.
                    if (profile == ProfileDvdPlusRw)
                    {
                        return new BurnMediaInfo { Status = BurnMediaStatus.Ready, Profile = profile, Erasable = info.Erasable };
                    }

                    return new BurnMediaInfo { Status = BurnMediaStatus.NotBlank, Erasable = info.Erasable, Profile = profile };
                }

                return new BurnMediaInfo { Status = BurnMediaStatus.Ready, CapacitySectors = info.CapacitySectors, Erasable = info.Erasable, Profile = profile };
            }
            catch (MediaNotPresentException)
            {
                return new BurnMediaInfo { Status = BurnMediaStatus.NoMedia, Profile = profile };
            }
            catch (DriveNotReadyException)
            {
                // NOT READY with a disc present - typically "operation in progress" (sense
                // 02/04/07) after an aborted burn left a half-written disc, or the drive is
                // still spinning up. Distinct from a dead drive; an eject/reinsert clears it.
                return new BurnMediaInfo { Status = BurnMediaStatus.Busy, Profile = profile };
            }
        }
        catch (MediaNotPresentException)
        {
            return new BurnMediaInfo { Status = BurnMediaStatus.NoMedia };
        }
        catch (DriveNotReadyException)
        {
            return new BurnMediaInfo { Status = BurnMediaStatus.Busy };
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Burn media pre-flight failed for {Drive}", drivePath);
            return new BurnMediaInfo { Status = BurnMediaStatus.DriveError };
        }
    }

    /// <summary>
    /// Entry point used by the GUI.  On Windows, spawns an elevated copy of
    /// OrgZ.exe via <see cref="CdElevation"/> (UAC per operation); on other
    /// platforms, falls through to <see cref="BurnAsync"/> in-process.
    /// Returns non-fatal warnings from the burn (e.g. CD-TEXT skipped).
    /// </summary>
    public static async Task<IReadOnlyList<string>> BurnWithElevationAsync(
        string drivePath,
        IReadOnlyList<CdBurnTrack> tracks,
        IProgress<CdBurnProgress>? progress = null,
        string? discTitle = null,
        string? discPerformer = null,
        bool testWrite = false,
        int? writeSpeedKBps = null,
        int gapSectors = 0,
        CancellationToken cancellationToken = default)
    {
        if (!CdElevation.RequiresElevation)
        {
            return await BurnAsync(drivePath, tracks, progress, discTitle, discPerformer, testWrite, writeSpeedKBps, gapSectors, cancellationToken);
        }

        var spec = new CdHelperSpec
        {
            Operation = "burn",
            DrivePath = drivePath,
            DiscTitle = discTitle,
            DiscPerformer = discPerformer,
            TestWrite = testWrite,
            WriteSpeedKBps = writeSpeedKBps,
            GapSectors = gapSectors,
            Tracks = tracks.Select((t, i) => new CdHelperTrack
            {
                TrackNumber = i + 1,
                WavFilePath = t.WavFilePath,
                Title = t.Title,
                Artist = t.Performer,
            }).ToList(),
        };

        string? error = null;
        var warnings = new List<string>();

        var exitCode = await CdElevation.RunElevatedAsync(spec, evt =>
        {
            switch (evt.Type)
            {
                case "burn-progress":
                {
                    progress?.Report(new CdBurnProgress
                    {
                        TrackNumber = evt.TrackNumber,
                        TrackCount = evt.TrackCount,
                        TrackSectors = evt.TrackSectors,
                        SectorsWritten = evt.SectorsWritten,
                        TotalDiscSectors = evt.TotalDiscSectors,
                        TotalSectorsWritten = evt.TotalSectorsWritten,
                    });
                    break;
                }
                case "warning":
                {
                    if (evt.Message is { } warning)
                    {
                        warnings.Add(warning);
                    }
                    break;
                }
                case "error":
                {
                    error = evt.Message;
                    break;
                }
            }
        }, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(error ?? $"Elevated burn helper exited with code {exitCode}.");
        }

        return warnings;
    }

    // MMC BLANK (0xA1) is issued here with IMMED=1 and completion polled via TEST UNIT
    // READY: FoxOrangebook alpha.4's BurnSession.Blank() sends IMMED=0, which parks the
    // drive inside a single SCSI command for the whole erase - longer than the transport's
    // 30 s command timeout. Fold into FoxOrangebook once Blank() grows the immediate+poll shape.
    private const byte OpBlank = 0xA1;

    /// <summary>
    /// Entry point used by the GUI. On Windows, spawns an elevated helper (the same
    /// UAC path as a burn - BLANK needs write rights on the drive handle) to run
    /// <see cref="EraseMediaAsync"/>; on other platforms, runs it in-process.
    /// </summary>
    public static async Task EraseWithElevationAsync(string drivePath, CancellationToken cancellationToken = default)
    {
        if (!CdElevation.RequiresElevation)
        {
            await EraseMediaAsync(drivePath, cancellationToken);
            return;
        }

        var spec = new CdHelperSpec
        {
            Operation = "erase",
            DrivePath = drivePath,
        };

        string? error = null;

        var exitCode = await CdElevation.RunElevatedAsync(spec, evt =>
        {
            if (evt.Type == "error")
            {
                error = evt.Message;
            }
        }, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(error ?? $"Elevated erase helper exited with code {exitCode}.");
        }
    }

    /// <summary>
    /// Quick-blanks (PMA/TOC/pregap only) the rewritable disc in <paramref name="drivePath"/>,
    /// then polls the drive until the erase finishes - typically 1-2 minutes on CD-RW.
    /// The disc reports <see cref="DiscStatus.Blank"/> afterwards and is ready to burn.
    /// </summary>
    public static async Task EraseMediaAsync(string drivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(drivePath);

        var opticalDrive = OpticalDrive.Open(drivePath);
        await using (opticalDrive)
        {
            if (opticalDrive is not IScsiTransport transport)
            {
                throw new InvalidOperationException($"Drive '{drivePath}' does not expose an IScsiTransport (required for erasing).");
            }

            _log.Information("Erasing disc in {Drive}: {Vendor} {Product} (fw {Rev})", drivePath, opticalDrive.Inquiry.Vendor, opticalDrive.Inquiry.Product, opticalDrive.Inquiry.Revision);

            var blankCdb = new byte[12];
            blankCdb[0] = OpBlank;
            blankCdb[1] = 0x10 | 0x01;   // IMMED=1, blanking type 001b = minimal (PMA/TOC/pregap)
            transport.Execute(blankCdb, Span<byte>.Empty, ScsiDirection.None);

            // The drive answers TEST UNIT READY with NOT READY sense until the blank
            // finishes. 10 minutes of headroom - a quick blank is 1-2 minutes, but slow
            // or worn media can drag.
            const int maxAttempts = 1200;
            var turCdb = new byte[6];   // all-zero CDB = TEST UNIT READY

            for (int attempt = 0; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    transport.Execute(turCdb, Span<byte>.Empty, ScsiDirection.None);
                    break;
                }
                catch (DriveNotReadyException)
                {
                    if (attempt >= maxAttempts)
                    {
                        throw new InvalidOperationException("The drive didn't finish erasing within 10 minutes.");
                    }

                    await Task.Delay(500, cancellationToken);
                }
            }

            _log.Information("Erase complete for {Drive}", drivePath);
        }
    }

    /// <summary>
    /// Entry point used by the GUI for data discs. On Windows, spawns the elevated
    /// helper (same UAC path as an audio burn); elsewhere runs in-process.
    /// </summary>
    public static async Task DataBurnWithElevationAsync(
        string drivePath,
        IReadOnlyList<DataBurnFile> files,
        string? volumeLabel,
        IProgress<CdBurnProgress>? progress = null,
        bool testWrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!CdElevation.RequiresElevation)
        {
            await DataBurnAsync(drivePath, files, volumeLabel, progress, testWrite, cancellationToken);
            return;
        }

        var spec = new CdHelperSpec
        {
            Operation = "burn-data",
            DrivePath = drivePath,
            DiscTitle = volumeLabel,
            TestWrite = testWrite,
            Tracks = files.Select((f, i) => new CdHelperTrack
            {
                TrackNumber = i + 1,
                SourcePath = f.SourcePath,
                DiscPath = f.DiscPath,
            }).ToList(),
        };

        string? error = null;

        var exitCode = await CdElevation.RunElevatedAsync(spec, evt =>
        {
            switch (evt.Type)
            {
                case "burn-progress":
                {
                    progress?.Report(new CdBurnProgress
                    {
                        TrackNumber = evt.TrackNumber,
                        TrackCount = evt.TrackCount,
                        TrackSectors = evt.TrackSectors,
                        SectorsWritten = evt.SectorsWritten,
                        TotalDiscSectors = evt.TotalDiscSectors,
                        TotalSectorsWritten = evt.TotalSectorsWritten,
                    });
                    break;
                }
                case "error":
                {
                    error = evt.Message;
                    break;
                }
            }
        }, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(error ?? $"Elevated data-burn helper exited with code {exitCode}.");
        }
    }

    /// <summary>
    /// Builds an ISO 9660/Joliet/UDF image over the given files (contents stream lazily
    /// at write time - no staging copy) and burns it: TAO Mode 1 on CD-R/CD-RW,
    /// in-place overwrite on DVD+RW.
    /// </summary>
    public static async Task DataBurnAsync(
        string drivePath,
        IReadOnlyList<DataBurnFile> files,
        string? volumeLabel,
        IProgress<CdBurnProgress>? progress = null,
        bool testWrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(drivePath);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            throw new ArgumentException("At least one file is required.", nameof(files));
        }

        var builder = new DiscImageBuilder(new DiscImageOptions
        {
            VolumeIdentifier = SanitizeVolumeLabel(volumeLabel),
            ApplicationIdentifier = "ORGZ",
        });

        foreach (var f in files)
        {
            if (!File.Exists(f.SourcePath))
            {
                throw new FileNotFoundException("Data burn source missing.", f.SourcePath);
            }

            builder.AddFile(f.DiscPath, f.SourcePath);
        }

        var image = builder.Build();

        var opticalDrive = OpticalDrive.Open(drivePath);
        await using (opticalDrive)
        {
            if (opticalDrive is not IScsiTransport transport)
            {
                throw new InvalidOperationException($"Drive '{drivePath}' does not expose an IScsiTransport (required for burning).");
            }

            _log.Information("Data burn: {Count} file(s), {Sectors} sectors ({Bytes:N0} bytes) to {Drive}: {Vendor} {Product} (fw {Rev}) testWrite={Test}", files.Count, image.SectorCount, image.ByteLength, drivePath, opticalDrive.Inquiry.Vendor, opticalDrive.Inquiry.Product, opticalDrive.Inquiry.Revision, testWrite);

            var session = new DataBurnSession(transport, new DataBurnOptions
            {
                TestWrite = testWrite,
                BufferUnderrunProtection = true,
            });

            IProgress<BurnProgress>? rawProgress = null;
            if (progress != null)
            {
                rawProgress = new Progress<BurnProgress>(p => progress.Report(new CdBurnProgress
                {
                    TrackNumber = 1,
                    TrackCount = 1,
                    TrackSectors = p.TrackSectors,
                    SectorsWritten = p.SectorsWritten,
                    TotalDiscSectors = p.TotalDiscSectors,
                    TotalSectorsWritten = p.TotalSectorsWritten,
                }));
            }

            await session.BurnAsync(image, rawProgress, cancellationToken);

            _log.Information("Data burn complete: {Count} file(s) to {Drive}", files.Count, drivePath);
        }
    }

    /// <summary>ISO volume identifiers cap at 16 Joliet characters; empty falls back to "ORGZ".</summary>
    private static string SanitizeVolumeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return "ORGZ";
        }

        var trimmed = label.Trim();
        return trimmed.Length <= 16 ? trimmed : trimmed[..16];
    }

    /// <summary>
    /// Burns a list of WAV files to a blank CD-R/CD-RW in disc-at-once mode.
    /// Returns non-fatal warnings from the burn (e.g. CD-TEXT skipped).
    /// </summary>
    public static async Task<IReadOnlyList<string>> BurnAsync(
        string drivePath,
        IReadOnlyList<CdBurnTrack> tracks,
        IProgress<CdBurnProgress>? progress = null,
        string? discTitle = null,
        string? discPerformer = null,
        bool testWrite = false,
        int? writeSpeedKBps = null,
        int gapSectors = 0,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivePath);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentOutOfRangeException.ThrowIfNegative(gapSectors);

        if (tracks.Count == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(tracks));
        }

        if (tracks.Count > MaxRedbookTracks)
        {
            throw new ArgumentException($"Audio CDs hold at most {MaxRedbookTracks} tracks; {tracks.Count} were supplied.", nameof(tracks));
        }

        // Validate all sources up front - a burn that starts and aborts halfway is
        // just a coaster.  Opening the file streams also locks them for the session.
        var openedStreams = new List<FileStream>(tracks.Count);
        var audioSources = new List<AudioTrackSource>(tracks.Count);

        try
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (!File.Exists(track.WavFilePath))
                {
                    throw new FileNotFoundException($"Track {i + 1} source missing.", track.WavFilePath);
                }

                var fs = new FileStream(track.WavFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                openedStreams.Add(fs);

                var (dataOffset, dataLength) = ParseCdAudioWav(fs, track.WavFilePath);

                if (dataLength % BytesPerSector != 0)
                {
                    throw new InvalidDataException($"Track {i + 1} ({track.WavFilePath}) PCM length {dataLength} is not a multiple of 2352 bytes (one CD sector).");
                }

                if (dataLength / BytesPerSector < MinRedbookTrackSectors)
                {
                    throw new InvalidDataException($"Track {i + 1} ({track.WavFilePath}) is {dataLength / BytesPerSector} sectors ({dataLength / BytesPerSector / 75.0:F1}s); Red Book requires at least 4 seconds ({MinRedbookTrackSectors} sectors) per track.");
                }

                // Track 1's pregap is the mandatory Red Book 2 seconds; later tracks
                // carry the user's inter-track gap (0 = gapless).
                audioSources.Add(new AudioTrackSource
                {
                    Pcm = new SubStream(fs, dataOffset, dataLength),
                    PregapSectors = i == 0 ? 150 : gapSectors,
                    Title = track.Title,
                    Performer = track.Performer,
                });
            }

            var opticalDrive = OpticalDrive.Open(drivePath);
            await using (opticalDrive)
            {
                if (opticalDrive is not IScsiTransport transport)
                {
                    throw new InvalidOperationException($"Drive '{drivePath}' does not expose an IScsiTransport (required for burning).");
                }

                _log.Information("Burning {Count} tracks to {Drive}: {Vendor} {Product} (fw {Rev}) testWrite={Test}", tracks.Count, drivePath, opticalDrive.Inquiry.Vendor, opticalDrive.Inquiry.Product, opticalDrive.Inquiry.Revision, testWrite);

                var options = new BurnOptions
                {
                    TestWrite = testWrite,
                    BufferUnderrunProtection = true,
                    DiscTitle = discTitle,
                    DiscPerformer = discPerformer,
                    WriteSpeedKBps = writeSpeedKBps,

                    // 26 × 2352 = 61,152 bytes per WRITE (10). The library default (32 =
                    // 75,264) exceeds the 64 KB SCSI pass-through transfer cap of common
                    // USB Mass Storage adapters - DeviceIoControl rejects it with Win32
                    // error 87 (seen on the Pioneer BDR-XS07U) before the drive ever
                    // sees the command.
                    SectorsPerWrite = 26,
                };

                var session = new BurnSession(transport, options);

                IProgress<BurnProgress>? rawProgress = null;
                if (progress != null)
                {
                    rawProgress = new Progress<BurnProgress>(p => progress.Report(new CdBurnProgress
                    {
                        TrackNumber = p.TrackNumber,
                        TrackCount = tracks.Count,
                        TrackSectors = p.TrackSectors,
                        SectorsWritten = p.SectorsWritten,
                        TotalDiscSectors = p.TotalDiscSectors,
                        TotalSectorsWritten = p.TotalSectorsWritten,
                    }));
                }

                await session.BurnAsync(audioSources, rawProgress, cancellationToken);

                foreach (var warning in session.Warnings)
                {
                    _log.Warning("Burn warning for {Drive}: {Warning}", drivePath, warning);
                }

                _log.Information("Burn complete: {Count} tracks to {Drive}", tracks.Count, drivePath);
                return session.Warnings.ToList();
            }
        }
        finally
        {
            foreach (var fs in openedStreams)
            {
                fs.Dispose();
            }
        }
    }

    /// <summary>
    /// Walks a RIFF/WAVE file, validates it contains 16-bit stereo 44.1 kHz PCM
    /// (CD-DA native format), and returns the byte range of the <c>data</c> chunk
    /// payload.  Accepts files with extra LIST/INFO chunks preceding or following
    /// the <c>data</c> chunk as long as the format chunk describes CD-DA.
    /// </summary>
    internal static (long DataOffset, long DataLength) ParseCdAudioWav(Stream stream, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;

        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12)
        {
            throw new InvalidDataException($"{sourceLabel}: file too short for RIFF header.");
        }

        if (!MatchesFourCc(header[..4], "RIFF") || !MatchesFourCc(header.Slice(8, 4), "WAVE"))
        {
            throw new InvalidDataException($"{sourceLabel}: not a RIFF/WAVE file.");
        }

        bool fmtSeen = false;
        long dataOffset = -1;
        long dataLength = -1;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        while (stream.Position < stream.Length)
        {
            if (stream.Read(chunkHeader) != 8)
            {
                break;
            }

            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.Slice(4, 4));
            long chunkStart = stream.Position;

            if (MatchesFourCc(chunkHeader[..4], "fmt "))
            {
                int got = stream.Read(fmt);
                if (got < 16)
                {
                    throw new InvalidDataException($"{sourceLabel}: truncated fmt chunk.");
                }

                ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(0, 2));
                ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(2, 2));
                uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.Slice(4, 4));
                ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(14, 2));

                if (formatTag != 1)
                {
                    throw new InvalidDataException($"{sourceLabel}: not uncompressed PCM (format tag 0x{formatTag:X4}).");
                }

                if (channels != RedbookChannels || sampleRate != RedbookSampleRate || bitsPerSample != RedbookBitsPerSample)
                {
                    throw new InvalidDataException($"{sourceLabel}: format is {channels}ch {sampleRate}Hz {bitsPerSample}bit; must be 2ch 44100Hz 16bit for CD-DA.");
                }

                fmtSeen = true;
                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
            }
            else if (MatchesFourCc(chunkHeader[..4], "data"))
            {
                if (!fmtSeen)
                {
                    throw new InvalidDataException($"{sourceLabel}: data chunk precedes fmt chunk.");
                }

                dataOffset = chunkStart;
                dataLength = chunkSize;
                break;
            }
            else
            {
                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
            }
        }

        if (dataOffset < 0)
        {
            throw new InvalidDataException($"{sourceLabel}: no data chunk found.");
        }

        return (dataOffset, dataLength);
    }

    private static bool MatchesFourCc(ReadOnlySpan<byte> bytes, string fourCc)
    {
        if (bytes.Length != 4 || fourCc.Length != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] != (byte)fourCc[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Read-only view over a contiguous byte range of an underlying seekable stream.
    /// Used to expose just the PCM payload of a WAV file to <see cref="AudioTrackSource"/>
    /// without re-copying the data.  Multiple instances may share one base stream,
    /// so reads are serialized via a lock while seeking the base stream to the
    /// right absolute offset.
    /// </summary>
    internal sealed class SubStream : Stream
    {
        private readonly Stream _base;
        private readonly long _offset;
        private readonly long _length;
        private long _position;

        public SubStream(Stream baseStream, long offset, long length)
        {
            ArgumentNullException.ThrowIfNull(baseStream);
            if (!baseStream.CanSeek || !baseStream.CanRead)
            {
                throw new ArgumentException("Base stream must be seekable and readable.", nameof(baseStream));
            }

            if (offset < 0 || length < 0 || offset + length > baseStream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            _base = baseStream;
            _offset = offset;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, remaining);
            lock (_base)
            {
                _base.Position = _offset + _position;
                int got = _base.Read(buffer[..toRead]);
                _position += got;
                return got;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, remaining);

            // The base stream is shared across AudioTrackSource instances only within
            // a single track (same FileStream per source in our wiring), so contention
            // is benign - but keep the lock for correctness if that assumption changes.
            int got;
            lock (_base)
            {
                _base.Position = _offset + _position;
                got = _base.Read(buffer[..toRead].Span);
                _position += got;
            }

            await Task.CompletedTask;
            return got;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            if (target < 0 || target > _length)
            {
                throw new IOException("Seek outside of substream bounds.");
            }

            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
