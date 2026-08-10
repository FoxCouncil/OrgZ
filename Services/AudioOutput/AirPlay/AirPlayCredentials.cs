// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// Remembers the AirPlay password for each receiver that asks for one, so the prompt is a
/// one-time cost per speaker rather than a per-track interruption.
///
/// Storage is the OS secret store (<see cref="SecretStore"/>) - DPAPI, Keychain or
/// libsecret - never settings.json. Where no store exists the password simply isn't kept,
/// and <see cref="CanRemember"/> lets the UI say so instead of offering a "remember" that
/// silently does nothing.
/// </summary>
internal static class AirPlayCredentials
{
    /// <summary>Keyed by mDNS instance name, which is stable across reboots and IP changes.</summary>
    private static string KeyFor(string deviceId) => $"AirPlay/{deviceId}";

    /// <summary>False when this platform has nowhere safe to keep a password.</summary>
    public static bool CanRemember => SecretStore.IsAvailable;

    // Passwords for this run only: what "don't remember" means, and the fallback when the
    // platform has no secret store. Consulted before the OS store so a freshly entered
    // password wins over a stale saved one.
    private static readonly Dictionary<string, string> _session = new(StringComparer.OrdinalIgnoreCase);

    public static string? Get(string deviceId)
    {
        lock (_session)
        {
            if (_session.TryGetValue(deviceId, out var cached))
            {
                return cached;
            }
        }

        var password = SecretStore.Get(KeyFor(deviceId));
        return string.IsNullOrEmpty(password) ? null : password;
    }

    /// <summary>Keeps a password for this run without writing it anywhere.</summary>
    public static void SetForSession(string deviceId, string? password)
    {
        lock (_session)
        {
            if (string.IsNullOrEmpty(password))
            {
                _session.Remove(deviceId);
            }
            else
            {
                _session[deviceId] = password;
            }
        }
    }

    /// <summary>Persists a password to the OS secret store, and uses it for this run.</summary>
    public static void Set(string deviceId, string? password)
    {
        SetForSession(deviceId, password);
        SecretStore.Set(KeyFor(deviceId), password);
    }
}
