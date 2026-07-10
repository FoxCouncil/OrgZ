// Copyright (c) 2026 FoxCouncil (https://github.com/FoxCouncil/OrgZ)

using OrgZ.Services;

namespace OrgZ.StationCurator.Services;

public sealed record ProbeOutcome(
    bool Ok,
    string Status,              // ProbeStatus.* value
    string? Detail,             // icy-name / error text, whatever a human wants to see
    string? Format,             // normalized format per the server's Content-Type claim
    int? Bitrate,               // icy-br when the server reports it
    string? IcyGenre,
    string? ResolvedUrl,        // post-redirect, post-playlist direct URL
    bool GeoSuspect,
    int Redirects = 0,          // HTTP redirects + playlist indirections crossed before audio
    int? MetaInt = null,        // icy-metaint the server granted → in-stream metadata works
    string? HlsMeta = null,     // HLS timed-ID3 channel: "ts" (0x15 PES) | "seg" (packed-audio tag) | "emsg" (fMP4)
    string? StreamTitle = null, // live StreamTitle captured from the first metadata block
    string? MeasuredFormat = null,  // what the audio bytes actually are
    int? MeasuredBitrate = null,    // bitrate measured off real frames
    string? ServerIp = null,
    string? ServerCountry = null,
    string? ServerCountryCode = null,
    int? TuneInMs = null);          // request start → first audio byte, across every redirect/playlist hop

/// <summary>
/// The curator's health check, now a thin adapter over <see cref="StreamSession"/> - the
/// SAME engine playback uses, run in fact-only mode: connect once, sample once, close.
/// Adds the curation-side extras a live pump doesn't need: GeoIP of the server actually
/// reached and the mapping onto <see cref="Models.ProbeStatus"/> strings curated.json stores.
/// </summary>
public static class StreamProber
{
    public static async Task<ProbeOutcome> ProbeAsync(string url, CancellationToken ct)
    {
        using var session = await StreamSession.ConnectAsync(url, ct);
        var facts = await session.CompleteProbeAsync(ct);
        return await FinishOutcomeAsync(FromFacts(facts), url);
    }

    /// <summary>Maps live-session facts onto the same outcome shape a standalone probe produces - the audition path's free probe.</summary>
    public static ProbeOutcome FromFacts(StreamFacts facts)
    {
        var status = facts.Status switch
        {
            StreamSessionStatus.Ok => Models.ProbeStatus.Ok,
            StreamSessionStatus.GeoBlocked => Models.ProbeStatus.Geo,
            _ => Models.ProbeStatus.Dead,
        };
        var detail = facts.Detail;
        if (facts.Status == StreamSessionStatus.Ok && !string.IsNullOrEmpty(facts.LiveTitle))
        {
            detail = $"{detail} ♪ {facts.LiveTitle}";
        }
        return new ProbeOutcome(facts.Status == StreamSessionStatus.Ok, status, detail,
            string.IsNullOrEmpty(facts.ContentFormat) ? null : facts.ContentFormat,
            facts.AdvertisedBitrate, facts.IcyGenre, facts.FinalUrl, facts.GeoSuspect,
            Redirects: facts.Redirects, MetaInt: facts.MetaInt, HlsMeta: facts.HlsMetaKind,
            StreamTitle: facts.LiveTitle, MeasuredFormat: facts.MeasuredFormat,
            MeasuredBitrate: facts.MeasuredBitrate, TuneInMs: facts.TuneInMs);
    }

    /// <summary>Geolocates the server the outcome actually reached (post-redirect, post-playlist) - DNS + GeoIP, no stream connection.</summary>
    public static async Task<ProbeOutcome> FinishOutcomeAsync(ProbeOutcome outcome, string originalUrl)
    {
        if ((outcome.Ok || outcome.Status == Models.ProbeStatus.Geo) && Uri.TryCreate(outcome.ResolvedUrl ?? originalUrl, UriKind.Absolute, out var final))
        {
            var geo = await GeoIp.LookupAsync(final.Host);
            if (geo != null)
            {
                outcome = outcome with { ServerIp = geo.Ip, ServerCountry = geo.Country, ServerCountryCode = geo.CountryCode };
            }
        }
        return outcome;
    }
}
