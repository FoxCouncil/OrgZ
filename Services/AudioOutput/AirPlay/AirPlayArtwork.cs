// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using SkiaSharp;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Gets a cover down to a size worth putting on a control channel.
///
/// A modern rip carries art at 1500x1500 or larger - three quarters of a megabyte is normal,
/// and this app has sent a 771KB cover. That goes out TWICE per track change (once as the
/// DMAP artwork, once inline in the MediaRemote push), through an encrypted channel that
/// frames everything in 1024-byte blocks, on the same connection carrying the session's
/// keep-alive. Fifteen hundred sealed frames per track is why a track change feels slow.
///
/// The reference sender resizes to 600x600 before sending and that is what this does. A
/// speaker's display is a few hundred pixels; nothing above that is ever seen.
/// </summary>
internal static class AirPlayArtwork
{
    /// <summary>What the reference sender resizes to.</summary>
    private const int MaxEdge = 600;

    /// <summary>
    /// The byte ceiling for the JPEG we ship. A real iPhone puts a ~43KB baseline JPEG on
    /// the MediaRemote push; a HomePod 200-accepts a larger one and then renders NOTHING -
    /// which is exactly the "no metadata, no controls" a real 120KB cover produced while a
    /// 5KB stub rendered fine. So the output is quality-stepped down until it fits here.
    /// </summary>
    private const int TargetBytes = 45_000;

    /// <summary>
    /// Below this, resizing costs more than it saves - and it's already inside the ceiling.
    /// </summary>
    private const int LeaveAloneBelow = 45_000;

    private static readonly ILogger _log = Logging.For("AirPlayArt");

    /// <summary>
    /// Returns a cover no larger than 600x600 as JPEG, or the original bytes when it is
    /// already small enough or cannot be decoded.
    ///
    /// Never throws and never returns null for a non-null input: artwork is decoration, and a
    /// picture we cannot resize is still a picture worth trying to send.
    /// </summary>
    public static byte[] Fit(byte[] image)
    {
        // Only a JPEG gets the small-enough pass. The MediaRemote push carries JPEG only -
        // a real iPhone sends nothing else, and PNG art silently never displays there - so a
        // small PNG still goes through the transcode below.
        if (image.Length <= LeaveAloneBelow && AirPlay2Session.ImageContentType(image) is "image/jpeg")
        {
            return image;
        }

        try
        {
            using var source = SKBitmap.Decode(image);
            if (source is null)
            {
                return image;
            }

            var scale = Math.Min((double)MaxEdge / source.Width, (double)MaxEdge / source.Height);
            if (scale >= 1.0)
            {
                // Already small in pixels but large in bytes - a lightly compressed JPEG, or
                // a PNG photograph. Re-encoding alone is worth it.
                scale = 1.0;
            }

            var width = Math.Max(1, (int)Math.Round(source.Width * scale));
            var height = Math.Max(1, (int)Math.Round(source.Height * scale));

            using var scaled = source.Resize(new SKImageInfo(width, height), SKSamplingOptions.Default);
            if (scaled is null)
            {
                return image;
            }

            using var skImage = SKImage.FromBitmap(scaled);

            // Step the JPEG quality down until the encoded size fits the ceiling a HomePod
            // will actually render. A detailed 600x600 cover at q80 is ~120KB - three times
            // an iPhone's - so start high and drop until it's iPhone-sized.
            byte[] result = [];
            foreach (var quality in new[] { 80, 65, 50, 40, 30 })
            {
                using var encoded = skImage.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (encoded is null)
                {
                    return image;
                }

                result = encoded.ToArray();
                if (result.Length <= TargetBytes)
                {
                    break;
                }
            }

            if (result.Length == 0)
            {
                return image;
            }

            // Only if it actually helped. A tiny source can re-encode LARGER, and shipping a
            // bigger file to save bandwidth would be a poor joke - unless the point of the
            // exercise was the FORMAT: a PNG has to leave here as JPEG regardless, because
            // the MediaRemote push cannot carry it otherwise.
            if (result.Length >= image.Length && AirPlay2Session.ImageContentType(image) is "image/jpeg")
            {
                return image;
            }

            _log.Debug("AirPlay artwork {From}B ({FromW}x{FromH}) -> {To}B ({ToW}x{ToH})",
                image.Length, source.Width, source.Height, result.Length, width, height);

            return result;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay artwork resize failed - sending the original");
            return image;
        }
    }
}
