// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;
using TagLib;

namespace OrgZ.Services;

/// <summary>
/// Star ratings round-trip with the FILE, not just library.db: POPM for ID3v2 (MP3),
/// the RATING vorbis comment for FLAC/Ogg. Losing the database no longer loses every
/// rating, and an incoming library tagged by another player imports with its stars.
/// MP4/WAV have no interoperable rating tag, so those stay database-only - same as the
/// check-tick, which is library state everywhere (iTunes included) and never a tag.
/// </summary>
public static class TagRating
{
    private static readonly ILogger _log = Logging.For("TagRating");

    /// <summary>POPM 0-255 → stars, using the WMP bands mainstream players agree on.</summary>
    public static int? StarsFromPopm(int popm) => popm switch
    {
        <= 0 => null,
        <= 31 => 1,
        <= 95 => 2,
        <= 159 => 3,
        <= 223 => 4,
        _ => 5,
    };

    public static byte PopmFromStars(int stars) => stars switch
    {
        <= 0 => 0,
        1 => 1,
        2 => 64,
        3 => 128,
        4 => 196,
        _ => 255,
    };

    /// <summary>Vorbis RATING is 0-100 in the wild (MusicBee et al), occasionally 1-5.</summary>
    public static int? StarsFromVorbis(string? value)
    {
        if (!int.TryParse(value, out var n) || n <= 0)
        {
            return null;
        }

        return n <= 5 ? n : Math.Clamp((int)Math.Round(n / 20.0, MidpointRounding.AwayFromZero), 1, 5);
    }

    public static int? Read(TagLib.File file)
    {
        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph
            && StarsFromVorbis(xiph.GetFirstField("RATING")) is { } vorbisStars)
        {
            return vorbisStars;
        }

        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
        {
            foreach (var frame in id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>())
            {
                if (StarsFromPopm(frame.Rating) is { } stars)
                {
                    return stars;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Sets (or clears, when null) the rating on whichever tag the container supports.
    /// True when it landed - the caller saves; false when this container has no rating home.
    /// </summary>
    public static bool Write(TagLib.File file, int? stars)
    {
        if (file.GetTag(TagTypes.Xiph, create: true) is TagLib.Ogg.XiphComment xiph)
        {
            if (stars is null)
            {
                xiph.RemoveField("RATING");
            }
            else
            {
                xiph.SetField("RATING", (stars.Value * 20).ToString());
            }

            return true;
        }

        if (file.GetTag(TagTypes.Id3v2, create: true) is TagLib.Id3v2.Tag id3)
        {
            var frames = id3.GetFrames<TagLib.Id3v2.PopularimeterFrame>().ToList();
            if (stars is null)
            {
                foreach (var frame in frames)
                {
                    id3.RemoveFrame(frame);
                }

                return true;
            }

            var target = frames.FirstOrDefault() ?? TagLib.Id3v2.PopularimeterFrame.Get(id3, "OrgZ", create: true);
            target.Rating = PopmFromStars(stars.Value);
            return true;
        }

        return false;
    }

    /// <summary>Best-effort file write; a failure never blocks the DB-side rating.</summary>
    public static bool WriteToFile(string path, int? stars)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            if (!Write(file, stars))
            {
                _log.Debug("No rating tag home in {Path} - rating stays database-only", path);
                return false;
            }

            file.Save();
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Rating tag write failed for {Path}", path);
            return false;
        }
    }
}
