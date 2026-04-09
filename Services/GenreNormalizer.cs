// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Maps provider-specific radio genre tags (Radio Browser, user-added, etc.) into a
/// canonical iTunes-style taxonomy. The canonical set is fixed and language-neutral;
/// raw tags are matched case-insensitively via substring against an ordered rule list
/// where the most specific rules appear first.
/// </summary>
public static class GenreNormalizer
{
    // -- Canonical genre buckets (iTunes 2000s radio tuner style) --

    public const string FiftiesSixtiesPop  = "50s/60s Pop";
    public const string SeventiesEightiesPop = "70s/80s Pop";
    public const string AltModernRock      = "Alt/Modern Rock";
    public const string Ambient            = "Ambient";
    public const string Americana          = "Americana";
    public const string Blues              = "Blues";
    public const string ClassicRock        = "Classic Rock";
    public const string Classical          = "Classical";
    public const string Country            = "Country";
    public const string Eclectic           = "Eclectic";
    public const string Electronica        = "Electronica";
    public const string HardRockMetal      = "Hard Rock / Metal";
    public const string International      = "International";
    public const string Jazz               = "Jazz";
    public const string Public             = "Public";
    public const string ReggaeIsland       = "Reggae/Island";
    public const string Religious          = "Religious";
    public const string Sports             = "Sports";
    public const string TalkSpokenWord     = "Talk/Spoken Word";
    public const string Top40Pop           = "Top 40/Pop";
    public const string Urban              = "Urban";
    public const string Other              = "Other";

    /// <summary>
    /// All canonical buckets, in the order they should appear in UI lists.
    /// "Other" lives at the bottom.
    /// </summary>
    public static readonly IReadOnlyList<string> AllCanonical =
    [
        FiftiesSixtiesPop,
        SeventiesEightiesPop,
        AltModernRock,
        Ambient,
        Americana,
        Blues,
        ClassicRock,
        Classical,
        Country,
        Eclectic,
        Electronica,
        HardRockMetal,
        International,
        Jazz,
        Public,
        ReggaeIsland,
        Religious,
        Sports,
        TalkSpokenWord,
        Top40Pop,
        Urban,
        Other,
    ];

