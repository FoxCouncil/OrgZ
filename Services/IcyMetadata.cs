// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Text;

namespace OrgZ.Services;

/// <summary>
/// ICY (SHOUTcast/Icecast) in-stream metadata primitives: de-interleaving Icy-MetaData:1
/// response bodies (batch for probe samples, incremental for the live playback pump),
/// parsing StreamTitle, and scrubbing iHeart-style tracking junk.
/// </summary>
public static class IcyMetadata
{
    /// <summary>
    /// Splits an Icy-MetaData:1 response body back into clean audio and the first embedded
    /// metadata block: [metaint audio bytes][length byte][length*16 metadata]... repeating.
    /// </summary>
    public static (byte[] Audio, string? Title) Deinterleave(byte[] raw, int metaint)
    {
        using var audio = new MemoryStream(raw.Length);
        string? title = null;
        var pos = 0;
        while (pos < raw.Length)
        {
            var chunk = Math.Min(metaint, raw.Length - pos);
            audio.Write(raw, pos, chunk);
            pos += chunk;
            if (pos >= raw.Length)
            {
                break;
            }

            var metaLen = raw[pos] * 16;
            pos++;
            if (metaLen > 0)
            {
                var available = Math.Min(metaLen, raw.Length - pos);
                title ??= ParseStreamTitle(DecodeMetadata(raw, pos, available));
                pos += available;
            }
        }
        return (audio.ToArray(), title);
    }

    public static string? ParseStreamTitle(string metadata)
    {
        const string marker = "StreamTitle='";
        var start = metadata.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return null;
        }
        start += marker.Length;
        var end = metadata.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
        {
            end = metadata.LastIndexOf('\'');
        }
        if (end <= start)
        {
            return null;
        }
        var title = metadata[start..end].Trim('\0', ' ');
        return title.Length == 0 ? null : CleanStreamTitle(title);
    }

    /// <summary>
    /// Scrubs broadcast-automation junk out of StreamTitle before display. Two formats in
    /// the wild so far:
    ///
    ///  - iHeart tracking attributes: <c>SURFARIS - text="Wipe Out" song_spot="M" ...</c>
    ///    - pull the real song out of text="..." and keep the artist prefix;
    ///  - tilde-delimited playout payloads (United Music et al.):
    ///    <c>Dove Non So~Orietta Berti~~1966~ITR008900478~169~2026-07-04T18:51:13~...</c>
    ///    - field 0 is the title, field 1 the artist, the rest is scheduling metadata.
    ///
    /// Anything matching neither pattern passes through untouched.
    /// </summary>
    public static string CleanStreamTitle(string title)
    {
        var marker = title.IndexOf("text=\"", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var end = title.IndexOf('"', marker + 6);
            if (end > marker)
            {
                var song = title[(marker + 6)..end];
                var artist = title[..marker].TrimEnd(' ', '-');
                return artist.Length > 0 ? $"{artist} - {song}" : song;
            }
        }

        // Three or more tildes reads as a delimited payload, not punctuation - a real song
        // title with one decorative "~" (or an "Artist ~ Title" separator) stays intact.
        if (title.Count(c => c == '~') >= 3)
        {
            var fields = title.Split('~');
            var song = fields[0].Trim();
            var artist = fields.Length > 1 ? fields[1].Trim() : "";
            if (song.Length > 0)
            {
                return artist.Length > 0 ? $"{artist} - {song}" : song;
            }
        }

        return title;
    }

    internal static string DecodeMetadata(byte[] raw, int offset, int length)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(raw, offset, length);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(raw, offset, length);
        }
    }
}

/// <summary>
/// Incremental ICY de-interleaver for a live connection: feed raw response-body bytes as
/// they arrive, get clean audio spans and titles out. The interleave format is
/// [metaint audio bytes][length byte][length*16 metadata]... - a metadata block can straddle
/// any read boundary, so this keeps the parse state between feeds.
/// </summary>
public sealed class IcyDeinterleaver
{
    private enum State { Audio, MetaLength, MetaBody }

    private readonly byte[] _metaBuffer = new byte[255 * 16];
    private readonly int _metaint;
    private State _state = State.Audio;
    private int _audioRemaining;
    private int _metaLength;
    private int _metaFill;

    public IcyDeinterleaver(int metaint)
    {
        _metaint = metaint;
        _audioRemaining = metaint;
    }

    /// <summary>
    /// Consumes <paramref name="count"/> bytes from <paramref name="buffer"/>. Clean audio
    /// is handed to <paramref name="onAudio"/> as (buffer, offset, length) slices of the
    /// caller's buffer - copy before the next feed. Each parsed StreamTitle (already
    /// scrubbed via <see cref="IcyMetadata.ParseStreamTitle"/>) goes to <paramref name="onTitle"/>.
    /// </summary>
    public void Feed(byte[] buffer, int offset, int count, Action<byte[], int, int> onAudio, Action<string> onTitle)
    {
        if (_metaint <= 0)
        {
            onAudio(buffer, offset, count);
            return;
        }

        var pos = offset;
        var end = offset + count;
        while (pos < end)
        {
            switch (_state)
            {
                case State.Audio:
                {
                    var take = Math.Min(_audioRemaining, end - pos);
                    if (take > 0)
                    {
                        onAudio(buffer, pos, take);
                        pos += take;
                        _audioRemaining -= take;
                    }
                    if (_audioRemaining == 0)
                    {
                        _state = State.MetaLength;
                    }
                }
                break;

                case State.MetaLength:
                {
                    _metaLength = buffer[pos] * 16;
                    pos++;
                    _metaFill = 0;
                    if (_metaLength == 0)
                    {
                        _audioRemaining = _metaint;
                        _state = State.Audio;
                    }
                    else
                    {
                        _state = State.MetaBody;
                    }
                }
                break;

                case State.MetaBody:
                {
                    var take = Math.Min(_metaLength - _metaFill, end - pos);
                    Array.Copy(buffer, pos, _metaBuffer, _metaFill, take);
                    pos += take;
                    _metaFill += take;
                    if (_metaFill == _metaLength)
                    {
                        var title = IcyMetadata.ParseStreamTitle(IcyMetadata.DecodeMetadata(_metaBuffer, 0, _metaLength));
                        if (title != null)
                        {
                            onTitle(title);
                        }
                        _audioRemaining = _metaint;
                        _state = State.Audio;
                    }
                }
                break;
            }
        }
    }
}
