// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// A live AirPlay 2 stream: transient pairing, then a plist-negotiated SETUP, then ALAC
/// frames sealed with ChaCha20-Poly1305 over UDP.
///
/// Differs from <see cref="RaopSession"/> in the negotiation, not the audio: the ALAC
/// framing is identical (<see cref="RaopAlac"/>), but the transport is negotiated with
/// binary plists instead of Transport headers, and every audio frame is individually
/// sealed rather than AES-CBC'd.
/// </summary>
internal sealed class AirPlay2Session : IDisposable
{
    private static readonly ILogger _log = Logging.For("AirPlay2");

    private const int SampleRate = 44100;
    private const uint LatencyFrames = 88200;

    /// <summary>ALAC 44.1 kHz 16-bit stereo, as the stream SETUP advertises it.</summary>
    private const long AudioFormatAlac441 = 0x40000;

    private readonly string _host;
    private readonly int _rtspPort;
    private readonly AirPlay2Pairing _pairing = new();

    private RtspClient? _rtsp;
    private AirPlay2Cipher? _cipher;
    private UdpClient? _audio;
    private IPEndPoint? _audioEndpoint;

    private readonly uint _ssrc = BitConverter.ToUInt32(RandomNumberGenerator.GetBytes(4));
    private ushort _sequence;
    private uint _timestamp;
    private bool _first = true;
    private DateTime _streamStart;
    private uint _framesSent;

    public AirPlay2Session(string host, int rtspPort)
    {
        _host = host;
        _rtspPort = rtspPort;
    }

    public bool IsConnected { get; private set; }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        var sessionId = Random.Shared.NextInt64(1_000_000_000, 9_999_999_999).ToString();

        _rtsp = new RtspClient();
        await _rtsp.ConnectAsync(_host, _rtspPort, ct);

        _rtsp.DefaultHeaders["User-Agent"] = "AirPlay/320.20";
        _rtsp.DefaultHeaders["Client-Instance"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
        _rtsp.DefaultHeaders["DACP-ID"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToUpperInvariant();
        _rtsp.DefaultHeaders["Active-Remote"] = Random.Shared.Next(1, int.MaxValue).ToString();

        // Pairing FIRST: until this completes the receiver answers everything with 401.
        await _pairing.PairAsync(_rtsp, ct);
        _cipher = new AirPlay2Cipher(_pairing.AudioKey);

        // /info is the expected precursor to SETUP; a receiver can refuse SETUP without it.
        var info = await _rtsp.PostAsync("/info", "application/x-apple-binary-plist", BinaryPlist.Write(new Dictionary<string, object?>()), ct: ct);
        if (!info.IsSuccess)
        {
            _log.Debug("AirPlay 2 /info returned {Status} - continuing to SETUP", info.StatusCode);
        }

        // Session SETUP, then the stream SETUP that carries the audio key.
        var setupBody = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["timingProtocol"] = "None",
            ["isMultiSelectAirPlay"] = false,
            ["model"] = "OrgZ",
            ["name"] = Environment.MachineName,
            ["sourceVersion"] = "665.13.1",
        });

        var setup = await _rtsp.SendAsync("SETUP", $"rtsp://{_rtsp.LocalAddress}/{sessionId}",
            contentType: "application/x-apple-binary-plist", body: setupBody, ct: ct);
        if (!setup.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 SETUP refused ({setup.StatusCode} {setup.StatusText}).");
        }

