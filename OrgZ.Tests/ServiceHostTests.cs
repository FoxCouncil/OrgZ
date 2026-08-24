// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text.Json;
using OrgZ.Services.DeviceHelper;

namespace OrgZ.Tests;

/// <summary>
/// The background-service host: op-registry dispatch, capability discovery, and the
/// length-prefixed wire protocol. Verification proves the contract; adversarial cases
/// attack version mismatches, unknown/duplicate ops, handler crashes, and hostile frames.
/// </summary>
// Same collection as the other service classes: this dispatches through the shared op
// registry and the singletons behind it, so running alongside a class that starts a job
// makes both flaky for reasons unrelated to either.
[Collection(ServiceOpsCollection.Name)]
public class ServiceHostTests
{
    private static DeviceHelperProtocol.Request Req(string op, int? version = null, string? payload = null)
        => new(version ?? DeviceHelperProtocol.Version, op, MountPath: "", Generation: null, PayloadJson: payload);

    // ── Dispatch: verification ────────────────────────────────

    [Fact]
    public void Ping_answers_ok()
    {
        var resp = DeviceHelperDaemon.Handle(Req(DeviceHelperProtocol.OpPing));
        Assert.True(resp.Ok);
        Assert.Equal(DeviceHelperProtocol.Version, resp.Version);
    }

    [Fact]
    public void Status_lists_protocol_version_and_every_registered_op()
    {
        var resp = DeviceHelperDaemon.Handle(Req(DeviceHelperProtocol.OpStatus));

        Assert.True(resp.Ok);
        Assert.NotNull(resp.ResultJson);
        using var doc = JsonDocument.Parse(resp.ResultJson!);
        Assert.Equal(DeviceHelperProtocol.Version, doc.RootElement.GetProperty("protocol").GetInt32());

        var ops = doc.RootElement.GetProperty("ops").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains(DeviceHelperProtocol.OpPing, ops);
        Assert.Contains(DeviceHelperProtocol.OpStatus, ops);
        Assert.Contains(DeviceHelperProtocol.OpReadIdentity, ops);
        Assert.Equal(DeviceHelperDaemon.RegisteredOps.Count, ops.Count);
    }

    [Fact]
    public void Registered_op_dispatches_and_receives_the_payload()
    {
        DeviceHelperDaemon.RegisterOp("test-echo", r => new(
            DeviceHelperProtocol.Version, Ok: true, null, null, null, null, ResultJson: r.PayloadJson));

        var resp = DeviceHelperDaemon.Handle(Req("test-echo", payload: """{"n":42}"""));

        Assert.True(resp.Ok);
        Assert.Equal("""{"n":42}""", resp.ResultJson);
    }

    // ── Dispatch: adversarial ─────────────────────────────────

    [Fact]
    public void Version_mismatch_is_refused_not_served()
    {
        var resp = DeviceHelperDaemon.Handle(Req(DeviceHelperProtocol.OpPing, version: DeviceHelperProtocol.Version - 1));
        Assert.False(resp.Ok);
        Assert.Contains("version mismatch", resp.Error);
    }

    [Fact]
    public void Unknown_op_is_refused_with_the_name_echoed()
    {
        var resp = DeviceHelperDaemon.Handle(Req("format-c-colon"));
        Assert.False(resp.Ok);
        Assert.Contains("format-c-colon", resp.Error);
    }

    [Fact]
    public void Duplicate_registration_throws_instead_of_silently_replacing()
    {
        DeviceHelperDaemon.RegisterOp("test-dupe", _ => new(DeviceHelperProtocol.Version, true, null, null, null, null));
        Assert.Throws<InvalidOperationException>(() =>
            DeviceHelperDaemon.RegisterOp("test-dupe", _ => new(DeviceHelperProtocol.Version, true, null, null, null, null)));
    }

    [Fact]
    public void A_crashing_handler_returns_a_failure_response_not_a_dead_connection()
    {
        DeviceHelperDaemon.RegisterOp("test-crash", _ => throw new InvalidOperationException("boom"));

        var resp = DeviceHelperDaemon.Handle(Req("test-crash"));

        Assert.False(resp.Ok);
        Assert.Contains("boom", resp.Error);
    }

    // ── Wire protocol ─────────────────────────────────────────

    [Fact]
    public async Task Request_round_trips_with_generic_payload()
    {
        var original = Req("status", payload: """{"deep":{"nested":true}}""");
        using var ms = new MemoryStream();
        await DeviceHelperProtocol.WriteMessageAsync(ms, original, CancellationToken.None);
        ms.Position = 0;

        var decoded = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Request>(ms, CancellationToken.None);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public async Task Response_round_trips_with_result_json()
    {
        var original = new DeviceHelperProtocol.Response(DeviceHelperProtocol.Version, true, "SER123", "1.3", "MA446", null, """{"ops":[]}""");
        using var ms = new MemoryStream();
        await DeviceHelperProtocol.WriteMessageAsync(ms, original, CancellationToken.None);
        ms.Position = 0;

        var decoded = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Response>(ms, CancellationToken.None);

        Assert.Equal(original, decoded);
    }

    [Fact]
    public async Task Oversized_frame_is_rejected_not_allocated()
    {
        // A hostile client claiming a 2 GB body must not make the service allocate it.
        using var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(int.MaxValue));
        ms.Position = 0;

        var decoded = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Request>(ms, CancellationToken.None);

        Assert.Null(decoded);
    }

    [Fact]
    public async Task Truncated_and_empty_streams_yield_null_not_exceptions()
    {
        using var empty = new MemoryStream();
        Assert.Null(await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Request>(empty, CancellationToken.None));

        using var negative = new MemoryStream(BitConverter.GetBytes(-5));
        Assert.Null(await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Request>(negative, CancellationToken.None));
    }
}
