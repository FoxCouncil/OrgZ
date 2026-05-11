// Cross-platform icon generator. Two subcommands:
//
//   svg-to-png <in.svg> <out.png> [size=1024]   Rasterize SVG to a transparent
//                                               square PNG at the given size.
//
//   make-ico <in.png> <out.ico> [sizes...]      Pack a single PNG at multiple
//                                               sizes into a PNG-in-ICO file.
//                                               Defaults to 16 32 48 64 128 256.
//
// Replaces the macOS-only Swift scripts. Runs on every dev OS via:
//   dotnet run --project tools/IconGen -- <subcommand> ...

using SkiaSharp;
using Svg.Skia;

if (args.Length < 1)
{
    PrintUsage();
    return 2;
}

return args[0] switch
{
    "svg-to-png" => RunSvgToPng(args),
    "make-ico"   => RunMakeIco(args),
    _            => Fail("unknown subcommand"),
};

static int RunSvgToPng(string[] args)
{
    if (args.Length < 3)
    {
        return Fail("usage: svg-to-png <in.svg> <out.png> [size]");
    }

    var inPath = args[1];
    var outPath = args[2];
    var size = args.Length >= 4 && int.TryParse(args[3], out var s) ? s : 1024;

    using var svg = new SKSvg();
    if (svg.Load(inPath) is null)
    {
        return Fail($"failed to load SVG: {inPath}");
    }

    var picture = svg.Picture!;
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);

    // Scale uniformly so the SVG's intrinsic bounds fit the target square. Some
    // SVGs declare a tiny viewBox (Microsoft Fluent Emoji are 32 × 32) — without
    // this scale we'd render a postage stamp in the top-left corner.
    var cull = picture.CullRect;
    var scaleX = size / cull.Width;
    var scaleY = size / cull.Height;
    var scale = Math.Min(scaleX, scaleY);
    canvas.Scale(scale, scale);
    canvas.Translate(-cull.Left, -cull.Top);
    canvas.DrawPicture(picture);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.OpenWrite(outPath);
    data.SaveTo(stream);

    Console.WriteLine($"Wrote {outPath} ({size}×{size})");
    return 0;
}

static int RunMakeIco(string[] args)
{
    if (args.Length < 3)
    {
        return Fail("usage: make-ico <in.png> <out.ico> [sizes...]");
    }

    var inPath = args[1];
    var outPath = args[2];
    var sizes = args.Length > 3
        ? args[3..].Select(int.Parse).ToArray()
        : new[] { 16, 32, 48, 64, 128, 256 };

    using var sourceBitmap = SKBitmap.Decode(inPath);
    if (sourceBitmap is null)
    {
        return Fail($"failed to load PNG: {inPath}");
    }

    // Pre-render each requested size to a PNG byte array so we know exact lengths
    // before laying out the ICO directory entries.
    var entries = sizes.Select(size =>
    {
        using var resized = sourceBitmap.Resize(new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul), SKSamplingOptions.Default);
        using var image = SKImage.FromBitmap(resized);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return (size, png: data.ToArray());
    }).ToArray();

    // ICO file layout: 6-byte ICONDIR header, N × 16-byte ICONDIRENTRY records,
    // then concatenated image blobs. Each blob is a complete PNG (the
    // "PNG-in-ICO" form Windows Vista+ accepts). PNG bytes are stored verbatim,
    // no BMP/AND-mask repacking required.
    const int HeaderLen = 6;
    var dirLen = 16 * entries.Length;
    var blobOffset = HeaderLen + dirLen;

    using var fs = File.Create(outPath);
    using var bw = new BinaryWriter(fs);

    // ICONDIR
    bw.Write((ushort)0);                       // reserved
    bw.Write((ushort)1);                       // type = icon
    bw.Write((ushort)entries.Length);

    // ICONDIRENTRY × N
    foreach (var (size, png) in entries)
    {
        // BYTE width / height: 0 sentinel means 256
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)(size >= 256 ? 0 : size));
        bw.Write((byte)0);                     // colorCount (0 for >256 colors)
        bw.Write((byte)0);                     // reserved
        bw.Write((ushort)1);                   // planes
        bw.Write((ushort)32);                  // bitCount
        bw.Write((uint)png.Length);            // bytesInRes
        bw.Write((uint)blobOffset);            // imageOffset
        blobOffset += png.Length;
    }

    foreach (var (_, png) in entries)
    {
        bw.Write(png);
    }

    Console.WriteLine($"Wrote {outPath} with sizes {string.Join(", ", sizes)}");
    return 0;
}

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    return 1;
}

static void PrintUsage()
{
    Console.Error.WriteLine("usage:");
    Console.Error.WriteLine("  dotnet run --project tools/IconGen -- svg-to-png <in.svg> <out.png> [size]");
    Console.Error.WriteLine("  dotnet run --project tools/IconGen -- make-ico   <in.png> <out.ico> [sizes...]");
}
