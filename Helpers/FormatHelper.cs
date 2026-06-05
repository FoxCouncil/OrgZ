// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Helpers;

internal static class FormatHelper
{
    public static string FormatFileSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return unit == 0 ? $"{size:F0} {units[unit]}" : $"{size:F2} {units[unit]}";
    }

    public static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return ts.ToString(@"h\:mm\:ss\.fff");
        }

        return ts.ToString(@"m\:ss\.fff");
    }

    /// <summary>
    /// LCD-style duration: <c>m:ss</c> when under an hour, <c>h:mm:ss</c> when
    /// equal to or above. Hides the hours segment until needed so music tracks
    /// look natural (3:24) while a 2-hour podcast or audiobook chapter shows
    /// the hours segment without forcing music to a 0:03:24 layout.
    /// </summary>
    public static string FormatDurationCompact(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return ts.ToString(@"h\:mm\:ss");
        }
        return ts.ToString(@"m\:ss");
    }

    public static string FormatDurationCompact(long milliseconds)
        => FormatDurationCompact(TimeSpan.FromMilliseconds(milliseconds));

    public static string FormatDurationCompact(int seconds)
        => FormatDurationCompact(TimeSpan.FromSeconds(seconds));

    public static TimeSpan? TryParseTimeSpan(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // Try parsing common formats: "m:ss", "m:ss.fff", "h:mm:ss", "h:mm:ss.fff"
        if (TimeSpan.TryParse(text.Contains(':') ? (text.Count(c => c == ':') == 1 ? "0:" + text : text) : "0:0:" + text, out var result))
        {
            return result;
        }

        return null;
    }

    public static string FormatDateWithRelative(DateTime? date)
    {
        if (!date.HasValue)
        {
            return "-";
        }

        return $"{date.Value.ToLocalTime():g} ({FormatTimeAgo(DateTime.UtcNow - date.Value)})";
    }

    public static string FormatTimeAgo(TimeSpan ago)
    {
        if (ago.TotalMinutes < 1)
        {
            return "just now";
        }

        if (ago.TotalHours < 1)
        {
            return $"{(int)ago.TotalMinutes}m ago";
        }

        if (ago.TotalDays < 1)
        {
            return $"{(int)ago.TotalHours}h ago";
        }

        if (ago.TotalDays < 30)
        {
            return $"{(int)ago.TotalDays}d ago";
        }

        if (ago.TotalDays < 365)
        {
            return $"{(int)(ago.TotalDays / 30)}mo ago";
        }

        return $"{(int)(ago.TotalDays / 365)}y ago";
    }
}
