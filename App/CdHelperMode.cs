// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using System.Text.Json.Serialization;
using OrgZ.Services;
using Serilog;

namespace OrgZ;

/// <summary>
/// Elevated helper entry point.  When <c>OrgZ.exe</c> is launched with
/// <c>--cd-helper</c>, <see cref="Program.Main"/> dispatches here instead of
/// bringing up the GUI.  A second elevated instance runs this code (triggered
/// by <see cref="CdElevation"/> via <c>ShellExecute</c> / <c>runas</c>), while
/// the foreground GUI process tails the progress file for UI updates.
/// </summary>
internal static class CdHelperMode
{
    internal const string ArgSwitch = "--cd-helper";
    private const string ArgSpec = "--spec";
    private const string ArgProgress = "--progress";

    /// <summary>
    /// True when <paramref name="args"/> contains <see cref="ArgSwitch"/>, telling
    /// <see cref="Program.Main"/> to skip Avalonia/Velopack/SingleInstance init.
    /// </summary>
    public static bool ShouldRun(string[] args)
    {
        return args.Any(a => string.Equals(a, ArgSwitch, StringComparison.Ordinal));
    }

    /// <summary>
    /// Executes a CD rip or burn described by the spec file, streaming progress to
    /// the progress file.  Returns a process exit code (0 = success, non-zero = failure).
    /// </summary>
    public static int Run(string[] args)
    {
        string? specPath = null;
        string? progressPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == ArgSpec && i + 1 < args.Length)
            {
                specPath = args[++i];
            }
            else if (args[i] == ArgProgress && i + 1 < args.Length)
            {
                progressPath = args[++i];
            }
        }

        if (specPath == null || progressPath == null)
        {
            Console.Error.WriteLine("cd-helper: --spec and --progress are required");
            return 2;
        }

        Logging.Initialize();
        var log = Logging.For("CdHelper");

        using var progressWriter = new ProgressWriter(progressPath);

        try
        {
            var specJson = File.ReadAllText(specPath);
            var spec = JsonSerializer.Deserialize(specJson, CdHelperJsonContext.Default.CdHelperSpec) ?? throw new InvalidDataException("Empty spec");

            log.Information("cd-helper: operation={Op} drive={Drive}", spec.Operation, spec.DrivePath);

            return spec.Operation switch
            {
                "rip" => RunRip(spec, progressWriter, log),
                "burn" => RunBurn(spec, progressWriter, log),
                "ipod-firmware-read" => RunIPodFirmwareRead(spec, progressWriter, log),
                _ => FailWith(progressWriter, $"Unknown operation '{spec.Operation}'"),
            };
        }
        catch (Exception ex)
        {
            log.Error(ex, "cd-helper fatal");
            progressWriter.WriteEvent(new CdHelperEvent { Type = "error", Message = ex.Message });
            return 1;
        }
        finally
        {
            Logging.Shutdown();
        }
    }

    private static int RunRip(CdHelperSpec spec, ProgressWriter progress, ILogger log)
    {
        if (spec.Tracks == null || spec.Tracks.Count == 0)
        {
            return FailWith(progress, "No tracks in spec");
        }

        var mediaItems = spec.Tracks.Select(t => new MediaItem
        {
            Id = $"cd:{spec.DrivePath}:{t.TrackNumber}",
            Kind = MediaKind.Music,
            Title = t.Title,
            Artist = t.Artist,
            Album = t.Album,
            Year = t.Year,
            Track = (uint)t.TrackNumber,
            Source = "cdda",
        }).ToList();

        var ripProgress = new Progress<RipTrackProgress>(p => progress.WriteEvent(new CdHelperEvent
        {
            Type = "rip-progress",
            TrackNumber = p.TrackNumber,
            TrackCount = p.TrackCount,
            TrackTitle = p.TrackTitle,
            SectorsDone = p.SectorsDone,
            SectorsTotal = p.SectorsTotal,
            RetryCount = p.RetryCount,
        }));

        var options = new CdRipOptions
        {
            Format = (RipFormat)spec.Format,
            FlacCompression = spec.FlacCompression,
            Mp3Mode = (Mp3Mode)spec.Mp3Mode,
            Mp3Quality = spec.Mp3Quality,
            ReReadAttempts = spec.ReReadAttempts,
        };

        static CdHelperOutcome ToHelper(RipOutcome o) => new()
        {
            TrackNumber = o.TrackNumber,
            TrackTitle = o.TrackTitle,
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

        var trackCompleted = new Progress<RipOutcome>(o => progress.WriteEvent(new CdHelperEvent
        {
            Type = "rip-track-done",
            Outcomes = [ToHelper(o)],
        }));

        var outcomes = CdRipService.RipTracksAsync(spec.DrivePath!, mediaItems, spec.OutputDirectory!, options, ripProgress, trackCompleted, spec.CoverArt, spec.DiscId).GetAwaiter().GetResult();

        progress.WriteEvent(new CdHelperEvent
        {
            Type = "rip-done",
            Outcomes = outcomes.Select(ToHelper).ToList(),
        });

        log.Information("cd-helper: rip done, {Count} outcome(s)", outcomes.Count);
        return 0;
    }

    private static int RunBurn(CdHelperSpec spec, ProgressWriter progress, ILogger log)
    {
        if (spec.Tracks == null || spec.Tracks.Count == 0)
        {
            return FailWith(progress, "No tracks in spec");
        }

        var burnTracks = spec.Tracks.Select(t => new CdBurnTrack
        {
            WavFilePath = t.WavFilePath ?? throw new InvalidDataException($"Track {t.TrackNumber}: wavFilePath required for burn"),
            Title = t.Title,
            Performer = t.Artist,
        }).ToList();

        var burnProgress = new Progress<CdBurnProgress>(p => progress.WriteEvent(new CdHelperEvent
        {
            Type = "burn-progress",
            TrackNumber = p.TrackNumber,
            TrackCount = p.TrackCount,
            TrackSectors = p.TrackSectors,
            SectorsWritten = p.SectorsWritten,
            TotalDiscSectors = p.TotalDiscSectors,
            TotalSectorsWritten = p.TotalSectorsWritten,
        }));

        CdBurnService.BurnAsync(
            spec.DrivePath!,
            burnTracks,
            burnProgress,
            spec.DiscTitle,
            spec.DiscPerformer,
            spec.TestWrite)
            .GetAwaiter()
            .GetResult();

        progress.WriteEvent(new CdHelperEvent { Type = "burn-done" });
        log.Information("cd-helper: burn done");
        return 0;
    }

    /// <summary>
    /// Elevated path for <see cref="IPodFirmwarePartition.TryReadOsosVersion"/>.
    /// Re-uses the same UAC-spawned process used by burn/rip — reads the iPod's
    /// firmware partition via <c>\\.\PhysicalDriveN</c> SCSI pass-through and
    /// streams the decoded OS version back as an <c>ipod-firmware-result</c>
    /// event. Returns non-zero exit when no version was decoded so the caller
    /// can distinguish "ran but found nothing" from "couldn't read at all".
    /// </summary>
    private static int RunIPodFirmwareRead(CdHelperSpec spec, ProgressWriter progress, ILogger log)
    {
        if (string.IsNullOrWhiteSpace(spec.DrivePath))
        {
            return FailWith(progress, "DrivePath required for ipod-firmware-read");
        }

        // Preferred source: the device-info plist over SCSI VPD — the same data iTunes reads,
        // carrying VisibleBuildID across the whole 4G→Nano 7G range, including the NOR-firmware
        // Nanos (4G/5G+) whose firmware image never lands on the disk. Only fall back to the
        // on-disk firmware-partition osos.vers for older HDD iPods that don't answer VPD.
        var diag = new System.Text.StringBuilder();
        string? version = null;

        var fields = IPodScsiInquiry.TryReadDeviceInfo(spec.DrivePath, out _, out var vpdDiag);
        diag.AppendLine(vpdDiag);
        if (fields != null)
        {
            version = IPodScsiInquiry.ExtractOsVersion(fields, spec.IpodGeneration, out var verDetail);
            diag.AppendLine(verDetail);
        }

        if (version == null)
        {
            diag.AppendLine("VPD yielded no version — falling back to firmware-partition osos.vers…");
            version = IPodFirmwarePartition.TryReadOsosVersion(spec.DrivePath, spec.IpodGeneration, out var ososDiag);
            diag.AppendLine(ososDiag);
        }

        progress.WriteEvent(new CdHelperEvent
        {
            Type = "ipod-firmware-result",
            OsosVersion = version,
            Message = diag.ToString(),
        });

        log.Information("cd-helper: iPod version read done — version={Version}", version ?? "(unknown)");
        return version != null ? 0 : 3;
    }

    private static int FailWith(ProgressWriter progress, string message)
    {
        progress.WriteEvent(new CdHelperEvent { Type = "error", Message = message });
        return 1;
    }

    private sealed class ProgressWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly object _lock = new();

        public ProgressWriter(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete))
            {
                AutoFlush = true,
            };
        }

        public void WriteEvent(CdHelperEvent evt)
        {
            var json = JsonSerializer.Serialize(evt, CdHelperJsonContext.Default.CdHelperEvent);
            lock (_lock)
            {
                _writer.WriteLine(json);
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}

// -- Shared DTOs between elevated helper and the GUI process -----------------

internal sealed class CdHelperSpec
{
    public string Operation { get; set; } = "";
    public string? DrivePath { get; set; }
    public string? OutputDirectory { get; set; }
    public int Format { get; set; }
    public int FlacCompression { get; set; } = 5;
    public int Mp3Mode { get; set; }
    public int Mp3Quality { get; set; } = 2;
    public int ReReadAttempts { get; set; } = 40;
    /// <summary>Front-cover image bytes, JSON-serialized as base64.</summary>
    public byte[]? CoverArt { get; set; }
    /// <summary>MusicBrainz DiscID stamped into ripped files as MUSICBRAINZ_DISCID.</summary>
    public string? DiscId { get; set; }
    public string? DiscTitle { get; set; }
    public string? DiscPerformer { get; set; }
    public bool TestWrite { get; set; }
    public List<CdHelperTrack>? Tracks { get; set; }

    /// <summary>
    /// iPod generation key for <c>ipod-firmware-read</c> operations. Used by
    /// <see cref="IPodFirmwarePartition.TryReadOsosVersion"/> to translate the
    /// raw build ID into a human version string via the per-generation lookup
    /// table. Ignored for rip/burn.
    /// </summary>
    public string? IpodGeneration { get; set; }
}

internal sealed class CdHelperTrack
{
    public int TrackNumber { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public uint? Year { get; set; }
    public string? WavFilePath { get; set; }
}

internal sealed class CdHelperEvent
{
    public string Type { get; set; } = "";
    public string? Message { get; set; }

    public int TrackNumber { get; set; }
    public int TrackCount { get; set; }
    public string? TrackTitle { get; set; }
    public long SectorsDone { get; set; }
    public long SectorsTotal { get; set; }
    public int RetryCount { get; set; }

    public int TrackSectors { get; set; }
    public int SectorsWritten { get; set; }
    public long TotalDiscSectors { get; set; }
    public long TotalSectorsWritten { get; set; }

    public List<CdHelperOutcome>? Outcomes { get; set; }

    /// <summary>Decoded iPod OS version string from <c>ipod-firmware-read</c>.</summary>
    public string? OsosVersion { get; set; }
}

internal sealed class CdHelperOutcome
{
    public int TrackNumber { get; set; }
    public string? TrackTitle { get; set; }
    public string OutputPath { get; set; } = "";
    public long SectorsRipped { get; set; }
    public uint AccurateRipV1 { get; set; }
    public uint AccurateRipV2 { get; set; }
    public bool HadErrors { get; set; }
    public int SkippedSectors { get; set; }
    public int ReadErrorSectors { get; set; }
    public int JitterCorrectedSectors { get; set; }
    public long FirstSkippedLba { get; set; } = -1;
}

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(CdHelperSpec))]
[JsonSerializable(typeof(CdHelperEvent))]
internal partial class CdHelperJsonContext : JsonSerializerContext { }
