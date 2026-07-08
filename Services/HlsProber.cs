// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using System.Diagnostics;
using System.Net.Http;
using System.Text;

namespace OrgZ.Services;

/// <summary>What an HLS media segment actually carries - see <see cref="HlsProber.SniffSegment"/>.</summary>
public sealed record HlsSegmentInfo(string? MetaKind, string? Title, string? Codec, int? SniffedKbps, bool HasLeadingId3 = false);

/// <summary>The full HLS inspection result the prober folds into its outcome.</summary>
public sealed record HlsInspection(string? MetaKind, string? StreamTitle, string? MeasuredFormat, int? MeasuredBitrate, bool HasDateRange, int ExtraHops, int? FirstAudioMs = null);

/// <summary>
/// Looks INSIDE an HLS stream instead of stopping at "the playlist parses": descends a master
/// playlist to its highest-bandwidth variant, fetches the newest media segment, and identifies
/// the metadata channel in use - timed ID3 as a TS elementary stream (stream_type 0x15, the
/// Apple way), an ID3v2 tag heading a packed-audio (raw ADTS/MP3) segment, or an 'emsg' box in
/// fMP4/CMAF - extracting the live StreamTitle when one is in reach. Also measures the real
/// codec from the segment bytes and estimates bitrate from segment size over declared duration.
/// </summary>
public static class HlsProber
{
    private static readonly HttpClient Http = Web.Create(TimeSpan.FromSeconds(15));

