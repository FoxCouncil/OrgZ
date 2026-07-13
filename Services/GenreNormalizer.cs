// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Maps provider-specific radio genre tags (Radio Browser, SHOUTcast, Icecast) into the
/// canonical OrgZ radio taxonomy - the same display names as <see cref="Models.RadioGenre"/>,
/// plus "Other" for anything unmatched. Raw tags are matched case-insensitively via an ordered
/// rule list where the most specific rules appear first.
///
/// The app itself never normalizes at runtime: bundled stations ship with a curated genreId and
/// user-added streams get an explicit genre picker. This class exists for CURATION time - the
/// tools/station-curator import path uses it to suggest a genre for each directory row.
/// </summary>
public static class GenreNormalizer
{
    // -- Canonical genre buckets. Every value except Other mirrors a RadioGenre display name. --

    public const string Fifties         = "50s";
    public const string Sixties         = "60s";
    public const string Seventies       = "70s";
    public const string Eighties        = "80s";
    public const string Nineties        = "90s";
    public const string TwoThousands    = "2000s";
    public const string AmbientChillout = "Ambient/Chillout";
    public const string Bluegrass       = "Bluegrass";
    public const string Classical       = "Classical";
    public const string Comedy          = "Comedy";
    public const string Country         = "Country";
    public const string DiscoFunk       = "Disco/Funk";
    public const string ElectronicDance = "Electronic/Dance";
    public const string HipHopRap       = "Hip-Hop/Rap";
    public const string Holiday         = "Holiday";
    public const string Indie           = "Indie";
    public const string JazzBlues       = "Jazz/Blues";
    public const string Latin           = "Latin";
    public const string LoFi            = "Lo-Fi";
    public const string Metal           = "Metal";
    public const string MotownSoul      = "Motown/Soul";
    public const string NewsTalkRadio   = "News/Talk Radio";
    public const string Oldies          = "Oldies";
    public const string Punk            = "Punk";
    public const string Reggae          = "Reggae";
    public const string Religious       = "Religious";
    public const string Rock            = "Rock";
    public const string SportsTalk      = "Sports Talk";
    public const string Synthwave       = "Synthwave";
    public const string Top40Pop        = "Top 40/Pop";
    public const string World           = "World";
    public const string Other           = "Other";

    /// <summary>
    /// All canonical buckets, in the order they should appear in UI lists.
    /// "Other" lives at the bottom.
    /// </summary>
    public static readonly IReadOnlyList<string> AllCanonical =
    [
        Fifties,
        Sixties,
        Seventies,
        Eighties,
        Nineties,
        TwoThousands,
        AmbientChillout,
        Bluegrass,
        Classical,
        Comedy,
        Country,
        DiscoFunk,
        ElectronicDance,
        HipHopRap,
        Holiday,
        Indie,
        JazzBlues,
        Latin,
        LoFi,
        Metal,
        MotownSoul,
        NewsTalkRadio,
        Oldies,
        Punk,
        Reggae,
        Religious,
        Rock,
        SportsTalk,
        Synthwave,
        Top40Pop,
        World,
        Other,
    ];

