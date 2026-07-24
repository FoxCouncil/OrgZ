// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// Reports the service's in-flight work so a relaunched GUI can reattach instead of
/// showing an idle window while a burn or sync is still running. Read-only: this op
/// never starts or cancels anything.
/// </summary>
public static class JobsServiceOps
{
    public const string OpJobs = "jobs";

    /// <summary>One running job the GUI can follow.</summary>
    public sealed record RunningJob(string Kind, string ProgressPath, string? Target);

    public static void RegisterAll()
    {
        DeviceHelperDaemon.RegisterOp(OpJobs, HandleJobs);
    }

    /// <summary>The live jobs, newest state at call time.</summary>
    internal static List<RunningJob> CurrentJobs()
    {
        var jobs = new List<RunningJob>();

        if (CdServiceOps.CurrentJob is { } disc)
        {
            jobs.Add(new RunningJob("disc", disc.ProgressPath, disc.SpecPath));
        }

        if (SyncServiceOps.CurrentJob is { } sync)
        {
            jobs.Add(new RunningJob("sync", sync.ProgressPath, sync.MountPath));
        }

        return jobs;
    }

    // The record serializes PascalCase by default while the reader (and the rest of the
    // protocol) speaks camelCase - without this the service reports jobs the GUI can
    // never parse, and a relaunched OrgZ silently fails to reattach.
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static DeviceHelperProtocol.Response HandleJobs(DeviceHelperProtocol.Request request)
        => new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null,
            JsonSerializer.Serialize(new { jobs = CurrentJobs() }, _json));

    /// <summary>Client-side parse. Malformed or absent JSON reads as "nothing running".</summary>
    internal static List<RunningJob> ParseJobs(string? resultJson)
    {
        var jobs = new List<RunningJob>();
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return jobs;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("jobs", out var array) || array.ValueKind != JsonValueKind.Array)
            {
                return jobs;
            }

            foreach (var entry in array.EnumerateArray())
            {
                string? Text(string name) => entry.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

                var kind = Text("kind");
                var progress = Text("progressPath");
                if (string.IsNullOrEmpty(kind) || string.IsNullOrEmpty(progress))
                {
                    continue;   // a job we can't follow is not a job worth reporting
                }

                jobs.Add(new RunningJob(kind, progress, Text("target")));
            }
        }
        catch (JsonException)
        {
            // Fall through with whatever parsed.
        }

        return jobs;
    }
}