    public static async Task<HlsInspection> InspectAsync(string playlistUrl, string playlistBody, CancellationToken ct, Stopwatch? tuneIn = null)
    {
        var hops = 0;
        int? firstAudioMs = null;
        try
        {
            var url = playlistUrl;
            var body = playlistBody;

            // Master playlist → descend into the variant the player would favor (highest bandwidth).
            if (body.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal))
            {
                var variant = PickVariant(body, url);
                if (variant == null)
                {
                    return new HlsInspection(null, null, null, null, false, hops);
                }
                url = variant;
                hops++;
                body = await FetchTextAsync(url, ct) ?? "";
            }

            var hasDateRange = body.Contains("#EXT-X-DATERANGE", StringComparison.Ordinal);

            // iHeart/revma chunklists carry now-playing as plaintext EXTINF attributes even when
            // the segments have no metadata channel at all - an in-segment channel still wins below.
            var (hasExtinf, extinfTitle) = ExtInfMetadata(body, url);

            // AES-encrypted segments can't be sniffed; report what the playlist alone tells us.
            if (body.Contains("#EXT-X-KEY", StringComparison.Ordinal) && !body.Contains("METHOD=NONE", StringComparison.Ordinal))
            {
                return new HlsInspection(hasExtinf ? "extinf" : null, extinfTitle, null, null, hasDateRange, hops);
            }

            // Sample from the live edge backwards: packed-audio titles often only ride the
            // segment where a song starts, so one segment can land mid-song and miss them.
            var candidates = LastSegments(body, url, 3);
            HlsSegmentInfo? info = null;
            int? estimatedKbps = null;
            foreach (var (segmentUrl, duration) in candidates)
            {
                var (segment, contentLength, firstByteMs) = await FetchBytesAsync(segmentUrl, 192 * 1024, ct, tuneIn);
                if (segment.Length == 0)
                {
                    continue;
                }
                firstAudioMs ??= firstByteMs;

                var sniffed = SniffSegment(segment);
                if (info == null)
                {
                    info = sniffed;
                    // Whole-segment size over declared duration - includes container overhead,
                    // but that's the honest on-the-wire rate. Frame-sniffed rates win below.
                    estimatedKbps = contentLength is long len && duration is > 0 ? (int)Math.Round(len * 8.0 / duration.Value / 1000.0) : null;
                }
                else if (sniffed.MetaKind != null)
                {
                    info = info with { MetaKind = sniffed.MetaKind, Title = sniffed.Title };
                }

                if (info.MetaKind != null || !info.HasLeadingId3)
                {
                    break;
                }
            }

            if (info == null)
            {
                return new HlsInspection(hasExtinf ? "extinf" : null, extinfTitle, null, null, hasDateRange, hops, firstAudioMs);
            }
            var format = info.Codec != null ? $"hls+{info.Codec}" : null;
            var metaKind = info.MetaKind ?? (hasExtinf ? "extinf" : null);
            return new HlsInspection(metaKind, info.Title ?? extinfTitle, format, info.SniffedKbps ?? estimatedKbps, hasDateRange, hops, firstAudioMs);
        }
        catch
        {
            return new HlsInspection(null, null, null, null, false, hops, firstAudioMs);
        }
    }

    /// <summary>
    /// EXTINF metadata channel presence + newest music-spot now-playing. Presence is judged
    /// by the ATTRIBUTES existing at all (title/spot markers on any entry) - a probe that
    /// lands mid ad-break sees nothing but filtered junk titles, yet the channel is
    /// plainly there and will carry songs the rest of the day.
    /// </summary>
    private static (bool HasChannel, string? Title) ExtInfMetadata(string mediaPlaylist, string baseUrl)
    {
        try
        {
            var segments = HlsMediaPlaylist.Parse(mediaPlaylist, baseUrl).Segments;
            var hasChannel = segments.Any(s => s.SpotType != null || s.Title != null);
            return (hasChannel, segments.LastOrDefault(s => s.NowPlaying != null)?.NowPlaying);
        }
        catch
        {
            return (false, null);
        }
    }

    /// <summary>Identifies the segment container, its metadata channel, live title, and codec.</summary>
    public static HlsSegmentInfo SniffSegment(byte[] segment)
    {
        // Packed audio: the segment IS the elementary stream, fronted by an ID3v2 tag. The
        // spec REQUIRES a timestamp PRIV tag on every packed segment, so a leading tag alone
        // proves nothing - only real text frames (TIT2/TPE1) count as a metadata channel.
        if (segment.Length > 10 && segment[0] == 'I' && segment[1] == 'D' && segment[2] == '3')
        {
            var (title, _) = Id3.Parse(segment, 0);
            var sniff = AudioSniffer.Sniff(segment);
            return new HlsSegmentInfo(title != null ? "seg" : null, title, sniff?.Format, sniff?.Bitrate, HasLeadingId3: true);
        }

        // MPEG-TS: 0x47 sync every 188 bytes.
        if (segment.Length >= 2 * 188 && segment[0] == 0x47 && segment[188] == 0x47)
        {
            return ScanTransportStream(segment);
        }

        // fMP4 / CMAF: box-structured.
        if (IsMp4(segment))
        {
            var (hasId3Emsg, title) = ScanEmsg(segment);
            return new HlsSegmentInfo(hasId3Emsg ? "emsg" : null, title, null, null);
        }

        // Bare elementary stream with no leading tag - still worth a codec sniff.
        var plain = AudioSniffer.Sniff(segment);
        return new HlsSegmentInfo(null, null, plain?.Format, plain?.Bitrate);
    }

    // -- Playlist walking --

    /// <summary>Highest-BANDWIDTH variant URI from a master playlist, resolved absolute.</summary>
    public static string? PickVariant(string master, string baseUrl)
    {
        string? bestUri = null;
        var bestBandwidth = -1L;
        var lines = master.Split('\n');
        for (var i = 0; i < lines.Length - 1; i++)
        {
            var line = lines[i].Trim();
            if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                continue;
            }

            long bandwidth = 0;
            var idx = line.IndexOf("BANDWIDTH=", StringComparison.Ordinal);
            if (idx >= 0)
            {
                var start = idx + "BANDWIDTH=".Length;
                var end = start;
                while (end < line.Length && char.IsAsciiDigit(line[end]))
                {
                    end++;
                }
                long.TryParse(line[start..end], out bandwidth);
            }

            // The URI is the next non-comment, non-blank line.
            for (var j = i + 1; j < lines.Length; j++)
            {
                var candidate = lines[j].Trim();
                if (candidate.Length == 0)
                {
                    continue;
                }
                if (!candidate.StartsWith('#') && bandwidth > bestBandwidth)
                {
                    bestBandwidth = bandwidth;
                    bestUri = candidate;
                }
                break;
            }
        }
        return bestUri == null ? null : Resolve(baseUrl, bestUri);
    }

    /// <summary>Newest media segment (live edge) and its #EXTINF duration.</summary>
    public static (string? Url, double? Duration) LastSegment(string playlist, string baseUrl)
    {
        var segments = LastSegments(playlist, baseUrl, 1);
        return segments.Count > 0 ? (segments[0].Url, segments[0].Duration) : (null, null);
    }

    /// <summary>Up to <paramref name="count"/> newest media segments, live edge first.</summary>
    public static List<(string Url, double? Duration)> LastSegments(string playlist, string baseUrl, int count)
    {
        var all = new List<(string Url, double? Duration)>();
        double? pendingDuration = null;
        foreach (var raw in playlist.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..];
                var comma = value.IndexOf(',');
                pendingDuration = double.TryParse(comma >= 0 ? value[..comma] : value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                all.Add((Resolve(baseUrl, line), pendingDuration));
                pendingDuration = null;
            }
        }
        all.Reverse();
        return all.Take(count).ToList();
    }

    internal static string Resolve(string baseUrl, string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var abs) ? abs.ToString() : new Uri(new Uri(baseUrl), uri).ToString();

    // -- MPEG-TS: PAT → PMT → stream types; timed ID3 rides stream_type 0x15 --

    private static HlsSegmentInfo ScanTransportStream(byte[] d)
    {
        var pmtPid = -1;
        var id3Pid = -1;
        string? codec = null;

        for (var off = 0; off + 188 <= d.Length; off += 188)
        {
            if (d[off] != 0x47)
            {
                break; // lost sync - trust what we have
            }
            var pid = ((d[off + 1] & 0x1F) << 8) | d[off + 2];
            var pusi = (d[off + 1] & 0x40) != 0;
            var afc = (d[off + 3] >> 4) & 0x3;
            var p = off + 4;
            if ((afc & 0x2) != 0)
            {
                p += 1 + d[p];
            }
            if ((afc & 0x1) == 0 || p >= off + 188 || !pusi)
            {
                continue;
            }

            if (pid == 0 && pmtPid < 0)
            {
                // PAT: pointer_field, then table_id 0x00 section.
                var q = p + 1 + d[p];
                if (q + 12 > off + 188 || d[q] != 0x00)
                {
                    continue;
                }
                var sectionLength = ((d[q + 1] & 0x0F) << 8) | d[q + 2];
                var end = Math.Min(q + 3 + sectionLength - 4, off + 188); // -4 CRC
                for (var r = q + 8; r + 4 <= end; r += 4)
                {
                    var program = (d[r] << 8) | d[r + 1];
                    if (program != 0)
                    {
                        pmtPid = ((d[r + 2] & 0x1F) << 8) | d[r + 3];
                        break;
                    }
                }
            }
            else if (pid == pmtPid)
            {
                // PMT: table_id 0x02, program_info descriptors, then the ES loop.
                var q = p + 1 + d[p];
                if (q + 13 > off + 188 || d[q] != 0x02)
                {
                    continue;
                }
                var sectionLength = ((d[q + 1] & 0x0F) << 8) | d[q + 2];
                var end = Math.Min(q + 3 + sectionLength - 4, off + 188);
                var programInfoLength = ((d[q + 10] & 0x0F) << 8) | d[q + 11];
                for (var r = q + 12 + programInfoLength; r + 5 <= end;)
                {
                    var streamType = d[r];
                    var esPid = ((d[r + 1] & 0x1F) << 8) | d[r + 2];
                    var esInfoLength = ((d[r + 3] & 0x0F) << 8) | d[r + 4];
                    switch (streamType)
                    {
                        case 0x15: { id3Pid = esPid; } break;              // timed ID3 metadata
                        case 0x0F or 0x11: { codec ??= "aac"; } break;     // ADTS / LATM AAC
                        case 0x03 or 0x04: { codec ??= "mp3"; } break;     // MPEG audio
                        case 0x81: { codec ??= "ac3"; } break;
                    }
                    r += 5 + esInfoLength;
                }
            }
        }

        string? title = null;
        if (id3Pid > 0)
        {
            title = ExtractTimedId3Title(d, id3Pid);
        }
        return new HlsSegmentInfo(id3Pid > 0 ? "ts" : null, title, codec, null);
    }

    /// <summary>Reassembles the timed-ID3 PES payload from its PID's packets and parses the tag.</summary>
    private static string? ExtractTimedId3Title(byte[] d, int id3Pid)
    {
        var payload = new MemoryStream();
        var collecting = false;
        for (var off = 0; off + 188 <= d.Length; off += 188)
        {
            if (d[off] != 0x47)
            {
                break;
            }
            var pid = ((d[off + 1] & 0x1F) << 8) | d[off + 2];
            if (pid != id3Pid)
            {
                continue;
            }
            var pusi = (d[off + 1] & 0x40) != 0;
            var afc = (d[off + 3] >> 4) & 0x3;
            var p = off + 4;
            if ((afc & 0x2) != 0)
            {
                p += 1 + d[p];
            }
            if ((afc & 0x1) == 0 || p >= off + 188)
            {
                continue;
            }

            if (pusi)
            {
                if (collecting)
                {
                    break; // next PES started; parse what we gathered
                }
                // PES header: 00 00 01 stream_id len(2) flags(2) header_length(1).
                if (p + 9 > off + 188 || d[p] != 0 || d[p + 1] != 0 || d[p + 2] != 1)
                {
                    continue;
                }
                var headerLength = d[p + 8];
                p += 9 + headerLength;
                if (p >= off + 188)
                {
                    continue;
                }
                collecting = true;
            }
            else if (!collecting)
            {
                continue;
            }

            payload.Write(d, p, off + 188 - p);
            if (payload.Length > 4096)
            {
                break; // timed ID3 tags are tiny; cap the reassembly
            }
        }

        return payload.Length > 10 ? Id3.Parse(payload.ToArray(), 0).Title : null;
    }

    // -- fMP4 / CMAF: top-level box walk for ID3-bearing 'emsg' --

    private static bool IsMp4(byte[] d) =>
        d.Length >= 12 && Encoding.ASCII.GetString(d, 4, 4) is "ftyp" or "styp" or "moof" or "sidx" or "emsg" or "free";

    private static (bool HasId3Emsg, string? Title) ScanEmsg(byte[] d)
    {
        var found = false;
        string? title = null;
        long pos = 0;
        while (pos + 8 <= d.Length)
        {
            long size = ((uint)d[pos] << 24) | ((uint)d[pos + 1] << 16) | ((uint)d[pos + 2] << 8) | d[pos + 3];
            var type = Encoding.ASCII.GetString(d, (int)pos + 4, 4);
            var headerSize = 8;
            if (size == 1 && pos + 16 <= d.Length)
            {
                size = 0;
                for (var i = 0; i < 8; i++)
                {
                    size = (size << 8) | d[pos + 8 + i];
                }
                headerSize = 16;
            }
            if (size < headerSize)
            {
                break;
            }

            if (type == "emsg")
            {
                var bodyStart = (int)(pos + headerSize);
                var bodyEnd = (int)Math.Min(pos + size, d.Length);
                var body = Encoding.ASCII.GetString(d, bodyStart, Math.Min(bodyEnd - bodyStart, 256));
                if (body.Contains("id3", StringComparison.OrdinalIgnoreCase))
                {
                    found = true;
                    // The message payload is a raw ID3 tag - find it inside the box. The scheme
                    // URI itself contains "ID3" (".../emsg/ID3"), so keep scanning past matches
                    // that don't parse into an actual tag.
                    for (var i = bodyStart; i + 10 < bodyEnd && title == null; i++)
                    {
                        if (d[i] == 'I' && d[i + 1] == 'D' && d[i + 2] == '3')
                        {
                            title = Id3.Parse(d, i).Title;
                        }
                    }
                }
            }

            if (pos + size > d.Length)
            {
                break; // truncated fetch - the last box runs past our sample
            }
            pos += size;
        }
        return (found, title);
    }

    // -- HTTP helpers --

    internal static async Task<string?> FetchTextAsync(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return null;
        }
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }
            total += read;
        }
        return Encoding.UTF8.GetString(buffer, 0, total);
    }

    internal static async Task<(byte[] Bytes, long? ContentLength, int? FirstByteMs)> FetchBytesAsync(string url, int maxBytes, CancellationToken ct, Stopwatch? tuneIn = null)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return ([], null, null);
        }
        var contentLength = resp.Content.Headers.ContentLength;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[maxBytes];
        var total = 0;
        int? firstByteMs = null;
        while (total < maxBytes)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct);
            if (read == 0)
            {
                break;
            }
            firstByteMs ??= tuneIn != null ? (int)tuneIn.ElapsedMilliseconds : null;
            total += read;
        }
        return (buffer[..total], contentLength, firstByteMs);
    }
}

