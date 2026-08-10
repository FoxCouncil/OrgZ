// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// SRP-6a client for HomeKit transient pairing - the gate a HomePod puts in front of
/// AirPlay 2. The receiver answers <c>OPTIONS</c> with 401 until this completes.
///
/// HAP's parameters: the RFC 5054 3072-bit group, SHA-512, username "Pair-Setup", and
/// the fixed PIN "3939" (transient pairing has no user-entered code - the exchange proves
/// both sides speak the protocol rather than proving possession of a secret).
///
/// Every value that feeds a hash is left-padded to the modulus width. Skipping that
/// padding is the classic SRP interop bug: it works for most keys and fails for the ~1 in
/// 256 that happen to have a leading zero byte, which reads as a flaky receiver.
/// </summary>
internal sealed class Srp6aClient
{
    /// <summary>RFC 5054 / RFC 3526 group 15 - the 3072-bit MODP prime HAP uses.</summary>
    private const string ModulusHex =
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A637ED6B0BFF5CB6F406B7ED" +
        "EE386BFB5A899FA5AE9F24117C4B1FE649286651ECE45B3DC2007CB8A163BF05" +
        "98DA48361C55D39A69163FA8FD24CF5F83655D23DCA3AD961C62F356208552BB" +
        "9ED529077096966D670C354E4ABC9804F1746C08CA18217C32905E462E36CE3B" +
        "E39E772C180E86039B2783A2EC07A28FB5C55DF06F4C52C9DE2BCBF695581718" +
        "3995497CEA956AE515D2261898FA051015728E5A8AAAC42DAD33170D04507A33" +
        "A85521ABDF1CBA64ECFB850458DBEF0A8AEA71575D060C7DB3970F85A6E1E4C7" +
        "ABF5AE8CDB0933D71E8C94E04A25619DCEE3D2261AD2EE6BF12FFA06D98A0864" +
        "D87602733EC86A64521F2B18177B200CBBE117577A615D6C770988C0BAD946E2" +
        "08E24FA074E5AB3143DB5BFCE0FD108E4B82D120A93AD2CAFFFFFFFFFFFFFFFF";

    /// <summary>
    /// The password transient pairing uses when the receiver has none of its own. A
    /// receiver that advertises the 0x80 status bit wants the user's real AirPlay password
    /// here instead - passing this one gets a flat "authentication failed" at M4.
    /// </summary>
    public const string TransientPin = "3939";
    public const string Username = "Pair-Setup";

    private readonly BigInteger _n;
    private readonly BigInteger _g = new(5);
    private readonly int _width;         // modulus width in bytes - the padding target
    private readonly BigInteger _a;      // client private
    private readonly BigInteger _bigA;   // client public
    private readonly string _password;

    private byte[]? _sessionKey;

    public Srp6aClient(string? password = null)
        : this(RandomNumberGenerator.GetBytes(32), password)
    {
    }

    /// <summary>Test seam: a fixed client private so the exchange is reproducible.</summary>
    internal Srp6aClient(byte[] privateKey, string? password = null)
    {
        _password = string.IsNullOrEmpty(password) ? TransientPin : password;

        _n = ToPositive(Convert.FromHexString(ModulusHex));
        _width = ModulusHex.Length / 2;

        _a = ToPositive(privateKey) % _n;
        _bigA = BigInteger.ModPow(_g, _a, _n);
    }

    /// <summary>The client public key (A), left-padded to the modulus width.</summary>
    public byte[] PublicKey => Pad(_bigA);

    /// <summary>
    /// The shared session key K = SHA-512(S), available after <see cref="ComputeProof"/>.
    /// AirPlay's audio key is the FIRST 32 BYTES of this, used raw - no HKDF. Getting that
    /// wrong is the difference between audio and silence.
    /// </summary>
    public byte[] SessionKey => _sessionKey ?? throw new InvalidOperationException("SRP proof has not been computed yet.");

    /// <summary>
    /// Completes the client side against the server's salt and public key (B), returning
    /// the client proof M1. Throws when B is invalid - B ≡ 0 (mod N) means a broken or
    /// hostile peer and must never be treated as a usable session.
    /// </summary>
    public byte[] ComputeProof(byte[] salt, byte[] serverPublicKey)
    {
        var bigB = ToPositive(serverPublicKey);
        if (bigB % _n == BigInteger.Zero)
        {
            throw new InvalidOperationException("SRP: the receiver's public key is invalid (B mod N == 0).");
        }

        // k = H(N | PAD(g)) - g IS padded here. (N's minimal form is already full width,
        // its top byte being 0xFF, so the two spellings coincide for it.)
        var k = ToPositive(Sha512(Minimal(_n), Pad(_g)));

        // x = H(salt | H(username : pin))
        var identityHash = Sha512(Encoding.UTF8.GetBytes($"{Username}:{_password}"));
        var x = ToPositive(Sha512(salt, identityHash));

        // u = H(PAD(A) | PAD(B))
        var u = ToPositive(Sha512(Pad(_bigA), Pad(bigB)));

        // S = (B - k * g^x) ^ (a + u * x) mod N, kept positive before the exponentiation.
        var kgx = k * BigInteger.ModPow(_g, x, _n) % _n;
        var baseValue = (bigB - kgx) % _n;
        if (baseValue < BigInteger.Zero)
        {
            baseValue += _n;
        }

        var s = BigInteger.ModPow(baseValue, _a + (u * x), _n);

        // K = H(S) over S's MINIMAL bytes. Padding is applied only where the spec asks for
        // PAD() - k and u - and nowhere else. Padding here instead produces a key the
        // receiver never derives, which surfaces as "authentication failed" at M4.
        _sessionKey = Sha512(Minimal(s));

        // M1 = H(H(N) XOR H(g) | H(I) | salt | A | B | K), all values MINIMAL. In
        // particular H(g) hashes the single byte 0x05, not g padded to 384 - the two uses
        // of g in this protocol genuinely differ.
        var hn = Sha512(Minimal(_n));
        var hg = Sha512(Minimal(_g));
        var xored = new byte[hn.Length];
        for (var i = 0; i < hn.Length; i++)
        {
            xored[i] = (byte)(hn[i] ^ hg[i]);
        }

        return Sha512(xored, Sha512(Encoding.UTF8.GetBytes(Username)), salt, Minimal(_bigA), Minimal(bigB), _sessionKey);
    }

    /// <summary>Verifies the server's proof M2 = H(A | M1 | K), closing the mutual authentication.</summary>
    public bool VerifyServerProof(byte[] clientProof, byte[] serverProof)
    {
        var expected = Sha512(Minimal(_bigA), clientProof, SessionKey);
        return CryptographicOperations.FixedTimeEquals(expected, serverProof);
    }

    /// <summary>The value's shortest big-endian encoding - the default everywhere PAD() isn't specified.</summary>
    private static byte[] Minimal(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

    /// <summary>Left-pads to the modulus width - required before every hash input.</summary>
    private byte[] Pad(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (bytes.Length == _width)
        {
            return bytes;
        }

        var padded = new byte[_width];
        bytes.CopyTo(padded, _width - bytes.Length);
        return padded;
    }

    /// <summary>Big-endian bytes to a non-negative BigInteger.</summary>
    private static BigInteger ToPositive(byte[] bytes) => new(bytes, isUnsigned: true, isBigEndian: true);

    private static byte[] Sha512(params byte[][] parts)
    {
        using var sha = SHA512.Create();
        foreach (var part in parts)
        {
            sha.TransformBlock(part, 0, part.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return sha.Hash!;
    }
}
