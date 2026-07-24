// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using Serilog;

namespace OrgZ.Services.DeviceHelper;

/// <summary>
/// The background service's device-sync op. The GUI hands over a mount path plus the
/// library IDs to copy; the service - the same OrgZ binary, so it owns the whole
/// import stack - re-fingerprints the device, loads those tracks from the shared
/// library database, and runs the standard <see cref="IPodDevice.AddTrackAsync"/>
/// path per track, streaming the usual progress events to a progress file the GUI
/// tails. Because the work lives here, an in-flight sync survives the GUI closing.
/// </summary>
public static class SyncServiceOps
{
    private static readonly ILogger _log = Logging.For("SyncServiceOps");

    public const string OpSyncRun = "sync-run";

    /// <summary>What the GUI sends as the sync-run payload.</summary>
    public sealed record SyncRunPayload(string MountPath, string ProgressPath, List<string> MediaIds);

    // One sync at a time: concurrent writers on one iPod database corrupt it.
    private static int _busy;

    // The sync currently running, so a relaunched GUI can reattach to its progress.
    private static SyncRunPayload? _current;

    /// <summary>The in-flight sync, or null when idle.</summary>
    internal static SyncRunPayload? CurrentJob => Volatile.Read(ref _current);

    /// <summary>Test seam: performs the sync (default: the real device import loop).</summary>
    internal static Func<SyncRunPayload, Task> Runner = RunSyncAsync;

    public static void RegisterAll()
    {
        DeviceHelperDaemon.RegisterOp(OpSyncRun, HandleSyncRun);
    }

    /// <summary>Strict payload parse: null for garbage, blanks, or an empty track list.</summary>
    internal static SyncRunPayload? ParsePayload(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<SyncRunPayload>(payloadJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (payload is null
                || string.IsNullOrWhiteSpace(payload.MountPath)
                || string.IsNullOrWhiteSpace(payload.ProgressPath)
                || payload.MediaIds is not { Count: > 0 })
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

    internal static DeviceHelperProtocol.Response HandleSyncRun(DeviceHelperProtocol.Request request)
    {
        var payload = ParsePayload(request.PayloadJson);
        if (payload is null)
        {
            return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, "sync-run requires a payload of { mountPath, progressPath, mediaIds[] }");
        }

        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            return new(DeviceHelperProtocol.Version, Ok: false, null, null, null, "a device sync is already running");
        }

        Volatile.Write(ref _current, payload);

        _ = Task.Run(async () =>
        {
            try
            {
                await Runner(payload);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "sync-run crashed for {Mount}", payload.MountPath);
            }
            finally
            {
                Volatile.Write(ref _current, null);
                Interlocked.Exchange(ref _busy, 0);
            }
        });

        return new(DeviceHelperProtocol.Version, Ok: true, null, null, null, null);
    }

    private static async Task RunSyncAsync(SyncRunPayload payload)
    {
        using var progress = new SyncProgressWriter(payload.ProgressPath);

        var device = DeviceFingerprint.Identify(new DriveInfo(payload.MountPath));
        if (device is null)
        {
            progress.Write(new SyncEvent("error", Message: $"no device recognized at {payload.MountPath}"));
            return;
        }

        var ffmpeg = ExecutableResolver.Find("ffmpeg");
        if (device.DeviceType == DeviceType.StockIPod && ffmpeg is null)
        {
            progress.Write(new SyncEvent("error", Message: "ffmpeg wasn't found — needed to transcode for the iPod."));
            return;
        }

        // The service shares the library database with the GUI, so IDs are all the
        // payload needs to carry - no track metadata crosses the wire.
        var byId = MediaCache.LoadAll().ToDictionary(i => i.Id, StringComparer.Ordinal);
        var tracks = payload.MediaIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        if (tracks.Count == 0)
        {
            progress.Write(new SyncEvent("error", Message: "none of the requested tracks are in the library"));
            return;
        }

        var ipod = IPodDevice.For(device);
        using var batch = ipod.BeginBatchWrite();
        int done = 0;

        foreach (var track in tracks)
        {
            var title = track.Title ?? track.FileName ?? "track";
            try
            {
                await ipod.AddTrackAsync(track, ffmpeg ?? "ffmpeg",
                    (stage, f) => progress.Write(new SyncEvent("sync-progress", title, done + 1, tracks.Count, stage, f)));
                done++;
                progress.Write(new SyncEvent("sync-track-done", title, done, tracks.Count));
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "sync-run: {Title} failed", title);
                progress.Write(new SyncEvent("sync-track-failed", title, done, tracks.Count, Message: ex.Message));
            }
        }

        IPodArtworkReader.Invalidate(device.MountPath);
        progress.Write(new SyncEvent("sync-done", Index: done, Count: tracks.Count));
        _log.Information("sync-run finished: {Done}/{Total} to {Mount}", done, tracks.Count, payload.MountPath);
    }

    /// <summary>One progress line the GUI tails; mirrors the CD helper's event shape.</summary>
    internal sealed record SyncEvent(
        string Type,
        string? Title = null,
        int Index = 0,
        int Count = 0,
        string? Stage = null,
        double Fraction = 0,
        string? Message = null);

    internal sealed class SyncProgressWriter : IDisposable
    {
        private readonly StreamWriter _writer;
        private readonly Lock _lock = new();

        public SyncProgressWriter(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read | FileShare.Delete)) { AutoFlush = true };
        }

        public void Write(SyncEvent evt)
        {
            var json = JsonSerializer.Serialize(evt);
            lock (_lock)
            {
                _writer.WriteLine(json);
            }
        }

        public void Dispose() => _writer.Dispose();
    }
}
