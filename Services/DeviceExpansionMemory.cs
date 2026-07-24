// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

namespace OrgZ.Services;

/// <summary>
/// Session-scoped memory of each device's sidebar expansion state. First connect of a
/// session auto-expands (per the roadmap contract); after that, whatever the user set
/// survives disconnect/reconnect cycles within the session. Keyed by serial so the
/// same physical iPod is recognized at a different drive letter; falls back to the
/// mount path for devices without a readable serial.
/// </summary>
public sealed class DeviceExpansionMemory
{
    private readonly Dictionary<string, bool> _bySerial = new(StringComparer.OrdinalIgnoreCase);

    internal static string KeyFor(string? serial, string mountPath)
        => string.IsNullOrWhiteSpace(serial) ? $"mount:{mountPath}" : $"serial:{serial}";

    /// <summary>Expansion for a connecting device: remembered value, or true on first sight.</summary>
    public bool GetOrDefault(string? serial, string mountPath)
        => _bySerial.TryGetValue(KeyFor(serial, mountPath), out var expanded) ? expanded : true;

    public void Remember(string? serial, string mountPath, bool expanded)
        => _bySerial[KeyFor(serial, mountPath)] = expanded;
}
