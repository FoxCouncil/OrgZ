// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Models;

namespace OrgZ.Helpers;

/// <summary>
/// Pure sort helper for album track ordering. Extracted so <c>ApplyDrillDownSongsFilter</c>
/// is unit-testable without needing a full MainWindowViewModel harness.
/// </summary>
internal static class AlbumTrackSort
{
    /// <summary>
    /// Returns tracks ordered by (Disc, Track, Title). Missing Disc defaults to 1 so
    /// single-disc albums stay in track order. Missing Track sorts to the end so
    /// unnumbered bonus files don't push numbered tracks around. Title is the final
    /// tiebreaker in case two tracks share disc+track numbers (unlikely but defensive).
    /// </summary>
    public static IEnumerable<MediaItem> Order(IEnumerable<MediaItem> tracks)
        => tracks
            .OrderBy(i => i.Disc ?? 1)
            .ThenBy(i => i.Track ?? uint.MaxValue)
            .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase);
}


/// <summary>
/// Pure dictionary logic for per-sidebar-view search text. Stored as its own small helper
/// so the save-on-leave / restore-on-enter semantics can be unit-tested without needing
/// a fully-constructed MainWindowViewModel (which pulls in Avalonia and LibVLC).
///
/// The MainWindowViewModel owns an instance + a suppression flag; this class just
/// handles the dictionary side so we can assert the shape in tests.
/// </summary>
internal static class PerViewSearchState
{
    /// <summary>
    /// Persists <paramref name="text"/> under <paramref name="key"/>. Empty or null
    /// keys are ignored. Empty or null text removes the entry — we don't want the
    /// dict to grow with "" placeholders for every view the user has ever visited.
    /// </summary>
    public static void Save(Dictionary<string, string> store, string? key, string? text)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        if (string.IsNullOrEmpty(text))
        {
            store.Remove(key);
        }
        else
        {
            store[key] = text;
        }
    }

    /// <summary>
    /// Returns the saved text for <paramref name="key"/>, or empty string when the key
    /// is missing / has no saved value. Never returns null so callers can assign
    /// directly to a non-nullable string property.
    /// </summary>
    public static string Restore(Dictionary<string, string> store, string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return string.Empty;
        }
        return store.TryGetValue(key, out var saved) ? saved : string.Empty;
    }
}
