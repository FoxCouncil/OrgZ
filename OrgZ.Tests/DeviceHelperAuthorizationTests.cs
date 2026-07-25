// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.IO.Pipes;
using OrgZ.Services.DeviceHelper;

namespace OrgZ.Tests;

/// <summary>
/// The two things standing between a local process and a root/LocalSystem daemon that
/// issues raw SCSI: the peer-credential gate, and the transport carrying the frames.
///
/// Both were untested. Every op BEHIND the gate had tests; the gate itself had none, and
/// <c>ServiceHostTests</c> proved the frame codec against a MemoryStream but never opened
/// a real pipe - so partial reads, mid-frame disconnects, and concurrent clients were all
/// unexercised. Sharing taught us in 0.9.18 what "each half tested separately" is worth.
///
/// Adversarial cases attack the direction that actually matters: a stranger's UID, a
/// credential read that fails, a client that hangs up mid-frame, and a declared length
/// that would have us allocate on command.
/// </summary>
public class DeviceHelperAuthorizationTests
{
    private const uint Owner = 501;
    private const uint Stranger = 502;
    private const uint Root = 0;

    // ── The gate ──────────────────────────────────────────────

    [Fact]
    public void The_owner_the_installer_recorded_is_served()
    {
        Assert.True(DeviceHelperDaemon.IsPeerAllowed(Owner, credentialsReadable: true, peerUid: Owner));
    }

    [Fact]
    public void Another_local_account_is_refused()
    {
        // The whole point: a second user on the same machine cannot drive this daemon,
        // and neither can anything they run.
        Assert.False(DeviceHelperDaemon.IsPeerAllowed(Owner, credentialsReadable: true, peerUid: Stranger));
    }

    [Fact]
    public void Root_is_always_served()
    {
        // Not a hole: root can already do everything this daemon does. Locking it out
        // would only break diagnosing the daemon while root-owned.
        Assert.True(DeviceHelperDaemon.IsPeerAllowed(Owner, credentialsReadable: true, peerUid: Root));
    }

    [Fact]
    public void Unreadable_credentials_fail_CLOSED_when_an_owner_is_enforced()
    {
        // The single most important line in the gate. "We couldn't establish who you are"
        // must never resolve to "come in" - that would turn any platform where the
        // getsockopt path fails into a wide-open root service.
        Assert.False(DeviceHelperDaemon.IsPeerAllowed(Owner, credentialsReadable: false, peerUid: 0));
        Assert.False(DeviceHelperDaemon.IsPeerAllowed(Owner, credentialsReadable: false, peerUid: Owner));
    }

    [Fact]
    public void Unreadable_credentials_fail_open_only_for_a_legacy_install_with_no_owner()
    {
        // A pre-owner-UID install keeps working rather than bricking on upgrade; there the
        // socket's file mode is the only guard, which is why every install path since
        // stamps an owner.
        Assert.True(DeviceHelperDaemon.IsPeerAllowed(ownerUid: null, credentialsReadable: false, peerUid: 0));
        Assert.True(DeviceHelperDaemon.IsPeerAllowed(ownerUid: null, credentialsReadable: true, peerUid: Stranger));
    }

    [Fact]
    public void Owner_uid_zero_is_enforced_like_any_other_and_not_read_as_unset()
    {
        // uint? not uint, precisely so a root-owned install isn't mistaken for "no owner
        // configured" and quietly downgraded to fail-open.
        Assert.True(DeviceHelperDaemon.IsPeerAllowed(0, credentialsReadable: true, peerUid: 0));
        Assert.False(DeviceHelperDaemon.IsPeerAllowed(0, credentialsReadable: false, peerUid: 0));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(1000u)]
    [InlineData(uint.MaxValue)]
    public void No_uid_but_the_owner_and_root_gets_through(uint peer)
    {
        Assert.False(DeviceHelperDaemon.IsPeerAllowed(Owner, credentialsReadable: true, peerUid: peer));
    }

    // ── The transport, for real ───────────────────────────────

    private static string PipeName() => $"orgz-test-{Guid.NewGuid():N}";

