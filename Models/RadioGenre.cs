// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>
/// Canonical radio genre taxonomy. Integer values match the genreId in
/// <c>Assets/stations.json</c>; the loader maps each JSON entry to one of these
/// values, and the Radio DataGrid groups by their display name. Order is
/// alphabetical-by-name for stable, predictable IDs across edits.
/// </summary>
public enum RadioGenre
{
    Unknown            = 0,
    Seventies          = 1,
    Eighties           = 2,
    Nineties           = 3,
    AdultContemporary  = 4,
    AlternativeRock    = 5,
    Ambient            = 6,
    Blues              = 7,
    ClassicRock        = 8,
    Classical          = 9,
    CollegeUniversity  = 10,
    Comedy             = 11,
    Country            = 12,
    Eclectic           = 13,
    Electronica        = 14,
    Folk               = 15,
    GoldenOldies       = 16,
    HardRockMetal      = 17,
    HipHopRap          = 18,
    InternationalWorld = 19,
    Jazz               = 20,
    NewsTalkRadio      = 21,
    ReggaeIsland       = 22,
    Religious          = 23,
    RnbSoul            = 24,
    SportsRadio        = 25,
    Top40Pop           = 26,
}

public static class RadioGenres
{
    private static readonly Dictionary<RadioGenre, string> _names = new()
    {
        { RadioGenre.Seventies,          "70's" },
        { RadioGenre.Eighties,           "80's" },
        { RadioGenre.Nineties,           "90's" },
        { RadioGenre.AdultContemporary,  "Adult Contemporary" },
        { RadioGenre.AlternativeRock,    "Alternative Rock" },
        { RadioGenre.Ambient,            "Ambient" },
        { RadioGenre.Blues,              "Blues" },
        { RadioGenre.ClassicRock,        "Classic Rock" },
        { RadioGenre.Classical,          "Classical" },
        { RadioGenre.CollegeUniversity,  "College / University" },
        { RadioGenre.Comedy,             "Comedy" },
        { RadioGenre.Country,            "Country" },
        { RadioGenre.Eclectic,           "Eclectic" },
        { RadioGenre.Electronica,        "Electronica" },
        { RadioGenre.Folk,               "Folk" },
        { RadioGenre.GoldenOldies,       "Golden Oldies" },
        { RadioGenre.HardRockMetal,      "Hard Rock / Metal" },
        { RadioGenre.HipHopRap,          "Hip Hop / Rap" },
        { RadioGenre.InternationalWorld, "International / World" },
        { RadioGenre.Jazz,               "Jazz" },
        { RadioGenre.NewsTalkRadio,      "News / Talk Radio" },
        { RadioGenre.ReggaeIsland,       "Reggae / Island" },
        { RadioGenre.Religious,          "Religious" },
        { RadioGenre.RnbSoul,            "RnB / Soul" },
        { RadioGenre.SportsRadio,        "Sports Radio" },
        { RadioGenre.Top40Pop,           "Top 40 / Pop" },
    };

    public static string DisplayName(this RadioGenre genre) =>
        _names.TryGetValue(genre, out var name) ? name : "";

    public static string DisplayName(int genreId) =>
        DisplayName((RadioGenre)genreId);

    /// <summary>All known genres in display order.</summary>
    public static IEnumerable<RadioGenre> All => _names.Keys;
}
