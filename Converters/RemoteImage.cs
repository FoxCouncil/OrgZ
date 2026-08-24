// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Collections.Concurrent;
using System.Net.Http;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace OrgZ.Converters;

/// <summary>
/// Attached property that fetches an <c>http(s)://</c> URL in the background and
/// assigns the resulting <see cref="Bitmap"/> to an <see cref="Image"/>'s
/// <see cref="Image.SourceProperty"/>. Bitmaps are cached per-URL so DataGrid
/// row recycling never re-downloads the same artwork.
///
/// Usage in XAML:
/// <code>
///   xmlns:img="clr-namespace:OrgZ.Converters"
///   ...
///   &lt;Image img:RemoteImage.Url="{Binding DisplayImage}" /&gt;
/// </code>
/// Avalonia's Image control can't decode network URLs natively, so this is the
/// path for podcast artwork (and anything else served by a remote host).
/// </summary>
public static class RemoteImage
{
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36" },
        },
    };

    // Throttle parallel network fetches: a podcast store can show 60+ tiles on
    // first paint; firing them all at once saturates the network and starves
    // the UI thread when each bitmap decodes.
    private static readonly SemaphoreSlim _gate = new(initialCount: 6, maxCount: 6);

    private static readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> _inFlight = new(StringComparer.Ordinal);

    // The memory cache is BOUNDED (insertion-order eviction). It used to grow forever:
    // every artwork URL a browsing session touched kept its decoded bitmap alive for the
    // process lifetime, and the podcast store shows 60+ tiles per screen. 300 decoded
    // 240px bitmaps is a few MB - comfortably more than any one view needs.
    private const int MaxCachedBitmaps = 300;
    private static readonly ConcurrentQueue<string> _cacheOrder = new();

    // A FETCH failure is remembered here rather than as a null in _cache, because a cached
    // null is a permanent hit: one bad minute (hotel wifi, the VPN still coming up) used to
    // blank those tiles for the rest of the process, and only a restart brought them back.
    // The cooldown still stops a scroll from re-requesting a dead URL on every row recycle.
    private static readonly ConcurrentDictionary<string, long> _failedAtTicks = new(StringComparer.Ordinal);
    private const long FailureCooldownMs = 60_000;

    private static bool RecentlyFailed(string url)
        => _failedAtTicks.TryGetValue(url, out var ticks) && Environment.TickCount64 - ticks < FailureCooldownMs;

    private static void CacheBitmap(string key, Bitmap? bmp)
    {
        if (bmp is not null)
        {
            _failedAtTicks.TryRemove(key, out _);
        }

        if (_cache.TryAdd(key, bmp))
        {
            _cacheOrder.Enqueue(key);
        }
        else
        {
            _cache[key] = bmp;
        }

        while (_cache.Count > MaxCachedBitmaps && _cacheOrder.TryDequeue(out var evicted))
        {
            // Dropped, never disposed: a live Image may still be rendering this bitmap,
            // and disposing one under a render pass is a hard crash (the ref-counting
            // lesson from the album-art path). The GC reclaims it once nothing shows it,
            // and the disk cache makes re-decoding a re-visited tile cheap.
            _cache.TryRemove(evicted, out _);
        }
    }

    // Prefer the library-root .podcasts/images/ so the cache travels with the
    // user's library and stays visible (hidden by leading dot but inspectable).
    // Fall back to %AppData% when no library folder is configured -- that's the
    // first-run state before the user picks a folder.
    private static string DiskCacheDir
    {
        get
        {
            var root = App.FolderPath;
            if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
            {
                return Path.Combine(root, ".podcasts", "images");
            }
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OrgZ", "imgcache");
        }
    }

    // Tiles in the podcast store are 120-160px wide. Decoding straight to 240
    // (~2x for retina) keeps things crisp without uploading 3000x3000 bitmaps
    // to the GPU. The decoded image is what we render -- we never need the
    // source resolution anywhere in the app.
    private const int DecodeTargetWidth = 240;

    public static readonly AttachedProperty<string?> UrlProperty =
        AvaloniaProperty.RegisterAttached<Image, string?>("Url", typeof(RemoteImage));

    public static string? GetUrl(Image element) => element.GetValue(UrlProperty);
    public static void SetUrl(Image element, string? value) => element.SetValue(UrlProperty, value);

    static RemoteImage()
    {
        UrlProperty.Changed.AddClassHandler<Image>((image, args) =>
        {
            var url = args.NewValue as string;
            if (string.IsNullOrWhiteSpace(url))
            {
                image.Source = null;
                return;
            }

            if (_cache.TryGetValue(url, out var cached))
            {
                image.Source = cached;
                return;
            }

            image.Source = null;

            if (RecentlyFailed(url))
            {
                return;
            }

            _ = LoadAndAssignAsync(image, url);
        });
    }

    private static async Task LoadAndAssignAsync(Image image, string url)
    {
        var bmp = await FetchAsync(url);
        if (bmp == null) return;

        // Only assign if the Image is still asking for this url -- the row
        // may have been recycled to a different DataContext while we were
        // fetching.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (GetUrl(image) == url)
            {
                image.Source = bmp;
            }
        });
    }

    private static Task<Bitmap?> FetchAsync(string url)
    {
        return _inFlight.GetOrAdd(url, async key =>
        {
            try
            {
                // Disk cache: persistent across launches. Filename is a SHA-1
                // hex of the URL so URL characters don't pollute filenames.
                var dir = DiskCacheDir;
                Directory.CreateDirectory(dir);
                var diskPath = Path.Combine(dir, UrlHash(key) + ".png");
                // Disk cache stores the ALREADY-DOWNSCALED PNG. Original 3000x3000
                // podcast artwork is decoded to 240px once, then re-encoded as
                // PNG (~20-40KB) and persisted; every subsequent launch decodes
                // a tiny file instead of re-rescaling the source.
                if (File.Exists(diskPath))
                {
                    try
                    {
                        await using var fs = new FileStream(diskPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                        var diskBmp = new Bitmap(fs);
                        CacheBitmap(key, diskBmp);
                        return diskBmp;
                    }
                    catch
                    {
                        try { File.Delete(diskPath); } catch { }
                    }
                }

                await _gate.WaitAsync();
                try
                {
                    using var resp = await _http.GetAsync(key);
                    resp.EnsureSuccessStatusCode();
                    var bytes = await resp.Content.ReadAsByteArrayAsync();
                    // ImageDecoder resamples rasters at decode time (a 3000x3000 JPEG never
                    // materializes at full size) and rasterizes SVG logos, which Avalonia's
                    // own decoder can't touch.
                    var bmp = Helpers.ImageDecoder.Decode(bytes, DecodeTargetWidth);
                    if (bmp == null)
                    {
                        CacheBitmap(key, null);
                        return null;
                    }
                    // Persist the downscaled bitmap (PNG) so the next launch
                    // skips both the HTTP fetch AND the heavyweight re-decode.
                    try { bmp.Save(diskPath); } catch { }
                    CacheBitmap(key, bmp);
                    return bmp;
                }
                finally
                {
                    _gate.Release();
                }
            }
            catch
            {
                // Transient by assumption - the host, the DNS or the link failed, not the
                // image. Record the time instead of poisoning the cache so the tile refetches
                // once the cooldown lapses. (A DECODE failure above is a property of the bytes
                // themselves and stays cached.)
                _failedAtTicks[key] = Environment.TickCount64;
                return null;
            }
            finally
            {
                _inFlight.TryRemove(key, out _);
            }
        });
    }

    private static string UrlHash(string url)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url));
        return System.Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
