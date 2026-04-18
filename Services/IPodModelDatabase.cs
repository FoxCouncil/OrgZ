// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// iPod model lookup tables ported from libgpod's itdb_device.c. libgpod uses a two-step
/// identification: the last 3 characters of the USB serial number map to a 4-character
/// Apple model code, which in turn maps to a generation + color + capacity record. This
/// is the same algorithm iTunes and Amarok use and it works even when /iPod_Control/Device/SysInfo
/// is empty or missing, because the serial is burned into the device's USB descriptor
/// (readable via Win32_DiskDrive.SerialNumber on Windows).
///
/// Tables extracted from https://github.com/gtkpod/libgpod/blob/master/src/itdb_device.c
/// which itself cites the podsleuth project.
/// </summary>
public static class IPodModelDatabase
{
    public record IPodInfo(string Generation, string Color, int CapacityGb)
    {
        public string DisplayName
        {
            get
            {
                var cap = CapacityGb > 0 ? $" {CapacityGb}GB" : "";
                var color = string.IsNullOrEmpty(Color) ? "" : $" {Color}";
                return $"iPod {Generation}{cap}{color}".Trim();
            }
        }

        /// <summary>
        /// Builds a display string reflecting whether the physical drive has been swapped
        /// for something larger than factory. Format is always
        /// <c>iPod {Generation} {Color} ({CapacityOrModded})</c> so the parenthetical
        /// trails consistently regardless of mod state. Apple uses decimal GB (10^9),
        /// so an 80GB iPod reports ~74.5 GiB — we compare with 20% tolerance so normal
        /// rounding doesn't trigger false "Modded" tags. SSD/CF swaps are typically 2×–8×.
        /// </summary>
        public string DisplayNameForActualCapacity(long actualBytes)
        {
            if (CapacityGb <= 0 || actualBytes <= 0)
            {
                return DisplayName;
            }

            long originalBytes = (long)CapacityGb * 1_000_000_000L;
            bool isModded = actualBytes > originalBytes * 12 / 10;

            var tag = isModded ? "Modded" : $"{CapacityGb}GB";
            var color = string.IsNullOrEmpty(Color) ? "" : $" {Color}";
            return $"iPod {Generation}{color} ({tag})".Trim();
        }
    }

    /// <summary>
    /// Looks up an iPod by the last 3 characters of its USB serial number. Returns null
    /// if the suffix isn't in the libgpod table (unknown or non-iPod device).
    /// </summary>
    public static IPodInfo? LookupBySerial(string serial)
    {
        if (string.IsNullOrWhiteSpace(serial) || serial.Length < 3)
        {
            return null;
        }

        var suffix = serial[^3..].ToUpperInvariant();
        if (!SerialSuffixToModelCode.TryGetValue(suffix, out var modelCode))
        {
            return null;
        }

        return ModelCodeToInfo.TryGetValue(modelCode, out var info) ? info : null;
    }

    /// <summary>
    /// Looks up by Apple model number (as stored in SysInfo's ModelNumStr field, or the
    /// first VPD page of a SCSI INQUIRY). libgpod strips a single leading letter if
    /// alphabetic: "MA446" → "A446", "xA446" → "A446" (already stripped), etc.
    /// </summary>
    public static IPodInfo? LookupByModelNumber(string modelNumStr)
    {
        if (string.IsNullOrWhiteSpace(modelNumStr))
        {
            return null;
        }

        var code = modelNumStr.Trim().ToUpperInvariant();

        // Strip region suffix after /
        var slash = code.IndexOf('/');
        if (slash > 0) code = code[..slash];

        // libgpod: strip first letter if alphabetic (M in MA446 → A446)
        if (code.Length > 0 && char.IsLetter(code[0]))
        {
            code = code[1..];
        }

        // After stripping, the core code is 4 chars (e.g., "A446")
        if (code.Length >= 4)
        {
            code = code[..4];
        }

        return ModelCodeToInfo.TryGetValue(code, out var info) ? info : null;
    }

