// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Writes and removes embedded cover art on library files. Kept out of the dialog so
/// the rules - what image formats are accepted, what happens to a file that can't
/// carry art, how a failure leaves the original - are testable rather than clicked.
/// </summary>
public static class AlbumArtWriter
{
    private static readonly ILogger _log = Logging.For("AlbumArt");

    /// <summary>Outcome of an artwork edit; Error carries a user-showable reason.</summary>
    public readonly record struct ArtResult(bool Ok, string? Error)
    {
        public static ArtResult Success => new(true, null);
        public static ArtResult Fail(string reason) => new(false, reason);
    }

    /// <summary>Image types worth embedding - what players and iPods actually decode.</summary>
    private static readonly HashSet<string> _allowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".png" };

    /// <summary>Guards against embedding something absurd; iPod art is a few hundred KB at most.</summary>
    internal const int MaxImageBytes = 16 * 1024 * 1024;

    /// <summary>True when the file extension is one we can attach art to.</summary>
    internal static bool IsSupportedImage(string path)
        => !string.IsNullOrWhiteSpace(path) && _allowedExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// Sniffs the real format rather than trusting the extension - a .png that is
    /// actually a text file would embed cleanly and then render as nothing everywhere.
    /// </summary>
    internal static string? MimeTypeFor(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
        {
            return "image/png";
        }

        return null;
    }

    /// <summary>Validates an image before any file is touched. Null when it's usable.</summary>
    internal static string? ValidateImage(byte[] data)
    {
        if (data.Length == 0)
        {
            return "That image file is empty.";
        }

        if (data.Length > MaxImageBytes)
        {
            return $"That image is too large ({data.Length / (1024 * 1024)} MB); the limit is {MaxImageBytes / (1024 * 1024)} MB.";
        }

        return MimeTypeFor(data) is null ? "That file isn't a JPEG or PNG image." : null;
    }

    /// <summary>
    /// Embeds <paramref name="imagePath"/> as the file's front cover, replacing any
    /// existing art. Validates fully before opening the audio file, so a rejected
    /// image never leaves a half-written tag.
    /// </summary>
    public static ArtResult SetArtwork(string audioPath, string imagePath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return ArtResult.Fail("That track's file is missing.");
        }

        if (!IsSupportedImage(imagePath))
        {
            return ArtResult.Fail("Choose a JPEG or PNG image.");
        }

        byte[] data;
        try
        {
            data = File.ReadAllBytes(imagePath);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not read artwork {Image}", imagePath);
            return ArtResult.Fail($"Couldn't read that image: {ex.Message}");
        }

        if (ValidateImage(data) is { } invalid)
        {
            return ArtResult.Fail(invalid);
        }

        return SetArtwork(audioPath, data, MimeTypeFor(data)!);
    }

    /// <summary>Embeds raw image bytes. Exposed for callers that already hold the image.</summary>
    public static ArtResult SetArtwork(string audioPath, byte[] data, string mimeType)
    {
        if (ValidateImage(data) is { } invalid)
        {
            return ArtResult.Fail(invalid);
        }

        try
        {
            using var file = TagLib.File.Create(audioPath);

            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector(data))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = mimeType,
                    Description = "Cover",
                },
            ];

            file.Save();
            _log.Information("Embedded {Bytes} byte cover in {Path}", data.Length, audioPath);
            return ArtResult.Success;
        }
        catch (TagLib.UnsupportedFormatException)
        {
            return ArtResult.Fail("That audio format can't store artwork.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Embedding artwork failed for {Path}", audioPath);
            return ArtResult.Fail($"Couldn't save the artwork: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the embedded cover out of a file - front cover first, otherwise whatever
    /// picture is there. Null when the file has no art, isn't readable, or carries a
    /// picture in a format we wouldn't have written (so a caller can trust the mime type).
    /// </summary>
    public static (byte[] Data, string MimeType)? ReadArtwork(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return null;
        }

        try
        {
            using var file = TagLib.File.Create(audioPath);
            var pictures = file.Tag.Pictures;
            if (pictures is null || pictures.Length == 0)
            {
                return null;
            }

            var picture = Array.Find(pictures, p => p.Type == TagLib.PictureType.FrontCover) ?? pictures[0];
            var data = picture.Data?.Data;
            if (data is null || data.Length == 0)
            {
                return null;
            }

            // Sniff rather than trust the tag's own MimeType - files in the wild carry
            // "image/jpg", empty strings, and outright lies.
            return MimeTypeFor(data) is { } mime ? (data, mime) : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Reading artwork failed for {Path}", audioPath);
            return null;
        }
    }

    /// <summary>
    /// Strips every embedded picture. Removing art from a file that has none is a
    /// no-op success - the caller asked for "no artwork", and that's the state.
    /// </summary>
    public static ArtResult RemoveArtwork(string audioPath)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            return ArtResult.Fail("That track's file is missing.");
        }

        try
        {
            using var file = TagLib.File.Create(audioPath);
            if (file.Tag.Pictures is { Length: 0 })
            {
                return ArtResult.Success;
            }

            file.Tag.Pictures = [];
            file.Save();
            _log.Information("Removed embedded cover from {Path}", audioPath);
            return ArtResult.Success;
        }
        catch (TagLib.UnsupportedFormatException)
        {
            return ArtResult.Fail("That audio format can't store artwork.");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Removing artwork failed for {Path}", audioPath);
            return ArtResult.Fail($"Couldn't remove the artwork: {ex.Message}");
        }
    }
}
