// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// The background service's disc operations: one "cd-run" op that executes a standard
/// <see cref="CdHelperSpec"/> file (burn / burn-data / erase / rip / firmware read) and
/// streams the usual progress events to the given progress file. The GUI tails that
/// file exactly as it does for the UAC helper - same contract, zero prompts, and the
/// job runs to completion even if the GUI process exits (a started disc must finish).
/// </summary>
public static class CdServiceOps
{
    private static readonly ILogger _log = Logging.For("CdServiceOps");

    public const string OpCdRun = "cd-run";

    /// <summary>What the GUI sends as the cd-run payload.</summary>
    public sealed record CdRunPayload(string SpecPath, string ProgressPath);

    // One disc job at a time: two concurrent SCSI writers on optical hardware is a
    // coaster factory. 0 = idle, 1 = running.
    private static int _busy;

    // The job currently running, so a relaunched GUI can find its progress file and
    // reattach instead of showing nothing while a burn is still going.
    private static CdRunPayload? _current;

    /// <summary>The in-flight disc job, or null when idle.</summary>
    internal static CdRunPayload? CurrentJob => Volatile.Read(ref _current);

    /// <summary>Test seam: the spec runner (default: the real CdHelperMode core).</summary>
    internal static Func<string, string, int> Runner = CdHelperMode.RunSpec;

    /// <summary>Called once from the --device-helper entry before the daemon serves.</summary>
    public static void RegisterAll()
    {
        DeviceHelperDaemon.RegisterOp(OpCdRun, HandleCdRun);
    }

    /// <summary>Strict payload parse: null for garbage/missing/blank paths (never throws).</summary>
    internal static CdRunPayload? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<CdRunPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is null || string.IsNullOrWhiteSpace(payload.SpecPath) || string.IsNullOrWhiteSpace(payload.ProgressPath))
            {
                return null;
            }

            return payload;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static DeviceHelperProtocol.Response HandleCdRun(DeviceHelperProtocol.Request request)
    {
        var payload = ParsePayload(request.PayloadJson);
        if (payload is null)
        {
            return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, "cd-run requires a payload of { specPath, progressPath }");
        }

        if (!File.Exists(payload.SpecPath))
        {
            return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, $"spec file not found: {payload.SpecPath}");
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, "a disc operation is already running");
        }

        Volatile.Write(ref _current, payload);

        _ = Task.Run(() =>
        {
            try
            {
                var exit = Runner(payload.SpecPath, payload.ProgressPath);
                _log.Information("cd-run finished with exit {Exit} (spec {Spec})", exit, payload.SpecPath);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "cd-run crashed (spec {Spec})", payload.SpecPath);
            }
            finally
            {
                Volatile.Write(ref _current, null);
                Interlocked.Exchange(ref _busy, 0);
            }
        });

        return new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null);
    }
}
