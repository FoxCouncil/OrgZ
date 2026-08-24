// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Per-user secret storage backed by whatever the OS actually provides: DPAPI on Windows,
/// the login Keychain on macOS, libsecret on Linux.
///
/// The one rule here is that it never pretends. If no platform store is reachable,
/// <see cref="IsAvailable"/> is false and <see cref="Set"/> stores nothing - callers are
/// expected to tell the user the secret won't be remembered rather than quietly writing it
/// somewhere readable. Base64 or a fixed XOR key would satisfy the API and protect nobody,
/// which is worse than not storing it at all, because it invites trusting it.
/// </summary>
internal static class SecretStore
{
    private static readonly ILogger _log = Logging.For("SecretStore");

    /// <summary>Namespaces our entries inside the shared OS keychain/collection.</summary>
    private const string ServiceName = "OrgZ";

    /// <summary>Where the Windows backend keeps its DPAPI blobs (ciphertext, not secrets).</summary>
    private const string WindowsSettingsKey = "OrgZ.Secrets";

    public static bool IsAvailable => Backend != Platform.None;

    private enum Platform
    {
        None,
        Windows,
        MacOs,
        Linux,
    }

    private static Platform? _backend;

    private static Platform Backend => _backend ??= Detect();

    private static Platform Detect()
    {
        if (OperatingSystem.IsWindows())
        {
            return Platform.Windows;
        }

        // macOS and Linux both go through a CLI that may not be installed (libsecret in
        // particular is optional on headless installs), so probe rather than assume.
        if (OperatingSystem.IsMacOS() && ToolExists("security"))
        {
            return Platform.MacOs;
        }

        if (OperatingSystem.IsLinux() && ToolExists("secret-tool"))
        {
            return Platform.Linux;
        }

        _log.Information("No OS secret store available - secrets will not be persisted");
        return Platform.None;
    }

    public static string? Get(string key)
    {
        try
        {
            return Backend switch
            {
                Platform.Windows => WindowsGet(key),
                Platform.MacOs => Run("security", ["find-generic-password", "-a", key, "-s", ServiceName, "-w"], out var found) && found.Length > 0 ? found.TrimEnd('\n') : null,
                Platform.Linux => Run("secret-tool", ["lookup", "service", ServiceName, "account", key], out var secret) && secret.Length > 0 ? secret.TrimEnd('\n') : null,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Reading secret {Key} failed", key);
            return null;
        }
    }

    public static void Set(string key, string? secret)
    {
        try
        {
            switch (Backend)
            {
                case Platform.Windows:
                {
                    WindowsSet(key, secret);
                }
                break;

                case Platform.MacOs:
                {
                    // -U updates in place; without it a second save fails as a duplicate.
                    if (string.IsNullOrEmpty(secret))
                    {
                        Run("security", ["delete-generic-password", "-a", key, "-s", ServiceName], out _);
                    }
                    else
                    {
                        // Unlike the Linux branch below, the secret rides argv, where any process
                        // running as this user can read it (KERN_PROCARGS2) for the lifetime of
                        // the child. security(1) offers no stdin channel that survives both
                        // launch contexts: `-w` with no value prompts through getpass(), which
                        // reads /dev/tty whenever the process has one - so a terminal-launched
                        // OrgZ would hang on the prompt instead of taking the pipe. Closing this
                        // properly means P/Invoking SecItemAdd/SecItemUpdate.
                        Run("security", ["add-generic-password", "-U", "-a", key, "-s", ServiceName, "-w", secret], out _);
                    }
                }
                break;

                case Platform.Linux:
                {
                    if (string.IsNullOrEmpty(secret))
                    {
                        Run("secret-tool", ["clear", "service", ServiceName, "account", key], out _);
                    }
                    else
                    {
                        // secret-tool takes the secret on stdin, so it never appears in ps.
                        Run("secret-tool", ["store", "--label", $"{ServiceName} ({key})", "service", ServiceName, "account", key], out _, secret);
                    }
                }
                break;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Storing secret {Key} failed", key);
        }
    }

    // ---- Windows: DPAPI ----------------------------------------------------------------
    //
    // CryptProtectData with a null entropy and no flags encrypts to the CURRENT USER, so
    // the blob is useless to another account or on another machine. P/Invoked directly
    // rather than via System.Security.Cryptography.ProtectedData to avoid taking a
    // Windows-only package reference into a cross-platform build.

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[]? DpapiTransform(byte[] input, bool protect)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var blobIn = new DataBlob { cbData = input.Length, pbData = handle.AddrOfPinnedObject() };
        var blobOut = default(DataBlob);

        try
        {
            var ok = protect
                ? CryptProtectData(ref blobIn, ServiceName, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out blobOut)
                : CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out blobOut);

            if (!ok)
            {
                _log.Warning("DPAPI {Op} failed: 0x{Error:X8}", protect ? "protect" : "unprotect", Marshal.GetLastWin32Error());
                return null;
            }

            var output = new byte[blobOut.cbData];
            Marshal.Copy(blobOut.pbData, output, 0, blobOut.cbData);
            return output;
        }
        finally
        {
            handle.Free();
            if (blobOut.pbData != IntPtr.Zero)
            {
                LocalFree(blobOut.pbData);
            }
        }
    }

    private static Dictionary<string, string> WindowsBlobs()
        => Settings.Get<Dictionary<string, string>>(WindowsSettingsKey, null!) ?? new(StringComparer.OrdinalIgnoreCase);

    private static string? WindowsGet(string key)
    {
        if (!WindowsBlobs().TryGetValue(key, out var encoded) || string.IsNullOrEmpty(encoded))
        {
            return null;
        }

        var plain = DpapiTransform(Convert.FromBase64String(encoded), protect: false);
        return plain is null ? null : System.Text.Encoding.UTF8.GetString(plain);
    }

    private static void WindowsSet(string key, string? secret)
    {
        var all = new Dictionary<string, string>(WindowsBlobs(), StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrEmpty(secret))
        {
            all.Remove(key);
        }
        else
        {
            var blob = DpapiTransform(System.Text.Encoding.UTF8.GetBytes(secret), protect: true);
            if (blob is null)
            {
                return;   // encryption failed - storing the plaintext instead would defeat the point
            }

            all[key] = Convert.ToBase64String(blob);
        }

        Settings.Set(WindowsSettingsKey, all);
        Settings.Save();
    }

    // ---- CLI backends ------------------------------------------------------------------

    private static bool ToolExists(string tool)
    {
        try
        {
            return Run(OperatingSystem.IsWindows() ? "where" : "which", [tool], out _);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Probing for {Tool} failed", tool);
            return false;
        }
    }

    private static bool Run(string file, string[] arguments, out string output, string? stdin = null)
    {
        output = string.Empty;

        var info = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var process = Process.Start(info);
        if (process is null)
        {
            return false;
        }

        if (stdin is not null)
        {
            process.StandardInput.Write(stdin);
            process.StandardInput.Close();
        }

        output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            // A store that hangs (a locked keyring waiting on a prompt that never comes)
            // must not wedge playback behind it.
            try { process.Kill(entireProcessTree: true); } catch (Exception ex) { _log.Debug(ex, "Killing {Tool} failed", file); }
            return false;
        }

        return process.ExitCode == 0;
    }
}
