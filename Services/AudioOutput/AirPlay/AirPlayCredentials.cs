// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Remembers the AirPlay password for each receiver that asks for one, so the prompt is a
/// one-time cost per speaker rather than a per-track interruption.
///
/// Stored in settings.json in the clear. That is a deliberate, stated choice rather than
/// an oversight: there is no cross-platform secret store here, and the alternatives
/// (base64, a fixed XOR key shipped in the binary) only look like protection. Anyone who
/// can read settings.json can read the library, the share config and the device caches
/// beside it, so this is the same trust boundary - not a new one.
/// </summary>
internal static class AirPlayCredentials
{
    private const string SettingsKey = "OrgZ.AirPlayPasswords";

    /// <summary>Keyed by mDNS instance name, which is stable across reboots and IP changes.</summary>
    private static Dictionary<string, string> Load()
        => Settings.Get<Dictionary<string, string>>(SettingsKey, null!) ?? new(StringComparer.OrdinalIgnoreCase);

    public static string? Get(string deviceId)
        => Load().TryGetValue(deviceId, out var password) && !string.IsNullOrEmpty(password) ? password : null;

    public static void Set(string deviceId, string? password)
    {
        var all = new Dictionary<string, string>(Load(), StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(password))
        {
            all.Remove(deviceId);
        }
        else
        {
            all[deviceId] = password;
        }

        Settings.Set(SettingsKey, all);
        Settings.Save();
    }
}
