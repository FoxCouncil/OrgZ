// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Xunit.Abstractions;
using Xunit.Sdk;

[assembly: Xunit.TestFramework("OrgZ.Tests.HeadlessUiTestFramework", "OrgZ.Tests")]

namespace OrgZ.Tests;

/// <summary>
/// A real Avalonia application, running headless, so DataGrid behaviour can be asserted in the
/// same suite as everything else.
///
/// Avalonia ships `Avalonia.Headless.XUnit`, which would be the obvious thing to use - but its
/// 12.x line depends on xunit **v3** and this suite is v2, and the two can't co-exist in one
/// assembly. Driving `HeadlessUnitTestSession` directly (the previous approach) produced an
/// intermittent, load-sensitive whole-suite HANG (~1 in 5 runs under pressure), root-caused
/// with testhost hang dumps on 2026-08-05:
///
///   - This suite MIXES headless UI tests with plain unit tests whose product code touches
///     `Dispatcher.UIThread` from xunit worker threads (posts, timers). Avalonia binds the
///     dispatcher to whichever thread reaches it first.
///   - The session (re)runs platform setup on ITS thread; when a foreign touch had already
///     claimed the dispatcher, setup's `DefaultRenderLoop.Add` threw "the calling thread
///     cannot access this object because a different thread owns it", the throw escaped the
///     session's queue consumer, and every later Dispatch waited forever - four UI test
///     classes parked on tasks nobody would complete, with the exception sitting on the heap.
///
/// The suite now owns the whole arrangement instead:
///   - ONE dedicated dispatcher thread, ONE `SetupWithoutStarting`, pumping
///     `Dispatcher.MainLoop` for the life of the run (a throwing job faults its own
///     DispatcherOperation task and can never kill the pump);
///   - started EAGERLY by <see cref="HeadlessUiTestFramework"/> - xunit constructs the
///     assembly's test framework before any discovery or test code runs, so the dispatcher
///     thread always claims the UI thread FIRST and the race ceases to exist. (A
///     ModuleInitializer cannot do this: the dispatcher thread executes this module's own
///     code, and blocking the initializer on it deadlocks the loader.)
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

/// <summary>
/// The assembly's xunit test framework: stock behaviour plus claiming the Avalonia UI thread
/// before anything else can. See <see cref="HeadlessApp"/> for the full story.
/// </summary>
public sealed class HeadlessUiTestFramework : XunitTestFramework
{
    public HeadlessUiTestFramework(IMessageSink messageSink) : base(messageSink)
    {
        HeadlessUi.EnsureStarted();
    }
}

/// <summary>
/// The xunit collection every HeadlessUi-dispatching test class MUST join. All headless UI work
/// funnels through the single dispatcher thread, so parallel classes never truly ran
/// concurrently - the collection makes that explicit and keeps their dispatches strictly ordered.
/// </summary>
[CollectionDefinition(Name)]
public sealed class HeadlessUiCollection
{
    public const string Name = "HeadlessUi";
}

/// <summary>Entry point + dispatch helper for headless UI tests.</summary>
public static class HeadlessUi
{
    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    public static AppBuilder BuildAvaloniaApp() => AppBuilder
        .Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });

    internal static void EnsureStarted() => DispatcherThread();

    private static Dispatcher DispatcherThread()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            using var ready = new ManualResetEventSlim();
            Exception? initFailure = null;
            Dispatcher? dispatcher = null;

            var thread = new Thread(() =>
            {
                try
                {
                    BuildAvaloniaApp().SetupWithoutStarting();
                    dispatcher = Dispatcher.UIThread;
                }
                catch (Exception ex)
                {
                    initFailure = ex;
                    ready.Set();
                    return;
                }

                ready.Set();

                // Pump for the life of the test run. A dispatched job that throws faults its
                // own DispatcherOperation task (which the awaiting test observes); anything
                // that still escapes MainLoop must not kill the only UI thread the suite has.
                while (true)
                {
                    try
                    {
                        dispatcher.MainLoop(CancellationToken.None);
                    }
                    catch
                    {
                        // Keep pumping - the faulted job already reported through its task.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "HeadlessUiDispatcher",
            };

            thread.Start();
            ready.Wait();

            if (initFailure is not null)
            {
                throw new InvalidOperationException("Headless Avalonia failed to initialize", initFailure);
            }

            return _dispatcher = dispatcher!;
        }
    }

    /// <summary>Runs <paramref name="body"/> on the Avalonia UI thread and returns its result.</summary>
    public static Task<T> RunAsync<T>(Func<T> body) => DispatcherThread().InvokeAsync(body).GetTask();

    /// <summary>Runs <paramref name="body"/> on the Avalonia UI thread.</summary>
    public static Task RunAsync(Action body) => DispatcherThread().InvokeAsync(body).GetTask();
}