    // Model-code → IPodInfo. Ported verbatim from libgpod's ipod_info_table[].
    private static readonly Dictionary<string, IPodInfo> ModelCodeToInfo = new(StringComparer.OrdinalIgnoreCase)
    {
        // 1st gen (mechanical scroll wheel)
        ["8513"] = new("1G", "", 5),
        ["8541"] = new("1G", "", 5),
        ["8697"] = new("1G", "", 5),
        ["8709"] = new("1G", "", 10),
        // 2nd gen (touch wheel)
        ["8737"] = new("2G", "", 10),
        ["8740"] = new("2G", "", 10),
        ["8738"] = new("2G", "", 20),
        ["8741"] = new("2G", "", 20),
        // 3rd gen (dock connector)
        ["8976"] = new("3G", "", 10),
        ["8946"] = new("3G", "", 15),
        ["9460"] = new("3G", "", 15),
        ["9244"] = new("3G", "", 20),
        ["8948"] = new("3G", "", 30),
        ["9245"] = new("3G", "", 40),
        // 4th gen (click wheel)
        ["9282"] = new("4G", "", 20),
        ["9787"] = new("4G", "U2 25GB", 25),
        ["9268"] = new("4G", "", 40),
        ["E436"] = new("4G", "", 40), // HP branded
        // Mini 1G
        ["9160"] = new("Mini 1G", "Silver", 4),
        ["9436"] = new("Mini 1G", "Blue", 4),
        ["9435"] = new("Mini 1G", "Pink", 4),
        ["9434"] = new("Mini 1G", "Green", 4),
        ["9437"] = new("Mini 1G", "Gold", 4),
        // Mini 2G
        ["9800"] = new("Mini 2G", "Silver", 4),
        ["9802"] = new("Mini 2G", "Blue", 4),
        ["9804"] = new("Mini 2G", "Pink", 4),
        ["9806"] = new("Mini 2G", "Green", 4),
        ["9801"] = new("Mini 2G", "Silver", 6),
        ["9803"] = new("Mini 2G", "Blue", 6),
        ["9805"] = new("Mini 2G", "Pink", 6),
        ["9807"] = new("Mini 2G", "Green", 6),
        // Photo / 4th gen color
        ["A079"] = new("Photo", "", 20),
        ["A127"] = new("Photo", "U2", 20),
        ["9829"] = new("Photo", "", 30),
        ["9585"] = new("Photo", "", 40),
        ["9830"] = new("Photo", "", 60),
        ["9586"] = new("Photo", "", 60),
        ["S492"] = new("Photo", "", 30), // HP branded
        // Shuffle 1G
        ["9724"] = new("Shuffle 1G", "", 0), // 512MB
        ["9725"] = new("Shuffle 1G", "", 1),
        // Shuffle 2G
        ["A546"] = new("Shuffle 2G", "Silver", 1),
        ["A947"] = new("Shuffle 2G", "Pink", 1),
        ["A949"] = new("Shuffle 2G", "Blue", 1),
        ["A951"] = new("Shuffle 2G", "Green", 1),
        ["A953"] = new("Shuffle 2G", "Orange", 1),
        ["C167"] = new("Shuffle 2G", "Gold", 1),
        ["B225"] = new("Shuffle 2G", "Silver", 1),
        ["B233"] = new("Shuffle 2G", "Purple", 1),
        ["B231"] = new("Shuffle 2G", "Red", 1),
        ["B227"] = new("Shuffle 2G", "Blue", 1),
        ["B228"] = new("Shuffle 2G", "Blue", 1),
        ["B229"] = new("Shuffle 2G", "Green", 1),
        ["B518"] = new("Shuffle 2G", "Silver", 2),
        ["B520"] = new("Shuffle 2G", "Blue", 2),
        ["B522"] = new("Shuffle 2G", "Green", 2),
        ["B524"] = new("Shuffle 2G", "Red", 2),
        ["B526"] = new("Shuffle 2G", "Purple", 2),
        // Shuffle 3G
        ["C306"] = new("Shuffle 3G", "Silver", 2),
        ["C323"] = new("Shuffle 3G", "Black", 2),
        ["C381"] = new("Shuffle 3G", "Green", 2),
        ["C384"] = new("Shuffle 3G", "Blue", 2),
        ["C387"] = new("Shuffle 3G", "Pink", 2),
        ["B867"] = new("Shuffle 3G", "Silver", 4),
        ["C164"] = new("Shuffle 3G", "Black", 4),
        ["C303"] = new("Shuffle 3G", "Stainless", 4),
        ["C307"] = new("Shuffle 3G", "Green", 4),
        ["C328"] = new("Shuffle 3G", "Blue", 4),
        ["C331"] = new("Shuffle 3G", "Pink", 4),
        // Shuffle 4G
        ["C584"] = new("Shuffle 4G", "Silver", 2),
        ["C585"] = new("Shuffle 4G", "Pink", 2),
        ["C749"] = new("Shuffle 4G", "Orange", 2),
        ["C750"] = new("Shuffle 4G", "Green", 2),
        ["C751"] = new("Shuffle 4G", "Blue", 2),
        // Nano 1G
        ["A350"] = new("Nano 1G", "White", 1),
        ["A352"] = new("Nano 1G", "Black", 1),
        ["A004"] = new("Nano 1G", "White", 2),
        ["A099"] = new("Nano 1G", "Black", 2),
        ["A005"] = new("Nano 1G", "White", 4),
        ["A107"] = new("Nano 1G", "Black", 4),
        // 5G Video
        ["A002"] = new("Video 5G", "White", 30),
        ["A146"] = new("Video 5G", "Black", 30),
        ["A003"] = new("Video 5G", "White", 60),
        ["A147"] = new("Video 5G", "Black", 60),
        ["A452"] = new("Video 5G", "U2", 30),
        // 5.5G Video (enhanced)
        ["A444"] = new("Video 5.5G", "White", 30),
        ["A446"] = new("Video 5.5G", "Black", 30),
        ["A664"] = new("Video 5.5G", "U2", 30),
        ["A448"] = new("Video 5.5G", "White", 80),
        ["A450"] = new("Video 5.5G", "Black", 80),
        // Nano 2G (aluminum)
        ["A477"] = new("Nano 2G", "Silver", 2),
        ["A426"] = new("Nano 2G", "Silver", 4),
        ["A428"] = new("Nano 2G", "Blue", 4),
        ["A487"] = new("Nano 2G", "Green", 4),
        ["A489"] = new("Nano 2G", "Pink", 4),
        ["A725"] = new("Nano 2G", "Red", 4),
        ["A726"] = new("Nano 2G", "Red", 8),
        ["A497"] = new("Nano 2G", "Black", 8),
        // Classic 1G (6G, 2007)
        ["B029"] = new("Classic 6G", "Silver", 80),
        ["B147"] = new("Classic 6G", "Black", 80),
        ["B145"] = new("Classic 6G", "Silver", 160),
        ["B150"] = new("Classic 6G", "Black", 160),
        // Classic 2G (6.5G, 2008)
        ["B562"] = new("Classic 6.5G", "Silver", 120),
        ["B565"] = new("Classic 6.5G", "Black", 120),
        // Classic 3G (7G, 2009)
        ["C293"] = new("Classic 7G", "Silver", 160),
        ["C297"] = new("Classic 7G", "Black", 160),
        // Nano 3G (fat / video)
        ["A978"] = new("Nano 3G", "Silver", 4),
        ["A980"] = new("Nano 3G", "Silver", 8),
        ["B261"] = new("Nano 3G", "Black", 8),
        ["B249"] = new("Nano 3G", "Blue", 8),
        ["B253"] = new("Nano 3G", "Green", 8),
        ["B257"] = new("Nano 3G", "Red", 8),
        // Nano 4G
        ["B480"] = new("Nano 4G", "Silver", 4),
        ["B651"] = new("Nano 4G", "Blue", 4),
        ["B654"] = new("Nano 4G", "Pink", 4),
        ["B657"] = new("Nano 4G", "Purple", 4),
        ["B660"] = new("Nano 4G", "Orange", 4),
        ["B663"] = new("Nano 4G", "Green", 4),
        ["B666"] = new("Nano 4G", "Yellow", 4),
        ["B598"] = new("Nano 4G", "Silver", 8),
        ["B732"] = new("Nano 4G", "Blue", 8),
        ["B735"] = new("Nano 4G", "Pink", 8),
        ["B739"] = new("Nano 4G", "Purple", 8),
        ["B742"] = new("Nano 4G", "Orange", 8),
        ["B745"] = new("Nano 4G", "Green", 8),
        ["B748"] = new("Nano 4G", "Yellow", 8),
        ["B751"] = new("Nano 4G", "Red", 8),
        ["B754"] = new("Nano 4G", "Black", 8),
        ["B903"] = new("Nano 4G", "Silver", 16),
        ["B905"] = new("Nano 4G", "Blue", 16),
        ["B907"] = new("Nano 4G", "Pink", 16),
        ["B909"] = new("Nano 4G", "Purple", 16),
        ["B911"] = new("Nano 4G", "Orange", 16),
        ["B913"] = new("Nano 4G", "Green", 16),
        ["B915"] = new("Nano 4G", "Yellow", 16),
        ["B917"] = new("Nano 4G", "Red", 16),
        ["B918"] = new("Nano 4G", "Black", 16),
        // Nano 5G (camera)
        ["C027"] = new("Nano 5G", "Silver", 8),
        ["C031"] = new("Nano 5G", "Black", 8),
        ["C034"] = new("Nano 5G", "Purple", 8),
        ["C037"] = new("Nano 5G", "Blue", 8),
        ["C040"] = new("Nano 5G", "Green", 8),
        ["C043"] = new("Nano 5G", "Yellow", 8),
        ["C046"] = new("Nano 5G", "Orange", 8),
        ["C049"] = new("Nano 5G", "Red", 8),
        ["C050"] = new("Nano 5G", "Pink", 8),
        ["C060"] = new("Nano 5G", "Silver", 16),
        ["C062"] = new("Nano 5G", "Black", 16),
        ["C064"] = new("Nano 5G", "Purple", 16),
        ["C066"] = new("Nano 5G", "Blue", 16),
        ["C068"] = new("Nano 5G", "Green", 16),
        ["C070"] = new("Nano 5G", "Yellow", 16),
        ["C072"] = new("Nano 5G", "Orange", 16),
        ["C074"] = new("Nano 5G", "Red", 16),
        ["C075"] = new("Nano 5G", "Pink", 16),
        // Nano 6G (touch)
        ["C525"] = new("Nano 6G", "Silver", 8),
        ["C688"] = new("Nano 6G", "Black", 8),
        ["C689"] = new("Nano 6G", "Blue", 8),
        ["C690"] = new("Nano 6G", "Green", 8),
        ["C691"] = new("Nano 6G", "Orange", 8),
        ["C692"] = new("Nano 6G", "Pink", 8),
        ["C693"] = new("Nano 6G", "Red", 8),
        ["C526"] = new("Nano 6G", "Silver", 16),
        ["C694"] = new("Nano 6G", "Black", 16),
        ["C695"] = new("Nano 6G", "Blue", 16),
        ["C696"] = new("Nano 6G", "Green", 16),
        ["C697"] = new("Nano 6G", "Orange", 16),
        ["C698"] = new("Nano 6G", "Pink", 16),
        ["C699"] = new("Nano 6G", "Red", 16),
        // Touch 1G / 2G / 3G / 4G
        ["A623"] = new("Touch 1G", "Silver", 8),
        ["A627"] = new("Touch 1G", "Silver", 16),
        ["B376"] = new("Touch 1G", "Silver", 32),
        ["B528"] = new("Touch 2G", "Silver", 8),
        ["B531"] = new("Touch 2G", "Silver", 16),
        ["B533"] = new("Touch 2G", "Silver", 32),
        ["C086"] = new("Touch 2G", "Silver", 8),
        ["C008"] = new("Touch 3G", "Silver", 32),
        ["C011"] = new("Touch 3G", "Silver", 64),
        ["C540"] = new("Touch 4G", "Silver", 8),
        ["C544"] = new("Touch 4G", "Silver", 32),
        ["C547"] = new("Touch 4G", "Silver", 64),
    };

