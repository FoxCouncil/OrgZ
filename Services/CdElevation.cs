// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Serilog;

namespace OrgZ.Services;

/// <summary>
/// Spawns an elevated copy of OrgZ.exe (via <c>ShellExecute</c> / <c>runas</c>)
/// to perform a CD rip or burn that needs <c>IOCTL_SCSI_PASS_THROUGH</c>
/// administrator rights, and tails the progress file that the elevated helper
/// appends JSON events to.  Non-Windows callers fall through to direct
/// in-process execution - <c>sr0</c> on Linux only needs group membership,
/// not root.
/// </summary>
internal static class CdElevation
{
    private static readonly ILogger _log = Logging.For("CdElevation");
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(150);

    // Native error code returned by CreateProcess when the user dismisses the UAC prompt.
    private const int ErrorCancelled = 1223;

    public static bool RequiresElevation => OperatingSystem.IsWindows();

    /// <summary>
    /// Runs an elevated helper with the given spec, forwarding progress events
    /// to <paramref name="onEvent"/> as they arrive.  Returns the helper's
    /// process exit code (0 = success).
    /// </summary>
    public static async Task<int> RunElevatedAsync(
        CdHelperSpec spec,
        Action<CdHelperEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(onEvent);

        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Environment.ProcessPath is unavailable — can't locate the OrgZ executable for the elevated helper.");

        var temp = Path.Combine(Path.GetTempPath(), "OrgZ", "CdHelper");
        Directory.CreateDirectory(temp);

        var sessionId = Guid.NewGuid().ToString("N");
        var specPath = Path.Combine(temp, $"spec-{sessionId}.json");
        var progressPath = Path.Combine(temp, $"progress-{sessionId}.jsonl");

        try
        {
            var specJson = JsonSerializer.Serialize(spec, CdHelperJsonContext.Default.CdHelperSpec);
            await File.WriteAllTextAsync(specPath, specJson, cancellationToken);

            // The installed background service runs disc ops with ZERO prompts and keeps a
            // started disc going even if the GUI exits - prefer it. Any refusal (not
            // installed, busy, old protocol) quietly falls through to the per-op UAC helper.
            if (await TryRunViaServiceAsync(specPath, progressPath, onEvent, cancellationToken) is int serviceExit)
            {
                return serviceExit;
            }

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            };

            psi.ArgumentList.Add(CdHelperMode.ArgSwitch);
            psi.ArgumentList.Add("--spec");
            psi.ArgumentList.Add(specPath);
            psi.ArgumentList.Add("--progress");
            psi.ArgumentList.Add(progressPath);

            Process proc;

            try
            {
                // ShellExecute with the runas verb blocks until the UAC prompt is
                // answered - keep that wait off the caller's (UI) thread.
                proc = await Task.Run(() => Process.Start(psi), cancellationToken) ?? throw new InvalidOperationException("Process.Start returned null for the elevated helper.");
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                _log.Information("UAC elevation declined for CD operation");
                onEvent(new CdHelperEvent { Type = "error", Message = "Administrator access declined." });
                return ErrorCancelled;
            }

            _log.Information("Spawned elevated CD helper PID {Pid} for {Op}", proc.Id, spec.Operation);

            using (proc)
            {
                var tailTask = TailProgressAsync(progressPath, proc, onEvent, cancellationToken);
                await proc.WaitForExitAsync(cancellationToken);
                await tailTask;
                return proc.ExitCode;
            }
        }
        finally
        {
            TryDelete(specPath);
            TryDelete(progressPath);
        }
    }

    /// <summary>
    /// Runs the spec through the background service. Returns the exit code when the
    /// service accepted and finished the job, or null when the service is unreachable
    /// or refused (caller falls back to the UAC helper).
    /// </summary>
    private static async Task<int?> TryRunViaServiceAsync(
        string specPath,
        string progressPath,
        Action<CdHelperEvent> onEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            var accepted = await DeviceHelper.DeviceHelperClient.RunCdSpecAsync(specPath, progressPath);
            if (!accepted)
            {
                return null;
            }

            _log.Information("Disc operation handed to the background service (spec {Spec})", specPath);

            // No process handle to watch - the job ends when a terminal event lands.
            var terminal = await TailProgressUntilTerminalAsync(progressPath, onEvent, cancellationToken);
            return terminal is null ? 1 : ExitCodeFor(terminal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Background service unavailable for disc op - using the UAC helper");
            return null;
        }
    }

    /// <summary>Events that end a helper job. The service tail stops on these.</summary>
    internal static bool IsTerminalEvent(string? type) => type
        is "burn-done" or "rip-done" or "erase-done" or "error" or "ipod-firmware-result";

    /// <summary>Exit code a terminal event implies - mirrors the helper process's codes.</summary>
    internal static int ExitCodeFor(CdHelperEvent evt) => evt.Type switch
    {
        "error" => 1,
        "ipod-firmware-result" => evt.OsosVersion != null ? 0 : 3,
        _ => 0,
    };

    private static async Task<CdHelperEvent?> TailProgressUntilTerminalAsync(
        string progressPath,
        Action<CdHelperEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var waitForFile = TimeSpan.FromSeconds(10);
        var startWait = DateTime.UtcNow;

        while (!File.Exists(progressPath))
        {
            if (DateTime.UtcNow - startWait > waitForFile)
            {
                _log.Warning("Service did not create progress file within {Seconds}s", waitForFile.TotalSeconds);
                return null;
            }

            await Task.Delay(PollInterval, cancellationToken);
        }

        using var fs = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        while (true)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                CdHelperEvent? evt;
                try
                {
                    evt = JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent);
                }
                catch (JsonException ex)
                {
                    _log.Warning(ex, "Malformed progress line: {Line}", line);
                    continue;
                }

                if (evt == null)
                {
                    continue;
                }

                onEvent(evt);
                if (IsTerminalEvent(evt.Type))
                {
                    return evt;
                }
            }

            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    private static async Task TailProgressAsync(
        string progressPath,
        Process helper,
        Action<CdHelperEvent> onEvent,
        CancellationToken cancellationToken)
    {
        var waitForFile = TimeSpan.FromSeconds(10);
        var startWait = DateTime.UtcNow;

        while (!File.Exists(progressPath))
        {
            if (helper.HasExited)
            {
                return;
            }

            if (DateTime.UtcNow - startWait > waitForFile)
            {
                _log.Warning("Elevated helper did not create progress file within {Seconds}s", waitForFile.TotalSeconds);
                return;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        using var fs = new FileStream(progressPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);

        while (true)
        {
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) != null)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                CdHelperEvent? evt;

                try
                {
                    evt = JsonSerializer.Deserialize(line, CdHelperJsonContext.Default.CdHelperEvent);
                }
                catch (JsonException ex)
                {
                    _log.Warning(ex, "Malformed progress line: {Line}", line);
                    continue;
                }

                if (evt != null)
                {
                    onEvent(evt);
                }
            }

            if (helper.HasExited)
            {
                return;
            }

            try
            {
                await Task.Delay(PollInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "Could not delete scratch file {Path}", path);
        }
    }
}
