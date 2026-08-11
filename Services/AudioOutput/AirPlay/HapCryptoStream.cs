// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace OrgZ.Services.AudioOutput.AirPlay;

/// <summary>
/// The HAP session layer: once transient pairing completes, EVERYTHING on the RTSP
/// connection is ChaCha20-Poly1305 encrypted.
///
/// This is the piece whose absence made every request after pair-setup fail. A receiver
/// that gets a plaintext SETUP on an encrypted connection doesn't answer with an error -
/// it can't even parse it, so it simply drops the connection. From the sender's side that
/// looks like "connection closed mid-response" with a request that appears perfectly
/// well-formed, which is a remarkably good disguise.
///
/// Framing, per HAP section 5.2.2: the payload is split into blocks of at most 1024 bytes;
/// each goes out as [2-byte LE length][ciphertext][16-byte tag], with that length prefix
/// authenticated as AAD. The nonce is four zero bytes followed by an 8-byte little-endian
/// counter, counted separately per direction.
/// </summary>
internal sealed class HapCryptoStream : Stream
{
    /// <summary>HAP mandates 1024-byte blocks.</summary>
    private const int FrameLength = 1024;
    private const int TagLength = 16;

    private readonly Stream _inner;

    private ChaCha20Poly1305? _out;
    private ChaCha20Poly1305? _in;
    private ulong _outCounter;
    private ulong _inCounter;

    private readonly List<byte> _ciphertext = [];
    private readonly List<byte> _plaintext = [];
    private readonly byte[] _readBuffer = new byte[8192];

    public HapCryptoStream(Stream inner) => _inner = inner;

    /// <summary>True once <see cref="Enable"/> has been called; before that this is a pass-through.</summary>
    public bool IsEncrypted => _out is not null;

    /// <summary>
    /// Turns on encryption. Keys are HKDF-SHA512 over the SRP shared secret - write and
    /// read are DIFFERENT keys, and swapping them yields a connection that encrypts fine
    /// and can never decrypt a reply.
    /// </summary>
    public void Enable(byte[] outputKey, byte[] inputKey)
    {
        _out = new ChaCha20Poly1305(outputKey);
        _in = new ChaCha20Poly1305(inputKey);
        _outCounter = 0;
        _inCounter = 0;
    }

    /// <summary>The HAP control-channel key derivation, matching the reference sender exactly.</summary>
    internal static (byte[] Output, byte[] Input) DeriveControlKeys(byte[] sharedSecret)
    {
        return (
            Derive(sharedSecret, "Control-Salt", "Control-Write-Encryption-Key"),
            Derive(sharedSecret, "Control-Salt", "Control-Read-Encryption-Key"));
    }

    internal static byte[] Derive(byte[] sharedSecret, string salt, string info)
        => HKDF.DeriveKey(
            HashAlgorithmName.SHA512,
            sharedSecret,
            32,
            System.Text.Encoding.ASCII.GetBytes(salt),
            System.Text.Encoding.ASCII.GetBytes(info));

    private static byte[] NonceFor(ulong counter)
    {
        var nonce = new byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce.AsSpan(4), counter);
        return nonce;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        if (_out is null)
        {
            await _inner.WriteAsync(buffer, ct);
            return;
        }

        var offset = 0;
        while (offset < buffer.Length)
        {
            var take = Math.Min(FrameLength, buffer.Length - offset);
            var frame = new byte[2 + take + TagLength];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)take);

            _out.Encrypt(
                NonceFor(_outCounter),
                buffer.Span.Slice(offset, take),
                frame.AsSpan(2, take),
                frame.AsSpan(2 + take, TagLength),
                frame.AsSpan(0, 2));

            _outCounter++;
            offset += take;

            await _inner.WriteAsync(frame, ct);
        }
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (_in is null)
        {
            return await _inner.ReadAsync(buffer, ct);
        }

        while (_plaintext.Count == 0)
        {
            var read = await _inner.ReadAsync(_readBuffer, ct);
            if (read == 0)
            {
                return 0;
            }

            _ciphertext.AddRange(_readBuffer.AsSpan(0, read));
            DrainBlocks();
        }

        var take = Math.Min(buffer.Length, _plaintext.Count);
        for (var i = 0; i < take; i++)
        {
            buffer.Span[i] = _plaintext[i];
        }
        _plaintext.RemoveRange(0, take);
        return take;
    }

    /// <summary>Decrypts every complete block currently buffered, leaving any partial one.</summary>
    private void DrainBlocks()
    {
        while (_ciphertext.Count >= 2)
        {
            var length = _ciphertext[0] | (_ciphertext[1] << 8);
            var blockLength = length + TagLength;
            if (_ciphertext.Count < blockLength + 2)
            {
                return;   // partial block - wait for the rest
            }

            var aad = new byte[] { _ciphertext[0], _ciphertext[1] };
            var payload = new byte[length];
            var tag = new byte[TagLength];
            _ciphertext.CopyTo(2, payload, 0, length);
            _ciphertext.CopyTo(2 + length, tag, 0, TagLength);

            var plain = new byte[length];
            _in!.Decrypt(NonceFor(_inCounter), payload, tag, plain, aad);
            _inCounter++;

            _plaintext.AddRange(plain);
            _ciphertext.RemoveRange(0, 2 + blockLength);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() => _inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);

    public override bool CanRead => _inner.CanRead;
    public override bool CanWrite => _inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _out?.Dispose();
            _in?.Dispose();
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }
}
