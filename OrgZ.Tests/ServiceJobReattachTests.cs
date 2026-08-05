// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.DeviceHelper;
using OrgZ.ViewModels;

namespace OrgZ.Tests;

/// <summary>
/// Reattaching to work the service kept running while OrgZ was closed: the jobs op
/// (what's in flight), its wire parsing, and the LCD wording. Adversarial cases attack
/// jobs that can't be followed, malformed payloads, and stale reporting after a job ends.
/// </summary>
[Collection(ServiceOpsCollection.Name)]
public class ServiceJobReattachTests
{
    private static DeviceHelperProtocol.Request Jobs()
        => new(DeviceHelperProtocol.Version, JobsServiceOps.OpJobs, MountPath: "", Generation: null);

    // ── Reporting live work ───────────────────────────────────

    [Fact]
    public void Nothing_running_reports_no_jobs()
    {
        var jobs = JobsServiceOps.ParseJobs(JobsServiceOps.HandleJobs(Jobs()).ResultJson);
        Assert.Empty(jobs);
    }

    [Fact]
    public async Task A_running_disc_job_is_reported_then_disappears_when_it_ends()
    {
        var spec = Path.GetTempFileName();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var original = CdServiceOps.Runner;

        try
        {
            CdServiceOps.Runner = (_, _) => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return 0; };

            var payload = $$"""{"specPath":{{System.Text.Json.JsonSerializer.Serialize(spec)}},"progressPath":"C:\\p.jsonl"}""";
            Assert.True(CdServiceOps.HandleCdRun(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, CdServiceOps.OpCdRun, "", null, payload)).Ok);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            var running = JobsServiceOps.ParseJobs(JobsServiceOps.HandleJobs(Jobs()).ResultJson);
            var job = Assert.Single(running);
            Assert.Equal("disc", job.Kind);
            Assert.Equal(@"C:\p.jsonl", job.ProgressPath);
            Assert.Equal(spec, job.Target);

            release.Set();

            // Once finished, the service must stop advertising it - a stale job would
            // make a relaunched GUI wait forever on a dead progress file.
            var cleared = false;
            for (int i = 0; i < 100 && !cleared; i++)
            {
                await Task.Delay(50);
                cleared = JobsServiceOps.ParseJobs(JobsServiceOps.HandleJobs(Jobs()).ResultJson).Count == 0;
            }

            Assert.True(cleared, "job still reported after it finished");
        }
        finally
        {
            release.Set();
            CdServiceOps.Runner = original;
            File.Delete(spec);
        }
    }

    [Fact]
    public async Task A_running_sync_reports_its_mount_as_the_target()
    {
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var original = SyncServiceOps.Runner;

        try
        {
            SyncServiceOps.Runner = _ => Task.Run(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });

            Assert.True(SyncServiceOps.HandleSyncRun(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, SyncServiceOps.OpSyncRun, "", null,
                """{"mountPath":"E:\\","progressPath":"C:\\s.jsonl","mediaIds":["a"]}""")).Ok);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            var job = Assert.Single(JobsServiceOps.ParseJobs(JobsServiceOps.HandleJobs(Jobs()).ResultJson));
            Assert.Equal("sync", job.Kind);
            Assert.Equal(@"E:\", job.Target);

            release.Set();
            await Task.Delay(100);
        }
        finally
        {
            release.Set();
            SyncServiceOps.Runner = original;
        }
    }

    // ── Reload safety ─────────────────────────────────────────

    [Fact]
    public async Task Reload_is_refused_while_a_disc_job_is_in_flight()
    {
        var spec = Path.GetTempFileName();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var original = CdServiceOps.Runner;

        try
        {
            CdServiceOps.Runner = (_, _) => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return 0; };

            var payload = $$"""{"specPath":{{System.Text.Json.JsonSerializer.Serialize(spec)}},"progressPath":"C:\\p.jsonl"}""";
            Assert.True(CdServiceOps.HandleCdRun(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, CdServiceOps.OpCdRun, "", null, payload)).Ok);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));

            // Mid-burn, a reload restarts the process and the job dies with it - refuse.
            var refused = DeviceHelperDaemon.Handle(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, DeviceHelperProtocol.OpReload, "", null));
            Assert.False(refused.Ok);
            Assert.Contains("busy", refused.Error, StringComparison.OrdinalIgnoreCase);

            release.Set();
            var cleared = false;
            for (int i = 0; i < 100 && !cleared; i++)
            {
                await Task.Delay(50);
                cleared = CdServiceOps.CurrentJob is null;
            }
            Assert.True(cleared, "disc job never drained");

            // Idle again: reload proceeds.
            Assert.True(DeviceHelperDaemon.Handle(new DeviceHelperProtocol.Request(
                DeviceHelperProtocol.Version, DeviceHelperProtocol.OpReload, "", null)).Ok);
        }
        finally
        {
            release.Set();
            CdServiceOps.Runner = original;
            File.Delete(spec);
        }
    }

    // ── Wire parsing ──────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("{}")]
    [InlineData("""{"jobs":"nope"}""")]
    [InlineData("""{"jobs":[]}""")]
    public void Malformed_job_payloads_read_as_nothing_running(string? json)
    {
        Assert.Empty(JobsServiceOps.ParseJobs(json));
    }

    [Fact]
    public void Jobs_without_a_followable_progress_path_are_dropped()
    {
        const string json = """
        {"jobs":[
          {"kind":"disc"},
          {"kind":"","progressPath":"C:\\p.jsonl"},
          {"progressPath":"C:\\q.jsonl"},
          {"kind":"sync","progressPath":"C:\\good.jsonl","target":"E:\\"}
        ]}
        """;

        var job = Assert.Single(JobsServiceOps.ParseJobs(json));
        Assert.Equal(@"C:\good.jsonl", job.ProgressPath);
    }

    // ── LCD wording ───────────────────────────────────────────

    [Fact]
    public void Resumed_jobs_announce_themselves_as_already_in_progress()
    {
        Assert.Equal("Burning (in progress)",
            MainWindowViewModel.DescribeResumedJob(new JobsServiceOps.RunningJob("disc", "p", null)));

        Assert.Equal("Syncing E:\\ (in progress)",
            MainWindowViewModel.DescribeResumedJob(new JobsServiceOps.RunningJob("sync", "p", @"E:\")));

        // A sync with no mount, and an unknown kind, still read as something.
        Assert.Equal("Syncing (in progress)",
            MainWindowViewModel.DescribeResumedJob(new JobsServiceOps.RunningJob("sync", "p", null)));
        Assert.Equal("Working (in progress)",
            MainWindowViewModel.DescribeResumedJob(new JobsServiceOps.RunningJob("future-op", "p", null)));
    }
}
