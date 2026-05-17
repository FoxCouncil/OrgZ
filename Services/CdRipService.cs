// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using FoxRedbook;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Progress snapshot reported during a rip, aggregating per-track progress into a
/// disc-level view suitable for UI display.  Immutable value type so it can be
/// posted across threads via <see cref="IProgress{T}"/> without allocation concerns.
/// </summary>
public readonly record struct RipTrackProgress
{
    public required int TrackNumber { get; init; }
    public required int TrackCount { get; init; }
    public required string TrackTitle { get; init; }
    public required long SectorsDone { get; init; }
    public required long SectorsTotal { get; init; }
    public required int RetryCount { get; init; }
    public double TrackPercent => SectorsTotal == 0 ? 0 : (double)SectorsDone / SectorsTotal;
}

public sealed record RipOutcome
{
    public required int TrackNumber { get; init; }
    public required string TrackTitle { get; init; }
    public required string OutputPath { get; init; }
    public required long SectorsRipped { get; init; }
    public required uint AccurateRipV1 { get; init; }
    public required uint AccurateRipV2 { get; init; }
    public required bool HadErrors { get; init; }

    /// <summary>
    /// Sectors that exhausted the re-read budget and were written with
    /// best-effort (unverified) data. These are the most likely source of
    /// audible clicks / glitches in the output.
    /// </summary>
    public required int SkippedSectors { get; init; }

    /// <summary>Sectors where the drive returned a SCSI sense error.</summary>
    public required int ReadErrorSectors { get; init; }

    /// <summary>
    /// Sectors that needed jitter-overlap correction. These are still
    /// verified - informational only, useful for diagnosing flaky drives.
    /// </summary>
    public required int JitterCorrectedSectors { get; init; }

    /// <summary>LBA of the first <see cref="SkippedSectors"/>, or -1 if none.</summary>
    public required long FirstSkippedLba { get; init; }

    /// <summary>True when the track came out clean - no skipped / read-error sectors.</summary>
    public bool Verified => !HadErrors && SkippedSectors == 0 && ReadErrorSectors == 0;
}

/// <summary>
/// Verified CD-DA extraction to WAV files, using FoxRedbook's AccurateRip pipeline.
/// One session per disc; tracks are ripped sequentially so verification state
/// (drive offset, jitter correction) persists across track boundaries.
/// </summary>
public static class CdRipService
{
    private const int BytesPerSector = 2352;
    private const int SampleRate = 44100;
    private const int ChannelCount = 2;
    private const int BitsPerSample = 16;
    private const int BytesPerSecond = SampleRate * ChannelCount * (BitsPerSample / 8);
    private const int BlockAlign = ChannelCount * (BitsPerSample / 8);

    private static readonly ILogger _log = Logging.For("CdRip");