/// <summary>
/// A parsed HLS MEDIA playlist - everything the live playback pump needs to fetch segments
/// itself: sequence numbering (so refreshes only pump NEW segments), the fMP4 init segment
/// (#EXT-X-MAP), per-segment AES-128 keys, and the refresh cadence (#EXT-X-TARGETDURATION).
/// </summary>
public sealed class HlsMediaPlaylist
{
    public sealed record Segment(long Sequence, string Url, double? Duration, string? KeyUrl, byte[]? KeyIv,
        string? Title = null, string? Artist = null, string? SpotType = null, string? ArtUrl = null, bool CatalogBacked = false)
    {
        /// <summary>
        /// Composed now-playing from EXTINF attributes (iHeart/revma chunklists carry these in
        /// plaintext even when segments have no metadata channel). Null for untitled entries and
        /// for non-music spots - song_spot "M" is music; "F"/"T" ad/talk/sweeper spots carry
        /// junk titles ("iH50s ComFree Sweepers - 06"). Some stations mislabel real songs as
        /// "F" while still stamping catalog IDs, so catalog-backed entries count as music too.
        /// </summary>
        public string? NowPlaying =>
            string.IsNullOrWhiteSpace(Title) || IsSpotBreak
                ? null
                : string.IsNullOrWhiteSpace(Artist) ? Title : $"{Artist} - {Title}";

        /// <summary>True when the EXTINF attributes POSITIVELY mark a non-music spot (ad/talk/sweeper) - distinct from "no metadata at all".</summary>
        public bool IsSpotBreak => SpotType != null && !SpotType.Equals("M", StringComparison.OrdinalIgnoreCase) && !CatalogBacked;
    }

