// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.FontAwesome;
using OrgZ.Services;
using Serilog;
using Velopack;

namespace OrgZ;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Elevated CD helper mode: ShellExecute(runas) relaunches OrgZ.exe with
        // --cd-helper to perform a rip or burn.  Bypass single-instance, Velopack,
        // and Avalonia init - this process only lives long enough to finish the
        // SCSI operation and write progress events to the shared file.
        if (CdHelperMode.ShouldRun(args))
        {
            return CdHelperMode.Run(args);
        }

        // Privileged device-helper daemon mode: the installed OS service (LaunchDaemon /
        // systemd unit / Windows service) launches OrgZ this way. It runs as root/LocalSystem,
        // serves silent iPod identity reads over a socket/pipe, and never touches Avalonia.
        if (Array.IndexOf(args, "--device-helper") >= 0)
        {
            Logging.Initialize();
            try
            {
                // Feature ops must be on the registry before the daemon serves.
                Services.DeviceHelper.CdServiceOps.RegisterAll();
                Services.DeviceHelper.SyncServiceOps.RegisterAll();
                Services.DeviceHelper.JobsServiceOps.RegisterAll();
                Services.Sharing.ShareServiceOps.RegisterAll();

                // A configured share comes back on its own after a restart - the GUI that
                // knew the library's path may not run again for days. Off the serving
                // path: the wait-for-network inside must not delay the SCM handshake.
                _ = Task.Run(Services.Sharing.ShareServiceOps.RestoreOnStartupAsync);

#if WINDOWS
                // Launched by the SCM (no interactive session) → run under ServiceBase so the
                // control-dispatcher handshake happens and `sc start` succeeds. A developer
                // running --device-helper from a console keeps the inline loop (no install).
                if (OperatingSystem.IsWindows() && !Environment.UserInteractive)
                {
                    System.ServiceProcess.ServiceBase.Run(new Services.DeviceHelper.DeviceHelperWindowsService());
                    return 0;
                }
#endif
                return Services.DeviceHelper.DeviceHelperDaemon.RunAsync().GetAwaiter().GetResult();
            }
            finally
            {
                Logging.Shutdown();
            }
        }

        // User-session agent that triggers the macOS Removable Volumes consent prompt so the
        // same-signed root daemon inherits the grant. Must run in a GUI session to prompt.
        if (Array.IndexOf(args, "--device-helper-agent") >= 0)
        {
            Logging.Initialize();
            try
            {
                var primed = Services.DeviceHelper.DeviceHelperAgent.PrimeRemovableAccess();
                Console.WriteLine($"primed {primed} device(s)");
                return 0;
            }
            finally
            {
                Logging.Shutdown();
            }
        }

        // One-shot installers (self-elevating). Invoked by the "Install device helper" action.
        if (Array.IndexOf(args, "--install-device-helper") >= 0)
        {
            Logging.Initialize();
            try
            {
                var result = Services.DeviceHelper.DeviceHelperInstaller.InstallAsync().GetAwaiter().GetResult();
                Console.WriteLine(result.Ok ? "installed" : $"failed: {result.Detail}");
                return result.Ok ? 0 : 1;
            }
            finally
            {
                Logging.Shutdown();
            }
        }

        // Logging must come up first so Velopack/Avalonia init failures are captured.
        Logging.Initialize();
        StartupTrace.Mark("logging");

        try
        {
            // Single-instance: D-Bus name ownership on Linux, a named mutex on Windows. If we
            // can't claim it, another OrgZ is already running - it's been asked to raise its
            // window and we exit this process.
            //
            // Except for Velopack's install/update hooks, which relaunch this very executable
            // with --veloapp-* (or the legacy --squirrel-*) and expect it to run the callback
            // and exit. Turning one of those away at the guard would silently skip the hook -
            // and ours are what stop and restart the background service around an update.
            var velopackHook = args.Any(a => a.StartsWith("--veloapp-", StringComparison.OrdinalIgnoreCase) || a.StartsWith("--squirrel-", StringComparison.OrdinalIgnoreCase));

            if (!velopackHook && !SingleInstanceGuard.TryAcquirePrimary())
            {
                return 0;
            }
            StartupTrace.Mark("single-instance guard");

            // The PerMachine MSI runs elevated, so its install hook can register the
            // background service outright - no button to find, no second prompt. The
            // per-user Setup.exe never elevates and the hook declines quietly there,
            // leaving that user on the per-operation UAC path. Both callbacks are
            // Windows-only in Velopack and both are hard-capped (30 s) before it
            // terminates them, which is why they do one `sc` chain and nothing else.
            var velopack = VelopackApp.Build();
#if WINDOWS
            // #if keeps the callbacks out of the linux/osx publishes entirely; the runtime
            // check is what the platform analyzer reads, since the TFM is plain net10.0
            // and Velopack marks these hooks [SupportedOSPlatform("windows")].
            if (OperatingSystem.IsWindows())
            {
                velopack = velopack
                    .OnAfterInstallFastCallback(_ => Services.DeviceHelper.ServiceInstallHook.OnInstall())
                    .OnBeforeUninstallFastCallback(_ => Services.DeviceHelper.ServiceInstallHook.OnUninstall())
                    // The service runs the very executable an update replaces, so it has to be
                    // stopped before the swap and started after it. Without this pair the update
                    // fails on a locked file, which reads to the user as an update that does
                    // nothing. These two get 15 s each, half what the install hooks get.
                    .OnBeforeUpdateFastCallback(_ => Services.DeviceHelper.ServiceInstallHook.OnBeforeUpdate())
                    .OnAfterUpdateFastCallback(_ => Services.DeviceHelper.ServiceInstallHook.OnAfterUpdate());
            }
#endif
            velopack.Run();
            StartupTrace.Mark("velopack");

#if WINDOWS
            SmtcNativeMethods.SetCurrentProcessExplicitAppUserModelID("com.foxcouncil.orgz");
            ShortcutInstaller.EnsureShortcut();
            StartupTrace.Mark("shortcut + app id");
#else
            // Both exits happen before Avalonia starts, so there is no window and no dialog to
            // carry the message - and the Console sink is DEBUG-only, so a shipped build would
            // otherwise leave stdout, stderr and the screen completely silent. stderr is the one
            // channel a terminal launch will show; the log file is all a double-click leaves.
            if (OperatingSystem.IsLinux() && !RegisterLinuxVlcResolver())
            {
                const string message = "libvlc (VLC runtime) not found. Install VLC and relaunch. Debian/Ubuntu: sudo apt install vlc | Fedora: sudo dnf install vlc | Arch: sudo pacman -S vlc";
                Log.Fatal(message);
                Console.Error.WriteLine(message);
                ShowLinuxFatalDialog(message);
                Environment.Exit(1);
            }

            if (OperatingSystem.IsMacOS() && !InitializeMacVlc())
            {
                const string message = "libvlc (VLC runtime) not found. Install VLC.app (brew install --cask vlc, or download from videolan.org) and relaunch.";
                Log.Fatal(message);
                Console.Error.WriteLine(message);
                Environment.Exit(1);
            }
#endif

            // App-data stores must exist before the UI constructs - the MainWindow
            // ctor reads them, and on a first launch on a clean machine nothing has
            // created the directory or schema yet.
            try
            {
                App.StartupNotice = EnsureAppDataStores();
            }
            catch (Exception dbex)
            {
                // A fresh database failed too, so there is no app to start and still no UI to
                // say why. Name the file and the log directory on stderr and leave with a
                // non-zero code instead of dying invisibly.
                Log.Fatal(dbex, "Library database at {Path} is unusable", LibraryDb.FilePath);
                Console.Error.WriteLine($"OrgZ cannot open its library database: {LibraryDb.FilePath}");
                Console.Error.WriteLine(dbex.Message);
                Console.Error.WriteLine($"Move that file aside and relaunch. Logs: {Logging.LogDirectory}");
                return 1;
            }

            _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Unhandled exception escaped Program.Main");
            throw;
        }
        finally
        {
            SingleInstanceGuard.Release();
            Logging.Shutdown();
        }

        return 0;
    }

    /// <summary>
    /// Last-resort visible error for a launch that dies before Avalonia exists. An AppImage
    /// started from a file manager has no terminal attached, so stderr reaches nobody and the
    /// user sees an icon that simply never appears. zenity/kdialog is what desktop helpers use
    /// for exactly this. Best effort: with neither installed nothing happens, and a dialog
    /// nobody is there to dismiss is killed rather than left holding the process.
    /// </summary>
    private static void ShowLinuxFatalDialog(string message)
    {
        string[][] candidates =
        [
            ["zenity", "--error", "--title=OrgZ", "--text=" + message],
            ["kdialog", "--title", "OrgZ", "--error", message],
        ];

        foreach (var candidate in candidates)
        {
            try
            {
                var psi = new ProcessStartInfo(candidate[0]) { UseShellExecute = false };
                foreach (var arg in candidate[1..])
                {
                    psi.ArgumentList.Add(arg);
                }

                using var dialog = Process.Start(psi);
                if (dialog is null)
                {
                    continue;
                }

                if (!dialog.WaitForExit(60_000))
                {
                    dialog.Kill(entireProcessTree: true);
                }

                return;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "Could not show the fatal-error dialog with {Tool}", candidate[0]);
            }
        }
    }

    /// <summary>
    /// Opens (and migrates) the three app-data stores, which all live in the one library.db.
    /// A file SQLite cannot read - a half-written scan after a power cut, a roaming profile
    /// copied mid-flight - throws here, before Avalonia exists, and every later launch throws
    /// in the same place: the app becomes permanently unlaunchable with nothing on screen.
    /// So set the unreadable file aside and open a fresh one. The library rebuilds from a
    /// folder scan and the old file is still on disk under its new name.
    /// Returns the line the UI should show when a file was set aside, null on a clean open.
    /// Throws only when the fresh database fails too - there is no app without one.
    /// </summary>
    // SQLite's own result codes for "this file is not a usable database", from sqlite3.h.
    // Microsoft.Data.Sqlite surfaces them on SqliteException.SqliteErrorCode.
    private const int RawSqliteCorrupt = 11;       // SQLITE_CORRUPT
    private const int RawSqliteFormat = 24;        // SQLITE_FORMAT
    private const int RawSqliteNotADatabase = 26;  // SQLITE_NOTADB

    private static string? EnsureAppDataStores()
    {
        try
        {
            OpenAppDataStores();
            return null;
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode is RawSqliteCorrupt or RawSqliteNotADatabase or RawSqliteFormat)
        {
            // ONLY genuine corruption. Catching everything here would be the more destructive
            // bug of the two this method exists to prevent: a locked file, a full disk, a
            // network profile that has not finished mounting, or an antivirus holding the
            // handle for a moment are all transient, and every one of them would have renamed
            // a perfectly good library aside and started the user from nothing. A corrupt
            // file, by contrast, fails identically on every launch forever.
            var database = LibraryDb.FilePath;
            Log.Error(ex, "Library database {Path} is corrupt ({Code}) - setting it aside and starting fresh", database, ex.SqliteErrorCode);

            var aside = SetDatabaseAside(database);

            OpenAppDataStores();

            return aside is null
                ? "The library database could not be opened, so OrgZ started with an empty one. Your music files were not touched."
                : $"The library database could not be opened. It was set aside as {Path.GetFileName(aside)} and OrgZ started with an empty one. Your music files were not touched.";
        }
    }

    private static void OpenAppDataStores()
    {
        MediaCache.EnsureCreated();
        StartupTrace.Mark("library db");
        Services.Podcast.PodcastCache.EnsureCreated();
        StartupTrace.Mark("podcast db");
        Services.Media.AcquisitionStore.EnsureCreated();
        StartupTrace.Mark("acquisition db");
    }

    /// <summary>
    /// Renames an unreadable database out of the way, journal siblings included - a -wal left
    /// beside a brand-new file would be replayed into it and break that one too. Returns the
    /// new path, or null when nothing could be moved (the caller's retry then fails and says so).
    /// </summary>
    private static string? SetDatabaseAside(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            // The failed open left a pooled connection holding the file, and Windows will not
            // rename a file that is still open.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            var aside = $"{path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, aside);

            string[] siblings = ["-wal", "-shm", "-journal"];
            foreach (var suffix in siblings)
            {
                if (File.Exists(path + suffix))
                {
                    File.Move(path + suffix, aside + suffix);
                }
            }

            Log.Warning("Library database set aside as {Path}", aside);
            return aside;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not set the unreadable library database aside");
            return null;
        }
    }

    // No LibVLC NuGet ships native binaries for macOS arm64, and the x64 package's search paths
    // (bin/.../libvlc/osx-x64/lib) won't match an Apple Silicon host anyway. Release builds ship
    // libvlc + a filtered plugin set next to the executable (see scripts/fetch-vlc-mac.sh and
    // .github/workflows/release.yml). Dev builds fall back to a system VLC.app install.
    private static bool InitializeMacVlc()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "vlc", "lib");
        string[] candidates =
        [
            bundled,
            "/Applications/VLC.app/Contents/MacOS/lib",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/VLC.app/Contents/MacOS/lib"),
        ];

        foreach (var dir in candidates)
        {
            if (File.Exists(Path.Combine(dir, "libvlc.dylib")))
            {
                // libvlc_new() resolves plugins relative to the binary by default; for a
                // dylib loaded out of VLC.app it can't find them, so point it explicitly.
                var pluginsDir = Path.Combine(Path.GetDirectoryName(dir)!, "plugins");
                if (Directory.Exists(pluginsDir))
                {
                    // Environment.SetEnvironmentVariable does not reliably propagate to libc
                    // getenv() on macOS in time for libvlc_new(), so call setenv(3) directly.
                    _ = MacSetEnv("VLC_PLUGIN_PATH", pluginsDir, 1);
                }

                LibVLCSharp.Shared.Core.Initialize(dir);
                return true;
            }
        }

        return false;
    }

    [DllImport("libc", EntryPoint = "setenv")]
    private static extern int MacSetEnv(string name, string value, int overwrite);

    // LibVLCSharp's P/Invoke asks for "libvlc" / "libvlccore" with no version suffix, but most
    // Linux distros only ship libvlc.so.5 / libvlccore.so.9. Redirect those loads instead of
    // requiring the user to install libvlc-dev just to get the unversioned symlink.
    //
    // The AppImage carries its own copy under vlc/ (staged by scripts/fetch-vlc-linux.sh),
    // which is tried FIRST: a self-contained bundle that silently preferred whatever the host
    // happened to have would be neither self-contained nor predictable. A system install is
    // still the fallback, so a dev run out of bin/ works with no bundle present.
    private static bool RegisterLinuxVlcResolver()
    {
        var asm = typeof(LibVLCSharp.Shared.LibVLC).Assembly;
        NativeLibrary.SetDllImportResolver(asm, ResolveLinuxVlc);

        // ORDER MATTERS, and only in the bundled case. libvlc.so.5 carries a DT_NEEDED on
        // libvlccore.so.9 but NO RUNPATH, so when it is loaded by absolute path out of our own
        // directory the loader has no idea where its sibling lives and the load fails. Loading
        // libvlccore first puts it in the process under its SONAME, which is what libvlc's
        // dependency then resolves against. Probing libvlc first - as this did - reports "no
        // VLC" on a machine that is carrying a perfectly good one.
        //
        // The core handle is deliberately NOT freed: releasing it would undo exactly the thing
        // that makes the next load work.
        if (!TryLoad("libvlccore", out _))
        {
            return false;
        }

        if (!TryLoad("libvlc", out _))
        {
            return false;
        }

        var plugins = Path.Combine(AppContext.BaseDirectory, "vlc", "plugins");
        if (Directory.Exists(plugins))
        {
            // libvlc looks for its plugins relative to the binary that loaded it, which for a
            // bundled copy is OrgZ, not VLC - so it has to be told.
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", plugins);
        }

        return true;

        static IntPtr ResolveLinuxVlc(string name, Assembly _, DllImportSearchPath? __)
        {
            if (name is not ("libvlc" or "libvlccore"))
            {
                return IntPtr.Zero;
            }

            return TryLoad(name, out var handle) ? handle : IntPtr.Zero;
        }

        static bool TryLoad(string name, out IntPtr handle)
        {
            foreach (var candidate in Candidates(name))
            {
                if (NativeLibrary.TryLoad(candidate, out handle))
                {
                    return true;
                }
            }

            handle = IntPtr.Zero;
            return false;
        }

        static IEnumerable<string> Candidates(string name)
        {
            var versions = name == "libvlc"
                ? new[] { "libvlc.so", "libvlc.so.5" }
                : new[] { "libvlccore.so", "libvlccore.so.9" };
            var bundled = Path.Combine(AppContext.BaseDirectory, "vlc", "lib") + Path.DirectorySeparatorChar;
            var dirs = new[] { bundled, "", "/usr/lib/x86_64-linux-gnu/", "/usr/lib64/", "/usr/local/lib/", "/lib/x86_64-linux-gnu/" };

            // Directory outer, version inner: each location is exhausted before moving on, so
            // the bundled copy wins outright. The other way round, a host that happens to have
            // the unversioned libvlc.so symlink would be preferred over the copy we shipped.
            foreach (var d in dirs)
            {
                foreach (var v in versions)
                {
                    yield return d + v;
                }
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithIcons().LogToTrace();

        // Automation hook: render popups (context menus, flyouts, tooltips) as in-window overlays
        // instead of native popup windows, so they live in the visual tree where the Avalonia devtools
        // MCP can search/click/screenshot them - right-click menus otherwise open in separate OS windows
        // the tooling can't see. Opt-in via env var; normal runs keep native popups (which may extend
        // past the window edge).
        if (Environment.GetEnvironmentVariable("ORGZ_OVERLAY_POPUPS") == "1")
        {
            builder = builder.With(new Avalonia.Win32PlatformOptions { OverlayPopups = true });
        }

        return builder;
    }
}

public static class AppBuilderExtensions
{
    public static AppBuilder WithIcons(this AppBuilder builder)
    {
        _ = IconProvider.Current.Register<FontAwesomeIconProvider>();

        return builder;
    }
}
