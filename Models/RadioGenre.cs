// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Models;

/// <summary>
/// Canonical radio genre taxonomy. Integer values match the genreId in
/// <c>Assets/stations.json</c> and the curator's curated.json, and they ARE the display
/// order: decades first (chronological), then alphabetical by display name. When the
/// taxonomy changes, renumber to keep that invariant and migrate curated.json in the same
/// commit - stations.json is regenerated wholesale by the curator's export.
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
    AmbientChillout = 7,
    Bluegrass       = 8,
    Classical       = 9,
    Comedy          = 10,
    Country         = 11,
    DiscoFunk       = 12,
    ElectronicDance = 13,
    HipHopRap       = 14,
    Holiday         = 15,
    Indie           = 16,
    JazzBlues       = 17,
    Latin           = 18,
    LoFi            = 19,
    Metal           = 20,
    MotownSoul      = 21,
    NewsTalkRadio   = 22,
    Oldies          = 23,
    Punk            = 24,
    Reggae          = 25,
    Religious       = 26,
    Rock            = 27,
    SportsTalk      = 28,
    Synthwave       = 29,
    Top40Pop        = 30,
    World           = 31,
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
        { RadioGenre.AmbientChillout, "Ambient/Chillout" },
        { RadioGenre.Bluegrass,       "Bluegrass" },
        { RadioGenre.Classical,       "Classical" },
        { RadioGenre.Comedy,          "Comedy" },
        { RadioGenre.Country,         "Country" },
        { RadioGenre.DiscoFunk,       "Disco/Funk" },
        { RadioGenre.ElectronicDance, "Electronic/Dance" },
        { RadioGenre.HipHopRap,       "Hip-Hop/Rap" },
        { RadioGenre.Holiday,         "Holiday" },
        { RadioGenre.Indie,           "Indie" },
        { RadioGenre.JazzBlues,       "Jazz/Blues" },
        { RadioGenre.Latin,           "Latin" },
        { RadioGenre.LoFi,            "Lo-Fi" },
        { RadioGenre.Metal,           "Metal" },
        { RadioGenre.MotownSoul,      "Motown/Soul" },
        { RadioGenre.NewsTalkRadio,   "News/Talk Radio" },
        { RadioGenre.Oldies,          "Oldies" },
        { RadioGenre.Punk,            "Punk" },
        { RadioGenre.Reggae,          "Reggae" },
        { RadioGenre.Religious,       "Religious" },
        { RadioGenre.Rock,            "Rock" },
        { RadioGenre.SportsTalk,      "Sports Talk" },
        { RadioGenre.Synthwave,       "Synthwave" },
        { RadioGenre.Top40Pop,        "Top 40/Pop" },
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

    /// <summary>All known genres in display order - which is id order, by invariant.</summary>
    public static IEnumerable<RadioGenre> All => _names.Keys;
}