    /// <summary>
    /// Entry point used by the GUI.  On Windows, spawns an elevated copy of
    /// OrgZ.exe via <see cref="CdElevation"/> (UAC per operation) because
    /// <c>IOCTL_SCSI_PASS_THROUGH</c> requires admin; on other platforms,
    /// falls through to <see cref="RipTracksAsync"/> in-process.
    /// </summary>
    public static async Task<List<RipOutcome>> RipTracksWithElevationAsync(
        string drivePath,
        IReadOnlyList<MediaItem> tracks,
        string outputDirectory,
        CdRipOptions options,
        IProgress<RipTrackProgress>? progress = null,
        IProgress<RipOutcome>? trackCompleted = null,
        byte[]? coverArt = null,
        CancellationToken cancellationToken = default)
    {
        if (!CdElevation.RequiresElevation)
        {
            return await RipTracksAsync(drivePath, tracks, outputDirectory, options, progress, trackCompleted, coverArt, cancellationToken);
        }

        var spec = new CdHelperSpec
        {
            Operation = "rip",
            DrivePath = drivePath,
            OutputDirectory = outputDirectory,
            Format = (int)options.Format,
            FlacCompression = options.FlacCompression,
            Mp3Mode = (int)options.Mp3Mode,
            Mp3Quality = options.Mp3Quality,
            ReReadAttempts = options.ReReadAttempts,
            CoverArt = coverArt,
            Tracks = tracks.Where(t => t.Track.HasValue).Select(t => new CdHelperTrack
            {
                TrackNumber = (int)t.Track!.Value,
                Title = t.Title,
                Artist = t.Artist,
                Album = t.Album,
                Year = t.Year,
            }).ToList(),
        };

        List<RipOutcome>? outcomes = null;
        string? error = null;

        var exitCode = await CdElevation.RunElevatedAsync(spec, evt =>
        {
            switch (evt.Type)
            {
                case "rip-progress":
                {
                    progress?.Report(new RipTrackProgress
                    {
                        TrackNumber = evt.TrackNumber,
                        TrackCount = evt.TrackCount,
                        TrackTitle = evt.TrackTitle ?? "",
                        SectorsDone = evt.SectorsDone,
                        SectorsTotal = evt.SectorsTotal,
                        RetryCount = evt.RetryCount,
                    });
                    break;
                }
                case "rip-track-done":
                {
                    if (evt.Outcomes is { Count: > 0 } single)
                    {
                        trackCompleted?.Report(FromHelper(single[0]));
                    }
                    break;
                }
                case "rip-done":
                {
                    outcomes = evt.Outcomes?.Select(FromHelper).ToList() ?? [];
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
            throw new InvalidOperationException(error ?? $"Elevated rip helper exited with code {exitCode}.");
        }

        return outcomes ?? throw new InvalidOperationException("Elevated rip helper finished without reporting outcomes.");
    }

    /// <summary>
    /// Rips the specified tracks from <paramref name="drivePath"/> into
    /// <paramref name="outputDirectory"/>.  Files are named
    /// <c>{track:D2} - {sanitized title}{ext}</c>, where <c>ext</c> matches
    /// <paramref name="format"/>.  FLAC and MP3 encoding runs through the
    /// external <c>flac</c> / <c>lame</c> binaries; WAV is written directly.
    /// </summary>
    /// <summary>
    /// Maps a helper-DTO outcome back to the in-process <see cref="RipOutcome"/>.
    /// Defined as a local helper because both <c>rip-track-done</c> and
    /// <c>rip-done</c> events carry the same payload shape.
    /// </summary>
    private static RipOutcome FromHelper(CdHelperOutcome o) => new()
    {
        TrackNumber = o.TrackNumber,
        TrackTitle = o.TrackTitle ?? string.Empty,
        OutputPath = o.OutputPath,
        SectorsRipped = o.SectorsRipped,
        AccurateRipV1 = o.AccurateRipV1,
        AccurateRipV2 = o.AccurateRipV2,
        HadErrors = o.HadErrors,
        SkippedSectors = o.SkippedSectors,
        ReadErrorSectors = o.ReadErrorSectors,
        JitterCorrectedSectors = o.JitterCorrectedSectors,
        FirstSkippedLba = o.FirstSkippedLba,
    };

    public static async Task<List<RipOutcome>> RipTracksAsync(
        string drivePath,
        IReadOnlyList<MediaItem> tracks,
        string outputDirectory,
        CdRipOptions options,
        IProgress<RipTrackProgress>? progress = null,
        IProgress<RipOutcome>? trackCompleted = null,
        byte[]? coverArt = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivePath);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        if (tracks.Count == 0)
        {
            return [];
        }

        Directory.CreateDirectory(outputDirectory);

        var outcomes = new List<RipOutcome>(tracks.Count);

        await using var drive = OpticalDrive.Open(drivePath);
        _log.Information("Opened {DrivePath} for rip: {Vendor} {Product} (fw {Rev}) format={Format}", drivePath, drive.Inquiry.Vendor, drive.Inquiry.Product, drive.Inquiry.Revision, options.ShortLabel);

        var toc = await drive.ReadTocAsync(cancellationToken);
        var redbookOptions = new FoxRedbook.RipOptions { MaxReReads = options.ReReadAttempts };
        using var session = RipSession.CreateAutoCorrected(drive, redbookOptions);
        _log.Information("Rip options: ReReadAttempts={ReReadAttempts}", options.ReReadAttempts);

        for (int i = 0; i < tracks.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var requested = tracks[i];
            if (!requested.Track.HasValue)
            {
                _log.Warning("Skipping track with no number: {Id}", requested.Id);
                continue;
            }

            var trackNumber = (int)requested.Track.Value;
            var tocTrack = toc.Tracks.FirstOrDefault(t => t.Number == trackNumber && t.Type == FoxRedbook.TrackType.Audio);
            if (tocTrack.SectorCount == 0)
            {
                _log.Warning("Track {TrackNumber} not found on disc or not audio; skipping", trackNumber);
                continue;
            }

            var fileName = BuildFileName(trackNumber, requested.Title, options.Format);
            var outputPath = Path.Combine(outputDirectory, fileName);
            var title = requested.Title ?? $"Track {trackNumber}";

            _log.Information("Ripping track {Track} ({Sectors} sectors) to {Path}", trackNumber, tocTrack.SectorCount, outputPath);

            var pcmBytes = (long)tocTrack.SectorCount * BytesPerSector;
            var metadata = new RipTrackMetadata
            {
                Title = requested.Title,
                Artist = requested.Artist,
                Album = requested.Album,
                Genre = requested.Genre,
                TrackNumber = trackNumber,
                Year = requested.Year,
                EncodedBy = $"OrgZ {App.Version}",
                CoverArt = coverArt,
            };

            long sectorsDone = 0;
            bool hadErrors = false;
            int latestRetries = 0;
            int skippedSectors = 0;
            int readErrorSectors = 0;
            int jitterCorrectedSectors = 0;
            long firstSkippedLba = -1;

            var ripProgress = new Progress<RipProgress>(p =>
            {
                latestRetries = p.RetryCount;
                if ((p.Status & SectorStatus.Skipped) != 0)
                {
                    skippedSectors++;
                    if (firstSkippedLba < 0)
                    {
                        firstSkippedLba = p.Lba;
                    }
                    hadErrors = true;
                }
                if ((p.Status & SectorStatus.ReadError) != 0)
                {
                    readErrorSectors++;
                    hadErrors = true;
                }
                if ((p.Status & SectorStatus.JitterCorrected) != 0)
                {
                    jitterCorrectedSectors++;
                }
            });

            var encoder = RipEncoder.Open(outputPath, pcmBytes, metadata, options);
            await using (encoder)
            {
                await foreach (var sector in session.RipTrackAsync(tocTrack, ripProgress, cancellationToken))
                {
                    await encoder.WriteAsync(sector.Pcm, cancellationToken);
                    sectorsDone++;

                    if (sector.HadErrors)
                    {
                        hadErrors = true;
                    }

                    if ((sectorsDone & 0x1F) == 0 || sectorsDone == tocTrack.SectorCount)
                    {
                        progress?.Report(new RipTrackProgress
                        {
                            TrackNumber = trackNumber,
                            TrackCount = tracks.Count,
                            TrackTitle = title,
                            SectorsDone = sectorsDone,
                            SectorsTotal = tocTrack.SectorCount,
                            RetryCount = latestRetries,
                        });
                    }
                }
            }

            var outcome = new RipOutcome
            {
                TrackNumber = trackNumber,
                TrackTitle = title,
                OutputPath = outputPath,
                SectorsRipped = sectorsDone,
                AccurateRipV1 = session.GetAccurateRipV1Crc(tocTrack),
                AccurateRipV2 = session.GetAccurateRipV2Crc(tocTrack),
                HadErrors = hadErrors,
                SkippedSectors = skippedSectors,
                ReadErrorSectors = readErrorSectors,
                JitterCorrectedSectors = jitterCorrectedSectors,
                FirstSkippedLba = firstSkippedLba,
            };
            outcomes.Add(outcome);
            if (outcome.Verified)
            {
                _log.Information("Track {Track} verified: AR1={AR1:X8} AR2={AR2:X8} jitter-corrected={Jitter}", trackNumber, outcome.AccurateRipV1, outcome.AccurateRipV2, jitterCorrectedSectors);
            }
            else
            {
                _log.Warning("Track {Track} HAS UNVERIFIED SECTORS: skipped={Skipped} read-errors={ReadErrors} first-bad-LBA={FirstBad} AR1={AR1:X8} AR2={AR2:X8}", trackNumber, skippedSectors, readErrorSectors, firstSkippedLba, outcome.AccurateRipV1, outcome.AccurateRipV2);
            }
            trackCompleted?.Report(outcome);
        }

        return outcomes;
    }

    /// <summary>
    /// Writes a canonical 44-byte RIFF/WAVE header for 16-bit stereo 44.1kHz PCM.
    /// <paramref name="pcmByteCount"/> is the size of the audio payload that
    /// follows the header (NOT including the header itself).
    /// </summary>
    internal static void WriteWavHeader(Stream destination, long pcmByteCount)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (pcmByteCount < 0 || pcmByteCount > uint.MaxValue - 36)
        {
            throw new ArgumentOutOfRangeException(nameof(pcmByteCount), "PCM payload does not fit in a 32-bit WAV file.");
        }

        Span<byte> header = stackalloc byte[44];

        // RIFF chunk
        header[0] = (byte)'R'; header[1] = (byte)'I'; header[2] = (byte)'F'; header[3] = (byte)'F';
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, 4), (uint)(pcmByteCount + 36));
        header[8] = (byte)'W'; header[9] = (byte)'A'; header[10] = (byte)'V'; header[11] = (byte)'E';

