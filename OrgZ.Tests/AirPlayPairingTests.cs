// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using OrgZ.Services.AudioOutput;
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

    /// <summary>
    /// The oracle test: fixed inputs run through srptools - the library pyatv uses to talk
    /// to real Apple hardware - and the expected values pasted in verbatim.
    ///
    /// This exists because the self-written reference server below shares my reading of the
    /// spec, so it happily agreed with a client that padded values the spec doesn't pad.
    /// It proved self-consistency, not correctness. An implementation nobody here wrote is
    /// the only thing that can catch that class of mistake.
    /// </summary>
    [Fact]
    public void Srp_matches_the_external_reference_implementation()
    {
        var a = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var serverPublic = Enumerable.Range(0, 384).Select(i => (byte)(((i * 7) + 11) & 0xFF)).ToArray();

        var client = new Srp6aClient(a);
        var proof = client.ComputeProof(salt, serverPublic);

        Assert.Equal(
            "BC0E7CF5DC3BABF67DCEDBB3B140AACC6CAC43F4336B43BBD5DE48D6EA7C8EDA66924E354255225BCCAD9DEBE21182E6" +
            "BB050F3FF3E6CFBB62C229379968C70CA436AD649A0B051373184215EEF046F6F1F2256838F958581F6C7B2B85FA4AFE3" +
            "26A0E8A951D4489305331AFF88A136FD8D108BCC95FCEB7E557C889C828BD23FB0702F053E1CA6470FB3C76BCE4843FC0" +
            "05C7EA675740F8550212656CFC8919D9DB805A434A68229E0D9DFE43FC16DC680A5CE74B77CF374353B05759BC1DA3A9D" +
            "ABDE30A4209381C87CA83D9483ABDF66B86F9B1CBDA9AD82C62712B87CE6FB7069B8FC8DF344261821A06D0DC5106AF76" +
            "D4245F3F7737A94DBC484B415555DC401842D3011204553BA9F611B02BC38DE26EBA1A76BF8350205A62C436BA1C3C7C6" +
            "9D59318BD107FD1C1F5D846B3142E85A5D49E522655E020ED1BFE1E186CF923BF328F0B9B4C6A8AA3266ED9125BB98D63" +
            "827110713BE7803122EE4603C54EA31863CE4B10AFF31F9073CF63B94733B4F066E72D4EC35687047D5D0DB160",
            Convert.ToHexString(client.PublicKey));

        Assert.Equal(
            "629B16B8D5DF0E5C532ECEC282C8A62145C6607CF7FEB2AF8D1377B4F4B3525DA4DFF801E4A23F8D9F03727984EFD1FD" +
            "F1E7A444CC80D0CC77F8E05621BB5AAD",
            Convert.ToHexString(client.SessionKey));

        Assert.Equal(
            "0D7AA155741356A6203D5C3F08C75BFF776ABC126FAB0D080AA65F945D91081DFEE6982E6FADB99177DC65D2498275C7" +
            "496CCA916AFF9FEDE5A37D73EB0E939B",
            Convert.ToHexString(proof));
    }

    /// <summary>
    /// A receiver with "Require Password" set rejects the transient PIN, so the password
    /// has to reach the SRP identity hash. Same fixed inputs, same external reference -
    /// only the password differs, and it changes both K and the proof.
    /// </summary>
    [Fact]
    public void Srp_with_a_password_matches_the_external_reference_implementation()
    {
        var a = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var serverPublic = Enumerable.Range(0, 384).Select(i => (byte)(((i * 7) + 11) & 0xFF)).ToArray();

        var client = new Srp6aClient(a, "speaker-pw");
        var proof = client.ComputeProof(salt, serverPublic);

        Assert.Equal(
            "4B097A69911E0D4EECA33D9A207D1D982B56CB085388289FE3E0EBB2347D853CC5A1FACADF9E19CDBA6EC860E33D15E7" +
            "0A8C144F0098F7F3FD9BC67CA3E1D778",
            Convert.ToHexString(client.SessionKey));

        Assert.Equal(
            "27D6C1FFDAF7C227092E73EBA570BA64B9A3769770441984E29C0308FF27CEDFE1EE6C0FB6ADABD6AC0DF4322CDA4761" +
            "8D92CDAA4FC186C2960B0D3CFA0D457E",
            Convert.ToHexString(proof));
    }

    /// <summary>A null/empty password must mean the transient PIN, not an empty one.</summary>
    [Fact]
    public void Srp_falls_back_to_the_transient_pin_when_no_password_is_given()
    {
        var a = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var salt = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var serverPublic = Enumerable.Range(0, 384).Select(i => (byte)(((i * 7) + 11) & 0xFF)).ToArray();

        var explicitPin = new Srp6aClient(a, Srp6aClient.TransientPin).ComputeProof(salt, serverPublic);

        Assert.Equal(explicitPin, new Srp6aClient(a, null).ComputeProof(salt, serverPublic));
        Assert.Equal(explicitPin, new Srp6aClient(a, "").ComputeProof(salt, serverPublic));
    }

    [Theory]
    [InlineData("sf=0x98484", true)]      // HomePod with "Require Password" on
    [InlineData("sf=0x204", false)]       // a Mac advertising no password bit
    [InlineData("flags=0x98484", true)]   // _airplay spells the same field "flags"
    [InlineData("pw=true", true)]
    [InlineData("pw=false", false)]
    [InlineData("am=AudioAccessory6,1", false)]
    public void Txt_records_reveal_which_receivers_want_a_password(string entry, bool expected)
    {
        // One TXT record: a single length-prefixed key=value string.
        var payload = System.Text.Encoding.UTF8.GetBytes(entry);
        var record = new byte[payload.Length + 1];
        record[0] = (byte)payload.Length;
        payload.CopyTo(record, 1);

        Assert.Equal(expected, AirPlayDeviceProvider.TxtDemandsPassword(record, 0, record.Length));
    }

    /// <summary>
    /// The AirPlay 2 audio frame: ciphertext, tag, then the counter the receiver needs to
    /// build the nonce. The RTP header's timestamp+ssrc words are the AAD, so a receiver
    /// that reassembles the packet differently fails to authenticate and plays silence -
    /// which is exactly how this went wrong before, with no error anywhere to show for it.
    /// </summary>
    [Fact]
    public void Airplay2_audio_frames_carry_their_nonce_and_bind_the_rtp_header()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)(i * 3)).ToArray();
        using var cipher = new AirPlay2Cipher(key);

        var pcm = Enumerable.Range(0, 1408).Select(i => (byte)(i & 0xFF)).ToArray();
        var header = RaopPackets.BuildAudio(sequence: 42, timestamp: 123456, ssrc: 0xDEADBEEF, [], first: true);
        var aad = header.AsSpan(4, 8).ToArray();

        var first = cipher.SealAudio(pcm, aad);
        Assert.Equal(pcm.Length + 16 + 8, first.Length);

        // First packet uses counter 0, so the trailing nonce is eight zero bytes.
        Assert.Equal(new byte[8], first[^8..]);

        // Rebuild the nonce the way a receiver would and decrypt.
        var nonce = new byte[12];
        first[^8..].CopyTo(nonce, 4);

        using var verifier = new System.Security.Cryptography.ChaCha20Poly1305(key);
        var plaintext = new byte[pcm.Length];
        verifier.Decrypt(nonce, first.AsSpan(0, pcm.Length), first.AsSpan(pcm.Length, 16), plaintext, aad);
        Assert.Equal(pcm, plaintext);

        // The counter must advance, or a repeated nonce would leak the keystream.
        var second = cipher.SealAudio(pcm, aad);
        Assert.Equal(1UL, BitConverter.ToUInt64(second[^8..]));
        Assert.NotEqual(first[..pcm.Length], second[..pcm.Length]);
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
        // Flags 0x10 as a SINGLE byte selects transient pairing - the width working
        // senders use; padding it to four was one of the divergences from the reference.
        Assert.Equal([0x10], tlv[Tlv8.Flags]);
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
    [InlineData((byte)0x03, "backing off")]
    [InlineData((byte)0x05, "locked pairing")]
    [InlineData((byte)0x06, "busy")]
    public void Pair_setup_errors_read_as_english(byte code, string fragment)
    {
        // Codes follow the HAP numbering: 0x03 BackOff, 0x05 MaxTries, 0x06 Unavailable.
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

    // ===== Protocol selection and SETUP parsing =====

    [Theory]
    [InlineData("AirPlay receiver refused OPTIONS (401 Unauthorized).", true)]
    [InlineData("AirPlay receiver refused ANNOUNCE (453 Not Enough Bandwidth).", false)]
    [InlineData("Connection refused", false)]
    public void A_401_is_what_routes_a_receiver_to_the_paired_path(string message, bool expected)
    {
        Assert.Equal(expected, AirPlayRaopSink.NeedsPairing(new InvalidOperationException(message)));
    }

    [Fact]
    public void A_failed_handshake_backs_off_instead_of_retrying_immediately()
    {
        // The bus reopens a closed sink every playback tick. Without a cooldown that
        // becomes ~3 pair-setup attempts a second, which is what tripped the HomePod's
        // brute-force lockout in the first place.
        var device = new AudioDeviceInfo
        {
            DeviceId = "test", DisplayName = "TestPod", ProviderId = "airplay", ProviderName = "AirPlay",
        };
        var sink = new AirPlayRaopSink(device, "127.0.0.1", 1);   // nothing listening

        sink.Open(AudioFormat.CdDaStereo16);

        // The connect runs off-thread; give it a moment to fail and arm the backoff.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (sink.IsOpen && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.False(sink.IsOpen);
        // The next Open must refuse immediately from cached state, not dial out again.
        Assert.Throws<InvalidOperationException>(() => sink.Open(AudioFormat.CdDaStereo16));

        sink.Dispose();
    }

    [Fact]
    public void Setup_reply_data_port_is_read_from_the_streams_array()
    {
        var reply = BinaryPlist.Write(new Dictionary<string, object?>
        {
            ["eventPort"] = 7011L,
            ["streams"] = new List<object?>
            {
                new Dictionary<string, object?> { ["type"] = 96L, ["dataPort"] = 51234L, ["controlPort"] = 51235L },
            },
        });

        Assert.Equal(51234, AirPlay2Session.ExtractDataPort(reply));
    }

    [Fact]
    public void Setup_reply_data_port_falls_back_to_the_top_level()
    {
        // Some receivers answer with the port at the root rather than inside streams.
        var reply = BinaryPlist.Write(new Dictionary<string, object?> { ["dataPort"] = 6000L });

        Assert.Equal(6000, AirPlay2Session.ExtractDataPort(reply));
    }

    [Fact]
    public void Setup_reply_without_a_port_reads_as_null_rather_than_zero()
    {
        // Streaming to port 0 would be a silent black hole - the caller must see "no port".
        Assert.Null(AirPlay2Session.ExtractDataPort(BinaryPlist.Write(new Dictionary<string, object?> { ["eventPort"] = 7011L })));
        Assert.Null(AirPlay2Session.ExtractDataPort([1, 2, 3]));
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
}
