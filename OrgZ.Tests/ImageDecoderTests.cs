// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Compression;
using System.Text;
using OrgZ.Helpers;

namespace OrgZ.Tests;

/// <summary>
/// SVG handling in the remote-artwork pipeline. Only the raster-bytes surface is exercised
/// here - it runs pure SkiaSharp, so no Avalonia render platform is needed in the test host.
/// </summary>
public class ImageDecoderTests
{
    private const string Svg = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32"><rect width="32" height="32" fill="#f00"/></svg>""";

    private static readonly byte[] PngMagic = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    private static bool StartsWithPngMagic(byte[] bytes) => bytes.Length > 4 && bytes.AsSpan(0, 4).SequenceEqual(PngMagic);

    [Fact]
    public void SvgBytesRasterizeToPng()
    {
        var result = ImageDecoder.EnsureRasterBytes(Encoding.UTF8.GetBytes(Svg));

        Assert.True(StartsWithPngMagic(result));
    }

    [Fact]
    public void SvgWithXmlPrologRasterizesToPng()
    {
        var result = ImageDecoder.EnsureRasterBytes(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + Svg));

        Assert.True(StartsWithPngMagic(result));
    }

    [Fact]
    public void GzippedSvgzRasterizesToPng()
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionMode.Compress, leaveOpen: true))
        {
            gzip.Write(Encoding.UTF8.GetBytes(Svg));
        }

        var result = ImageDecoder.EnsureRasterBytes(compressed.ToArray());

        Assert.True(StartsWithPngMagic(result));
    }

    [Fact]
    public void RasterBytesPassThroughUntouched()
    {
        var png = ImageDecoder.EnsureRasterBytes(Encoding.UTF8.GetBytes(Svg));

        Assert.Same(png, ImageDecoder.EnsureRasterBytes(png));
    }

    [Fact]
    public void GarbageBytesPassThroughUntouched()
    {
        var garbage = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };

        Assert.Same(garbage, ImageDecoder.EnsureRasterBytes(garbage));
    }

    [Fact]
    public void HtmlErrorPageIsNotMistakenForSvg()
    {
        // A 404 page is markup too - it must not be fed to the SVG rasterizer's happy path.
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Not Found</body></html>");

        Assert.Same(html, ImageDecoder.EnsureRasterBytes(html));
    }
}
