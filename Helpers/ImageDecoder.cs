// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Compression;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Svg.Skia;

namespace OrgZ.Helpers;

/// <summary>
/// Decodes downloaded image bytes into an Avalonia <see cref="Bitmap"/>, including SVG -
/// which Avalonia's raster decoder can't touch. Radio Browser favicons are frequently SVG
/// (station logos especially), so every remote-artwork path routes through here: rasters go
/// straight to Skia's decoder, SVGs get rasterized via Svg.Skia at the requested size, and
/// gzipped .svgz payloads are inflated first.
/// </summary>
public static class ImageDecoder
{
    /// <summary>Decode to a Bitmap; <paramref name="targetWidth"/> bounds both raster downscale and SVG rasterization. Null when undecodable.</summary>
    public static Bitmap? Decode(byte[] bytes, int? targetWidth = null)
    {
        try
        {
            bytes = Gunzip(bytes);
            if (LooksLikeSvg(bytes))
            {
                var png = RasterizeSvgToPng(bytes, targetWidth ?? 512);
                if (png == null)
                {
                    return null;
                }
                using var pngStream = new MemoryStream(png);
                return new Bitmap(pngStream);
            }

            using var stream = new MemoryStream(bytes);
            return targetWidth is int width ? Bitmap.DecodeToWidth(stream, width) : new Bitmap(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Raster bytes suitable for consumers that decode themselves (SMTC, macOS Now Playing):
    /// SVG input comes back as PNG bytes, anything else passes through untouched.
    /// </summary>
    public static byte[] EnsureRasterBytes(byte[] bytes, int targetWidth = 512)
    {
        try
        {
            var inflated = Gunzip(bytes);
            if (LooksLikeSvg(inflated))
            {
                return RasterizeSvgToPng(inflated, targetWidth) ?? bytes;
            }
        }
        catch
        {
            // Fall through to the original bytes.
        }
        return bytes;
    }

    /// <summary>Raster magic bytes are binary; SVG is markup whose first real character is '&lt;'.</summary>
    private static bool LooksLikeSvg(byte[] bytes)
    {
        var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] Gunzip(byte[] bytes)
    {
        if (bytes.Length < 2 || bytes[0] != 0x1F || bytes[1] != 0x8B)
        {
            return bytes;
        }
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static byte[]? RasterizeSvgToPng(byte[] bytes, int target)
    {
        using var svg = new SKSvg();
        using var input = new MemoryStream(bytes);
        if (svg.Load(input) is null || svg.Picture is not { } picture)
        {
            return null;
        }

        // Scale uniformly so the SVG's intrinsic bounds fit the target box - some SVGs
        // declare tiny viewBoxes and would otherwise render as a postage stamp.
        var cull = picture.CullRect;
        if (cull.Width <= 0 || cull.Height <= 0)
        {
            return null;
        }
        var scale = Math.Min(target / cull.Width, target / cull.Height);
        var width = Math.Max(1, (int)Math.Round(cull.Width * scale));
        var height = Math.Max(1, (int)Math.Round(cull.Height * scale));

        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale, scale);
        canvas.Translate(-cull.Left, -cull.Top);
        canvas.DrawPicture(picture);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