    /// <summary>
    /// Ordered match rules. Each rule: if the lowercased raw string contains the keyword
    /// (as a substring), the canonical bucket is returned. Order is load-bearing: more
    /// specific rules must come first so "christian rock" → Religious (not Rock).
    /// </summary>
    private static readonly (string Needle, string Canonical)[] Rules =
    [
        // -- Religious (must come before Rock/Pop so "christian rock" → Religious) --
        ("christian",       Religious),
        ("gospel",          Religious),
        ("religious",       Religious),
        ("worship",         Religious),
        ("praise",          Religious),
        ("catholic",        Religious),
        ("spiritual",       Religious),
        ("sermon",          Religious),
        ("bible",           Religious),

        // -- Public radio (before Talk so "npr" isn't just generic talk) --
        ("npr",             Public),
        ("public radio",    Public),
        ("pbs",             Public),

        // -- Talk / Spoken Word (before Pop so "pop culture talk" → Talk) --
        ("talk",            TalkSpokenWord),
        ("news",            TalkSpokenWord),
        ("spoken",          TalkSpokenWord),
        ("politics",        TalkSpokenWord),
        ("audiobook",       TalkSpokenWord),
        ("podcast",         TalkSpokenWord),
        ("comedy",          TalkSpokenWord),
        ("humor",           TalkSpokenWord),

        // -- Sports --
        ("sports",          Sports),
        ("football",        Sports),
        ("soccer",          Sports),
        ("baseball",        Sports),
        ("basketball",      Sports),
        ("hockey",          Sports),
        ("nfl",             Sports),
        ("nba",             Sports),
        ("mlb",             Sports),

        // -- Hard Rock / Metal (before "rock") --
        ("death metal",     HardRockMetal),
        ("black metal",     HardRockMetal),
        ("heavy metal",     HardRockMetal),
        ("thrash",          HardRockMetal),
        ("power metal",     HardRockMetal),
        ("metal",           HardRockMetal),
        ("hard rock",       HardRockMetal),

        // -- Alt/Modern Rock (before "rock") --
        ("alternative",     AltModernRock),
        ("alt rock",        AltModernRock),
        ("alt-rock",        AltModernRock),
        ("modern rock",     AltModernRock),
        ("indie rock",      AltModernRock),
        ("indie pop",       AltModernRock),
        ("indie",           AltModernRock),
        ("grunge",          AltModernRock),
        ("post-rock",       AltModernRock),
        ("post-punk",       AltModernRock),
        ("pop punk",        AltModernRock),
        ("punk",            AltModernRock),
        ("emo",             AltModernRock),
        ("shoegaze",        AltModernRock),

        // -- Classic Rock (before generic "rock" — before Top 40/Pop too) --
        ("classic rock",    ClassicRock),
        ("album rock",      ClassicRock),
        ("prog rock",       ClassicRock),
        ("progressive rock", ClassicRock),
        ("southern rock",   ClassicRock),

        // -- Decades (era buckets) --
        ("50s",             FiftiesSixtiesPop),
        ("fifties",         FiftiesSixtiesPop),
        ("60s",             FiftiesSixtiesPop),
        ("sixties",         FiftiesSixtiesPop),
        ("doo-wop",         FiftiesSixtiesPop),
        ("doowop",          FiftiesSixtiesPop),
        ("oldies",          FiftiesSixtiesPop),
        ("70s",             SeventiesEightiesPop),
        ("seventies",       SeventiesEightiesPop),
        ("80s",             SeventiesEightiesPop),
        ("eighties",        SeventiesEightiesPop),
        ("new wave",        SeventiesEightiesPop),
        ("synthpop",        SeventiesEightiesPop),
        ("synth-pop",       SeventiesEightiesPop),
        ("disco",           SeventiesEightiesPop),

        // -- Urban (hip-hop, r&b, soul) --
        ("hip-hop",         Urban),
        ("hip hop",         Urban),
        ("hiphop",          Urban),
        ("rap",             Urban),
        ("r&b",             Urban),
        ("rnb",             Urban),
        ("rhythm and blues", Urban),
        ("soul",            Urban),
        ("funk",            Urban),
        ("urban",           Urban),
        ("trap",            Urban),
        ("motown",          Urban),

        // -- Ambient --
        ("ambient",         Ambient),
        ("chillout",        Ambient),
        ("chill out",       Ambient),
        ("chill-out",       Ambient),
        ("chill",           Ambient),
        ("lounge",          Ambient),
        ("downtempo",       Ambient),
        ("new age",         Ambient),
        ("meditation",      Ambient),
        ("relax",           Ambient),

        // -- Electronica (before Dance catch-all) --
        ("electronica",     Electronica),
        ("electronic",      Electronica),
        ("edm",             Electronica),
        ("techno",          Electronica),
        ("trance",          Electronica),
        ("house",           Electronica),
        ("drum and bass",   Electronica),
        ("drum & bass",     Electronica),
        ("dnb",             Electronica),
        ("dubstep",         Electronica),
        ("breakbeat",       Electronica),
        ("idm",             Electronica),
        ("synth",           Electronica),
        ("dance",           Electronica),
        ("club",            Electronica),

        // -- Country --
        ("country",         Country),
        ("western",         Country),
        ("outlaw country",  Country),
        ("honky tonk",      Country),

        // -- Americana (folk, bluegrass, roots) --
        ("americana",       Americana),
        ("bluegrass",       Americana),
        ("folk rock",       Americana),
        ("folk",            Americana),
        ("acoustic",        Americana),
        ("singer-songwriter", Americana),
        ("singer songwriter", Americana),
        ("roots",           Americana),
        ("appalachian",     Americana),

        // -- Jazz (before "blues" so "blues jazz" → Jazz first) --
        ("smooth jazz",     Jazz),
        ("big band",        Jazz),
        ("bebop",           Jazz),
        ("swing",           Jazz),
        ("dixieland",       Jazz),
        ("fusion",          Jazz),
        ("jazz",            Jazz),

        // -- Blues --
        ("delta blues",     Blues),
        ("chicago blues",   Blues),
        ("blues",           Blues),

        // -- Classical --
        ("classical",       Classical),
        ("opera",           Classical),
        ("symphony",        Classical),
        ("symphonic",       Classical),
        ("baroque",         Classical),
        ("orchestra",       Classical),
        ("orchestral",      Classical),
        ("chamber",         Classical),

        // -- Reggae/Island (before International so "reggae" isn't "world music") --
        ("reggae",          ReggaeIsland),
        ("dub",             ReggaeIsland),
        ("ska",             ReggaeIsland),
        ("dancehall",       ReggaeIsland),
        ("rocksteady",      ReggaeIsland),
        ("ragga",           ReggaeIsland),
        ("island",          ReggaeIsland),
        ("tropical",        ReggaeIsland),
        ("calypso",         ReggaeIsland),

        // -- International --
        ("latin",           International),
        ("salsa",           International),
        ("bachata",         International),
        ("merengue",        International),
        ("cumbia",          International),
        ("reggaeton",       International),
        ("flamenco",        International),
        ("tango",           International),
        ("mariachi",        International),
        ("world music",     International),
        ("world",           International),
        ("african",         International),
        ("afrobeat",        International),
        ("afro",            International),
        ("asian",           International),
        ("indian",          International),
        ("bollywood",       International),
        ("arabic",          International),
        ("middle east",     International),
        ("celtic",          International),
        ("irish",           International),
        ("scottish",        International),
        ("j-pop",           International),
        ("k-pop",           International),
        ("c-pop",           International),
        ("french",          International),
        ("german",          International),
        ("italian",         International),
        ("spanish",         International),
        ("portuguese",      International),
        ("brazilian",       International),
        ("russian",         International),
        ("polish",          International),
        ("greek",           International),
        ("turkish",         International),
        ("japanese",        International),
        ("korean",          International),
        ("chinese",         International),
        ("mandarin",        International),
        ("cantonese",       International),
        ("ethnic",          International),
        ("traditional",     International),

        // -- Top 40 / Pop --
        ("top 40",          Top40Pop),
        ("top40",           Top40Pop),
        ("adult contemporary", Top40Pop),
        ("hot ac",          Top40Pop),
        ("chart",           Top40Pop),
        ("hits",            Top40Pop),
        ("pop",             Top40Pop),
        ("contemporary",    Top40Pop),

        // -- Eclectic (explicit "mixed" marker) --
        ("eclectic",        Eclectic),
        ("variety",         Eclectic),
        ("various",         Eclectic),
        ("mixed",           Eclectic),

        // -- Rock (fallback — anything still containing "rock") --
        ("rock",            ClassicRock),
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

        // Radio Browser uses commas; some sources use "/" or "&"
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
