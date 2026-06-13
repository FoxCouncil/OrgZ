// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Globalization;

namespace OrgZ.Services.Podcast;

public enum PodcastCheckInterval { Hour, Day, Week, Manual }

public enum PodcastNewEpisodeAction { All, Recent, None }

public enum PodcastKeepPolicy { All, Unplayed, Last1, Last2, Last5, Last10 }

/// <summary>
/// Typed accessor over the raw <c>OrgZ.Podcasts.*</c> settings keys the Settings dialog
/// writes, plus the derived scheduling helpers and the on-disk download location. This is
/// the single place the rest of the app reads the global podcast rules from - the dialog
/// owns writing them, everything else reads them here.
/// </summary>
public static class PodcastSettings
{
    public static PodcastCheckInterval CheckInterval => Settings.Get("OrgZ.Podcasts.CheckInterval", "day") switch
    {
        "hour"   => PodcastCheckInterval.Hour,
        "week"   => PodcastCheckInterval.Week,
        "manual" => PodcastCheckInterval.Manual,
        _        => PodcastCheckInterval.Day,
    };

    public static PodcastNewEpisodeAction NewEpisodeAction => Settings.Get("OrgZ.Podcasts.NewEpisodeAction", "recent") switch
    {
        "all"  => PodcastNewEpisodeAction.All,
        "none" => PodcastNewEpisodeAction.None,
        _      => PodcastNewEpisodeAction.Recent,
    };

    public static PodcastKeepPolicy Keep => Settings.Get("OrgZ.Podcasts.Keep", "all") switch
    {
        "unplayed" => PodcastKeepPolicy.Unplayed,
        "last1"    => PodcastKeepPolicy.Last1,
        "last2"    => PodcastKeepPolicy.Last2,
        "last5"    => PodcastKeepPolicy.Last5,
        "last10"   => PodcastKeepPolicy.Last10,
        _          => PodcastKeepPolicy.All,
    };

    /// <summary>Number of most-recent episodes a "last N" keep policy retains, else null.</summary>
    public static int? KeepCount(PodcastKeepPolicy keep) => keep switch
    {
        PodcastKeepPolicy.Last1  => 1,
        PodcastKeepPolicy.Last2  => 2,
        PodcastKeepPolicy.Last5  => 5,
        PodcastKeepPolicy.Last10 => 10,
        _                        => null,
    };

    // -- Per-feed overrides ------------------------------------------------------------
    // A subscription can override the new-episode action and the keep policy from its own
    // page. Stored as a tag under OrgZ.Podcasts.Feed.{feedId}.{leaf}; empty string means
    // "use the global default". The combo index 0 always maps to that empty/default tag.

    private static readonly string[] ActionTags = ["", "all", "recent", "none"];
    private static readonly string[] KeepTags   = ["", "all", "unplayed", "last1", "last2", "last5", "last10"];

    private static string FeedKey(long feedId, string leaf) => $"OrgZ.Podcasts.Feed.{feedId}.{leaf}";

    /// <summary>Effective new-episode action for a feed: its override, else the global default.</summary>
    public static PodcastNewEpisodeAction NewEpisodeActionFor(long feedId) =>
        Settings.Get(FeedKey(feedId, "NewEpisodeAction"), "") switch
        {
            "all"    => PodcastNewEpisodeAction.All,
            "recent" => PodcastNewEpisodeAction.Recent,
            "none"   => PodcastNewEpisodeAction.None,
            _        => NewEpisodeAction,
        };

    /// <summary>Effective keep policy for a feed: its override, else the global default.</summary>
    public static PodcastKeepPolicy KeepFor(long feedId) =>
        Settings.Get(FeedKey(feedId, "Keep"), "") switch
        {
            "all"      => PodcastKeepPolicy.All,
            "unplayed" => PodcastKeepPolicy.Unplayed,
            "last1"    => PodcastKeepPolicy.Last1,
            "last2"    => PodcastKeepPolicy.Last2,
            "last5"    => PodcastKeepPolicy.Last5,
            "last10"   => PodcastKeepPolicy.Last10,
            _          => Keep,
        };

    public static int FeedActionIndex(long feedId)
    {
        var i = Array.IndexOf(ActionTags, Settings.Get(FeedKey(feedId, "NewEpisodeAction"), ""));
        return i < 0 ? 0 : i;
    }

    public static int FeedKeepIndex(long feedId)
    {
        var i = Array.IndexOf(KeepTags, Settings.Get(FeedKey(feedId, "Keep"), ""));
        return i < 0 ? 0 : i;
    }

    public static void SetFeedActionIndex(long feedId, int index)
    {
        Settings.Set(FeedKey(feedId, "NewEpisodeAction"), index > 0 && index < ActionTags.Length ? ActionTags[index] : "");
        Settings.Save();
    }

    public static void SetFeedKeepIndex(long feedId, int index)
    {
        Settings.Set(FeedKey(feedId, "Keep"), index > 0 && index < KeepTags.Length ? KeepTags[index] : "");
        Settings.Save();
    }

    /// <summary>Refresh cadence, or null when the user picked "Manually".</summary>
    public static TimeSpan? Interval => CheckInterval switch
    {
        PodcastCheckInterval.Hour => TimeSpan.FromHours(1),
        PodcastCheckInterval.Day  => TimeSpan.FromDays(1),
        PodcastCheckInterval.Week => TimeSpan.FromDays(7),
        _                         => null,
    };

    /// <summary>UTC timestamp of the last completed refresh, or null if never checked.</summary>
    public static DateTime? LastCheck
    {
        get
        {
            var raw = Settings.Get("OrgZ.Podcasts.LastCheck", "");
            return DateTime.TryParse(raw, null, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : null;
        }
    }

    /// <summary>Next scheduled check (local time), or null under the "Manually" policy.</summary>
    public static DateTime? NextCheck =>
        Interval is { } iv ? (LastCheck ?? DateTime.UtcNow).Add(iv).ToLocalTime() : null;

    /// <summary>True when a non-manual interval has elapsed since the last check.</summary>
    public static bool IsDueForCheck =>
        Interval is { } iv && (LastCheck is not { } last || DateTime.UtcNow - last >= iv);

    public static void MarkChecked()
    {
        Settings.Set("OrgZ.Podcasts.LastCheck", DateTime.UtcNow.ToString("O"));
        Settings.Save();
    }

    /// <summary>The <c>{libraryRoot}/.podcasts</c> download root, or null if the root is unset.</summary>
    public static string? DownloadDir(string? libraryRoot) =>
        string.IsNullOrWhiteSpace(libraryRoot) ? null : Path.Combine(libraryRoot, ".podcasts");

    /// <summary>Total bytes occupied by downloaded episodes under the download root.</summary>
    public static long DownloadBytes(string? libraryRoot)
    {
        var dir = DownloadDir(libraryRoot);
        if (dir is null || !Directory.Exists(dir))
        {
            return 0;
        }
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { /* racing delete */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Deletes every downloaded episode under the download root. Returns bytes freed.</summary>
    public static long ClearDownloads(string? libraryRoot)
    {
        var dir = DownloadDir(libraryRoot);
        if (dir is null || !Directory.Exists(dir))
        {
            return 0;
        }
        var freed = DownloadBytes(libraryRoot);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                try { Directory.Delete(sub, recursive: true); } catch { }
            }
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch
        {
            // Best-effort; partial clears are fine - the next pass picks up the rest.
        }
        return freed;
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1)
        {
            size /= 1024;
            u++;
        }
        return u == 0 ? $"{bytes} B" : $"{size:0.#} {units[u]}";
    }
}
