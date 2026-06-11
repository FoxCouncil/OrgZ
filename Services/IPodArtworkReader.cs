// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Reads album art for stock-firmware iPod tracks from the device's own ArtworkDB +
/// .ithmb thumbnail files — the native iPod art store, linked to each track by its
/// iTunesDB dbid. Returns PNG bytes so callers share the same display path as
/// embedded-art tracks. The per-mount dbid→image index is cached; call
/// <see cref="Invalidate"/> after writing to the ArtworkDB (e.g. an import).
/// </summary>
public static class IPodArtworkReader
{
    private static readonly ILogger _log = Logging.For("IPodArtwork");
    private static readonly object _gate = new();
    private static readonly Dictionary<string, Dictionary<ulong, ArtImage>> _index = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forget the cached index for a mount whose ArtworkDB has changed.</summary>
    public static void Invalidate(string mountPath)
    {
        lock (_gate)
        {
            _index.Remove(mountPath);
        }
    }

    /// <summary>
    /// PNG-encoded thumbnail for the track with this dbid, or null when the device has no
    /// ArtworkDB, no entry for the track, or the .ithmb can't be read.
    /// </summary>
    public static byte[]? LoadThumbnail(string mountPath, ulong dbid)
    {
        if (dbid == 0)
        {
            return null;
        }

        var index = GetIndex(mountPath);
        if (!index.TryGetValue(dbid, out var image))
        {
            return null;
        }

        // Prefer the largest thumbnail (the now-playing-sized one over the list icon).
        var thumb = image.Thumbs.OrderByDescending(t => t.Width * t.Height).FirstOrDefault();
        if (thumb is null || thumb.Width <= 0 || thumb.Height <= 0)
        {
            return null;
        }

        var ithmb = Path.Combine(mountPath, "iPod_Control", "Artwork", $"F{thumb.FormatId}_1.ithmb");
        if (!File.Exists(ithmb))
        {
            return null;
        }

        try
        {
            int pixelBytes = thumb.Width * thumb.Height * 2;   // RGB565 = 2 bytes/pixel
            var raw = new byte[pixelBytes];
            using (var fs = File.OpenRead(ithmb))
            {
                fs.Seek(thumb.IthmbOffset, SeekOrigin.Begin);
                fs.ReadExactly(raw, 0, pixelBytes);
            }
            return EncodeRgb565(raw, thumb.Width, thumb.Height);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Failed to read iPod artwork thumbnail (dbid {Dbid}) from {Ithmb}", dbid, ithmb);
            return null;
        }
    }

    private static Dictionary<ulong, ArtImage> GetIndex(string mountPath)
    {
        lock (_gate)
        {
            if (_index.TryGetValue(mountPath, out var cached))
            {
                return cached;
            }

            var dbPath = Path.Combine(mountPath, "iPod_Control", "Artwork", "ArtworkDB");
            var map = new Dictionary<ulong, ArtImage>();
            if (File.Exists(dbPath))
            {
                try
                {
                    var doc = ITunesDbChunkTree.Parse(File.ReadAllBytes(dbPath));
                    foreach (var img in ArtworkDbWriter.ReadImages(doc))
                    {
                        map[img.Dbid] = img;   // one image per track; last wins
                    }
                }
                catch (Exception ex)
                {
                    _log.Warning(ex, "Failed to parse ArtworkDB at {Path}", dbPath);
                }
            }

            _index[mountPath] = map;
            return map;
        }
    }

    /// <summary>Converts an RGB565-LE pixel block to PNG bytes via a WriteableBitmap.</summary>
    private static byte[] EncodeRgb565(byte[] rgb565, int width, int height)
    {
        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormats.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bitmap.Lock())
        {
            unsafe
            {
                byte* dst = (byte*)fb.Address;
                int stride = fb.RowBytes;
                for (int y = 0; y < height; y++)
                {
                    byte* row = dst + (y * stride);
                    for (int x = 0; x < width; x++)
                    {
                        int si = ((y * width) + x) * 2;
                        ushort px = (ushort)(rgb565[si] | (rgb565[si + 1] << 8));
                        int r5 = (px >> 11) & 0x1F;
                        int g6 = (px >> 5) & 0x3F;
                        int b5 = px & 0x1F;
                        int di = x * 4;
                        row[di + 0] = (byte)((b5 << 3) | (b5 >> 2));   // B
                        row[di + 1] = (byte)((g6 << 2) | (g6 >> 4));   // G
                        row[di + 2] = (byte)((r5 << 3) | (r5 >> 2));   // R
                        row[di + 3] = 0xFF;                            // A
                    }
                }
            }
        }

        using var ms = new MemoryStream();
        bitmap.Save(ms);
        return ms.ToArray();
    }
}