    public List<Segment> Segments { get; } = [];
    public double TargetDuration { get; private set; } = 6;
    public long MediaSequence { get; private set; }
    public string? InitSegmentUrl { get; private set; }
    public bool EndList { get; private set; }
    /// <summary>An encryption method we can't decrypt (SAMPLE-AES, DRM) - playback is impossible.</summary>
    public string? UnsupportedKeyMethod { get; private set; }

    public static HlsMediaPlaylist Parse(string body, string baseUrl)
    {
        var playlist = new HlsMediaPlaylist();
        double? pendingDuration = null;
        string? pendingTitle = null, pendingArtist = null, pendingSpot = null, pendingArt = null;
        var pendingCatalog = false;
        string? keyUrl = null;      // active #EXT-X-KEY, applies to subsequent segments
        byte[]? keyIv = null;
        var sequence = 0L;
        var sawSequenceTag = false;

        foreach (var raw in body.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
            {
                if (double.TryParse(line["#EXT-X-TARGETDURATION:".Length..], System.Globalization.CultureInfo.InvariantCulture, out var td) && td > 0)
                {
                    playlist.TargetDuration = td;
                }
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                if (long.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..], out var seq))
                {
                    playlist.MediaSequence = seq;
                    if (!sawSequenceTag)
                    {
                        sequence = seq;
                        sawSequenceTag = true;
                    }
                }
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var uri = Attribute(line, "URI");
                if (uri != null)
                {
                    playlist.InitSegmentUrl = HlsProber.Resolve(baseUrl, uri);
                }
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var method = Attribute(line, "METHOD") ?? "NONE";
                if (method.Equals("NONE", StringComparison.OrdinalIgnoreCase))
                {
                    keyUrl = null;
                    keyIv = null;
                }
                else if (method.Equals("AES-128", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = Attribute(line, "URI");
                    keyUrl = uri != null ? HlsProber.Resolve(baseUrl, uri) : null;
                    var iv = Attribute(line, "IV");
                    keyIv = iv != null && iv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.FromHexString(iv[2..].PadLeft(32, '0')) : null;
                }
                else
                {
                    playlist.UnsupportedKeyMethod = method;
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var value = line["#EXTINF:".Length..];
                var comma = value.IndexOf(',');
                pendingDuration = double.TryParse(comma >= 0 ? value[..comma] : value, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
                pendingTitle = pendingArtist = pendingSpot = pendingArt = null;
                pendingCatalog = false;

                // iHeart/revma-style now-playing: `#EXTINF:10,title="...",artist="...",url="song_spot=\"M\" ..."`
                // - plaintext attributes after the comma. A plain M3U comment title has no ="
                // and parses to nothing here.
                var tail = comma >= 0 ? value[(comma + 1)..] : "";
                if (tail.Contains("=\"", StringComparison.Ordinal))
                {
                    var attrs = ParseExtInfAttributes(tail);
                    pendingTitle = attrs.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : null;
                    pendingArtist = attrs.TryGetValue("artist", out var a) && !string.IsNullOrWhiteSpace(a) ? a.Trim() : null;
                    if (attrs.TryGetValue("url", out var blob))
                    {
                        // The url attribute nests a second name="value" list (unescaped by the
                        // parser above) with the spot type, catalog IDs, and album art.
                        var nested = ParseExtInfAttributes(blob);
                        pendingSpot = nested.TryGetValue("song_spot", out var spot) && spot.Length > 0 ? spot : null;
                        pendingArt = nested.TryGetValue("amgArtworkURL", out var art) && art.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? art : null;
                        // Real songs carry catalog IDs even when the spot type lies; ads,
                        // talk, and sweepers leave them empty/zero.
                        pendingCatalog =
                            (nested.TryGetValue("MediaBaseId", out var mb) && long.TryParse(mb, out var mbId) && mbId > 0) ||
                            (nested.TryGetValue("TPID", out var tp) && long.TryParse(tp, out var tpId) && tpId > 0);
                    }
                }
            }
            else if (line == "#EXT-X-ENDLIST")
            {
                playlist.EndList = true;
            }
            else if (line.Length > 0 && !line.StartsWith('#'))
            {
                playlist.Segments.Add(new Segment(sequence, HlsProber.Resolve(baseUrl, line), pendingDuration, keyUrl, keyIv, pendingTitle, pendingArtist, pendingSpot, pendingArt, pendingCatalog));
                sequence++;
                pendingDuration = null;
                pendingTitle = pendingArtist = pendingSpot = pendingArt = null;
                pendingCatalog = false;
            }
        }
        return playlist;
    }

    /// <summary>
    /// Escape-aware `name="value"` scanner for EXTINF attribute lists. Values end at the first
    /// UNESCAPED quote - iHeart nests a whole second attribute blob inside url="..." with
    /// backslash-escaped quotes, which a bare IndexOf('"') split would shred. Unescapes
    /// \" and \\ so the url blob can be fed straight back through this parser.
    /// </summary>
    internal static Dictionary<string, string> ParseExtInfAttributes(string tail)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < tail.Length)
        {
            while (i < tail.Length && !char.IsLetter(tail[i]))
            {
                i++;
            }
            var nameStart = i;
            while (i < tail.Length && (char.IsLetterOrDigit(tail[i]) || tail[i] is '_' or '-'))
            {
                i++;
            }
            if (i + 1 >= tail.Length || tail[i] != '=' || tail[i + 1] != '"')
            {
                // Not a name="..." pair (bare token, plain comment text) - skip to the next comma.
                while (i < tail.Length && tail[i] != ',')
                {
                    i++;
                }
                continue;
            }
            var name = tail[nameStart..i];
            i += 2; // past ="

            var value = new StringBuilder();
            while (i < tail.Length)
            {
                var c = tail[i];
                if (c == '\\' && i + 1 < tail.Length && (tail[i + 1] == '"' || tail[i + 1] == '\\'))
                {
                    value.Append(tail[i + 1]);
                    i += 2;
                    continue;
                }
                if (c == '"')
                {
                    i++;
                    break;
                }
                value.Append(c);
                i++;
            }
            if (name.Length > 0)
            {
                attrs[name] = value.ToString();
            }
        }
        return attrs;
    }

    /// <summary>Quoted or bare attribute value from an #EXT-X-* tag line.</summary>
    private static string? Attribute(string line, string name)
    {
        var idx = line.IndexOf(name + "=", StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }
        var start = idx + name.Length + 1;
        if (start >= line.Length)
        {
            return null;
        }
        if (line[start] == '"')
        {
            var end = line.IndexOf('"', start + 1);
            return end > start ? line[(start + 1)..end] : null;
        }
        var stop = line.IndexOf(',', start);
        return stop > start ? line[start..stop] : line[start..];
    }
}

