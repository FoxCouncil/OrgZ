// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace OrgZ.Models;

public enum DeviceType
{
    Unknown,
    RockboxIPod,       // iPod hardware running Rockbox firmware
    StockIPod,         // iPod running Apple firmware (requires iTunesDB parsing)
    RockboxOther,      // Sansa, iRiver, Cowon, Fiio, etc. running Rockbox
    GenericPlayer,     // Removable drive with audio files but no specific marker
}

public partial class ConnectedDevice : ObservableObject
{
    public required string MountPath { get; init; }

    public required DeviceType DeviceType { get; init; }

    /// <summary>Observable so a rename propagates to the info bar (DisplayName) and sidebar label
    /// without a reconnect.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(SidebarLabel))]
    private string _name = "Device";

    /// <summary>Whether the device's tier has Podcasts/Audiobooks sub-views (set from
    /// <c>IPodDevice.HasKindSubViews</c> on connect). A one-list device (Shuffle) hides both its
    /// sidebar children and the Podcasts segment of the capacity legend.</summary>
    [ObservableProperty]
    private bool _hasKindSubViews = true;

    /// <summary>The identity trail read from the on-device iTunesPrefs - "user — every computer that
    /// ever adopted this iPod" (e.g. "Fox — DEBBIE-PC, FOXDESK"). Null when the device carries none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HostHistoryDisplay))]
    private string? _hostHistory;

