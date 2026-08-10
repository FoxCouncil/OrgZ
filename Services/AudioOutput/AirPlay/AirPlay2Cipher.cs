// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// AirPlay 2's audio transport framing: ChaCha20-Poly1305 over each ALAC frame.
///
/// Frame on the wire:  [2-byte LE length][ciphertext][16-byte tag]
///   nonce = 4 zero bytes || 8-byte LE packet counter
///   AAD   = the 2-byte length prefix
///
/// The counter increments per frame and must never repeat under one key - reusing a
/// nonce with ChaCha20-Poly1305 doesn't merely garble that frame, it leaks the keystream.
/// A session that runs out of counter (2^64 frames, ~13 million years at 44.1 kHz) is not
/// a practical concern, but wrapping is refused rather than silently reused.
/// </summary>
internal sealed class AirPlay2Cipher : IDisposable
{
    private readonly ChaCha20Poly1305 _cipher;
    private ulong _counter;
    private bool _disposed;

    /// <summary>Key is the pairing session key's first 32 bytes, used raw (no HKDF).</summary>
    public AirPlay2Cipher(byte[] audioKey)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(audioKey.Length, 32);
        _cipher = new ChaCha20Poly1305(audioKey);
    }

    /// <summary>Frames one ALAC payload for the wire, advancing the packet counter.</summary>
    public byte[] Seal(ReadOnlySpan<byte> payload)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (payload.Length > ushort.MaxValue)
        {
            throw new ArgumentException($"An AirPlay 2 audio frame can't exceed {ushort.MaxValue} bytes.", nameof(payload));
        }

        var frame = new byte[2 + payload.Length + 16];
        BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)payload.Length);

        // The length prefix authenticates as AAD - it's in the clear, so it must be bound
        // to the ciphertext or a peer could truncate a frame undetected.
        _cipher.Encrypt(
            NonceFor(_counter),
            payload,
            frame.AsSpan(2, payload.Length),
            frame.AsSpan(2 + payload.Length, 16),
            frame.AsSpan(0, 2));

        checked
        {
            _counter++;
        }

        return frame;
    }

    /// <summary>The nonce for a given packet index: four zero bytes then the LE counter.</summary>
    internal static byte[] NonceFor(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    /// <summary>Unseals a frame - used by the tests to prove the framing round-trips.</summary>
    internal byte[] Open(ReadOnlySpan<byte> frame, ulong counter)
    {
        var length = BinaryPrimitives.ReadUInt16LittleEndian(frame);
        var plaintext = new byte[length];

        _cipher.Decrypt(
            NonceFor(counter),
            frame.Slice(2, length),
            frame.Slice(2 + length, 16),
            plaintext,
            frame[..2]);

        return plaintext;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _cipher.Dispose();
    }
}
