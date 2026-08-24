// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Security.Cryptography;
using Serilog;

namespace OrgZ.Services.Audiobooks;

/// <summary>
/// Persists the Libro.fm bearer token across launches. Only the TOKEN is ever stored -
/// never the password. Per platform: DPAPI (CurrentUser) into settings on Windows, the
/// login Keychain via the <c>security</c> CLI on macOS, and libsecret via
/// <c>secret-tool</c> on Linux. A Linux box without secret-tool falls back to
/// memory-only (sign in per session) rather than writing a plaintext token anywhere.
/// </summary>
public static class LibroFmSession
{
    private const string TokenKey = "OrgZ.LibroFm.Token";
    private const string UsernameKey = "OrgZ.LibroFm.Username";
    private const string KeychainService = "OrgZ.LibroFm";
    private const string KeychainAccount = "orgz";

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
        else if (OperatingSystem.IsMacOS())
        {
            // -U updates in place on re-login instead of erroring on the duplicate.
            //
            // The bearer token rides argv here, readable by any process running as this user
            // while the child lives - the same exposure SecretStore.Set carries, and for the
            // same reason: security(1)'s only alternative is `-w` with no value, which prompts
            // through getpass() and so reads /dev/tty rather than our pipe whenever OrgZ was
            // launched from a terminal. SecItemAdd/SecItemUpdate is the way out of it.
            if (!RunTool("security", ["add-generic-password", "-U", "-a", KeychainAccount, "-s", KeychainService, "-w", token], stdin: null, out _))
            {
                _log.Warning("Keychain store failed — the Libro.fm token will not persist across launches");
            }
        }
        else if (OperatingSystem.IsLinux())
        {
            if (!RunTool("secret-tool", ["store", "--label=OrgZ Libro.fm", "service", KeychainService, "account", KeychainAccount], stdin: token, out _))
            {
                _log.Warning("secret-tool store failed (libsecret missing?) — the Libro.fm token will not persist across launches");
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

        if (OperatingSystem.IsWindows())
        {
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

        if (OperatingSystem.IsMacOS()
            && RunTool("security", ["find-generic-password", "-a", KeychainAccount, "-s", KeychainService, "-w"], stdin: null, out var macToken)
            && !string.IsNullOrWhiteSpace(macToken))
        {
            _memoryToken = macToken;
            return _memoryToken;
        }

        if (OperatingSystem.IsLinux()
            && RunTool("secret-tool", ["lookup", "service", KeychainService, "account", KeychainAccount], stdin: null, out var linuxToken)
            && !string.IsNullOrWhiteSpace(linuxToken))
        {
            _memoryToken = linuxToken;
            return _memoryToken;
        }

        return null;
    }

    public static string? Username => Settings.Get(UsernameKey, string.Empty) is { Length: > 0 } u ? u : null;

    public static void Clear()
    {
        _memoryToken = null;
        Settings.Set(TokenKey, string.Empty);
        Settings.Set(UsernameKey, string.Empty);
        Settings.Save();

        if (OperatingSystem.IsMacOS())
        {
            RunTool("security", ["delete-generic-password", "-a", KeychainAccount, "-s", KeychainService], stdin: null, out _);
        }
        else if (OperatingSystem.IsLinux())
        {
            RunTool("secret-tool", ["clear", "service", KeychainService, "account", KeychainAccount], stdin: null, out _);
        }
    }

    /// <summary>Runs a keychain CLI with a bounded wait. False when the tool is absent or errored.</summary>
    private static bool RunTool(string fileName, string[] args, string? stdin, out string stdout)
    {
        stdout = "";
        try
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            if (stdin is not null)
            {
                proc.StandardInput.Write(stdin);
                proc.StandardInput.Close();
            }

            stdout = proc.StandardOutput.ReadToEnd().TrimEnd('\r', '\n');
            _ = proc.StandardError.ReadToEnd();
            if (!proc.WaitForExit(10_000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                return false;
            }

            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "{Tool} unavailable", fileName);
            return false;
        }
    }
}
