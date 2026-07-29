// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;

namespace OrgZ.Tests;

/// <summary>
/// A real Avalonia application, running headless, so DataGrid behaviour can be asserted in the
/// same suite as everything else.
///
/// Avalonia ships `Avalonia.Headless.XUnit`, which would be the obvious thing to use - but its
/// 12.x line depends on xunit **v3** and this suite is v2, and the two can't co-exist in one
/// assembly. `Avalonia.Headless` itself is test-framework agnostic: `HeadlessUnitTestSession`
/// owns the dispatcher thread and marshals work onto it, which is the only part the attribute
/// package was providing. So we drive it directly.
///
/// The app is built in C# rather than XAML on purpose - the test project has no Avalonia XAML
/// compilation, and OrgZ's own `App` runs the whole library/service startup in
/// `OnFrameworkInitializationCompleted`. What a DataGrid test needs is exactly the two style
/// sets below.
/// </summary>
public sealed class HeadlessApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://OrgZ/App/App.axaml"))
        {
            Source = new Uri("avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"),
        });
    }
}

/// <summary>Entry point + dispatch helper for headless UI tests.</summary>
public static class HeadlessUi
{
    private static readonly Lock Gate = new();
    private static HeadlessUnitTestSession? _session;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    private static HeadlessUnitTestSession Session()
    {
        lock (Gate)
        {
            return _session ??= HeadlessUnitTestSession.StartNew(typeof(HeadlessUi));
        }
    }

    /// <summary>Runs <paramref name="body"/> on the Avalonia UI thread and returns its result.</summary>
    public static Task<T> RunAsync<T>(Func<T> body) => Session().Dispatch(body, CancellationToken.None);

    /// <summary>Runs <paramref name="body"/> on the Avalonia UI thread.</summary>
    public static Task RunAsync(Action body) => Session().Dispatch(() => { body(); return true; }, CancellationToken.None);
}
