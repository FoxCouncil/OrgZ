// Copyright (c) 2025 Fox Diller

using Avalonia;
using Projektanker.Icons.Avalonia;
using Projektanker.Icons.Avalonia.FontAwesome;

namespace OrgZ;

internal class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        _ = BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .WithIcons()
                .LogToTrace();
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
