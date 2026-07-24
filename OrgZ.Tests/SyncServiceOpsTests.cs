// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.DeviceHelper;
using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// The service's sync-run op and the GUI's hand-off gate. Verification proves
/// accept/execute; adversarial cases attack hostile payloads, the single-sync gate
/// (concurrent writers corrupt an iPod database), and the opt-in gate.
/// </summary>
public class SyncServiceOpsTests
{
    private static DeviceHelperProtocol.Request SyncRun(string? payload)
        => new(DeviceHelperProtocol.Version, SyncServiceOps.OpSyncRun, MountPath: "", Generation: null, PayloadJson: payload);

    private static MediaItem Track(string id) => new() { Id = id, Kind = MediaKind.Music, Title = id };

    // ── Payload parsing ───────────────────────────────────────

    [Fact]
    public void Valid_payload_parses_case_insensitively()
    {
        var p = SyncServiceOps.ParsePayload("""{"mountPath":"E:\\","progressPath":"p.jsonl","mediaIds":["a","b"]}""");
        Assert.NotNull(p);
        Assert.Equal(@"E:\", p!.MountPath);
        Assert.Equal(2, p.MediaIds.Count);

        Assert.NotNull(SyncServiceOps.ParsePayload("""{"MountPath":"E:\\","ProgressPath":"p","MediaIds":["a"]}"""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("""{"mountPath":"E:\\","progressPath":"p"}""")]
    [InlineData("""{"mountPath":"E:\\","progressPath":"p","mediaIds":[]}""")]
    [InlineData("""{"mountPath":"","progressPath":"p","mediaIds":["a"]}""")]
    [InlineData("""{"mountPath":"E:\\","progressPath":"  ","mediaIds":["a"]}""")]
    public void Hostile_or_incomplete_payloads_parse_to_null(string? payload)
    {
        Assert.Null(SyncServiceOps.ParsePayload(payload));
    }

    // ── The op ────────────────────────────────────────────────

    [Fact]
    public void Garbage_payload_is_refused()
    {
        var resp = SyncServiceOps.HandleSyncRun(SyncRun("nope"));
        Assert.False(resp.Ok);
        Assert.Contains("mediaIds", resp.Error);
    }

    [Fact]
    public async Task Second_sync_is_refused_while_one_runs_and_accepted_after()
    {
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var original = SyncServiceOps.Runner;

        try
        {
            SyncServiceOps.Runner = _ => Task.Run(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            const string payload = """{"mountPath":"E:\\","progressPath":"p.jsonl","mediaIds":["a"]}""";

            Assert.True(SyncServiceOps.HandleSyncRun(SyncRun(payload)).Ok);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "runner never started");

            var second = SyncServiceOps.HandleSyncRun(SyncRun(payload));
            Assert.False(second.Ok);
            Assert.Contains("already running", second.Error);

            release.Set();

            var accepted = false;
            for (int i = 0; i < 100 && !accepted; i++)
            {
                await Task.Delay(50);
                accepted = SyncServiceOps.HandleSyncRun(SyncRun(payload)).Ok;
            }

            Assert.True(accepted, "gate never released");
        }
        finally
        {
            release.Set();
            SyncServiceOps.Runner = original;
        }
    }

    // ── GUI hand-off gate ─────────────────────────────────────

    [Fact]
    public async Task Hand_off_is_skipped_when_keep_alive_is_off()
    {
        var called = false;

        var handed = await MainWindowViewModel.TryHandOffSyncToServiceAsync(@"E:\", [Track("a")],
            (_, _) => { called = true; return Task.FromResult(true); }, keepAliveEnabled: false);

        Assert.False(handed);
        Assert.False(called);
    }

    [Fact]
    public async Task Hand_off_sends_the_media_ids_when_enabled()
    {
        IReadOnlyList<string>? sent = null;

        var handed = await MainWindowViewModel.TryHandOffSyncToServiceAsync(@"E:\", [Track("a"), Track("b")],
            (_, ids) => { sent = ids; return Task.FromResult(true); }, keepAliveEnabled: true);

        Assert.True(handed);
        Assert.Equal(["a", "b"], sent!);
    }

    [Fact]
    public async Task A_refusing_service_falls_back_to_in_process()
    {
        var handed = await MainWindowViewModel.TryHandOffSyncToServiceAsync(@"E:\", [Track("a")],
            (_, _) => Task.FromResult(false), keepAliveEnabled: true);

        Assert.False(handed);
    }

    [Fact]
    public async Task Tracks_without_ids_never_reach_the_service()
    {
        var called = false;

        var handed = await MainWindowViewModel.TryHandOffSyncToServiceAsync(@"E:\", [new MediaItem { Id = "", Kind = MediaKind.Music }],
            (_, _) => { called = true; return Task.FromResult(true); }, keepAliveEnabled: true);

        Assert.False(handed);
        Assert.False(called);
    }
}