    // Serial-suffix → model code. Ported verbatim from libgpod's serial_to_model_mapping[].
    // Last 3 characters of the iPod's serial number uniquely identify the model.
    private static readonly Dictionary<string, string> SerialSuffixToModelCode = new(StringComparer.OrdinalIgnoreCase)
    {
        ["LG6"] = "8541", ["NAM"] = "8541", ["MJ2"] = "8541", ["ML1"] = "8709",
        ["MME"] = "8709", ["MMB"] = "8737", ["MMC"] = "8738", ["NGE"] = "8740",
        ["NGH"] = "8740", ["MMF"] = "8741", ["NLW"] = "8946", ["NRH"] = "8976",
        ["QQF"] = "9460", ["PQ5"] = "9244", ["PNT"] = "9244", ["NLY"] = "8948",
        ["NM7"] = "8948", ["PNU"] = "9245", ["PS9"] = "9282", ["Q8U"] = "9282",
        ["V9V"] = "9787", ["S2X"] = "9787", ["PQ7"] = "9268",
        ["TDU"] = "A079", ["TDS"] = "A079", ["TM2"] = "A127",
        ["SAZ"] = "9830", ["SB1"] = "9830", ["SAY"] = "9829",
        ["R5Q"] = "9585", ["R5R"] = "9586", ["R5T"] = "9586",
        ["PFW"] = "9160", ["PRC"] = "9160",
        ["QKL"] = "9436", ["QKQ"] = "9436", ["QKK"] = "9435", ["QKP"] = "9435",
        ["QKJ"] = "9434", ["QKN"] = "9434", ["QKM"] = "9437", ["QKR"] = "9437",
        ["S41"] = "9800", ["S4C"] = "9800", ["S43"] = "9802", ["S45"] = "9804",
        ["S47"] = "9806", ["S4J"] = "9806", ["S42"] = "9801", ["S44"] = "9803",
        ["S48"] = "9807",
        ["RS9"] = "9724", ["QGV"] = "9724", ["TSX"] = "9724", ["PFV"] = "9724",
        ["R80"] = "9724", ["RSA"] = "9725", ["TSY"] = "9725", ["C60"] = "9725",
        ["VTE"] = "A546", ["VTF"] = "A546",
        ["XQ5"] = "A947", ["XQS"] = "A947", ["XQV"] = "A949", ["XQX"] = "A949",
        ["XQY"] = "A951", ["YX8"] = "A951", ["XR1"] = "A953",
        ["YXA"] = "B233", ["YX6"] = "B225", ["YX7"] = "B228", ["YX9"] = "B225",
        ["8CQ"] = "C167", ["1ZH"] = "B518",
        ["UNA"] = "A350", ["UNB"] = "A350", ["UPR"] = "A352", ["UPS"] = "A352",
        ["SZB"] = "A004", ["SZV"] = "A004", ["SZW"] = "A004",
        ["SZC"] = "A005", ["SZT"] = "A005",
        ["TJT"] = "A099", ["TJU"] = "A099", ["TK2"] = "A107", ["TK3"] = "A107",
        ["VQ5"] = "A477", ["VQ6"] = "A477",
        ["V8T"] = "A426", ["V8U"] = "A426", ["V8W"] = "A428", ["V8X"] = "A428",
        ["VQH"] = "A487", ["VQJ"] = "A487", ["VQK"] = "A489", ["VKL"] = "A489",
        ["WL2"] = "A725", ["WL3"] = "A725", ["X9A"] = "A726", ["X9B"] = "A726",
        ["VQT"] = "A497", ["VQU"] = "A497",
        ["Y0P"] = "A978", ["Y0R"] = "A980", ["YXR"] = "B249", ["YXV"] = "B257",
        ["YXT"] = "B253", ["YXX"] = "B261",
        ["SZ9"] = "A002", ["WEC"] = "A002", ["WED"] = "A002", ["WEG"] = "A002",
        ["WEH"] = "A002", ["WEL"] = "A002",
        ["TXK"] = "A146", ["TXM"] = "A146", ["WEF"] = "A146",
        ["WEJ"] = "A146", ["WEK"] = "A146",
        ["SZA"] = "A003", ["SZU"] = "A003",
        ["TXL"] = "A147", ["TXN"] = "A147",
        ["V9K"] = "A444", ["V9L"] = "A444", ["WU9"] = "A444",
        ["VQM"] = "A446", ["V9M"] = "A446", ["V9N"] = "A446", ["WEE"] = "A446",
        ["V9P"] = "A448", ["V9Q"] = "A448",
        ["V9R"] = "A450", ["V9S"] = "A450", ["V95"] = "A450", ["V96"] = "A450", ["WUC"] = "A450",
        ["W9G"] = "A664",
        ["Y5N"] = "B029", ["YMV"] = "B147", ["YMU"] = "B145", ["YMX"] = "B150",
        ["2C5"] = "B562", ["2C7"] = "B565",
        ["9ZS"] = "C293", ["9ZU"] = "C297",
        ["37P"] = "B663", ["37Q"] = "B666", ["37H"] = "B654", ["1P1"] = "B480",
        ["37K"] = "B657", ["37L"] = "B660",
        ["2ME"] = "B598", ["3QS"] = "B732", ["3QT"] = "B735", ["3QU"] = "B739",
        ["3QW"] = "B742", ["3QX"] = "B745", ["3QY"] = "B748", ["3R0"] = "B754",
        ["3QZ"] = "B751",
        ["5B7"] = "B903", ["5B8"] = "B905", ["5B9"] = "B907", ["5BA"] = "B909",
        ["5BB"] = "B911", ["5BC"] = "B913", ["5BD"] = "B915", ["5BE"] = "B917",
        ["5BF"] = "B918",
        ["71V"] = "C027", ["71Y"] = "C031", ["721"] = "C034", ["726"] = "C037",
        ["72A"] = "C040", ["72F"] = "C046", ["72K"] = "C049", ["72L"] = "C050",
        ["72Q"] = "C060", ["72R"] = "C062", ["72S"] = "C064", ["72X"] = "C066",
        ["734"] = "C068", ["738"] = "C070", ["739"] = "C072", ["73A"] = "C074",
        ["73B"] = "C075",
        ["CMN"] = "C525", ["DVX"] = "C688", ["DVY"] = "C689", ["DW0"] = "C690",
        ["DW1"] = "C691", ["DW2"] = "C692", ["DW3"] = "C693",
        ["CMP"] = "C526", ["DW4"] = "C694", ["DW5"] = "C695", ["DW6"] = "C696",
        ["DW7"] = "C697", ["DW8"] = "C698", ["DW9"] = "C699",
        ["A1S"] = "C306", ["A78"] = "C323", ["ALB"] = "C381", ["ALD"] = "C384",
        ["ALG"] = "C387", ["4NZ"] = "B867", ["891"] = "C164", ["A1L"] = "C303",
        ["A1U"] = "C307", ["A7B"] = "C328", ["A7D"] = "C331",
        ["CMJ"] = "C584", ["CMK"] = "C585", ["FDM"] = "C749", ["FDN"] = "C750",
        ["FDP"] = "C751",
        ["W4N"] = "A623", ["W4T"] = "A627", ["0JW"] = "B376",
        ["201"] = "B528", ["203"] = "B531",
        ["75J"] = "C086", ["6K2"] = "C008", ["6K4"] = "C011",
        ["CP7"] = "C540", ["CP9"] = "C544", ["CPC"] = "C547",
    };
}
