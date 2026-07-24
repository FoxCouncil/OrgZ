// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services.DeviceHelper;
using OrgZ.Services.Sharing;
using OrgZ.Views;

namespace OrgZ.Tests;

/// <summary>
/// Turning the library share on from Settings: the service's start/stop/status ops
/// and the status wording the user reads. Adversarial cases attack repeat toggles,
/// a failing server, and never claiming to share when nothing is listening.
/// </summary>
public class ShareHostingTests : IDisposable
{
    private readonly Func<string, int, LibraryShareServer> _originalFactory = ShareServiceOps.ServerFactory;

    public ShareHostingTests()
    {
        // Never bind a real socket in tests: hand back an unstarted server object.
        ShareServiceOps.ServerFactory = (name, port) => new LibraryShareServer(name, port, () => []);
    }

    public void Dispose()
    {
        ShareServiceOps.ResetForTests();
        ShareServiceOps.ServerFactory = _originalFactory;
        GC.SuppressFinalize(this);
    }

    private static DeviceHelperProtocol.Request Req(string op, string? payload = null)
        => new(DeviceHelperProtocol.Version, op, MountPath: "", Generation: null, PayloadJson: payload);

    // ── Start / stop / status ─────────────────────────────────

    [Fact]
    public void Start_reports_the_live_name_and_port_then_status_agrees()
    {
        var start = ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart, """{"shareName":"Fox Library","port":7391}"""));

        Assert.True(start.Ok);
        var state = DeviceHelperClient.ParseShareState(start.ResultJson);
        Assert.True(state.Sharing);
        Assert.Equal("Fox Library", state.Name);
        Assert.Equal(7391, state.Port);

        // Status must not invent a different answer than start did.
        var status = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStatus(Req(ShareServiceOps.OpShareStatus)).ResultJson);
        Assert.Equal(state, status);
    }

    [Fact]
    public void Status_before_any_start_reports_not_sharing()
    {
        var state = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStatus(Req(ShareServiceOps.OpShareStatus)).ResultJson);
        Assert.False(state.Sharing);
    }

    [Fact]
    public void Starting_twice_is_idempotent_rather_than_spawning_a_second_server()
    {
        var first = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart, """{"shareName":"A","port":7391}""")).ResultJson);
        var second = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart, """{"shareName":"B","port":9999}""")).ResultJson);

        // The second call reports what is ACTUALLY live, not what it asked for.
        Assert.True(second.Sharing);
        Assert.Equal(first.Name, second.Name);
        Assert.Equal(first.Port, second.Port);
    }

    [Fact]
    public void Stop_is_safe_when_nothing_is_running_and_after_a_start()
    {
        Assert.True(ShareServiceOps.HandleStop(Req(ShareServiceOps.OpShareStop)).Ok);   // never started

        ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart));
        var stopped = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStop(Req(ShareServiceOps.OpShareStop)).ResultJson);
        Assert.False(stopped.Sharing);

        // And a second stop stays quiet.
        Assert.True(ShareServiceOps.HandleStop(Req(ShareServiceOps.OpShareStop)).Ok);
    }

    [Fact]
    public void Start_can_be_re_enabled_after_a_stop()
    {
        ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart));
        ShareServiceOps.HandleStop(Req(ShareServiceOps.OpShareStop));

        var restarted = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart)).ResultJson);
        Assert.True(restarted.Sharing);
    }

    [Fact]
    public void A_server_that_fails_to_start_reports_failure_and_leaves_nothing_running()
    {
        ShareServiceOps.ResetForTests();
        ShareServiceOps.ServerFactory = (_, _) => throw new InvalidOperationException("port in use");

        var start = ShareServiceOps.HandleStart(Req(ShareServiceOps.OpShareStart));

        Assert.False(start.Ok);
        Assert.Contains("port in use", start.Error);

        // Critically: status must not now claim we're sharing.
        var status = DeviceHelperClient.ParseShareState(ShareServiceOps.HandleStatus(Req(ShareServiceOps.OpShareStatus)).ResultJson);
        Assert.False(status.Sharing);
    }

    // ── Client-side state parsing ─────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"sharing":"yes"}""")]     // wrong type
    public void Malformed_share_results_read_as_not_sharing(string? json)
    {
        Assert.False(DeviceHelperClient.ParseShareState(json).Sharing);
    }

    // ── Status wording ────────────────────────────────────────

    [Fact]
    public void Status_line_never_claims_sharing_without_the_service()
    {
        var text = SettingsDialog.DescribeShareState(serviceAvailable: false, new DeviceHelperClient.ShareState(true, "Fox", 7391));

        Assert.Contains("background service", text);
        Assert.DoesNotContain("Sharing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Status_line_reads_plainly_in_each_state()
    {
        Assert.Equal("Not sharing", SettingsDialog.DescribeShareState(true, null));
        Assert.Equal("Not sharing", SettingsDialog.DescribeShareState(true, new DeviceHelperClient.ShareState(false, null, 0)));
        Assert.Equal("Sharing “Fox Library” on port 7391", SettingsDialog.DescribeShareState(true, new DeviceHelperClient.ShareState(true, "Fox Library", 7391)));

        // A share with no reported name still says something sensible.
        Assert.Contains("This library", SettingsDialog.DescribeShareState(true, new DeviceHelperClient.ShareState(true, "  ", 7391)));
    }
}