    /// <summary>Stands the daemon's own serve loop on one end of a real named pipe.</summary>
    private static async Task<DeviceHelperProtocol.Response?> RoundTripAsync(
        DeviceHelperProtocol.Request request,
        Func<NamedPipeClientStream, CancellationToken, Task>? clientOverride = null)
    {
        var name = PipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await DeviceHelperDaemon.ServeAsync(server, cts.Token);
        }, cts.Token);

        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);

        if (clientOverride is not null)
        {
            await clientOverride(client, cts.Token);
            await serving;
            return null;
        }

        await DeviceHelperProtocol.WriteMessageAsync(client, request, cts.Token);
        var response = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Response>(client, cts.Token);
        await serving;
        return response;
    }

    private static DeviceHelperProtocol.Request Ping()
        => new(DeviceHelperProtocol.Version, DeviceHelperProtocol.OpPing, MountPath: "", Generation: null);

    [Fact]
    public async Task A_request_survives_a_real_pipe_and_comes_back_answered()
    {
        var response = await RoundTripAsync(Ping());

        Assert.NotNull(response);
        Assert.True(response!.Ok);
        Assert.Equal(DeviceHelperProtocol.Version, response.Version);
    }

    [Fact]
    public async Task Capability_discovery_crosses_the_wire_with_its_payload_intact()
    {
        var response = await RoundTripAsync(new(DeviceHelperProtocol.Version, DeviceHelperProtocol.OpStatus, "", null));

        Assert.NotNull(response);
        Assert.True(response!.Ok);

        // ResultJson is the v2 addition - a field that only exists once a real response
        // has been serialized, sent, and deserialized by the other side.
        Assert.NotNull(response.ResultJson);
        using var doc = System.Text.Json.JsonDocument.Parse(response.ResultJson!);
        Assert.Equal(DeviceHelperProtocol.Version, doc.RootElement.GetProperty("protocol").GetInt32());
        Assert.Contains(DeviceHelperProtocol.OpPing, doc.RootElement.GetProperty("ops").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_version_mismatch_is_refused_politely_over_the_wire_not_by_hanging_up()
    {
        var response = await RoundTripAsync(new(Version: 99, DeviceHelperProtocol.OpPing, "", null));

        Assert.NotNull(response);
        Assert.False(response!.Ok);
        Assert.Contains("version mismatch", response.Error);
    }

    [Fact]
    public async Task A_request_split_across_writes_is_reassembled()
    {
        // A pipe delivers what it delivers; nothing guarantees the 4-byte length prefix
        // and the body arrive together. This is the failure that only shows up on a slow
        // machine, in front of a user.
        var name = PipeName();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(cts.Token);
            await DeviceHelperDaemon.ServeAsync(server, cts.Token);
        }, cts.Token);

        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(cts.Token);

        var body = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Ping(), new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
        var prefix = BitConverter.GetBytes(body.Length);

        // Byte at a time for the prefix, then the body in two halves.
        foreach (var b in prefix)
        {
            await client.WriteAsync(new[] { b }, cts.Token);
            await client.FlushAsync(cts.Token);
        }
        await client.WriteAsync(body.AsMemory(0, body.Length / 2), cts.Token);
        await client.FlushAsync(cts.Token);
        await client.WriteAsync(body.AsMemory(body.Length / 2), cts.Token);
        await client.FlushAsync(cts.Token);

        var response = await DeviceHelperProtocol.ReadMessageAsync<DeviceHelperProtocol.Response>(client, cts.Token);
        await serving;

        Assert.NotNull(response);
        Assert.True(response!.Ok);
    }

    [Fact]
    public async Task A_client_that_hangs_up_mid_frame_ends_the_connection_quietly()
    {
        // Announce a 400-byte body, send 10, vanish. A peer that promises a frame and then
        // disappears is the same event as one that never spoke: no message. The daemon
        // returns, and a hang would surface as the rig's 20 s token cancelling.
        var thrown = await Record.ExceptionAsync(() => RoundTripAsync(Ping(), async (client, ct) =>
        {
            await client.WriteAsync(BitConverter.GetBytes(400), ct);
            await client.WriteAsync(new byte[10], ct);
            await client.FlushAsync(ct);
            client.Close();
        }));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task A_client_that_connects_and_says_nothing_is_not_a_leaked_handler()
    {
        var thrown = await Record.ExceptionAsync(() => RoundTripAsync(Ping(), (client, _) =>
        {
            client.Close();
            return Task.CompletedTask;
        }));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task An_absurd_declared_length_is_refused_without_allocating_it()
    {
        // 1.5 GB claimed by a hostile client. The reader caps at 1 MB and returns no
        // message, so the daemon closes rather than dying on an OOM it was told to have.
        var thrown = await Record.ExceptionAsync(() => RoundTripAsync(Ping(), async (client, ct) =>
        {
            await client.WriteAsync(BitConverter.GetBytes(1_500_000_000), ct);
            await client.FlushAsync(ct);
            client.Close();
        }));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task A_frame_that_is_not_our_json_is_ignored_rather_than_crashing_the_handler()
    {
        // Anything at all can connect to a local endpoint - a port scanner, another app's
        // client, a garbled retry. None of it may take the daemon down.
        var thrown = await Record.ExceptionAsync(() => RoundTripAsync(Ping(), async (client, ct) =>
        {
            var junk = "this is not json"u8.ToArray();
            await client.WriteAsync(BitConverter.GetBytes(junk.Length), ct);
            await client.WriteAsync(junk, ct);
            await client.FlushAsync(ct);
            client.Close();
        }));

        Assert.Null(thrown);
    }

    [Fact]
    public async Task Several_clients_in_flight_each_get_their_own_answer()
    {
        // The daemon spawns a handler per connection; this is what proves two of them
        // don't share the stream they're writing to.
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RoundTripAsync(Ping())));

        Assert.All(responses, r =>
        {
            Assert.NotNull(r);
            Assert.True(r!.Ok);
        });
    }
}
