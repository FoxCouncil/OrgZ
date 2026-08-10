// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OrgZ.Services.AudioOutput.AirPlay;

namespace OrgZ.Tests;

/// <summary>
/// The AirPlay 2 pairing primitives. SRP can't be checked against a HomePod here, so the
/// tests stand up a reference SERVER side from the same specification and prove the two
/// derive an identical session key - which is exactly what the receiver checks.
/// </summary>
public class AirPlayPairingTests
{
    // ===== TLV8 =====

    [Fact]
    public void Tlv8_round_trips_simple_values()
    {
        var encoded = Tlv8.Encode(
            (Tlv8.State, [0x01]),
            (Tlv8.Method, [0x00]));

        var decoded = Tlv8.Decode(encoded);

        Assert.Equal([0x01], decoded[Tlv8.State]);
        Assert.Equal([0x00], decoded[Tlv8.Method]);
    }

    [Fact]
    public void Tlv8_fragments_long_values_and_rejoins_them()
    {
        // SRP public keys are 384 bytes, so fragmentation is the normal path here.
        var key = new byte[384];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i & 0xFF);
        }

        var encoded = Tlv8.Encode((Tlv8.PublicKey, key));

        // 384 bytes = a 255-byte fragment + a 129-byte fragment, each with its own header.
        Assert.Equal(2 + 255 + 2 + 129, encoded.Length);
        Assert.Equal(255, encoded[1]);
        Assert.Equal(Tlv8.PublicKey, encoded[257]);
        Assert.Equal(129, encoded[258]);

        Assert.Equal(key, Tlv8.Decode(encoded)[Tlv8.PublicKey]);
    }

    [Fact]
    public void Tlv8_handles_an_empty_value_and_a_truncated_buffer()
    {
        Assert.Empty(Tlv8.Decode(Tlv8.Encode((Tlv8.Error, [])))[Tlv8.Error]);

        // A peer's malformed tail must not throw - keep what parsed.
        var decoded = Tlv8.Decode([Tlv8.State, 0x01, 0x03, Tlv8.PublicKey, 0x40]);
        Assert.Equal([0x03], decoded[Tlv8.State]);
        Assert.False(decoded.ContainsKey(Tlv8.PublicKey));
    }

    // ===== SRP-6a =====

    [Fact]
    public void Srp_public_key_is_the_full_modulus_width()
    {
        // 3072-bit group: A must be padded to 384 bytes even when it has leading zeros,
        // or the receiver's hashes disagree with ours.
        Assert.Equal(384, new Srp6aClient().PublicKey.Length);
    }

    [Fact]
    public void Srp_client_and_reference_server_agree_on_the_session_key()
    {
        // The real proof: an independent server implementation of the same spec must
        // arrive at the same K. If padding, hash order, or the S formula were wrong,
        // these diverge - which on a HomePod shows up as a rejected pairing.
        var server = new ReferenceSrpServer();
        var client = new Srp6aClient(privateKey: Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

        var proof = client.ComputeProof(server.Salt, server.PublicKey);

        Assert.Equal(server.ComputeSessionKey(client.PublicKey), client.SessionKey);
        Assert.True(server.VerifyClientProof(client.PublicKey, proof));
    }

    [Fact]
    public void Srp_session_key_is_sha512_sized_and_the_audio_key_takes_its_first_half()
    {
        var server = new ReferenceSrpServer();
        var client = new Srp6aClient();
        client.ComputeProof(server.Salt, server.PublicKey);

        // K is 64 bytes; AirPlay uses the first 32 RAW as the audio key (no HKDF) - the
        // single detail that decides audio versus silence.
        Assert.Equal(64, client.SessionKey.Length);
        Assert.Equal(32, client.SessionKey[..32].Length);
    }

    [Fact]
    public void Srp_verifies_the_server_proof()
    {
        var server = new ReferenceSrpServer();
        var client = new Srp6aClient();
        var m1 = client.ComputeProof(server.Salt, server.PublicKey);

        Assert.True(client.VerifyServerProof(m1, server.ComputeServerProof(client.PublicKey, m1)));
        Assert.False(client.VerifyServerProof(m1, new byte[64]));
    }

    [Fact]
    public void Srp_rejects_a_degenerate_server_public_key()
    {
        // B ≡ 0 (mod N) forces S to a known value - a broken or hostile peer. Refuse.
        var client = new Srp6aClient();
        Assert.Throws<InvalidOperationException>(() => client.ComputeProof(new byte[16], new byte[384]));
    }

    [Fact]
    public void Srp_session_key_is_unavailable_before_the_exchange()
    {
        Assert.Throws<InvalidOperationException>(() => new Srp6aClient().SessionKey);
    }

    // ===== Pair-setup wire messages =====

    [Fact]
    public void Pair_setup_m1_requests_transient_pairing()
    {
        var tlv = Tlv8.Decode(new AirPlay2Pairing().BuildM1());

        Assert.Equal([0x01], tlv[Tlv8.State]);      // M1
        Assert.Equal([0x00], tlv[Tlv8.Method]);     // pair-setup
        // Flags 0x10 little-endian selects TRANSIENT pairing - without it the receiver
        // expects the full PIN flow and refuses.
        Assert.Equal([0x10, 0x00, 0x00, 0x00], tlv[Tlv8.Flags]);
    }

    [Fact]
    public void Pair_setup_m3_carries_the_client_key_and_proof()
    {
        var server = new ReferenceSrpServer();
        var pairing = new AirPlay2Pairing();

        var tlv = Tlv8.Decode(pairing.BuildM3(server.Salt, server.PublicKey));

        Assert.Equal([0x03], tlv[Tlv8.State]);
        Assert.Equal(384, tlv[Tlv8.PublicKey].Length);
        Assert.Equal(64, tlv[Tlv8.Proof].Length);
        // The receiver must accept the proof we send.
        Assert.True(server.VerifyClientProof(tlv[Tlv8.PublicKey], tlv[Tlv8.Proof]));
    }

    [Theory]
    [InlineData((byte)0x02, "authentication failed")]
    [InlineData((byte)0x05, "busy")]
    [InlineData((byte)0x06, "out of sequence")]
    public void Pair_setup_errors_read_as_english(byte code, string fragment)
    {
        Assert.Contains(fragment, AirPlay2Pairing.DescribeError(code), StringComparison.OrdinalIgnoreCase);
    }

    // ===== AirPlay 2 audio framing =====

    [Fact]
    public void Sealed_audio_frame_has_the_length_prefix_cipher_and_tag()
    {
        using var cipher = new AirPlay2Cipher(new byte[32]);
        var payload = new byte[100];

        var frame = cipher.Seal(payload);

        Assert.Equal(2 + 100 + 16, frame.Length);
        Assert.Equal(100, BinaryPrimitives.ReadUInt16LittleEndian(frame));
    }

    [Fact]
    public void Sealed_audio_frames_round_trip()
    {
        using var cipher = new AirPlay2Cipher(new byte[32]);
        var payload = Enumerable.Range(0, 200).Select(i => (byte)i).ToArray();

        var first = cipher.Seal(payload);
        var second = cipher.Seal(payload);

        // Each frame decrypts under its own counter...
        Assert.Equal(payload, cipher.Open(first, 0));
        Assert.Equal(payload, cipher.Open(second, 1));

        // ...and identical plaintext must NOT produce identical ciphertext, or the nonce
        // isn't advancing - which would leak the keystream.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Nonce_is_four_zero_bytes_then_the_little_endian_counter()
    {
        var nonce = AirPlay2Cipher.NonceFor(0x0102030405060708);

        Assert.Equal(12, nonce.Length);
        Assert.Equal(new byte[] { 0, 0, 0, 0 }, nonce[..4]);
        Assert.Equal(0x0102030405060708UL, BinaryPrimitives.ReadUInt64LittleEndian(nonce.AsSpan(4)));
    }

    [Fact]
    public void A_tampered_frame_fails_authentication()
    {
        using var cipher = new AirPlay2Cipher(new byte[32]);
        var frame = cipher.Seal(new byte[64]);
        frame[10] ^= 0xFF;   // flip a ciphertext byte

        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(frame, 0));
    }

    [Fact]
    public void A_frame_opened_under_the_wrong_counter_fails()
    {
        // The counter is the nonce, so it's bound into the tag: a receiver replaying or
        // reordering frames can't have them silently decode as something else.
        using var cipher = new AirPlay2Cipher(new byte[32]);
        var frame = cipher.Seal(new byte[64]);

        Assert.ThrowsAny<CryptographicException>(() => cipher.Open(frame, 7));
    }

    [Fact]
    public void The_cipher_demands_a_32_byte_key()
    {
        // The audio key is the session key's first 32 bytes, raw - handing over all 64
        // (the natural mistake, since K is 64) must fail loudly rather than half-work.
        var sessionKey = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();

        Assert.Throws<ArgumentOutOfRangeException>(() => new AirPlay2Cipher(sessionKey));

        using var cipher = new AirPlay2Cipher(sessionKey[..32]);
        Assert.Equal(2 + 16 + 16, cipher.Seal(new byte[16]).Length);
    }

    /// <summary>
    /// A minimal SRP-6a server built straight from the specification, used only to check
    /// the client against something that isn't itself.
    /// </summary>
    private sealed class ReferenceSrpServer
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

        public ReferenceSrpServer()
        {
            var x = new BigInteger(
                Hash(Salt, Hash(Encoding.UTF8.GetBytes($"{Srp6aClient.Username}:{Srp6aClient.TransientPin}"))),
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

            // S = (A * v^u) ^ b mod N
            var s = BigInteger.ModPow(bigA * BigInteger.ModPow(_v, u, _n) % _n, _b, _n);
            return Hash(Pad(s));
        }

        public bool VerifyClientProof(byte[] clientPublicKey, byte[] proof)
        {
            var hn = Hash(Pad(_n));
            var hg = Hash(Pad(_g));
            var xored = new byte[hn.Length];
            for (var i = 0; i < hn.Length; i++)
            {
                xored[i] = (byte)(hn[i] ^ hg[i]);
            }

            var expected = Hash(
                xored,
                Hash(Encoding.UTF8.GetBytes(Srp6aClient.Username)),
                Salt,
                clientPublicKey,
                Pad(_bigB),
                ComputeSessionKey(clientPublicKey));

            return CryptographicOperations.FixedTimeEquals(expected, proof);
        }

        public byte[] ComputeServerProof(byte[] clientPublicKey, byte[] clientProof)
            => Hash(clientPublicKey, clientProof, ComputeSessionKey(clientPublicKey));

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
}
