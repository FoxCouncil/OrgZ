// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

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

        // Logging must come up first so Velopack/Avalonia init failures are captured.
        Logging.Initialize();

        try
        {
            // Single-instance via D-Bus name ownership on Linux: if we can't claim the
            // singleton bus name, another OrgZ is already running - it's been asked to
            // raise its window and we exit this process.
            if (!SingleInstanceGuard.TryAcquirePrimary())
            {
                return 0;
            }

            VelopackApp.Build().Run();

#if WINDOWS
            SmtcNativeMethods.SetCurrentProcessExplicitAppUserModelID("com.foxcouncil.orgz");
            ShortcutInstaller.EnsureShortcut();
#else
            if (OperatingSystem.IsLinux() && !RegisterLinuxVlcResolver())
            {
                Log.Fatal("libvlc (VLC runtime) not found. Install VLC and relaunch. Debian/Ubuntu: sudo apt install vlc | Fedora: sudo dnf install vlc | Arch: sudo pacman -S vlc");
                Environment.Exit(1);
            }

            if (OperatingSystem.IsMacOS() && !InitializeMacVlc())
            {
                Log.Fatal("libvlc (VLC runtime) not found. Install VLC.app (brew install --cask vlc, or download from videolan.org) and relaunch.");
                Environment.Exit(1);
            }
#endif

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
    private static bool RegisterLinuxVlcResolver()
    {
        var asm = typeof(LibVLCSharp.Shared.LibVLC).Assembly;
        NativeLibrary.SetDllImportResolver(asm, ResolveLinuxVlc);
        return TryProbe("libvlc") && TryProbe("libvlccore");

        static IntPtr ResolveLinuxVlc(string name, Assembly _, DllImportSearchPath? __)
        {
            if (name is not ("libvlc" or "libvlccore"))
            {
                return IntPtr.Zero;
            }

            foreach (var candidate in Candidates(name))
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    return handle;
                }
            }

            return IntPtr.Zero;
        }

        static bool TryProbe(string name)
        {
            foreach (var candidate in Candidates(name))
            {
                if (NativeLibrary.TryLoad(candidate, out var handle))
                {
                    NativeLibrary.Free(handle);
                    return true;
                }
            }

            return false;
        }

        static IEnumerable<string> Candidates(string name)
        {
            var versions = name == "libvlc"
                ? new[] { "libvlc.so", "libvlc.so.5" }
                : new[] { "libvlccore.so", "libvlccore.so.9" };
            var dirs = new[] { "", "/usr/lib/x86_64-linux-gnu/", "/usr/lib64/", "/usr/local/lib/", "/lib/x86_64-linux-gnu/" };

            foreach (var v in versions)
            {
                foreach (var d in dirs)
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