        // fmt sub-chunk
        header[12] = (byte)'f'; header[13] = (byte)'m'; header[14] = (byte)'t'; header[15] = (byte)' ';
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(20, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(22, 2), ChannelCount);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(24, 4), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(28, 4), BytesPerSecond);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(32, 2), BlockAlign);
        BinaryPrimitives.WriteUInt16LittleEndian(header.Slice(34, 2), BitsPerSample);

        // data sub-chunk
        header[36] = (byte)'d'; header[37] = (byte)'a'; header[38] = (byte)'t'; header[39] = (byte)'a';
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(40, 4), (uint)pcmByteCount);

        destination.Write(header);
    }

    /// <summary>
    /// Builds a filesystem-safe filename for a ripped track:
    /// <c>"{N:D2} - {title}{ext}"</c> where <c>ext</c> comes from
    /// <see cref="RipEncoder.ExtensionFor"/>.  Invalid path characters are
    /// stripped and the base is capped at 120 chars.
    /// </summary>
    internal static string BuildFileName(int trackNumber, string? title, RipFormat format = RipFormat.Wav)
    {
        var cleanTitle = SanitizeForFileName(title);
        if (string.IsNullOrEmpty(cleanTitle))
        {
            cleanTitle = $"Track {trackNumber:D2}";
        }

        var baseName = $"{trackNumber:D2} - {cleanTitle}";
        if (baseName.Length > 120)
        {
            baseName = baseName[..120];
        }

        return baseName + RipEncoder.ExtensionFor(format);
    }

    internal static string SanitizeForFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalid = Path.GetInvalidFileNameChars();
        var result = new char[value.Length];
        int len = 0;
        foreach (var ch in value)
        {
            if (ch < 0x20 || Array.IndexOf(invalid, ch) >= 0)
            {
                continue;
            }

            result[len++] = ch;
        }

        return new string(result, 0, len).Trim().TrimEnd('.');
    }
}
