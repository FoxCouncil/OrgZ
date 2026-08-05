// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia.Media.Imaging;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Getting cover art from wherever it lives - a file's tags, a third-party CDN - and into the
/// forms the app and the OS need. Pure helpers with no view-model state: the FIRST piece
/// lifted out of MainWindowViewModel's art cluster, which is the split's beachhead. What stays
/// behind in the view model is the part that genuinely IS view-model work: deciding which art
/// belongs to the current epoch, and assigning it to the bound properties.
/// </summary>
public static class ArtworkSource
{
    private static readonly ILogger _log = Logging.For("ArtworkSource");

    /// <summary>
    /// The ONE client for third-party art (station favicons, podcast covers, radio track art).
    /// Stock browser UA via Web.Create - the art sits on third-party CDNs that can reject odd
    /// agents, and their request logs stay anonymous. Never put an app-identifying UA on a
    /// request that leaves the machine.
    /// </summary>
    public static readonly HttpClient Http = Web.Create(TimeSpan.FromSeconds(8));

    /// <summary>Embedded cover bytes from a file's tags, or null when it has none.</summary>
    public static byte[]? EmbeddedArt(string filePath) => ReadArtAndProperties(filePath).Art;

    /// <summary>
    /// Embedded art AND the audio properties the bit-perfect engine needs, from ONE TagLib
    /// open. The play path used to open the same file twice on the UI thread - once for art,
    /// once to probe an item that predates the BitDepth column - which on a sleeping disk is
    /// two spin-ups at the moment of a double-click.
    /// </summary>
    public static (byte[]? Art, int SampleRate, int? BitDepth, int Channels) ReadArtAndProperties(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var art = file.Tag.Pictures?.Length > 0 ? file.Tag.Pictures[0].Data.Data : null;
            return (art, file.Properties.AudioSampleRate,
                file.Properties.BitsPerSample > 0 ? file.Properties.BitsPerSample : null,
                file.Properties.AudioChannels);
        }
        catch
        {
            return (null, 0, null, 0);
        }
    }

    /// <summary>Decodes art bytes (raster or SVG) to a bitmap; null when undecodable.</summary>
    public static Bitmap? BitmapFromBytes(byte[] bytes)
    {
        try
        {
            return Helpers.ImageDecoder.Decode(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Re-encodes a decoded bitmap to PNG for the OS now-playing surface. Radio favicons
    /// arrive in formats Skia decodes but Windows' WIC (which renders the SMTC thumbnail)
    /// can't - WEBP, some ICOs - so the app showed the logo while the OS silently dropped
    /// it. Pushing the bitmap we already decoded, re-encoded as PNG, guarantees a format
    /// every platform's imaging can read. Falls back to the original bytes on failure.
    /// </summary>
    public static byte[] ToOsArtworkBytes(Bitmap bitmap, byte[] fallback)
    {
        try
        {
            using var ms = new MemoryStream();
            bitmap.Save(ms);   // Avalonia writes PNG
            return ms.Length > 0 ? ms.ToArray() : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Downloads a podcast show's cover to a temp file so it can be rendered into the iPod
    /// ArtworkDB. Returns the local path, or null when there's no URL / the fetch fails.
    /// </summary>
    public static string? DownloadShowArtToTempFile(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        try
        {
            var bytes = Http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            if (bytes.Length == 0)
            {
                return null;
            }

            var path = Path.Combine(Path.GetTempPath(), "orgz_pcart_" + Guid.NewGuid().ToString("N")[..8] + ".img");
            File.WriteAllBytes(path, bytes);
            return path;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Show art fetch failed for {Url}", url);
            return null;   // no art / fetch failed - the episode just imports without a cover
        }
    }
}
