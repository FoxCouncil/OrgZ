// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using Serilog;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// HomeKit TRANSIENT pair-setup - the handshake a HomePod (and modern Apple TV / macOS)
/// demands before it will answer anything else. Without it every request comes back 401.
///
/// Transient pairing is the short form: no user-entered PIN, no persisted long-term keys,
/// and it stops at M4 - there's no pair-verify round. What comes out is the SRP session
/// key, whose first 32 bytes become the audio key sent later in the stream SETUP.
///
/// Wire shape: POST /pair-setup, Content-Type application/octet-stream, TLV8 bodies, and
/// the X-Apple-HKP: 4 header that selects transient mode.
/// </summary>
internal sealed class AirPlay2Pairing
{
    private static readonly ILogger _log = Logging.For("AirPlay2Pair");

    /// <summary>Selects HomeKit transient pairing (4). Without it the receiver expects the full PIN flow.</summary>
    private const string HkpHeaderValue = "4";

    /// <summary>Transient pairing sets bit 4 (0x10) in the pairing flags.</summary>
    private const byte TransientFlag = 0x10;

    private readonly Srp6aClient _srp;

    /// <summary>
    /// <paramref name="password"/> is the receiver's AirPlay password, or null for the
    /// passwordless transient PIN. Receivers advertising the 0x80 status bit ("Require
    /// Password" in the Home app) reject the transient PIN outright.
    /// </summary>
    public AirPlay2Pairing(string? password = null)
    {
        _srp = new Srp6aClient(password);
        _hasPassword = !string.IsNullOrEmpty(password);
    }

    private readonly bool _hasPassword;

    /// <summary>Set when the receiver rejected our credentials, so the caller can ask for a password.</summary>
    public bool PasswordRejected { get; private set; }

    /// <summary>The SRP session key (64 bytes) once pairing succeeds.</summary>
    public byte[]? SessionKey { get; private set; }

    /// <summary>
    /// The AirPlay audio key: the FIRST 32 BYTES of the session key, used raw - no HKDF,
    /// no further derivation. This is the detail that decides audio versus silence.
    /// </summary>
    public byte[] AudioKey => SessionKey is null
        ? throw new InvalidOperationException("Pairing has not completed.")
        : SessionKey[..32];

    /// <summary>
    /// Builds the M1 body: start transient pair-setup. Field order and the single-byte
    /// flags match the reference sender exactly - HAV TLV integers are variable length,
    /// and a 4-byte flags value is not what a receiver is shown by working clients.
    /// </summary>
    internal byte[] BuildM1() => Tlv8.Encode(
        ((byte)Tlv8.Method, new byte[] { 0x00 }),
        ((byte)Tlv8.State, new byte[] { 0x01 }),
        ((byte)Tlv8.Flags, new byte[] { TransientFlag }));

    /// <summary>Builds the M3 body from the receiver's salt and public key: our public key + proof.</summary>
    internal byte[] BuildM3(byte[] salt, byte[] serverPublicKey) => Tlv8.Encode(
        ((byte)Tlv8.State, new byte[] { 0x03 }),
        ((byte)Tlv8.PublicKey, _srp.PublicKey),
        ((byte)Tlv8.Proof, _srp.ComputeProof(salt, serverPublicKey)));

    /// <summary>
    /// Runs M1→M4 against a connected receiver. Throws with the receiver's own reason on
    /// refusal rather than leaving a half-open session.
    /// </summary>
    public async Task PairAsync(RtspClient rtsp, CancellationToken ct = default)
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Apple-HKP"] = HkpHeaderValue,
            ["Connection"] = "keep-alive",
        };

        // Announce the pairing attempt before M1. Skipping it leaves the receiver in a
        // different pairing mode than the one our proof is computed against, which comes
        // back at M4 as a plain authentication failure with no hint about the cause.
        await rtsp.PostAsync("/pair-pin-start", "application/octet-stream", [], headers, ct);

        var m2 = await rtsp.PostAsync("/pair-setup", "application/octet-stream", BuildM1(), headers, ct);
        if (!m2.IsSuccess)
        {
            throw new InvalidOperationException($"Pair-setup M1 refused ({m2.StatusCode} {m2.StatusText}).");
        }

        var m2Tlv = Tlv8.Decode(m2.BodyBytes);
        ThrowIfError(m2Tlv, "M2");

        if (!m2Tlv.TryGetValue(Tlv8.PublicKey, out var serverPublicKey) || !m2Tlv.TryGetValue(Tlv8.Salt, out var salt))
        {
            throw new InvalidOperationException("Pair-setup M2 carried no SRP salt/public key - this receiver may not support transient pairing.");
        }

        var proof = BuildM3(salt, serverPublicKey);
        var m4 = await rtsp.PostAsync("/pair-setup", "application/octet-stream", proof, headers, ct);
        if (!m4.IsSuccess)
        {
            throw new InvalidOperationException($"Pair-setup M3 refused ({m4.StatusCode} {m4.StatusText}).");
        }

        var m4Tlv = Tlv8.Decode(m4.BodyBytes);
        ThrowIfError(m4Tlv, "M4");

        // The receiver proves it derived the same key. A missing proof means it accepted
        // us without proving itself - refuse rather than stream to an unauthenticated peer.
        if (!m4Tlv.TryGetValue(Tlv8.Proof, out var serverProof))
        {
            throw new InvalidOperationException("Pair-setup M4 carried no server proof.");
        }

        var clientProof = Tlv8.Decode(proof)[Tlv8.Proof];
        if (!_srp.VerifyServerProof(clientProof, serverProof))
        {
            throw new InvalidOperationException("Pair-setup M4 proof mismatch - the receiver derived a different key.");
        }

        SessionKey = _srp.SessionKey;

        // From here the connection is encrypted. This is not optional and there is no
        // negotiation of it - the receiver simply expects every subsequent byte to be
        // sealed, and silently drops the connection if it isn't.
        rtsp.EnableEncryption(SessionKey);

        _log.Information("AirPlay 2 transient pairing complete (connection encrypted)");
    }

    /// <summary>HAP reports refusals as a TLV error code rather than an HTTP status.</summary>
    private void ThrowIfError(Dictionary<byte, byte[]> tlv, string stage)
    {
        if (!tlv.TryGetValue(Tlv8.Error, out var error) || error.Length == 0 || error[0] == 0)
        {
            return;
        }

        // 0x02 is SRP saying "your proof didn't match mine", which for AirPlay means one
        // thing in practice: the wrong password. Flag it so the caller can ask for one
        // rather than reporting an authentication failure the user can't act on.
        if (error[0] == 0x02)
        {
            PasswordRejected = true;
            throw new InvalidOperationException(_hasPassword
                ? "That AirPlay password was rejected."
                : "This receiver requires an AirPlay password.");
        }

        throw new InvalidOperationException($"Pair-setup {stage} failed: {DescribeError(error[0])}");
    }

    /// <summary>The HAP error codes, matching the specification's numbering.</summary>
    internal static string DescribeError(byte code) => code switch
    {
        0x01 => "the receiver reported an unknown error",
        0x02 => "authentication failed",
        0x03 => "too many attempts - the receiver is backing off",
        0x04 => "the receiver has no pairing slots left",
        0x05 => "too many failed attempts - the receiver has locked pairing",
        0x06 => "the receiver is busy with another sender",
        _ => $"error code 0x{code:X2}",
    };
}
