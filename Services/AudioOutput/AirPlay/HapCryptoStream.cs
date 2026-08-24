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

    private static readonly Serilog.ILogger _log = Logging.For("Hap");

    /// <summary>
    /// Records what actually crosses the socket, encrypted. This is the packet capture -
    /// we own both ends, so there is no need for an external sniffer.
    /// </summary>
    private static void Trace(string what)
    {
        if (Environment.GetEnvironmentVariable("ORGZ_RTSP_DUMP") is { Length: > 0 } path)
        {
            File.AppendAllText(path + ".wire", what + "\n");
        }
        _log.Debug("HAP wire: {What}", what);
    }

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

    /// <summary>
    /// The EVENT-channel key derivation. Same shape as the control channel with a different
    /// salt - and the two keys swapped.
    ///
    /// The swap is not a quirk to paper over: the event channel is a REVERSE connection. The
    /// receiver is the one making requests on it, so from our end the thing we decrypt is
    /// what the receiver WROTE, and the thing we encrypt is what it will READ. Deriving these
    /// the same way round as the control channel yields a socket that connects, accepts
    /// bytes, and produces garbage on every decrypt.
    /// </summary>
    internal static (byte[] Output, byte[] Input) DeriveEventKeys(byte[] sharedSecret)
    {
        return (
            Derive(sharedSecret, "Events-Salt", "Events-Read-Encryption-Key"),
            Derive(sharedSecret, "Events-Salt", "Events-Write-Encryption-Key"));
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
                _log.Debug("HAP read: socket EOF (buffered ciphertext {Buffered}B)", _ciphertext.Count);
                return 0;
            }

            _ciphertext.AddRange(_readBuffer.AsSpan(0, read));
            DrainBlocks();
            _log.Debug("HAP read: {Read}B in, {Cipher}B ciphertext buffered, {Plain}B plaintext ready", read, _ciphertext.Count, _plaintext.Count);
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

    /// <summary>
    /// A REAL synchronous read - it must not defer to the async path.
    ///
    /// The RTSP handshake runs on its own thread precisely so it can't be delayed by a busy
    /// thread pool (in the app, LibVLC and the UI keep the pool busy; in a test process
    /// nothing does). Bouncing through ReadAsync here would put the completion straight
    /// back on the pool and reintroduce exactly the stall this avoids.
    /// </summary>
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_in is null)
        {
            return _inner.Read(buffer, offset, count);
        }

        while (_plaintext.Count == 0)
        {
            var read = _inner.Read(_readBuffer, 0, _readBuffer.Length);
            Trace($"IN  {read}B head={Convert.ToHexString(_readBuffer, 0, Math.Min(8, Math.Max(read, 1)))}");
            if (read == 0)
            {
                return 0;
            }

            _ciphertext.AddRange(_readBuffer.AsSpan(0, read));
            DrainBlocks();
            Trace($"IN  decrypted -> {_plaintext.Count}B plaintext, {_ciphertext.Count}B held");
        }

        var take = Math.Min(count, _plaintext.Count);
        _plaintext.CopyTo(0, buffer, offset, take);
        _plaintext.RemoveRange(0, take);
        return take;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        if (_out is null)
        {
            _inner.Write(buffer, offset, count);
            return;
        }

        var end = offset + count;
        while (offset < end)
        {
            var take = Math.Min(FrameLength, end - offset);
            var frame = new byte[2 + take + TagLength];
            BinaryPrimitives.WriteUInt16LittleEndian(frame, (ushort)take);

            _out.Encrypt(
                NonceFor(_outCounter),
                buffer.AsSpan(offset, take),
                frame.AsSpan(2, take),
                frame.AsSpan(2 + take, TagLength),
                frame.AsSpan(0, 2));

            Trace($"OUT nonce={_outCounter} plain={take} frame={frame.Length} head={Convert.ToHexString(frame, 0, Math.Min(8, frame.Length))}");
            _outCounter++;
            offset += take;
            _inner.Write(frame, 0, frame.Length);
        }
    }

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