        var streamBody = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?>
                {
                    ["type"] = 96L,                        // realtime audio
                    ["audioFormat"] = AudioFormatAlac441,
                    ["ct"] = 2L,                           // ALAC
                    ["spf"] = (long)RaopAlac.FramesPerPacket,
                    // The audio key goes over verbatim - the receiver seals/unseals with it.
                    ["shk"] = _pairing.AudioKey,
                    ["isMedia"] = true,
                    ["latencyMin"] = 11025L,
                    ["latencyMax"] = 88200L,
                },
            },
        });

        var streamSetup = await _rtsp.SendAsync("SETUP", $"rtsp://{_rtsp.LocalAddress}/{sessionId}",
            contentType: "application/x-apple-binary-plist", body: streamBody, ct: ct);
        if (!streamSetup.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 stream SETUP refused ({streamSetup.StatusCode} {streamSetup.StatusText}).");
        }

        var dataPort = ExtractDataPort(streamSetup.BodyBytes)
            ?? throw new InvalidOperationException("AirPlay 2 stream SETUP returned no dataPort.");

        var address = (await Dns.GetHostAddressesAsync(_host, ct)).First(a => a.AddressFamily == AddressFamily.InterNetwork);
        _audio = new UdpClient(0, AddressFamily.InterNetwork);
        _audioEndpoint = new IPEndPoint(address, dataPort);

        _sequence = (ushort)Random.Shared.Next(ushort.MaxValue);
        _timestamp = (uint)Random.Shared.Next();

        var record = await _rtsp.SendAsync("RECORD", $"rtsp://{_rtsp.LocalAddress}/{sessionId}", new Dictionary<string, string>
        {
            ["Range"] = "npt=0-",
            ["RTP-Info"] = $"seq={_sequence};rtptime={_timestamp}",
        }, ct: ct);
        if (!record.IsSuccess)
        {
            throw new InvalidOperationException($"AirPlay 2 RECORD refused ({record.StatusCode} {record.StatusText}).");
        }

        _streamStart = DateTime.UtcNow;
        _framesSent = 0;
        IsConnected = true;
        _log.Information("AirPlay 2 session up: {Host} audio->{Port}", _host, dataPort);
    }

    /// <summary>Digs dataPort out of the SETUP reply's streams array.</summary>
    internal static int? ExtractDataPort(byte[] plist)
    {
        try
        {
            if (BinaryPlist.Read(plist) is not Dictionary<string, object?> root)
            {
                return null;
            }

            if (root.TryGetValue("streams", out var streamsValue) && streamsValue is List<object?> streams)
            {
                foreach (var entry in streams)
                {
                    if (entry is Dictionary<string, object?> stream && stream.TryGetValue("dataPort", out var port) && port is long p and > 0)
                    {
                        return (int)p;
                    }
                }
            }

            // Some receivers answer with the port at the top level instead.
            return root.TryGetValue("dataPort", out var direct) && direct is long d and > 0 ? (int)d : null;
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 SETUP reply was not a readable plist");
            return null;
        }
    }

    /// <summary>Sends one 352-frame packet, sealed and paced against the stream clock.</summary>
    public async Task SendPacketAsync(ReadOnlyMemory<byte> pcm, CancellationToken ct)
    {
        if (!IsConnected || _audio is null || _audioEndpoint is null || _cipher is null)
        {
            return;
        }

        // Same ALAC framing RAOP uses - AirPlay 2 changed the negotiation, not the codec.
        var alac = RaopAlac.Encode(pcm.Span);
        var sealed_ = _cipher.Seal(alac);

        var packet = RaopPackets.BuildAudio(_sequence, _timestamp + LatencyFrames, _ssrc, sealed_, _first);
        _first = false;
        _sequence++;
        _timestamp += RaopAlac.FramesPerPacket;
        _framesSent += RaopAlac.FramesPerPacket;

        await _audio.SendAsync(packet, packet.Length, _audioEndpoint);

        var due = _streamStart.AddSeconds((double)_framesSent / SampleRate);
        var wait = due - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            await Task.Delay(wait, ct);
        }
    }

    public async Task SetVolumeAsync(float linear, CancellationToken ct = default)
    {
        if (_rtsp is null || !IsConnected)
        {
            return;
        }

        var db = linear <= 0.001f ? -144f : -30f + (Math.Clamp(linear, 0f, 1f) * 30f);
        try
        {
            await _rtsp.SendAsync("SET_PARAMETER", $"rtsp://{_rtsp.LocalAddress}/stream", contentType: "text/parameters",
                body: System.Text.Encoding.ASCII.GetBytes($"volume: {db.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture)}\r\n"), ct: ct);
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 volume set failed");
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        IsConnected = false;

        try
        {
            if (_rtsp is not null && _rtsp.SessionId is not null)
            {
                _rtsp.SendAsync("TEARDOWN", $"rtsp://{_rtsp.LocalAddress}/stream").Wait(TimeSpan.FromSeconds(2));
            }
        }
        catch (Exception ex)
        {
            _log.Debug(ex, "AirPlay 2 teardown failed");
        }

        _cipher?.Dispose();
        _audio?.Dispose();
        _rtsp?.Dispose();
    }
}
