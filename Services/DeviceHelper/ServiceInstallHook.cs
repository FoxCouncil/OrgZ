// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// Registers (and removes) the background service as part of installing OrgZ itself, so a
/// user never has to find a button for it.
///
/// The route is Velopack's PerMachine MSI. The default Setup.exe installs per-user into
/// %LocalAppData% and deliberately never elevates, which means <c>sc create</c> from its
/// hook would die on access denied; the MSI built with <c>--instLocation PerMachine</c>
/// installs to Program Files under HKLM and is elevated by construction, so the hook can
/// register a LocalSystem service with no interaction beyond the one UAC the installer
/// already raised.
///
/// Both installers ship. When OrgZ is installed the per-user way this hook declines
/// quietly and that user simply keeps the per-operation UAC path - the same experience
/// they had before the service existed. A failed registration must never fail the
/// install: the app works either way, and the difference is only how often Windows asks.
/// </summary>
public static class ServiceInstallHook
{
    private static readonly ILogger _log = Logging.For("ServiceInstallHook");

    /// <summary>
    /// Whether the install hook should register the service.
    ///
    /// Velopack only fires these callbacks on Windows, so the platform check is belt and
    /// braces. Elevation is the real gate: without it every <c>sc</c> command fails, and
    /// attempting anyway would put an access-denied error in the log of an install that
    /// actually succeeded.
    /// </summary>
    internal static bool ShouldRegister(bool isWindows, bool isElevated) => isWindows && isElevated;

    /// <summary>True when this process can create a Windows service - i.e. is running as administrator.</summary>
    internal static bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not determine elevation");
            return false;
        }
    }

    /// <summary>
    /// Velopack's after-install callback. Never throws: this runs inside the installer,
    /// and an exception escaping here would surface as a failed installation of an app
    /// that installed perfectly well.
    /// </summary>
    public static void OnInstall()
    {
        // Do this even when the service isn't going in: a per-user install doesn't need it,
        // but a PerMachine one crashes on the first non-admin launch without it, and this
        // hook is the only elevated moment we get. See PackagesDirFor.
        DeviceHelperInstaller.EnsurePackagesDirectoryElevated();

        Run("install", static () => DeviceHelperInstaller.InstallElevatedAsync());
    }

    /// <summary>
    /// Velopack's before-uninstall callback. Removing OrgZ must not leave a LocalSystem
    /// service behind pointing at an executable that is about to be deleted.
    /// </summary>
    public static void OnUninstall()
    {
        Run("uninstall", static () => DeviceHelperInstaller.UninstallElevatedAsync());
    }

    /// <summary>
    /// Velopack's before-update callback: stop the service so the update can replace the
    /// file it is running.
    ///
    /// The service's binPath IS the installed OrgZ.exe. While it runs, Windows holds that
    /// image open, so an in-place update either fails outright or half-swaps the install
    /// directory - and the user's only symptom is an update that silently never takes.
    /// Update.exe is already elevated when it calls this, so the plain <c>sc</c> chain works
    /// without a second prompt.
    /// </summary>
    public static void OnBeforeUpdate()
    {
        Run("stop for update", static () => DeviceHelperInstaller.StopElevatedAsync());
    }

    /// <summary>
    /// Velopack's after-update callback: start the freshly-updated service back up, so disc
    /// and iPod access stay silent instead of falling back to a UAC prompt per operation
    /// until the next reboot.
    /// </summary>
    public static void OnAfterUpdate()
    {
        Run("start after update", static () => DeviceHelperInstaller.StartElevatedAsync());
    }

    private static void Run(string what, Func<Task<DeviceHelperInstaller.InstallResult>> action)
    {
        try
        {
            if (!ShouldRegister(OperatingSystem.IsWindows(), IsElevated()))
            {
                // Not always a per-user install: a PerMachine MSI run by a standard user who
                // supplied an administrator's credentials executes its deferred custom actions
                // impersonating the INSTALLING user, so this lands here on a machine-wide
                // install too. Either way the outcome is the same - no service, and OrgZ falls
                // back to a UAC prompt per disc/iPod operation until it is installed from
                // Settings - so say that rather than diagnosing the cause wrongly.
                _log.Warning("Skipping service {What}: the hook is not running elevated. OrgZ will ask for consent per operation until the device helper is installed from Settings", what);
                return;
            }

            var result = action().GetAwaiter().GetResult();
            if (result.Ok)
            {
                _log.Information("Background service {What} succeeded during app {What}", what, what);
            }
            else
            {
                _log.Warning("Background service {What} failed during app {What}: {Detail}", what, what, result.Detail);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Background service {What} threw during app {What}", what, what);
        }
    }
}
