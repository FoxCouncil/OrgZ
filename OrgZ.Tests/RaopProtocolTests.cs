// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// The deterministic halves of the RAOP stack: ALAC framing, session crypto, RTP/sync/
/// timing packet layout, RTSP framing, SDP, and the mDNS SRV/A parse. Everything a wrong
/// byte would break silently on real hardware is pinned here; the live handshake itself
/// needs a receiver and is verified on metal.
/// </summary>
public class RaopProtocolTests
{
    // ===== ALAC uncompressed framing =====

    /// <summary>Little-endian s16 stereo frame: left then right.</summary>
    private static byte[] Pcm(params (short Left, short Right)[] frames)
    {
        var bytes = new byte[frames.Length * 4];
        for (var i = 0; i < frames.Length; i++)
        {
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), frames[i].Left);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), frames[i].Right);
        }
        return bytes;
    }

    [Fact]
    public void Alac_header_marks_an_uncompressed_frame_of_the_declared_block_size()
    {
        var encoded = RaopAlac.Encode(Pcm((0, 0)), blockSize: 352);

        Assert.Equal(0x20, encoded[0]);   // element tag
        Assert.Equal(0x00, encoded[1]);
        Assert.Equal(0x12, encoded[2]);   // uncompressed flag, block-size top bit clear for 352

        // Bytes 3-6 carry the block size shifted left one bit: 352 == 0b101100000.
        var packed = ((uint)encoded[3] << 24) | ((uint)encoded[4] << 16) | ((uint)encoded[5] << 8) | encoded[6];
        Assert.Equal(352u, (packed >> 1) & 0x7FFFFFF);
    }

    [Fact]
    public void Alac_payload_carries_samples_big_endian_shifted_one_bit()
    {
        // A single frame with distinct bytes in all four positions, so a swapped channel or
        // a lost shift shows up immediately.
        var encoded = RaopAlac.Encode(Pcm((unchecked((short)0x1234), unchecked((short)0x5678))), blockSize: 1);

        // Header is 7 bytes; the last of them already holds the first sample bit.
        // Reconstruct the sample run by undoing the one-bit shift across bytes 6..10.
        var bits = ((ulong)encoded[6] << 32) | ((ulong)encoded[7] << 24) | ((ulong)encoded[8] << 16)
                 | ((ulong)encoded[9] << 8) | encoded[10];
        var samples = (uint)((bits >> 1) & 0xFFFFFFFF);

        // Big-endian on the wire: left high byte, left low, right high, right low.
        Assert.Equal(0x12u, (samples >> 24) & 0xFF);
        Assert.Equal(0x34u, (samples >> 16) & 0xFF);
        Assert.Equal(0x56u, (samples >> 8) & 0xFF);
        Assert.Equal(0x78u, samples & 0xFF);
    }

    [Fact]
    public void Alac_pads_a_short_buffer_out_to_the_full_block()
    {
        // A final partial packet must still decode as blockSize frames, or the receiver
        // reads past the end of the payload.
        var full = RaopAlac.Encode(Pcm(new (short, short)[352]), blockSize: 352);
        var partial = RaopAlac.Encode(Pcm(new (short, short)[10]), blockSize: 352);

        Assert.Equal(full.Length, partial.Length);
    }

    [Fact]
    public void Alac_terminates_the_frame()
    {
        var encoded = RaopAlac.Encode(Pcm((1, 1)), blockSize: 352);

        Assert.Equal(0xC0, encoded[^1]);          // frame terminator
        Assert.Equal(1, encoded[^2] & 1);         // final data bit set
    }

    [Fact]
    public void Alac_full_packet_is_the_expected_size()
    {
        var encoded = RaopAlac.Encode(new byte[RaopAlac.PcmBytesPerPacket]);

        // 7-byte header + 352*4 shifted sample bytes + terminator.
        Assert.Equal(7 + (352 * 4) + 1, encoded.Length);
    }

    // ===== Session crypto =====

    [Fact]
    public void Crypto_encrypts_whole_blocks_and_leaves_the_tail_clear()
    {
        var crypto = new RaopCrypto(new byte[16], new byte[16]);
        var payload = new byte[40];   // two whole blocks + 8 trailing bytes
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)i;
        }

        var original = payload.ToArray();
        crypto.EncryptPayload(payload);

        Assert.NotEqual(original[..32], payload[..32]);          // 32 bytes encrypted
        Assert.Equal(original[32..], payload[32..]);             // remainder untouched - the RAOP rule
    }

    [Fact]
    public void Crypto_resets_the_iv_per_packet_so_identical_payloads_match()
    {
        // Receivers decrypt packets standalone (they can arrive out of order), so chaining
        // must never carry across packets.
        var crypto = new RaopCrypto(new byte[16], new byte[16]);
        var a = new byte[32];
        var b = new byte[32];

        crypto.EncryptPayload(a);
        crypto.EncryptPayload(b);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Crypto_ignores_a_payload_shorter_than_one_block()
    {
        var crypto = new RaopCrypto(new byte[16], new byte[16]);
        var payload = new byte[15];
        var original = payload.ToArray();

        crypto.EncryptPayload(payload);

        Assert.Equal(original, payload);
    }

    [Fact]
    public void Crypto_wraps_the_session_key_to_apples_key_size_without_padding_chars()
    {
        var crypto = new RaopCrypto();

        var wrapped = crypto.EncryptedKeyBase64();
        Assert.DoesNotContain('=', wrapped);
        Assert.DoesNotContain('=', crypto.IvBase64);
        // Apple's modulus is 2048-bit, so the ciphertext is 256 bytes.
        Assert.Equal(256, Convert.FromBase64String(wrapped + "==").Length);
    }

    // ===== RTP / sync / timing packets =====

    [Fact]
    public void Audio_packet_has_the_rtp_header_and_marks_only_the_first()
    {
        var payload = new byte[] { 1, 2, 3 };
        var first = RaopPackets.BuildAudio(0x1234, 0xAABBCCDD, 0x11223344, payload, first: true);
        var later = RaopPackets.BuildAudio(0x1235, 0xAABBCCDE, 0x11223344, payload, first: false);

        Assert.Equal(0x80, first[0]);
        Assert.Equal(0xE0, first[1]);   // marker | payload type 96
        Assert.Equal(0x60, later[1]);   // type 96, no marker

        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(first.AsSpan(2)));
        Assert.Equal(0xAABBCCDDu, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(4)));
        Assert.Equal(0x11223344u, BinaryPrimitives.ReadUInt32BigEndian(first.AsSpan(8)));
        Assert.Equal(payload, first[12..]);
    }

    [Fact]
    public void Sync_packet_ties_ntp_to_the_rtp_timeline()
    {
        var packet = RaopPackets.BuildSync(1000, 0x1122334455667788, 89200, first: true);

        Assert.Equal(20, packet.Length);
        Assert.Equal(0x90, packet[0]);   // first-sync flag
        Assert.Equal(0xD4, packet[1]);   // marker | type 84
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)));
        Assert.Equal(0x1122334455667788UL, BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(8)));
        Assert.Equal(89200u, BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(16)));

        Assert.Equal(0x80, RaopPackets.BuildSync(1000, 1, 2, first: false)[0]);
    }

    [Fact]
    public void Timing_reply_echoes_the_query_stamp_then_our_two()
    {
        var request = new byte[32];
        BinaryPrimitives.WriteUInt64BigEndian(request.AsSpan(24), 0xDEADBEEFCAFEBABE);
        request[1] = RaopPackets.PayloadTimingRequest;

        Assert.True(RaopPackets.IsTimingRequest(request));

        var reply = RaopPackets.BuildTimingReply(request, receivedNtp: 111, transmitNtp: 222);

        Assert.Equal(32, reply.Length);
        Assert.Equal(0xD3, reply[1]);   // marker | type 83
        Assert.Equal(0xDEADBEEFCAFEBABEUL, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(8)));
        Assert.Equal(111UL, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(16)));
        Assert.Equal(222UL, BinaryPrimitives.ReadUInt64BigEndian(reply.AsSpan(24)));
    }

    [Fact]
    public void Ntp_timestamp_counts_seconds_since_1900_in_the_high_half()
    {
        var ntp = RaopPackets.ToNtp(new DateTime(1900, 1, 2, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(86400u, (uint)(ntp >> 32));
    }

    [Fact]
    public void Audio_packet_is_not_a_timing_request()
    {
        var audio = RaopPackets.BuildAudio(1, 1, 1, new byte[4], first: false);
        Assert.False(RaopPackets.IsTimingRequest(audio));
    }

    // ===== RTSP framing =====

    [Fact]
    public void Rtsp_request_carries_cseq_session_and_content_length()
    {
        var body = System.Text.Encoding.UTF8.GetBytes("v=0\r\n");
        var bytes = RtspClient.BuildRequest(
            cSeq: 4,
            method: "ANNOUNCE",
            uri: "rtsp://10.0.0.2/1234",
            defaultHeaders: new Dictionary<string, string> { ["User-Agent"] = "iTunes/7.6.2 (Windows; N;)" },
            headers: null,
            contentType: "application/sdp",
            body: body,
            sessionId: "DEADBEEF");

        var text = System.Text.Encoding.ASCII.GetString(bytes);
        Assert.StartsWith("ANNOUNCE rtsp://10.0.0.2/1234 RTSP/1.0\r\n", text);
        Assert.Contains("CSeq: 4\r\n", text);
        Assert.Contains("User-Agent: iTunes/7.6.2 (Windows; N;)\r\n", text);
        Assert.Contains("Session: DEADBEEF\r\n", text);
        Assert.Contains("Content-Type: application/sdp\r\n", text);
        Assert.Contains($"Content-Length: {body.Length}\r\n", text);
        Assert.EndsWith("\r\n\r\nv=0\r\n", text);
    }

    [Fact]
    public void Rtsp_bodyless_request_still_declares_zero_length()
    {
        var text = System.Text.Encoding.ASCII.GetString(
            RtspClient.BuildRequest(1, "OPTIONS", "*", null, null, null, null, null));

        Assert.Contains("Content-Length: 0\r\n", text);
        Assert.DoesNotContain("Session:", text);
    }

    [Theory]
    [InlineData("RTSP/1.0 200 OK\r\nCSeq: 1\r\n\r\n", 200, true)]
    [InlineData("RTSP/1.0 453 Not Enough Bandwidth\r\nCSeq: 2\r\n\r\n", 453, false)]
    [InlineData("RTSP/1.0 401 Unauthorized\r\nCSeq: 3\r\n\r\n", 401, false)]
    public void Rtsp_response_status_parses(string raw, int expectedCode, bool success)
    {
        var response = RtspClient.ParseResponse(raw, "");

        Assert.Equal(expectedCode, response.StatusCode);
        Assert.Equal(success, response.IsSuccess);
    }

    [Fact]
    public void Rtsp_response_headers_are_case_insensitive()
    {
        var response = RtspClient.ParseResponse(
            "RTSP/1.0 200 OK\r\nSession: 6F5A1B2C;timeout=60\r\nTransport: RTP/AVP/UDP;server_port=6000\r\n\r\n", "");

        Assert.Equal("6F5A1B2C;timeout=60", response.Header("session"));
        Assert.NotNull(response.Header("TRANSPORT"));
    }

    // ===== SDP + transport negotiation =====

    [Fact]
    public void Sdp_announces_apple_lossless_with_the_wrapped_key()
    {
        var sdp = RaopSession.BuildSdp("1234567890", "10.0.0.5", "10.0.0.9", new RaopCrypto(new byte[16], new byte[16]));

        Assert.Contains("o=iTunes 1234567890 0 IN IP4 10.0.0.5\r\n", sdp);
        Assert.Contains("c=IN IP4 10.0.0.9\r\n", sdp);
        Assert.Contains("m=audio 0 RTP/AVP 96\r\n", sdp);
        Assert.Contains("a=rtpmap:96 AppleLossless\r\n", sdp);
        Assert.Contains($"a=fmtp:96 {RaopAlac.FramesPerPacket} 0 16 40 10 14 2 255 0 0 44100\r\n", sdp);
        Assert.Contains("a=rsaaeskey:", sdp);
        Assert.Contains("a=aesiv:", sdp);
    }

    [Theory]
    [InlineData("RTP/AVP/UDP;unicast;mode=record;server_port=6000;control_port=6001;timing_port=6002", 6000, 6001, 6002)]
    [InlineData("RTP/AVP/UDP;unicast;server_port=53124;timing_port=0", 53124, 0, 0)]
    [InlineData("RTP/AVP/UDP;unicast", 0, 0, 0)]
    public void Transport_ports_parse(string transport, int server, int control, int timing)
    {
        var ports = RaopSession.ParseTransportPorts(transport);

        Assert.Equal(server, ports.Server);
        Assert.Equal(control, ports.Control);
        Assert.Equal(timing, ports.Timing);
    }

    // ===== mDNS SRV/A resolution =====

    [Fact]
    public void Mdns_srv_and_a_records_resolve_a_receiver_to_host_and_port()
    {
        // A synthetic response holding one SRV (port 5000 -> "speaker.local") and its A record.
        var packet = BuildMdnsResponse();
        var receivers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AABBCCDDEEFF@Kitchen._raop._tcp.local"] = "Kitchen",
        };
        var endpoints = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);

        AirPlayDeviceProvider.ExtractEndpoints(packet, receivers, endpoints);

        Assert.True(endpoints.TryGetValue("AABBCCDDEEFF@Kitchen._raop._tcp.local", out var endpoint));
        Assert.Equal("192.168.1.50", endpoint.Host);
        Assert.Equal(5000, endpoint.Port);
    }

    [Fact]
    public void Mdns_parse_ignores_a_receiver_with_no_srv_record()
    {
        var endpoints = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);

        AirPlayDeviceProvider.ExtractEndpoints(
            BuildMdnsResponse(),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Other._raop._tcp.local"] = "Other" },
            endpoints);

        Assert.Empty(endpoints);
    }

    [Fact]
    public void Mdns_parse_survives_a_truncated_packet()
    {
        var endpoints = new Dictionary<string, (string Host, int Port)>(StringComparer.OrdinalIgnoreCase);

        // Must not throw - malformed multicast traffic is normal on a busy LAN.
        AirPlayDeviceProvider.ExtractEndpoints([0x00, 0x00, 0x84], new Dictionary<string, string>(), endpoints);
        AirPlayDeviceProvider.ExtractEndpoints(BuildMdnsResponse()[..20], new Dictionary<string, string>(), endpoints);

        Assert.Empty(endpoints);
    }

    /// <summary>Hand-built mDNS response: SRV for the Kitchen instance + an A record for its target.</summary>
    private static byte[] BuildMdnsResponse()
    {
        var bytes = new List<byte>
        {
            0x00, 0x00,             // id
            0x84, 0x00,             // flags: response, authoritative
            0x00, 0x00,             // QDCOUNT 0
            0x00, 0x02,             // ANCOUNT 2
            0x00, 0x00, 0x00, 0x00, // NSCOUNT, ARCOUNT
        };

        void Name(params string[] labels)
        {
            foreach (var label in labels)
            {
                bytes.Add((byte)label.Length);
                bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
            }
            bytes.Add(0);
        }

        // SRV: AABBCCDDEEFF@Kitchen._raop._tcp.local -> speaker.local:5000
        Name("AABBCCDDEEFF@Kitchen", "_raop", "_tcp", "local");
        bytes.AddRange([0x00, 0x21, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78]);   // type SRV, class IN, ttl
        var srvData = new List<byte> { 0x00, 0x00, 0x00, 0x00, 0x13, 0x88 };   // priority, weight, port 5000
        foreach (var label in new[] { "speaker", "local" })
        {
            srvData.Add((byte)label.Length);
            srvData.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }
        srvData.Add(0);
        bytes.AddRange([(byte)(srvData.Count >> 8), (byte)(srvData.Count & 0xFF)]);
        bytes.AddRange(srvData);

        // A: speaker.local -> 192.168.1.50
        Name("speaker", "local");
        bytes.AddRange([0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0x00, 0x78]);   // type A, class IN, ttl
        bytes.AddRange([0x00, 0x04, 192, 168, 1, 50]);

        return [.. bytes];
    }
}
