// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Buffers.Binary;
using FoxOrangebook;
using FoxRedbook;
using Serilog;

namespace OrgZ.Services;

public sealed record CdBurnTrack
{
    public required string WavFilePath { get; init; }
    public string? Title { get; init; }
    public string? Performer { get; init; }
}

public readonly record struct CdBurnProgress
{
    public required int TrackNumber { get; init; }
    public required int TrackCount { get; init; }
    public required int TrackSectors { get; init; }
    public required int SectorsWritten { get; init; }
    public required long TotalDiscSectors { get; init; }
    public required long TotalSectorsWritten { get; init; }
    public double DiscPercent => TotalDiscSectors == 0 ? 0 : (double)TotalSectorsWritten / TotalDiscSectors;
}

/// <summary>
/// Disc-At-Once audio burning via FoxOrangebook.  Accepts WAV files containing
/// 16-bit stereo 44.1 kHz PCM (the CD-DA native format) and programs the drive
/// with a full cue sheet before streaming sectors.
/// </summary>
/// <remarks>
/// Transcoding from lossy/lossless sources (MP3, FLAC, etc.) is not done here -
/// callers should rip via <see cref="CdRipService"/> or supply already-encoded
/// WAVs.  Non-CD-DA WAV formats are rejected up front so we never program a
/// coaster-inducing cue sheet.
/// </remarks>
public static class CdBurnService
{
    private const int BytesPerSector = 2352;
    private const int RedbookSampleRate = 44100;
    private const int RedbookChannels = 2;
    private const int RedbookBitsPerSample = 16;

    private static readonly ILogger _log = Logging.For("CdBurn");

    /// <summary>Result of <see cref="CheckBurnMedia"/>.</summary>
    public enum BurnMediaStatus
    {
        /// <summary>A blank, writable disc is loaded - ready to burn.</summary>
        Ready,
        /// <summary>The drive is a recorder but no disc is loaded.</summary>
        NoMedia,
        /// <summary>A disc is loaded but it already has content (not blank).</summary>
        NotBlank,
        /// <summary>The drive can't write discs (DAO unsupported).</summary>
        NotWritable,
        /// <summary>The drive couldn't be opened or queried.</summary>
        DriveError,
    }

    /// <summary>
    /// Un-elevated pre-flight before a burn: opens <paramref name="drivePath"/> and checks a
    /// blank, writable disc is loaded - the same SCSI passthrough as the recorder probe, so no
    /// UAC. Lets the GUI fail fast with a clear message instead of transcoding and prompting
    /// for elevation only to have the drive reject the burn.
    /// </summary>
    public static BurnMediaStatus CheckBurnMedia(string drivePath)
    {
        ArgumentException.ThrowIfNullOrEmpty(drivePath);

        try
        {
            using var drive = OpticalDrive.Open(drivePath);
            if (drive is not IScsiTransport transport)
            {
                return BurnMediaStatus.DriveError;
            }

            var session = new BurnSession(transport);

            if (!session.SupportsDaoBurn())
            {
                return BurnMediaStatus.NotWritable;
            }

            try
            {
                var info = session.ReadDiscInfo();
                return info.Status == DiscStatus.Blank ? BurnMediaStatus.Ready : BurnMediaStatus.NotBlank;
            }
            catch (MediaNotPresentException)
            {
                return BurnMediaStatus.NoMedia;
            }
        }
        catch (MediaNotPresentException)
        {
            return BurnMediaStatus.NoMedia;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Burn media pre-flight failed for {Drive}", drivePath);
            return BurnMediaStatus.DriveError;
        }
    }

    /// <summary>
    /// Entry point used by the GUI.  On Windows, spawns an elevated copy of
    /// OrgZ.exe via <see cref="CdElevation"/> (UAC per operation); on other
    /// platforms, falls through to <see cref="BurnAsync"/> in-process.
    /// </summary>
    public static async Task BurnWithElevationAsync(
        string drivePath,
        IReadOnlyList<CdBurnTrack> tracks,
        IProgress<CdBurnProgress>? progress = null,
        string? discTitle = null,
        string? discPerformer = null,
        bool testWrite = false,
        CancellationToken cancellationToken = default)
    {
        if (!CdElevation.RequiresElevation)
        {
            await BurnAsync(drivePath, tracks, progress, discTitle, discPerformer, testWrite, cancellationToken);
            return;
        }

        var spec = new CdHelperSpec
        {
            Operation = "burn",
            DrivePath = drivePath,
            DiscTitle = discTitle,
            DiscPerformer = discPerformer,
            TestWrite = testWrite,
            Tracks = tracks.Select((t, i) => new CdHelperTrack
            {
                TrackNumber = i + 1,
                WavFilePath = t.WavFilePath,
                Title = t.Title,
                Artist = t.Performer,
            }).ToList(),
        };

        string? error = null;

        var exitCode = await CdElevation.RunElevatedAsync(spec, evt =>
        {
            switch (evt.Type)
            {
                case "burn-progress":
                {
                    progress?.Report(new CdBurnProgress
                    {
                        TrackNumber = evt.TrackNumber,
                        TrackCount = evt.TrackCount,
                        TrackSectors = evt.TrackSectors,
                        SectorsWritten = evt.SectorsWritten,
                        TotalDiscSectors = evt.TotalDiscSectors,
                        TotalSectorsWritten = evt.TotalSectorsWritten,
                    });
                    break;
                }
                case "error":
                {
                    error = evt.Message;
                    break;
                }
            }
        }, cancellationToken);

        if (exitCode != 0)
        {
            throw new InvalidOperationException(error ?? $"Elevated burn helper exited with code {exitCode}.");
        }
    }

