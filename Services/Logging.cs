// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace OrgZ.Services;

/// <summary>
/// First-class logging for OrgZ. Single static entry point — call <see cref="Initialize"/>
/// once at process start before anything else logs. Sinks: file (rolling daily), Debug
/// (VS/Rider Output panel), and Console (stderr in DEBUG builds and unit tests).
///
/// Default minimum levels:
///   DEBUG build:    Verbose
///   RELEASE build:  Warning
///
/// Override at runtime with the <c>ORGZ_LOG_LEVEL</c> environment variable
/// (Verbose|Debug|Information|Warning|Error|Fatal).
///
/// Usage:
///   private static readonly ILogger _log = Logging.For&lt;MyClass&gt;();
///   _log.Information("Did the thing with {Count} items", count);
/// </summary>
public static class Logging
{
    /// <summary>
    /// Runtime-mutable level switch. Bumping this changes the minimum level for ALL
    /// Serilog sinks immediately — useful when wiring a "Verbose logging" toggle in
    /// the Settings dialog without restarting the app.
    /// </summary>
    public static readonly LoggingLevelSwitch LevelSwitch = new(DefaultLevel());

    private static bool _initialized;

    /// <summary>
    /// Per-platform log directory. Windows: %LOCALAPPDATA%\OrgZ\logs. macOS:
    /// ~/Library/Logs/OrgZ. Linux/other: $XDG_STATE_HOME/OrgZ/logs (falls back to
    /// ~/.local/state/OrgZ/logs).
    /// </summary>
    public static string LogDirectory { get; } = ResolveLogDirectory();

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        try
        {
            Directory.CreateDirectory(LogDirectory);
        }
        catch
        {
            // If we can't create the dir, the file sink will silently no-op — Debug
            // and Console sinks still work.
        }

        var logFile = Path.Combine(LogDirectory, "orgz-.log");

        var config = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(LevelSwitch)
            .Enrich.WithProperty("App", "OrgZ")
            .Enrich.WithProperty("Version", typeof(Logging).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: logFile,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: false,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .WriteTo.Debug(
                outputTemplate: "[{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

#if DEBUG
        config = config.WriteTo.Console(
            standardErrorFromLevel: LogEventLevel.Error,
            outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
#endif

        Log.Logger = config.CreateLogger();

        // Catch anything the BCL would otherwise swallow into Watson.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Fatal(e.ExceptionObject as Exception, "AppDomain.UnhandledException (terminating={Terminating})", e.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "TaskScheduler.UnobservedTaskException");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Log.CloseAndFlush();

        Log.Information("OrgZ logging initialized. Level={Level} Dir={Dir}", LevelSwitch.MinimumLevel, LogDirectory);
    }

    /// <summary>
    /// Convenience: per-class contextual logger. Equivalent to Log.ForContext&lt;T&gt;()
    /// but reads naturally as a field initializer: <c>private static readonly ILogger _log = Logging.For&lt;Foo&gt;();</c>
    /// </summary>
    public static ILogger For<T>() => Log.ForContext<T>();

    /// <summary>
    /// Convenience: source-string contextual logger for non-class call sites.
    /// </summary>
    public static ILogger For(string sourceContext) => Log.ForContext(Constants.SourceContextPropertyName, sourceContext);

    public static void Shutdown()
    {
        Log.CloseAndFlush();
    }

    private static LogEventLevel DefaultLevel()
    {
        var envLevel = Environment.GetEnvironmentVariable("ORGZ_LOG_LEVEL");
        if (!string.IsNullOrWhiteSpace(envLevel) && Enum.TryParse<LogEventLevel>(envLevel, true, out var parsed))
        {
            return parsed;
        }

#if DEBUG
        return LogEventLevel.Verbose;
#else
        return LogEventLevel.Warning;
#endif
    }

    private static string ResolveLogDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrgZ", "logs");
        }

        if (OperatingSystem.IsMacOS())
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "OrgZ");
        }

        // Linux / *nix — honor XDG_STATE_HOME, else fall back to ~/.local/state
        var xdgState = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (string.IsNullOrWhiteSpace(xdgState))
        {
            xdgState = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state");
        }
        return Path.Combine(xdgState, "OrgZ", "logs");
    }
}
