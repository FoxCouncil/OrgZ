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
    public static void Main(string[] args)
    {
        // Logging must come up first so Velopack/Avalonia init failures are captured.
        Logging.Initialize();

        try
        {
            // Single-instance via D-Bus name ownership on Linux: if another OrgZ is
            // already running, ask it to raise its window and exit this process.
            if (SingleInstanceGuard.TryBecomePrimary())
            {
                return;
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
    }

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
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().WithIcons().LogToTrace();
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
