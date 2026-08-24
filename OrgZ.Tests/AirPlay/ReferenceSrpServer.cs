// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// A minimal SRP-6a server built straight from the specification, used to check the client
/// against something that isn't itself and to stand in for a receiver's half of transient
/// pair-setup.
///
/// It proves the exchange COMPLETES and that both ends derive the same key. It does not
/// prove our client agrees with Apple - only an outside implementation can do that, and
/// <see cref="AirPlayPairingTests"/> pins that separately against srptools vectors.
/// </summary>
internal sealed class ReferenceSrpServer
{
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

    private readonly BigInteger _n = new(Convert.FromHexString(ModulusHex), isUnsigned: true, isBigEndian: true);
    private readonly BigInteger _g = new(5);
    private readonly int _width = ModulusHex.Length / 2;
    private readonly BigInteger _b;
    private readonly BigInteger _v;
    private readonly BigInteger _bigB;

    public byte[] Salt { get; } = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();

    /// <summary>
    /// <paramref name="password"/> is the secret the receiver expects. Null means the
    /// transient PIN, which is what a receiver without "Require Password" uses.
    /// </summary>
    public ReferenceSrpServer(string? password = null)
    {
        var secret = string.IsNullOrEmpty(password) ? Srp6aClient.TransientPin : password;

        var x = new BigInteger(
            Hash(Salt, Hash(Encoding.UTF8.GetBytes($"{Srp6aClient.Username}:{secret}"))),
            isUnsigned: true, isBigEndian: true);
        _v = BigInteger.ModPow(_g, x, _n);

        _b = new BigInteger(Enumerable.Range(50, 32).Select(i => (byte)i).ToArray(), isUnsigned: true, isBigEndian: true) % _n;

        var k = new BigInteger(Hash(Pad(_n), Pad(_g)), isUnsigned: true, isBigEndian: true);
        _bigB = ((k * _v) + BigInteger.ModPow(_g, _b, _n)) % _n;
    }

    public byte[] PublicKey => Pad(_bigB);

    public byte[] ComputeSessionKey(byte[] clientPublicKey)
    {
        var bigA = new BigInteger(clientPublicKey, isUnsigned: true, isBigEndian: true);
        var u = new BigInteger(Hash(Pad(bigA), Pad(_bigB)), isUnsigned: true, isBigEndian: true);

        // S = (A * v^u) ^ b mod N; K = H(S) over S's MINIMAL bytes.
        var s = BigInteger.ModPow(bigA * BigInteger.ModPow(_v, u, _n) % _n, _b, _n);
        return Hash(Minimal(s));
    }

    public bool VerifyClientProof(byte[] clientPublicKey, byte[] proof)
    {
        // Every term MINIMAL - notably H(g) over the single byte 0x05, matching the
        // srptools/pyatv convention that is proven against real Apple hardware.
        var bigA = new BigInteger(clientPublicKey, isUnsigned: true, isBigEndian: true);
        var hn = Hash(Minimal(_n));
        var hg = Hash(Minimal(_g));
        var xored = new byte[hn.Length];
        for (var i = 0; i < hn.Length; i++)
        {
            xored[i] = (byte)(hn[i] ^ hg[i]);
        }

        var expected = Hash(
            xored,
            Hash(Encoding.UTF8.GetBytes(Srp6aClient.Username)),
            Salt,
            Minimal(bigA),
            Minimal(_bigB),
            ComputeSessionKey(clientPublicKey));

        return CryptographicOperations.FixedTimeEquals(expected, proof);
    }

    public byte[] ComputeServerProof(byte[] clientPublicKey, byte[] clientProof)
    {
        var bigA = new BigInteger(clientPublicKey, isUnsigned: true, isBigEndian: true);
        return Hash(Minimal(bigA), clientProof, ComputeSessionKey(clientPublicKey));
    }

    private static byte[] Minimal(BigInteger value) => value.ToByteArray(isUnsigned: true, isBigEndian: true);

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

    private static byte[] Hash(params byte[][] parts)
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
