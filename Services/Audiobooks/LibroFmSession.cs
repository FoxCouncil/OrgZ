// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;
using Serilog;

namespace OrgZ.Services.Audiobooks;

/// <summary>
/// Persists the Libro.fm bearer token across launches. Only the TOKEN is ever stored - never the
/// password. On Windows it's wrapped with DPAPI (CurrentUser scope) before landing in settings;
/// elsewhere the token stays in memory only and the user signs in per session - an honest gap
/// until a cross-platform keychain story exists.
/// </summary>
public static class LibroFmSession
{
    private const string TokenKey = "OrgZ.LibroFm.Token";
    private const string UsernameKey = "OrgZ.LibroFm.Username";

    private static readonly ILogger _log = Logging.For("LibroFm");

    private static string? _memoryToken;

    public static void Save(string token, string username)
    {
        _memoryToken = token;
        Settings.Set(UsernameKey, username);
        if (OperatingSystem.IsWindows())
        {
            try
            {
                var protectedBytes = ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
                Settings.Set(TokenKey, Convert.ToBase64String(protectedBytes));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Could not protect the Libro.fm token — it will not persist across launches");
            }
        }
        Settings.Save();
    }

    public static string? LoadToken()
    {
        if (_memoryToken is not null)
        {
            return _memoryToken;
        }
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        try
        {
            var stored = Settings.Get(TokenKey, string.Empty);
            if (string.IsNullOrEmpty(stored))
            {
                return null;
            }
            _memoryToken = System.Text.Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored), null, DataProtectionScope.CurrentUser));
            return _memoryToken;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Stored Libro.fm token could not be unprotected — sign in again");
            return null;
        }
    }

    public static string? Username => Settings.Get(UsernameKey, string.Empty) is { Length: > 0 } u ? u : null;

    public static void Clear()
    {
        _memoryToken = null;
        Settings.Set(TokenKey, string.Empty);
        Settings.Set(UsernameKey, string.Empty);
        Settings.Save();
    }
}
