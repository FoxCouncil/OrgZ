// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>
/// Canonical radio genre taxonomy. Integer values match the genreId in
/// <c>Assets/stations.json</c>; the loader maps each JSON entry to one of these
/// values, and the Radio DataGrid groups by their display name. Order is
/// decades-first (chronological), then alphabetical-by-name, for stable,
/// predictable IDs across edits.
/// </summary>
public enum RadioGenre
{
    Unknown         = 0,
    Fifties         = 1,
    Sixties         = 2,
    Seventies       = 3,
    Eighties        = 4,
    Nineties        = 5,
    TwoThousands    = 6,
    AlternativeRock = 7,
    AmbientChillout = 8,
    Bluegrass       = 9,
    Blues           = 10,
    Classical       = 11,
    Comedy          = 12,
    Country         = 13,
    DiscoFunk       = 14,
    ElectronicDance = 15,
    HipHopRap       = 16,
    Indie           = 17,
    Jazz            = 18,
    Latin           = 19,
    LoFi            = 20,
    Metal           = 21,
    MotownSoul      = 22,
    NewsTalkRadio   = 23,
    Punk            = 24,
    Reggae          = 25,
    Religious       = 26,
    SportsTalk      = 27,
    Synthwave       = 28,
    World           = 29,
}

public static class RadioGenres
{
    private static readonly Dictionary<RadioGenre, string> _names = new()
    {
        { RadioGenre.Fifties,         "50s" },
        { RadioGenre.Sixties,         "60s" },
        { RadioGenre.Seventies,       "70s" },
        { RadioGenre.Eighties,        "80s" },
        { RadioGenre.Nineties,        "90s" },
        { RadioGenre.TwoThousands,    "2000s" },
        { RadioGenre.AlternativeRock, "Alternative Rock" },
        { RadioGenre.AmbientChillout, "Ambient/Chillout" },
        { RadioGenre.Bluegrass,       "Bluegrass" },
        { RadioGenre.Blues,           "Blues" },
        { RadioGenre.Classical,       "Classical" },
        { RadioGenre.Comedy,          "Comedy" },
        { RadioGenre.Country,         "Country" },
        { RadioGenre.DiscoFunk,       "Disco/Funk" },
        { RadioGenre.ElectronicDance, "Electronic/Dance" },
        { RadioGenre.HipHopRap,       "Hip-Hop/Rap" },
        { RadioGenre.Indie,           "Indie" },
        { RadioGenre.Jazz,            "Jazz" },
        { RadioGenre.Latin,           "Latin" },
        { RadioGenre.LoFi,            "Lo-Fi" },
        { RadioGenre.Metal,           "Metal" },
        { RadioGenre.MotownSoul,      "Motown/Soul" },
        { RadioGenre.NewsTalkRadio,   "News/Talk Radio" },
        { RadioGenre.Punk,            "Punk" },
        { RadioGenre.Reggae,          "Reggae" },
        { RadioGenre.Religious,       "Religious" },
        { RadioGenre.SportsTalk,      "Sports Talk" },
        { RadioGenre.Synthwave,       "Synthwave" },
        { RadioGenre.World,           "World" },
    };

    private static readonly Dictionary<string, RadioGenre> _byName =
        _names.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    public static string DisplayName(this RadioGenre genre) =>
        _names.TryGetValue(genre, out var name) ? name : "";

    public static string DisplayName(int genreId) =>
        DisplayName((RadioGenre)genreId);

    /// <summary>Reverse lookup: display name back to the enum value; Unknown for anything else.</summary>
    public static RadioGenre FromDisplayName(string? name) =>
        name != null && _byName.TryGetValue(name, out var genre) ? genre : RadioGenre.Unknown;

    /// <summary>All known genres in display order.</summary>
    public static IEnumerable<RadioGenre> All => _names.Keys;
}