    /// <summary>
    /// Ordered match rules. Each rule: if the lowercased raw string contains the keyword
    /// (as a substring for multi-word needles, whole-word for single-word ones), the canonical
    /// bucket is returned. Order is load-bearing: more specific rules must come first so
    /// "christian rock" → Religious (not a rock bucket) and "sports talk" → Sports Talk (not News/Talk).
    /// </summary>
    private static readonly (string Needle, string Canonical)[] Rules =
    [
        // -- Religious (must come before rock/talk so "christian rock" → Religious) --
        ("christian",       Religious),
        ("gospel",          Religious),
        ("religious",       Religious),
        ("worship",         Religious),
        ("praise",          Religious),
        ("catholic",        Religious),
        ("spiritual",       Religious),
        ("sermon",          Religious),
        ("bible",           Religious),

        // -- Comedy (before Talk so "comedy talk" → Comedy) --
        ("comedy",          Comedy),
        ("humor",           Comedy),
        ("humour",          Comedy),
        ("stand-up",        Comedy),
        ("standup",         Comedy),

        // -- Sports Talk (before Talk so "sports talk" isn't generic talk) --
        ("sports",          SportsTalk),
        ("sport",           SportsTalk),
        ("football",        SportsTalk),
        ("soccer",          SportsTalk),
        ("baseball",        SportsTalk),
        ("basketball",      SportsTalk),
        ("hockey",          SportsTalk),
        ("nfl",             SportsTalk),
        ("nba",             SportsTalk),
        ("mlb",             SportsTalk),
        ("nhl",             SportsTalk),

        // -- News / Talk Radio --
        ("npr",             NewsTalkRadio),
        ("public radio",    NewsTalkRadio),
        ("pbs",             NewsTalkRadio),
        ("talk",            NewsTalkRadio),
        ("news",            NewsTalkRadio),
        ("spoken",          NewsTalkRadio),
        ("politics",        NewsTalkRadio),
        ("audiobook",       NewsTalkRadio),
        ("podcast",         NewsTalkRadio),
        ("current affairs", NewsTalkRadio),

        // -- Holiday (seasonal; before genre/decade catches so "christmas country" → Holiday) --
        ("christmas",       Holiday),
        ("xmas",            Holiday),
        ("holiday",         Holiday),
        ("noel",            Holiday),

        // -- Synthwave (before 80s and before the Electronic "synth" catch) --
        ("synthwave",       Synthwave),
        ("retrowave",       Synthwave),
        ("outrun",          Synthwave),
        ("vaporwave",       Synthwave),
        ("darksynth",       Synthwave),

        // -- Lo-Fi (before Ambient so "lofi chill" → Lo-Fi) --
        ("lo-fi",           LoFi),
        ("lofi",            LoFi),
        ("lo fi",           LoFi),
        ("chillhop",        LoFi),
        ("study beats",     LoFi),

        // -- Metal (before "rock") --
        ("death metal",     Metal),
        ("black metal",     Metal),
        ("heavy metal",     Metal),
        ("thrash",          Metal),
        ("power metal",     Metal),
        ("metalcore",       Metal),
        ("doom",            Metal),
        ("metal",           Metal),
        ("hard rock",       Metal),

        // -- Rock (post-punk predates the Punk rules so it lands here) --
        ("post-punk",       Rock),
        ("post punk",       Rock),
        ("alternative",     Rock),
        ("alt rock",        Rock),
        ("alt-rock",        Rock),
        ("modern rock",     Rock),
        ("grunge",          Rock),
        ("post-rock",       Rock),
        ("shoegaze",        Rock),

        // -- Punk (before Indie/Reggae so "pop punk" → Punk and "ska punk" → Punk) --
        ("pop punk",        Punk),
        ("punk",            Punk),
        ("hardcore",        Punk),
        ("emo",             Punk),

        // -- Indie --
        ("indie rock",      Indie),
        ("indie pop",       Indie),
        ("indie",           Indie),
        ("college",         Indie),

        // -- Decades (era buckets; "classic rock" family lands in the 70s) --
        ("50s",             Fifties),
        ("1950s",           Fifties),
        ("fifties",         Fifties),
        ("doo-wop",         Fifties),
        ("doowop",          Fifties),
        ("rockabilly",      Fifties),
        ("60s",             Sixties),
        ("1960s",           Sixties),
        ("sixties",         Sixties),
        ("oldies",          Oldies),
        ("british invasion", Sixties),
        ("classic rock",    Seventies),
        ("album rock",      Seventies),
        ("prog rock",       Seventies),
        ("progressive rock", Seventies),
        ("southern rock",   Seventies),
        ("yacht rock",      Seventies),
        ("70s",             Seventies),
        ("1970s",           Seventies),
        ("seventies",       Seventies),
        ("80s",             Eighties),
        ("1980s",           Eighties),
        ("eighties",        Eighties),
        ("new wave",        Eighties),
        ("synthpop",        Eighties),
        ("synth-pop",       Eighties),
        ("90s",             Nineties),
        ("1990s",           Nineties),
        ("nineties",        Nineties),
        ("britpop",         Nineties),
        ("2000s",           TwoThousands),
        ("00s",             TwoThousands),
        ("noughties",       TwoThousands),

        // -- Motown / Soul (before Disco/Funk so "soul funk" → Motown/Soul) --
        ("motown",          MotownSoul),
        ("soul",            MotownSoul),
        ("r&b",             MotownSoul),
        ("rnb",             MotownSoul),
        ("rhythm and blues", MotownSoul),

        // -- Disco / Funk --
        ("disco",           DiscoFunk),
        ("funk",            DiscoFunk),
        ("funky",           DiscoFunk),
        ("boogie",          DiscoFunk),

        // -- Hip-Hop / Rap --
        ("hip-hop",         HipHopRap),
        ("hip hop",         HipHopRap),
        ("hiphop",          HipHopRap),
        ("rap",             HipHopRap),
        ("trap",            HipHopRap),
        ("urban",           HipHopRap),
        ("grime",           HipHopRap),

        // -- Ambient / Chillout --
        ("ambient",         AmbientChillout),
        ("chillout",        AmbientChillout),
        ("chill out",       AmbientChillout),
        ("chill-out",       AmbientChillout),
        ("chillwave",       AmbientChillout),
        ("chill",           AmbientChillout),
        ("lounge",          AmbientChillout),
        ("downtempo",       AmbientChillout),
        ("new age",         AmbientChillout),
        ("meditation",      AmbientChillout),
        ("relax",           AmbientChillout),
        ("drone",           AmbientChillout),

        // -- Electronic / Dance --
        ("electronica",     ElectronicDance),
        ("electronic",      ElectronicDance),
        ("edm",             ElectronicDance),
        ("techno",          ElectronicDance),
        ("trance",          ElectronicDance),
        ("house",           ElectronicDance),
        ("drum and bass",   ElectronicDance),
        ("drum & bass",     ElectronicDance),
        ("dnb",             ElectronicDance),
        ("jungle",          ElectronicDance),
        ("dubstep",         ElectronicDance),
        ("breakbeat",       ElectronicDance),
        ("idm",             ElectronicDance),
        ("eurodance",       ElectronicDance),
        ("hardstyle",       ElectronicDance),
        ("synth",           ElectronicDance),
        ("dance",           ElectronicDance),
        ("club",            ElectronicDance),

        // -- Bluegrass (before Country so "bluegrass country" → Bluegrass) --
        ("bluegrass",       Bluegrass),
        ("appalachian",     Bluegrass),

        // -- Country (folk/americana fold in here - closest bucket in the taxonomy) --
        ("country",         Country),
        ("western",         Country),
        ("honky tonk",      Country),
        ("honky-tonk",      Country),
        ("americana",       Country),
        ("folk",            Country),
        ("singer-songwriter", Country),
        ("singer songwriter", Country),
        ("acoustic",        Country),

        // -- Jazz / Blues (one bucket since July 2026) --
        ("smooth jazz",     JazzBlues),
        ("big band",        JazzBlues),
        ("bebop",           JazzBlues),
        ("swing",           JazzBlues),
        ("dixieland",       JazzBlues),
        ("fusion",          JazzBlues),
        ("jazz",            JazzBlues),
        ("delta blues",     JazzBlues),
        ("chicago blues",   JazzBlues),
        ("blues",           JazzBlues),

        // -- Classical --
        ("classical",       Classical),
        ("opera",           Classical),
        ("symphony",        Classical),
        ("symphonic",       Classical),
        ("baroque",         Classical),
        ("orchestra",       Classical),
        ("orchestral",      Classical),
        ("chamber",         Classical),

        // -- Reggae --
        ("reggae",          Reggae),
        ("dub",             Reggae),
        ("ska",             Reggae),
        ("dancehall",       Reggae),
        ("rocksteady",      Reggae),
        ("ragga",           Reggae),
        ("calypso",         Reggae),

        // -- Latin (before World so Latin America doesn't drown in "international") --
        ("latin",           Latin),
        ("salsa",           Latin),
        ("bachata",         Latin),
        ("merengue",        Latin),
        ("cumbia",          Latin),
        ("reggaeton",       Latin),
        ("flamenco",        Latin),
        ("tango",           Latin),
        ("mariachi",        Latin),
        ("bossa nova",      Latin),
        ("bossa",           Latin),
        ("tejano",          Latin),
        ("brazilian",       Latin),
        ("spanish",         Latin),

        // -- World --
        ("world music",     World),
        ("world",           World),
        ("african",         World),
        ("afrobeat",        World),
        ("afro",            World),
        ("asian",           World),
        ("indian",          World),
        ("bollywood",       World),
        ("arabic",          World),
        ("middle east",     World),
        ("celtic",          World),
        ("irish",           World),
        ("scottish",        World),
        ("j-pop",           World),
        ("jpop",            World),
        ("k-pop",           World),
        ("kpop",            World),
        ("c-pop",           World),
        ("french",          World),
        ("german",          World),
        ("italian",         World),
        ("portuguese",      World),
        ("russian",         World),
        ("polish",          World),
        ("greek",           World),
        ("turkish",         World),
        ("japanese",        World),
        ("korean",          World),
        ("chinese",         World),
        ("mandarin",        World),
        ("cantonese",       World),
        ("ethnic",          World),
        ("traditional",     World),

        // -- Top 40 / Pop (chart radio; AFTER World so "j-pop"/"k-pop" stay World, and the
        //    whole-word matcher keeps "pop" out of "synthpop" which the 80s rules own) --
        ("top 40",          Top40Pop),
        ("top40",           Top40Pop),
        ("chr",             Top40Pop),          // Contemporary Hit Radio, the industry term
        ("contemporary hit", Top40Pop),
        ("adult contemporary", Top40Pop),
        ("hot ac",          Top40Pop),
        ("hit music",       Top40Pop),
        ("hits",            Top40Pop),
        ("pop",             Top40Pop),

        // -- Rock (fallback - anything still containing "rock" reads as rock radio) --
        ("rock",            Rock),

        // Deliberately unmapped: "eclectic", "variety" - genuinely genreless, so they fall
        // through to Other and get bucketed by hand during curation.
    ];

