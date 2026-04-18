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

    public string Name { get; set; } = "Device";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModelDisplay), nameof(ModelLabelDisplay), nameof(DisplayName))]
    private string? _model;

    /// <summary>
    /// Apple's official model number (e.g., "MA446LL/A", "MB147LL/A"). Sourced from
    /// SysInfo's ModelNumStr field, VPD XML, or Apple opcode 0xC6 — whichever path
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
    /// whichever source exposes it — SCSI INQUIRY VPD XML, Apple opcode 0xC6 blob, or
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
    [NotifyPropertyChangedFor(nameof(FirmwareVersionDisplay))]
    private string? _appleFirmwareVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FormatDisplay))]
    private string? _format;

    // RawSysInfo removed — detection diagnostics now go through Serilog and the
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
    /// Per-generation product image lazily loaded from Assets/Devices/ipod_{slug}.png,
    /// where the slug comes from normalizing <see cref="IpodGeneration"/> to lower_snake.
    /// Returns null when no image ships for this generation — the view falls back to
    /// the FontAwesome icon. Image load failures are silent so a bad asset file doesn't
    /// crash device detection.
    /// </summary>
    private byte[]? _generationImageBytes;
    private string? _generationImageSlug;

    /// <summary>
    /// Returns a FRESH Bitmap each call — callers must NOT share instances across
    /// Image controls. Avalonia's ref-counted bitmap lifecycle disposes the underlying
    /// SKBitmap when a binding changes, and if two Image controls share the same
    /// Bitmap instance, the second one crashes with ObjectDisposedException during
    /// its next layout measure. Raw PNG bytes are cached, new Bitmap is cheap (~20 KB).
    /// </summary>
    public Bitmap? GenerationImage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(IpodGeneration))
            {
                return null;
            }

            var slug = IpodGeneration
                .ToLowerInvariant()
                .Replace(' ', '_')
                .Replace('.', '_');

            if (!KnownGenerationImages.Contains(slug))
            {
                return null;
            }

            if (slug != _generationImageSlug || _generationImageBytes == null)
            {
                try
                {
                    var uri = new Uri($"avares://Orgz/Assets/Devices/ipod_{slug}.png");
                    using var assetStream = AssetLoader.Open(uri);
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

    public bool HasGenerationImage => _generationImageBytes != null || (!string.IsNullOrWhiteSpace(IpodGeneration) && KnownGenerationImages.Contains(IpodGeneration.ToLowerInvariant().Replace(' ', '_').Replace('.', '_')));

    /// <summary>
    /// Set of generation slugs we have product imagery for. Files live in
    /// Assets/Devices/ipod_{slug}.png and get embedded at build time via the
    /// existing &lt;AvaloniaResource Include="Assets\**" /&gt; glob in OrgZ.csproj.
    /// When you drop a new image in, add the slug here.
    ///
    /// Images sourced from Wikimedia Commons under CC BY-SA 3.0 / GFDL.
    /// See Assets/Devices/ATTRIBUTIONS.md for credit details.
    /// </summary>
    private static readonly HashSet<string> KnownGenerationImages = new(StringComparer.OrdinalIgnoreCase)
    {
        "4g",             // iPod 4G click wheel (mono)
        "photo",          // iPod Photo (color screen, same form factor as 4G)
        "mini_1g",
        "mini_2g",
        "video_5g",       // iPod 5G Video
        "video_5_5g",     // iPod 5.5G Video (enhanced)
        "classic_6g",
        "classic_6_5g",
        "classic_7g",
        "nano_1g",
        "nano_2g",
        "nano_7g",
        "shuffle_4g",
    };

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
    /// while the Windows updater uses FAT32 — iTunes on Windows will only sync with a
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

            return Format.ToUpperInvariant() switch
            {
                "FAT" or "FAT32" or "EXFAT"   => $"Windows ({Format})",
                "NTFS"                         => $"Windows ({Format})",
                "HFS" or "HFS+" or "HFSPLUS"   => $"Mac ({Format})",
                "APFS"                          => $"Mac ({Format})",
                "EXT2" or "EXT3" or "EXT4"      => $"Linux ({Format})",
                "BTRFS" or "XFS" or "F2FS"      => $"Linux ({Format})",
                _                               => Format,
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
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(TotalSpaceLabel), nameof(FreeSpaceLabel), nameof(AudioSpaceLabel), nameof(OtherSpaceLabel), nameof(AudioPercent), nameof(OtherPercent), nameof(FreePercent))]
    private long _totalSpace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(FreeSpaceLabel), nameof(OtherSpaceLabel), nameof(OtherPercent), nameof(FreePercent))]
    private long _freeSpace;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OtherSpace), nameof(TotalSpaceLabel), nameof(FreeSpaceLabel), nameof(AudioSpaceLabel), nameof(OtherSpaceLabel), nameof(AudioPercent), nameof(OtherPercent), nameof(FreePercent))]
    private long _audioSpace;

    public long OtherSpace => Math.Max(0, TotalSpace - FreeSpace - AudioSpace);

    public string TotalSpaceLabel => FormatBytes(TotalSpace);
    public string FreeSpaceLabel => FormatBytes(FreeSpace);
    public string AudioSpaceLabel => FormatBytes(AudioSpace);
    public string OtherSpaceLabel => FormatBytes(OtherSpace);

    public double AudioPercent => TotalSpace > 0 ? (double)AudioSpace / TotalSpace * 100.0 : 0;
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

    public bool IsReadOnly => DeviceType == DeviceType.StockIPod;

    public string Icon => DeviceType switch
    {
        DeviceType.StockIPod => "fa-solid fa-music",
        DeviceType.RockboxIPod => "fa-solid fa-music",
        DeviceType.RockboxOther => "fa-solid fa-headphones",
        _ => "fa-solid fa-hard-drive",
    };

    /// <summary>
    /// Sidebar label. On Windows: "NAME (L:)". On macOS: "NAME". On Linux: "NAME (sdb1)"
    /// using the block device node stripped of "/dev/".
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

            if (OperatingSystem.IsLinux())
            {
                var devNode = MountPath;
                if (devNode.StartsWith("/dev/"))
                {
                    devNode = devNode[5..];
                }
                return string.IsNullOrEmpty(devNode) ? Name : $"{Name} ({devNode})";
            }

            // macOS and anything else: just the volume name
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
            // Drive disappeared between connect and refresh — handled by detection service
        }
    }
}