/// <summary>
/// Minimal ID3v2 reader for the frames timed metadata actually carries: TIT2 (title) and
/// TPE1 (artist), composed into one display string. Handles v2.3 (plain frame sizes) and
/// v2.4 (syncsafe frame sizes) with latin1/utf16/utf8 text encodings.
/// </summary>
public static class Id3
{
    public static (string? Title, int TagSize) Parse(byte[] d, int offset)
    {
        if (offset + 10 > d.Length || d[offset] != 'I' || d[offset + 1] != 'D' || d[offset + 2] != '3')
        {
            return (null, 0);
        }

        var major = d[offset + 3];
        var size = ((d[offset + 6] & 0x7F) << 21) | ((d[offset + 7] & 0x7F) << 14) | ((d[offset + 8] & 0x7F) << 7) | (d[offset + 9] & 0x7F);
        var end = Math.Min(offset + 10 + size, d.Length);

        string? title = null, artist = null;
        var p = offset + 10;
        while (p + 10 <= end)
        {
            if (d[p] == 0)
            {
                break; // padding
            }
            var id = Encoding.ASCII.GetString(d, p, 4);
            var frameSize = major >= 4
                ? ((d[p + 4] & 0x7F) << 21) | ((d[p + 5] & 0x7F) << 14) | ((d[p + 6] & 0x7F) << 7) | (d[p + 7] & 0x7F)
                : (d[p + 4] << 24) | (d[p + 5] << 16) | (d[p + 6] << 8) | d[p + 7];
            var data = p + 10;
            if (frameSize <= 0 || data + frameSize > end)
            {
                break;
            }

            if (id is "TIT2" or "TPE1")
            {
                var text = DecodeText(d, data, frameSize);
                if (id == "TIT2")
                {
                    title = text;
                }
                else
                {
                    artist = text;
                }
            }
            p = data + frameSize;
        }

        var composed = (artist, title) switch
        {
            (not null, not null) => $"{artist} - {title}",
            (null, not null) => title,
            (not null, null) => artist,
            _ => null,
        };
        return (composed, 10 + size);
    }

    private static string? DecodeText(byte[] d, int offset, int length)
    {
        if (length < 2)
        {
            return null;
        }
        var text = d[offset] switch
        {
            0 => Encoding.Latin1.GetString(d, offset + 1, length - 1),
            1 when length >= 3 && d[offset + 1] == 0xFF && d[offset + 2] == 0xFE => Encoding.Unicode.GetString(d, offset + 3, length - 3),
            1 when length >= 3 && d[offset + 1] == 0xFE && d[offset + 2] == 0xFF => Encoding.BigEndianUnicode.GetString(d, offset + 3, length - 3),
            2 => Encoding.BigEndianUnicode.GetString(d, offset + 1, length - 1),
            3 => Encoding.UTF8.GetString(d, offset + 1, length - 1),
            _ => null,
        };
        text = text?.Trim('\0', ' ');
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