    /// <summary>
    /// Maps a single tag string (e.g., "classic rock") to a canonical bucket.
    /// Matching is case-insensitive. Multi-word needles (containing a space) use substring
    /// match; single-word needles require whole-word boundaries so "emo" does NOT match
    /// "ceremonial" and "dance" does NOT match "dancehall".
    /// </summary>
    public static string Normalize(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return Other;
        }

        var lower = tag.ToLowerInvariant();
        foreach (var (needle, canonical) in Rules)
        {
            if (needle.Contains(' '))
            {
                if (lower.Contains(needle, StringComparison.Ordinal))
                {
                    return canonical;
                }
            }
            else if (ContainsWholeWord(lower, needle))
            {
                return canonical;
            }
        }

        return Other;
    }

    /// <summary>
    /// Returns true if <paramref name="haystack"/> contains <paramref name="needle"/> bounded
    /// by non-alphanumeric characters (or string boundaries). Prevents short keywords like
    /// "emo" from matching inside longer words like "ceremonial".
    /// </summary>
    private static bool ContainsWholeWord(string haystack, string needle)
    {
        int idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            int end = idx + needle.Length;
            bool rightOk = end == haystack.Length || !char.IsLetterOrDigit(haystack[end]);
            if (leftOk && rightOk)
            {
                return true;
            }
            idx = end;
        }
        return false;
    }

    /// <summary>
    /// Given a comma-separated tag string (the common Radio Browser format), returns the
    /// first canonical bucket any of the individual tags resolves to. Returns "Other" if
    /// none match.
    /// </summary>
    public static string ExtractPrimaryGenre(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags))
        {
            return Other;
        }

        // Radio Browser uses commas; some sources use "/" or "|"
        var parts = tags.Split([',', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var part in parts)
        {
            var result = Normalize(part);
            if (result != Other)
            {
                return result;
            }
        }

        // Also try the whole string in case there's a multi-word tag like "hip hop dance"
        var whole = Normalize(tags);
        return whole;
    }
}