    public string HostHistoryDisplay => string.IsNullOrWhiteSpace(HostHistory) ? "—" : HostHistory!;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplay), nameof(ModelLabelDisplay), nameof(DisplayName))]
    private string? _model;

    /// <summary>
    /// Apple's official model number (e.g., "MA446LL/A", "MB147LL/A"). Sourced from
    /// SysInfo's ModelNumStr field, VPD XML, or Apple opcode 0xC6 - whichever path
    /// succeeds. Stored verbatim (with region suffix) so the /.orgz/device record
    /// persists the authoritative Apple-assigned identifier.
    /// </summary>
    [ObservableProperty]
    private string? _appleModelNumber;

    /// <summary>
    /// Hardware-level model string as reported by WMI's Win32_DiskDrive (e.g. "iFlash",
    /// "Apple iPod", or the actual SSD/HDD controller name on modded iPods). Exposed in
    /// the info bar when the user clicks the Model label to toggle between the decoded
    /// libgpod iPod identity and the underlying storage hardware.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelLabelDisplay), nameof(HasHardwareModel))]
    private string? _hardwareModel;

    /// <summary>
    /// When true the info bar shows <see cref="HardwareModel"/> instead of <see cref="Model"/>.
    /// Toggled by clicking the Model label.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelLabelDisplay))]
    private bool _showHardwareModel;

    public bool HasHardwareModel => !string.IsNullOrWhiteSpace(HardwareModel);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SerialDisplay))]
    private string? _serial;

    /// <summary>
    /// iPod FireWire GUID, 16-hex-char form (e.g. "000A2700153A9E6B"). Extracted from
    /// whichever source exposes it - SCSI INQUIRY VPD XML, Apple opcode 0xC6 blob, or
    /// the WMI USB descriptor on modded/bridged iPods where the iFlash adapter embeds
    /// the real iPod GUID inside its own synthetic USB iSerial string.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FireWireGuidDisplay), nameof(HasFireWireGuid))]
    private string? _fireWireGuid;

    public string FireWireGuidDisplay => string.IsNullOrWhiteSpace(FireWireGuid) ? "\u2014" : FireWireGuid;
    public bool HasFireWireGuid => !string.IsNullOrWhiteSpace(FireWireGuid);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirmwareVersionDisplay))]
    private string? _firmwareVersion;

    /// <summary>
    /// Apple iPod OS version (e.g. "iPod OS 1.3") read from SysInfo, SysInfoExtended,
    /// or SCSI INQUIRY VPD. Stored separately from FirmwareVersion so we can show both
    /// on iPods running Rockbox alongside their original Apple firmware.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FirmwareVersionDisplay), nameof(IsAppleFirmwareReadable))]
    private string? _appleFirmwareVersion;

    /// <summary>
    /// True when we both lack <see cref="AppleFirmwareVersion"/> AND know the
    /// device is on stock iPod OS (so reading the firmware partition would
    /// actually find a build ID we could decode). Drives the click affordance
    /// on the "Software Version" row in the info bar - Rockbox-booted iPods
    /// never light up because Apple's osos isn't reachable from there. Windows
    /// reads via the UAC-elevated helper, macOS via authopen (one auth dialog);
    /// Linux has no unprivileged raw-disk path yet.
    /// </summary>
    public bool IsAppleFirmwareReadable =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        && DeviceType == DeviceType.StockIPod
        && string.IsNullOrWhiteSpace(AppleFirmwareVersion);

    /// <summary>
    /// True when a privileged raw-disk read could still recover identity we don't have -
    /// the OS version OR (on platforms with no unprivileged serial source, i.e. macOS) the
    /// serial. Drives the automatic read-on-first-connect; broader than
    /// <see cref="IsAppleFirmwareReadable"/> (which gates the info-bar affordance on the
    /// version alone) so a device that already knows its version but not its serial still
    /// gets read without the user hunting for a click.
    /// </summary>
    public bool NeedsPrivilegedIdentity =>
        (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        && DeviceType == DeviceType.StockIPod
        && (string.IsNullOrWhiteSpace(AppleFirmwareVersion) || string.IsNullOrWhiteSpace(Serial));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatDisplay))]
    private string? _format;

    // RawSysInfo removed - detection diagnostics now go through Serilog and the
    // upcoming device-service helper will expose structured data via a Settings dialog
    // instead of dumping raw bytes into the main info bar.

    /// <summary>
    /// Raw libgpod generation string ("Classic 6G", "Nano 5G", "Video 5.5G", etc.) kept
    /// separate from the decorated <see cref="Model"/> display string so we can use it
    /// as a key for per-generation product imagery.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerationImage), nameof(HasGenerationImage))]
    private string? _ipodGeneration;

    /// <summary>
    /// True when <see cref="IpodGeneration"/> was only narrowed to a family from artwork
    /// correlation IDs (no serial / model number) - enough to pick the write tier, but NOT a
    /// confirmed identity. A spot-on model/colour/capacity requires the serial, so callers
    /// must not treat a provisional generation as an authoritative model.
    /// </summary>
    public bool IsGenerationProvisional { get; set; }

    /// <summary>
    /// Per-generation product image lazily loaded from Assets/Devices/ipod_{slug}.png,
    /// where the slug comes from normalizing <see cref="IpodGeneration"/> to lower_snake.
    /// Returns null when no image ships for this generation - the view falls back to
    /// the FontAwesome icon. Image load failures are silent so a bad asset file doesn't
    /// crash device detection.
    /// </summary>
    private byte[]? _generationImageBytes;
    private string? _generationImageSlug;

    /// <summary>
    /// The device's product colour ("Red", "Silver", "Black", ...) decoded from the model database, used
    /// to pick the colour-specific artwork in Assets/Devices. Blank/null for models with a single finish.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GenerationImage), nameof(HasGenerationImage))]
    private string? _color;

    private const string DeviceAssetBase = "avares://Orgz/Assets/Devices/";

    /// <summary>
    /// Returns a FRESH Bitmap each call - callers must NOT share instances across Image controls.
    /// Avalonia's ref-counted bitmap lifecycle disposes the underlying SKBitmap when a binding changes,
    /// and if two Image controls share the same Bitmap instance, the second crashes with
    /// ObjectDisposedException during its next layout measure. Raw PNG bytes are cached; a new Bitmap
    /// is cheap. Resolves the best asset for this device - see <see cref="ResolveImageSlug"/>.
    /// </summary>
    public Bitmap? GenerationImage
    {
        get
        {
            var slug = ResolveImageSlug();
            if (slug is null)
            {
                return null;
            }

            if (slug != _generationImageSlug || _generationImageBytes == null)
            {
                try
                {
                    using var assetStream = AssetLoader.Open(new Uri($"{DeviceAssetBase}{slug}.png"));
                    var ms = new MemoryStream();
                    assetStream.CopyTo(ms);
                    _generationImageBytes = ms.ToArray();
                    _generationImageSlug = slug;
                }
                catch
                {
                    return null;
                }
            }

            return new Bitmap(new MemoryStream(_generationImageBytes));
        }
    }

    public bool HasGenerationImage => _generationImageBytes != null || ResolveImageSlug() is not null;

    // Memoized ResolveImageSlug result, keyed on the inputs it derives from. Asset probing (Exists
    // checks + the colour-fallback directory enumeration) is I/O, and GenerationImage /
    // HasGenerationImage are bound properties re-evaluated on the UI thread - so resolve once per
    // (generation, colour), including the "no art ships" null result.
    private (string? Gen, string? Color, string? Slug) _resolvedSlug;
    private bool _slugResolved;

    internal string? ResolveImageSlug()
    {
        if (_slugResolved && _resolvedSlug.Gen == IpodGeneration && _resolvedSlug.Color == Color)
        {
            return _resolvedSlug.Slug;
        }
        var slug = ResolveImageSlugCore();
        _resolvedSlug = (IpodGeneration, Color, slug);
        _slugResolved = true;
        return slug;
    }

    /// <summary>
    /// Picks the artwork file base name (no extension, always the 1x - the 500px source is already sharp
    /// at the thumbnail size, so <c>@2x</c> variants are ignored) for this device, or null when none ships.
    /// Order: exact generation+colour (<c>ipod_nano_5g_red</c>) → generation-only (<c>ipod_4g</c>) → any
    /// colour of that generation, so a finish the art set doesn't cover still shows the right model.
    /// Files are embedded via the <c>&lt;AvaloniaResource Include="Assets\**" /&gt;</c> glob in OrgZ.csproj.
    /// Art from Wikimedia Commons - see Assets/Devices/ATTRIBUTIONS.md.
    /// </summary>
    private string? ResolveImageSlugCore()
    {
        if (string.IsNullOrWhiteSpace(IpodGeneration))
        {
            return null;
        }
        var gen = "ipod_" + IpodGeneration.ToLowerInvariant().Replace(' ', '_').Replace('.', '_');

        if (!string.IsNullOrWhiteSpace(Color))
        {
            var colour = $"{gen}_{Color.ToLowerInvariant().Replace(' ', '_')}";
            if (AssetExists(colour))
            {
                return colour;
            }
        }
        if (AssetExists(gen))
        {
            return gen;
        }
        return FirstColourFor(gen);
    }

    // ── Device art catalogue ──────────────────────────────────────────────
    // Every 1x art base name under Assets/Devices, enumerated ONCE (the set is fixed at build) and
    // shared by the exact-name probe and the colour-fallback scan. AssetLoader needs a running
    // Avalonia platform - headless contexts (unit tests, the XAML previewer) get an empty set
    // instead of a throw, and tests inject their own catalogue via <see cref="ArtCatalogOverride"/>
    // to exercise the resolution rules deterministically.
    private static readonly Lazy<IReadOnlySet<string>> _artCatalog = new(LoadArtCatalog);

    /// <summary>Test hook: replaces the asset catalogue art resolution reads. Set BEFORE the device
    /// resolves (results are memoized per instance); reset to null when done.</summary>
    internal static Func<IReadOnlySet<string>>? ArtCatalogOverride;

    private static IReadOnlySet<string> ArtCatalog => ArtCatalogOverride?.Invoke() ?? _artCatalog.Value;

    private static IReadOnlySet<string> LoadArtCatalog()
    {
        try
        {
            return AssetLoader.GetAssets(new Uri(DeviceAssetBase), null)
                .Select(u => Path.GetFileNameWithoutExtension(u.AbsolutePath))
                .Where(n => n.StartsWith("ipod_", StringComparison.OrdinalIgnoreCase) && !n.Contains("@2x", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>();   // no Avalonia platform (tests / previewer) → no art
        }
    }

    private static bool AssetExists(string baseName) => ArtCatalog.Contains(baseName);

    /// <summary>First (alphabetical) 1x colour variant for a generation - a sensible default when the
    /// device's exact colour isn't in the art set.</summary>
    private static string? FirstColourFor(string gen)
    {
        var prefix = gen + "_";
        return ArtCatalog
            .Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public string ModelDisplay => string.IsNullOrWhiteSpace(Model) ? "\u2014" : Model;

    /// <summary>
    /// What the info bar renders in the Model row. Normally shows the libgpod-decoded
    /// iPod identity, but when the user clicks to toggle, swaps in the raw hardware
    /// Model string from WMI (e.g. "iFlash" for an iFlash Solo/Quad CF adapter).
    /// </summary>
    public string ModelLabelDisplay => ShowHardwareModel && HasHardwareModel
        ? HardwareModel!
        : ModelDisplay;
    public string SerialDisplay => string.IsNullOrWhiteSpace(Serial) ? "\u2014" : Serial;

    /// <summary>
    /// Classifies the filesystem into the iTunes-style "Windows (FAT32)" or "Mac (HFS+)"
    /// naming. An iPod formatted via the Apple-supplied updater on macOS gets HFS+,
    /// while the Windows updater uses FAT32 - iTunes on Windows will only sync with a
    /// FAT32-formatted iPod, hence the labelling convention is well-known.
    /// </summary>
    public string FormatDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Format))
            {
                return "\u2014";
            }

            // On Linux, .NET reports FAT32 as "vfat" and FAT16 as "msdos" - rewrite them to
            // the canonical Apple/Windows names since the filesystem is identical and users
            // expect to see "FAT32", not "vfat".
            return Format.ToUpperInvariant() switch
            {
                "FAT" or "FAT32" or "EXFAT"    => $"Windows ({Format})",
                "VFAT"                         => "Windows (FAT32)",
                "MSDOS"                        => "Windows (FAT)",
                "NTFS"                         => $"Windows ({Format})",
                "HFS" or "HFS+" or "HFSPLUS"   => $"Mac ({Format})",
                "APFS"                         => $"Mac ({Format})",
                "EXT2" or "EXT3" or "EXT4"     => $"Linux ({Format})",
                "BTRFS" or "XFS" or "F2FS"     => $"Linux ({Format})",
                _                              => Format,
            };
        }
    }

    /// <summary>
    /// Combined firmware display. Shows "iPod OS X.Y / Rockbox Y.Z" on dual-firmware
    /// iPods, falls back to either alone when only one was detected, and shows an
    /// em-dash when neither source yielded anything.
    /// </summary>
    public string FirmwareVersionDisplay
    {
        get
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(AppleFirmwareVersion))
            {
                parts.Add(AppleFirmwareVersion);
            }
            if (!string.IsNullOrWhiteSpace(FirmwareVersion))
            {
                parts.Add(FirmwareVersion);
            }
            return parts.Count > 0 ? string.Join(" / ", parts) : "\u2014";
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(TotalSpaceLabel), nameof(FreeSpaceLabel), nameof(AudioSpaceLabel), nameof(PodcastSpaceLabel), nameof(OtherSpaceLabel), nameof(AudioPercent), nameof(PodcastPercent), nameof(OtherPercent), nameof(FreePercent))]
    private long _totalSpace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(FreeSpaceLabel), nameof(OtherSpaceLabel), nameof(OtherPercent), nameof(FreePercent))]
    private long _freeSpace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(AudioSpaceLabel), nameof(OtherSpaceLabel), nameof(AudioPercent), nameof(OtherPercent))]
    private long _audioSpace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(PodcastSpaceLabel), nameof(OtherSpaceLabel), nameof(PodcastPercent), nameof(OtherPercent))]
    private long _podcastSpace;

    public long OtherSpace => Math.Max(0, TotalSpace - FreeSpace - AudioSpace - PodcastSpace);

    public string TotalSpaceLabel => FormatBytes(TotalSpace);
    public string FreeSpaceLabel => FormatBytes(FreeSpace);
    public string AudioSpaceLabel => FormatBytes(AudioSpace);
    public string PodcastSpaceLabel => FormatBytes(PodcastSpace);
    public string OtherSpaceLabel => FormatBytes(OtherSpace);

    public double AudioPercent => TotalSpace > 0 ? (double)AudioSpace / TotalSpace * 100.0 : 0;
    public double PodcastPercent => TotalSpace > 0 ? (double)PodcastSpace / TotalSpace * 100.0 : 0;
    public double OtherPercent => TotalSpace > 0 ? (double)OtherSpace / TotalSpace * 100.0 : 0;
    public double FreePercent => TotalSpace > 0 ? (double)FreeSpace / TotalSpace * 100.0 : 0;

    public string SubLabel
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Model))
            {
                parts.Add(Model);
            }
            if (!string.IsNullOrWhiteSpace(FirmwareVersion))
            {
                parts.Add(FirmwareVersion);
            }
            if (!string.IsNullOrWhiteSpace(Serial))
            {
                parts.Add($"S/N: {Serial}");
            }
            return string.Join("  \u00B7  ", parts);
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return unit >= 3 ? $"{size:0.##} {units[unit]}" : $"{size:0} {units[unit]}";
    }

    /// <summary>
    /// Recomputes the capacity-bar buckets from a device track set - podcasts get their own
    /// segment, every other kind counts as audio. The ONE owner of that partition: call sites
    /// hand over the tracks and never spell the Kind rule themselves.
    /// </summary>
    public void SetSpaceFrom(IEnumerable<MediaItem> deviceTracks)
    {
        long audio = 0, podcast = 0;
        foreach (var t in deviceTracks)
        {
            if (t.Kind == MediaKind.Podcast)
            {
                podcast += t.FileSize ?? 0;
            }
            else
            {
                audio += t.FileSize ?? 0;
            }
        }
        AudioSpace = audio;
        PodcastSpace = podcast;
        RefreshSpace();
    }

    /// <summary>Incremental adjustment to the same partition for one item (delta may be negative).</summary>
    public void AdjustSpaceFor(MediaItem track, long deltaBytes)
    {
        if (track.Kind == MediaKind.Podcast)
        {
            PodcastSpace += deltaBytes;
        }
        else
        {
            AudioSpace += deltaBytes;
        }
        RefreshSpace();
    }

    /// <summary>
    /// Playlists discovered on the device during the scan. For stock iPods this comes
    /// from iTunesDB MHYP/MHIP chunks; for Rockbox from <c>Playlists/*.m3u</c> files.
    /// Populated on the UI thread at the end of the scan - view models subscribe to
    /// changes to rebuild the sidebar tree children.
    /// </summary>
    public System.Collections.ObjectModel.ObservableCollection<DevicePlaylist> Playlists { get; } = [];

    public string Icon => DeviceType switch
    {
        DeviceType.StockIPod => "fa-solid fa-music",
        DeviceType.RockboxIPod => "fa-solid fa-music",
        DeviceType.RockboxOther => "fa-solid fa-headphones",
        _ => "fa-solid fa-hard-drive",
    };

    /// <summary>
    /// Sidebar label. On Windows: "NAME (L:)". On Linux/macOS: just "NAME" - mount paths
    /// like "/media/fox/FOXPOD" aren't helpful next to the volume label the way a Windows
    /// drive letter is.
    /// </summary>
    public string SidebarLabel
    {
        get
        {
            if (OperatingSystem.IsWindows())
            {
                var letter = MountPath.TrimEnd('\\', '/');
                return string.IsNullOrEmpty(letter) ? Name : $"{Name} ({letter})";
            }

            return Name;
        }
    }

    /// <summary>
    /// Info-bar header: "Name (Model)" or just "Name". Kept for DeviceInfoBar header,
    /// but sidebar uses SidebarLabel instead.
    /// </summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Model) && Model != Name
        ? $"{Name} ({Model})"
        : Name;

    /// <summary>
    /// Refreshes live free/used space from the mounted drive.
    /// </summary>
    public void RefreshSpace()
    {
        try
        {
            var drive = new DriveInfo(MountPath);
            if (drive.IsReady)
            {
                TotalSpace = drive.TotalSize;
                FreeSpace = drive.TotalFreeSpace;
                Format = drive.DriveFormat;
            }
        }
        catch
        {
            // Drive disappeared between connect and refresh - handled by detection service
        }
    }
}
