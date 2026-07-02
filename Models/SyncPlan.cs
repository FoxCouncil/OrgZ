// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;

namespace OrgZ.Models;

/// <summary>
/// What a given iPod syncs - the user's saved choices, persisted per device. A device with no
/// saved plan is "never configured" (the Sync gesture opens settings first); an empty-but-saved
/// plan is a deliberate "sync nothing", which is different.
/// </summary>
public sealed class SyncPlan
{
    public bool Podcasts { get; set; }
    public bool Audiobooks { get; set; }

    /// <summary>The Favorites pseudo-playlist - synced as a native device playlist named "Favorites".</summary>
    public bool Favorites { get; set; }

    /// <summary>Library playlist ids to sync (each becomes/refreshes a native device playlist).</summary>
    public List<int> PlaylistIds { get; set; } = [];

    public bool SyncsAnything => Podcasts || Audiobooks || Favorites || PlaylistIds.Count > 0;
}

/// <summary>
/// Persists each iPod's <see cref="SyncPlan"/> in app settings, keyed by the device's most stable
/// identity (FireWire GUID, else serial, else mount path). Host-side on purpose - a plan references
/// THIS library's playlist ids, which don't travel with the iPod.
/// </summary>
public static class SyncPlanStore
{
    private const string SettingsKey = "OrgZ.SyncPlans";

    /// <summary>The stable key for a device; empty only when it has no identity at all (won't persist).</summary>
    public static string KeyFor(ConnectedDevice device)
    {
        if (!string.IsNullOrWhiteSpace(device.FireWireGuid)) { return "guid:" + device.FireWireGuid; }
        if (!string.IsNullOrWhiteSpace(device.Serial)) { return "serial:" + device.Serial; }
        return "mount:" + device.MountPath;
    }

    /// <summary>The saved plan, or null when this device has never been configured.</summary>
    public static SyncPlan? Load(ConnectedDevice device)
        => LoadAll().GetValueOrDefault(KeyFor(device));

    public static void Save(ConnectedDevice device, SyncPlan plan)
    {
        var all = LoadAll();
        all[KeyFor(device)] = plan;
        Settings.Set(SettingsKey, all);
        Settings.Save();
    }

    private static Dictionary<string, SyncPlan> LoadAll()
    {
        try
        {
            return Settings.Get<Dictionary<string, SyncPlan>>(SettingsKey, [])
                ?? new Dictionary<string, SyncPlan>();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