    /// <summary>
    /// Burns a list of WAV files to a blank CD-R/CD-RW in disc-at-once mode.
    /// </summary>
    public static async Task BurnAsync(
        string drivePath,
        IReadOnlyList<CdBurnTrack> tracks,
        IProgress<CdBurnProgress>? progress = null,
        string? discTitle = null,
        string? discPerformer = null,
        bool testWrite = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(drivePath);
        ArgumentNullException.ThrowIfNull(tracks);

        if (tracks.Count == 0)
        {
            throw new ArgumentException("At least one track is required.", nameof(tracks));
        }

        // Validate all sources up front - a burn that starts and aborts halfway is
        // just a coaster.  Opening the file streams also locks them for the session.
        var openedStreams = new List<FileStream>(tracks.Count);
        var audioSources = new List<AudioTrackSource>(tracks.Count);

        try
        {
            for (int i = 0; i < tracks.Count; i++)
            {
                var track = tracks[i];
                if (!File.Exists(track.WavFilePath))
                {
                    throw new FileNotFoundException($"Track {i + 1} source missing.", track.WavFilePath);
                }

                var fs = new FileStream(track.WavFilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                openedStreams.Add(fs);

                var (dataOffset, dataLength) = ParseCdAudioWav(fs, track.WavFilePath);

                if (dataLength % BytesPerSector != 0)
                {
                    throw new InvalidDataException($"Track {i + 1} ({track.WavFilePath}) PCM length {dataLength} is not a multiple of 2352 bytes (one CD sector).");
                }

                audioSources.Add(new AudioTrackSource
                {
                    Pcm = new SubStream(fs, dataOffset, dataLength),
                    PregapSectors = i == 0 ? 150 : 0,
                    Title = track.Title,
                    Performer = track.Performer,
                });
            }

            var opticalDrive = OpticalDrive.Open(drivePath);
            await using (opticalDrive)
            {
                if (opticalDrive is not IScsiTransport transport)
                {
                    throw new InvalidOperationException($"Drive '{drivePath}' does not expose an IScsiTransport (required for burning).");
                }

                _log.Information("Burning {Count} tracks to {Drive}: {Vendor} {Product} (fw {Rev}) testWrite={Test}", tracks.Count, drivePath, opticalDrive.Inquiry.Vendor, opticalDrive.Inquiry.Product, opticalDrive.Inquiry.Revision, testWrite);

                var options = new BurnOptions
                {
                    TestWrite = testWrite,
                    BufferUnderrunProtection = true,
                    DiscTitle = discTitle,
                    DiscPerformer = discPerformer,
                };

                var session = new BurnSession(transport, options);

                IProgress<BurnProgress>? rawProgress = null;
                if (progress != null)
                {
                    rawProgress = new Progress<BurnProgress>(p => progress.Report(new CdBurnProgress
                    {
                        TrackNumber = p.TrackNumber,
                        TrackCount = tracks.Count,
                        TrackSectors = p.TrackSectors,
                        SectorsWritten = p.SectorsWritten,
                        TotalDiscSectors = p.TotalDiscSectors,
                        TotalSectorsWritten = p.TotalSectorsWritten,
                    }));
                }

                await session.BurnAsync(audioSources, rawProgress, cancellationToken);

                _log.Information("Burn complete: {Count} tracks to {Drive}", tracks.Count, drivePath);
            }
        }
        finally
        {
            foreach (var fs in openedStreams)
            {
                fs.Dispose();
            }
        }
    }

    /// <summary>
    /// Walks a RIFF/WAVE file, validates it contains 16-bit stereo 44.1 kHz PCM
    /// (CD-DA native format), and returns the byte range of the <c>data</c> chunk
    /// payload.  Accepts files with extra LIST/INFO chunks preceding or following
    /// the <c>data</c> chunk as long as the format chunk describes CD-DA.
    /// </summary>
    internal static (long DataOffset, long DataLength) ParseCdAudioWav(Stream stream, string sourceLabel)
    {
        ArgumentNullException.ThrowIfNull(stream);

        stream.Position = 0;

        Span<byte> header = stackalloc byte[12];
        if (stream.Read(header) != 12)
        {
            throw new InvalidDataException($"{sourceLabel}: file too short for RIFF header.");
        }

        if (!MatchesFourCc(header[..4], "RIFF") || !MatchesFourCc(header.Slice(8, 4), "WAVE"))
        {
            throw new InvalidDataException($"{sourceLabel}: not a RIFF/WAVE file.");
        }

        bool fmtSeen = false;
        long dataOffset = -1;
        long dataLength = -1;

        Span<byte> chunkHeader = stackalloc byte[8];
        Span<byte> fmt = stackalloc byte[16];
        while (stream.Position < stream.Length)
        {
            if (stream.Read(chunkHeader) != 8)
            {
                break;
            }

            uint chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader.Slice(4, 4));
            long chunkStart = stream.Position;

            if (MatchesFourCc(chunkHeader[..4], "fmt "))
            {
                int got = stream.Read(fmt);
                if (got < 16)
                {
                    throw new InvalidDataException($"{sourceLabel}: truncated fmt chunk.");
                }

                ushort formatTag = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(0, 2));
                ushort channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(2, 2));
                uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(fmt.Slice(4, 4));
                ushort bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(fmt.Slice(14, 2));

                if (formatTag != 1)
                {
                    throw new InvalidDataException($"{sourceLabel}: not uncompressed PCM (format tag 0x{formatTag:X4}).");
                }

                if (channels != RedbookChannels || sampleRate != RedbookSampleRate || bitsPerSample != RedbookBitsPerSample)
                {
                    throw new InvalidDataException($"{sourceLabel}: format is {channels}ch {sampleRate}Hz {bitsPerSample}bit; must be 2ch 44100Hz 16bit for CD-DA.");
                }

                fmtSeen = true;
                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
            }
            else if (MatchesFourCc(chunkHeader[..4], "data"))
            {
                if (!fmtSeen)
                {
                    throw new InvalidDataException($"{sourceLabel}: data chunk precedes fmt chunk.");
                }

                dataOffset = chunkStart;
                dataLength = chunkSize;
                break;
            }
            else
            {
                stream.Position = chunkStart + chunkSize + (chunkSize & 1);
            }
        }

        if (dataOffset < 0)
        {
            throw new InvalidDataException($"{sourceLabel}: no data chunk found.");
        }

        return (dataOffset, dataLength);
    }

    private static bool MatchesFourCc(ReadOnlySpan<byte> bytes, string fourCc)
    {
        if (bytes.Length != 4 || fourCc.Length != 4)
        {
            return false;
        }

        for (int i = 0; i < 4; i++)
        {
            if (bytes[i] != (byte)fourCc[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Read-only view over a contiguous byte range of an underlying seekable stream.
    /// Used to expose just the PCM payload of a WAV file to <see cref="AudioTrackSource"/>
    /// without re-copying the data.  Multiple instances may share one base stream,
    /// so reads are serialized via a lock while seeking the base stream to the
    /// right absolute offset.
    /// </summary>
    internal sealed class SubStream : Stream
    {
        private readonly Stream _base;
        private readonly long _offset;
        private readonly long _length;
        private long _position;

        public SubStream(Stream baseStream, long offset, long length)
        {
            ArgumentNullException.ThrowIfNull(baseStream);
            if (!baseStream.CanSeek || !baseStream.CanRead)
            {
                throw new ArgumentException("Base stream must be seekable and readable.", nameof(baseStream));
            }

            if (offset < 0 || length < 0 || offset + length > baseStream.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            _base = baseStream;
            _offset = offset;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _length)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _position = value;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }

        public override int Read(Span<byte> buffer)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, remaining);
            lock (_base)
            {
                _base.Position = _offset + _position;
                int got = _base.Read(buffer[..toRead]);
                _position += got;
                return got;
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            long remaining = _length - _position;
            if (remaining <= 0)
            {
                return 0;
            }

            int toRead = (int)Math.Min(buffer.Length, remaining);

            // The base stream is shared across AudioTrackSource instances only within
            // a single track (same FileStream per source in our wiring), so contention
            // is benign - but keep the lock for correctness if that assumption changes.
            int got;
            lock (_base)
            {
                _base.Position = _offset + _position;
                got = _base.Read(buffer[..toRead].Span);
                _position += got;
            }

            await Task.CompletedTask;
            return got;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };

            if (target < 0 || target > _length)
            {
                throw new IOException("Seek outside of substream bounds.");
            }

            _position = target;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
