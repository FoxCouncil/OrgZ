// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;

namespace OrgZ.Helpers;

/// <summary>
/// Persists per-view group expand/collapse state across app launches. Keyed by view
/// config ("Radio", etc.) and group key (normalized genre string). On a fresh install
/// unknown groups default to collapsed; once a user expands or re-collapses a group,
/// the choice is remembered across filter re-binds, view switches, and app restarts.
///
/// Storage is a JSON-serialized <c>Dictionary&lt;string, bool&gt;</c> in Settings,
/// where true = expanded. Missing keys mean "we've never seen this group" and the
/// caller should apply the default-collapsed behavior.
/// </summary>
internal static class GroupExpansionState
{
    /// <summary>
    /// Loads saved per-group state for a view. Returns empty when nothing has been
    /// saved yet or the saved JSON is malformed (treated as "no history").
    /// </summary>
    public static Dictionary<string, bool> Load(string viewKey)
    {
        var raw = Settings.Get<string>(BuildSettingsKey(viewKey), string.Empty);
        if (string.IsNullOrEmpty(raw))
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, bool>>(raw)
                   ?? new Dictionary<string, bool>(StringComparer.Ordinal);
        }
        catch
        {
            return new Dictionary<string, bool>(StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Saves the full state dict for a view. Empty dict removes the setting entirely
    /// so the next launch gets a fresh "everything collapsed" start — useful if a
    /// future feature wants to reset a view's state.
    /// </summary>
    public static void Save(string viewKey, IReadOnlyDictionary<string, bool> states)
    {
        var settingsKey = BuildSettingsKey(viewKey);

        if (states.Count == 0)
        {
            Settings.Set(settingsKey, string.Empty);
        }
        else
        {
            Settings.Set(settingsKey, JsonSerializer.Serialize(states));
        }
        Settings.Save();
    }

    private static string BuildSettingsKey(string viewKey)
        => $"OrgZ.GroupExpansion.{viewKey}";
}
