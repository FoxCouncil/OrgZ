// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;
using OrgZ.Services.DeviceHelper;

namespace OrgZ.Tests;

/// <summary>
/// The service's cd-run op and the client's terminal-event contract. Verification
/// proves accept/execute/complete; adversarial cases attack garbage payloads, missing
/// specs, and the single-job gate (two concurrent disc jobs = a coaster factory).
/// </summary>
[Collection(ServiceOpsCollection.Name)]
public class CdServiceOpsTests
{
    private static DeviceHelperProtocol.Request CdRun(string? payload)
        => new(DeviceHelperProtocol.Version, CdServiceOps.OpCdRun, MountPath: "", Generation: null, PayloadJson: payload);

    // ── Payload parsing ───────────────────────────────────────

    [Fact]
    public void Valid_payload_parses_case_insensitively()
    {
        var p = CdServiceOps.ParsePayload("""{"specPath":"C:\\a.json","progressPath":"C:\\a.jsonl"}""");
        Assert.NotNull(p);
        Assert.Equal(@"C:\a.json", p!.SpecPath);

        var upper = CdServiceOps.ParsePayload("""{"SpecPath":"C:\\a.json","ProgressPath":"C:\\a.jsonl"}""");
        Assert.NotNull(upper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{}")]
    [InlineData("""{"specPath":"","progressPath":"x"}""")]
    [InlineData("""{"specPath":"x","progressPath":" "}""")]
    [InlineData("""{"specPath":"x"}""")]
    public void Hostile_or_incomplete_payloads_parse_to_null(string? payload)
    {
        Assert.Null(CdServiceOps.ParsePayload(payload));
    }

    // ── The op itself ─────────────────────────────────────────

    [Fact]
    public void Garbage_payload_is_refused()
    {
        var resp = CdServiceOps.HandleCdRun(CdRun("lol"));
        Assert.False(resp.Ok);
        Assert.Contains("specPath", resp.Error);
    }

    [Fact]
    public void Missing_spec_file_is_refused_before_any_work_starts()
    {
        var resp = CdServiceOps.HandleCdRun(CdRun("""{"specPath":"Z:\\nope\\missing.json","progressPath":"Z:\\nope\\p.jsonl"}"""));
        Assert.False(resp.Ok);
        Assert.Contains("not found", resp.Error);
    }

    [Fact]
    public async Task Second_disc_job_is_refused_while_one_runs_and_accepted_after()
    {
        var spec = Path.GetTempFileName();
        var release = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var originalRunner = CdServiceOps.Runner;

        try
        {
            CdServiceOps.Runner = (_, _) => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); return 0; };
            var payload = $$"""{"specPath":{{System.Text.Json.JsonSerializer.Serialize(spec)}},"progressPath":"p.jsonl"}""";

            var first = CdServiceOps.HandleCdRun(CdRun(payload));
            Assert.True(first.Ok);
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)), "runner never started");

            var second = CdServiceOps.HandleCdRun(CdRun(payload));
            Assert.False(second.Ok);
            Assert.Contains("already running", second.Error);

            release.Set();

            // The gate frees once the job ends; a new job is then accepted.
            var accepted = false;
            for (int i = 0; i < 100 && !accepted; i++)
            {
                await Task.Delay(50);
                var retry = CdServiceOps.HandleCdRun(CdRun(payload));
                accepted = retry.Ok;
            }

            Assert.True(accepted, "gate never released after the job finished");
            release.Set();
        }
        finally
        {
            release.Set();
            CdServiceOps.Runner = originalRunner;
            File.Delete(spec);
        }
    }

    // ── Terminal-event contract (client tail) ─────────────────

    [Theory]
    [InlineData("burn-done", true)]
    [InlineData("rip-done", true)]
    [InlineData("erase-done", true)]
    [InlineData("error", true)]
    [InlineData("ipod-firmware-result", true)]
    [InlineData("burn-progress", false)]
    [InlineData("rip-progress", false)]
    [InlineData("warning", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void Only_job_ending_events_are_terminal(string? type, bool expected)
    {
        Assert.Equal(expected, CdElevation.IsTerminalEvent(type));
    }

    [Fact]
    public void Exit_codes_mirror_the_helper_process()
    {
        Assert.Equal(0, CdElevation.ExitCodeFor(new CdHelperEvent { Type = "burn-done" }));
        Assert.Equal(1, CdElevation.ExitCodeFor(new CdHelperEvent { Type = "error", Message = "x" }));
        Assert.Equal(0, CdElevation.ExitCodeFor(new CdHelperEvent { Type = "ipod-firmware-result", OsosVersion = "1.3" }));
        Assert.Equal(3, CdElevation.ExitCodeFor(new CdHelperEvent { Type = "ipod-firmware-result", OsosVersion = null }));
    }
}
