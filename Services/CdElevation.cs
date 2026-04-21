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
/// in-process execution — <c>sr0</c> on Linux only needs group membership,
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
                proc = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null for the elevated helper.");
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
