// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Security.Cryptography;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// The RAOP session crypto: a random AES-128 key + IV, the key wrapped with Apple's
/// well-known AirPort public key (RSA-OAEP/SHA-1) for the SDP's <c>rsaaeskey</c> line,
/// and the per-packet payload encryption.
///
/// The payload rule is RAOP-specific and easy to get wrong: AES-128-CBC over WHOLE
/// 16-byte blocks only, the IV reset to the session IV at the start of EVERY packet,
/// and any trailing bytes shipped in the clear. A receiver decrypting a stream encrypted
/// any other way emits noise.
/// </summary>
internal sealed class RaopCrypto
{
    /// <summary>
    /// Apple's AirPort Express public modulus, recovered by reverse engineering and used
    /// verbatim by every open AirPlay sender. Public key material only - it wraps the
    /// session key so the receiver (which holds the private half) can unwrap it.
    /// </summary>
    private const string AppleModulusBase64 =
        "59dE8qLieItsH1WgjrcFRKj6eUWqi+bGLOX1HL3U3GhC/j0Qg90u3sG/1CUtwC" +
        "5vOYvfDmFI6oSFXi5ELabWJmT2dKHzBJKa3k9ok+8t9ucRqMd6DZHJ2YCCLlDR" +
        "KSKv6kDqnw4UwPdpOMXziC/AMj3Z/lUVX1G7WSHCAWKf1zNS1eLvqr+boEjXuB" +
        "OitnZ/bDzPHrTOZz0Dew0uowxf/+sG+NCK3eQJVxqcaJ/vEHKIVd2M+5qL71yJ" +
        "Q+87X6oV3eaYvt3zWZYD6z5vYTcrtij2VZ9Zmni/UAaHqn9JdsBWLUEpVviYnh" +
        "imNVvYFZeCXg/IdTQ+x4IRdiXNv5hEew==";

    private const string AppleExponentBase64 = "AQAB";

    private readonly byte[] _key;
    private readonly byte[] _iv;

    public RaopCrypto()
    {
        _key = RandomNumberGenerator.GetBytes(16);
        _iv = RandomNumberGenerator.GetBytes(16);
    }

    /// <summary>Test seam: a fixed key/IV so encryption output is reproducible.</summary>
    internal RaopCrypto(byte[] key, byte[] iv)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(key.Length, 16);
        ArgumentOutOfRangeException.ThrowIfNotEqual(iv.Length, 16);
        _key = key;
        _iv = iv;
    }

    /// <summary>The SDP <c>a=aesiv:</c> value.</summary>
    public string IvBase64 => Base64NoPadding(_iv);

    /// <summary>The SDP <c>a=rsaaeskey:</c> value - the session key under Apple's public key.</summary>
    public string EncryptedKeyBase64()
    {
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Convert.FromBase64String(AppleModulusBase64),
            Exponent = Convert.FromBase64String(AppleExponentBase64),
        });

        return Base64NoPadding(rsa.Encrypt(_key, RSAEncryptionPadding.OaepSHA1));
    }

    /// <summary>
    /// Encrypts one audio payload in place. Whole blocks only, IV reset per packet -
    /// the tail (payload length % 16) is deliberately left as plaintext.
    /// </summary>
    public void EncryptPayload(Span<byte> payload)
    {
        var whole = payload.Length / 16 * 16;
        if (whole == 0)
        {
            return;
        }

        using var aes = Aes.Create();
        aes.Key = _key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;

        // A fresh IV per packet: CBC chaining never carries across packet boundaries,
        // because a receiver decrypts each packet standalone (they can arrive out of order).
        aes.IV = _iv;
        using var encryptor = aes.CreateEncryptor();

        var block = payload[..whole].ToArray();
        var cipher = encryptor.TransformFinalBlock(block, 0, block.Length);
        cipher.CopyTo(payload[..whole]);
    }

    /// <summary>RAOP strips base64 padding in SDP attribute values.</summary>
    internal static string Base64NoPadding(byte[] data) => Convert.ToBase64String(data).TrimEnd('=');
}
