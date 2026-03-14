// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;
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
        VelopackApp.Build().Run();

#if WINDOWS
        SmtcNativeMethods.SetCurrentProcessExplicitAppUserModelID("com.foxcouncil.orgz");
        ShortcutInstaller.EnsureShortcut();
#endif

        _ = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
