#!/usr/bin/env python3
"""
Seeds Assets/stations.json with curated radio entries from radio-browser.info.

Pulls top-clicked stations per nubango-style genre, filters for quality
(working URL, real bitrate, has logo, non-bot click counts), maps to the
OrgZ station schema, and merges with the existing hand-curated entries
(SomaFM, BBC, KEXP, etc.) preserved verbatim.
"""

from __future__ import annotations

import json
import sys
import urllib.parse
import urllib.request
from pathlib import Path

API_BASE = "https://de1.api.radio-browser.info"
UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"

# Integer genre IDs match the RadioGenre enum on the C# side. Order is
# alphabetical-by-name for stable, predictable values.
# (id, name, radio-browser tag query). Tag=None means hand-curated only.
GENRE_DEFS: list[tuple[int, str, str | None]] = [
    ( 1, "70's"          ,            "70s"),
    ( 2, "80's"          ,        "80s"),
    ( 3, "90's"          ,             "90s"),
    ( 4, "Adult Contemporary",    "adult contemporary"),
    ( 5, "Alternative Rock",      "alternative"),
    ( 6, "Ambient",               "ambient"),
    ( 7, "Blues",                 "blues"),
    ( 8, "Classic Rock",          "classic rock"),
    ( 9, "Classical",             "classical"),
    (10, "College / University",  "college"),
    (11, "Comedy",                "comedy"),
    (12, "Country",               "country"),
    (13, "Eclectic",              None),
    (14, "Electronica",           "electronic"),
    (15, "Folk",                  "folk"),
    (16, "Golden Oldies",         "oldies"),
    (17, "Hard Rock / Metal",     "metal"),
    (18, "Hip Hop / Rap",         "hip hop"),
    (19, "International / World", "world"),
    (20, "Jazz",                  "jazz"),
    (21, "News / Talk Radio",     "talk"),
    (22, "Reggae / Island",       "reggae"),
    (23, "Religious",             "christian"),
    (24, "RnB / Soul",            "soul"),
    (25, "Sports Radio",          "sport"),
    (26, "Top 40 / Pop",          "pop"),
]

PER_GENRE_TARGET = 50

# Stations excluded from the seed, by radio-browser stationuuid.
# Add an entry here when re-runs surface something we don't want shipped.
BLOCKED_UUIDS: set[str] = {
    "aeee1931-d6a2-4ac5-9212-dec8fb4bb4c0",  # Sud Radio (FR) — conservative talk
    "153777aa-209e-4769-bde5-46abafd375e7",  # Radio Courtoisie (FR) — conservative talk
    "188c36ae-f1ef-4b5c-8587-f29c216bfaa2",  # INFORMA RADIO (ES) — conservative talk
}

# Stations kept verbatim regardless of API results. genreId values match the
# RadioGenre enum on the C# side (see GENRE_DEFS above).
HAND_CURATED: list[dict] = [
    {
        "id": "somafm.groovesalad",
        "name": "SomaFM: Groove Salad",
        "streamUrl": "https://ice1.somafm.com/groovesalad-128-mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 6,  # Ambient
        "country": "United States", "countryCode": "US",
        "homepage": "https://somafm.com/groovesalad/",
        "logoUrl": "https://somafm.com/logos/512/groovesalad512.png",
    },
    {
        "id": "somafm.dronezone",
        "name": "SomaFM: Drone Zone",
        "streamUrl": "https://ice1.somafm.com/dronezone-128-mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 6,  # Ambient
        "country": "United States", "countryCode": "US",
        "homepage": "https://somafm.com/dronezone/",
        "logoUrl": "https://somafm.com/logos/512/dronezone512.png",
    },
    {
        "id": "somafm.indiepoprocks",
        "name": "SomaFM: Indie Pop Rocks!",
        "streamUrl": "https://ice1.somafm.com/indiepop-128-mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 5,  # Alternative Rock
        "country": "United States", "countryCode": "US",
        "homepage": "https://somafm.com/indiepop/",
        "logoUrl": "https://somafm.com/logos/512/indiepop512.png",
    },
    {
        "id": "somafm.folkforward",
        "name": "SomaFM: Folk Forward",
        "streamUrl": "https://ice1.somafm.com/folkfwd-128-mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 15,  # Folk
        "country": "United States", "countryCode": "US",
        "homepage": "https://somafm.com/folkfwd/",
        "logoUrl": "https://somafm.com/logos/512/folkfwd512.png",
    },
    {
        "id": "kexp.fm",
        "name": "KEXP 90.3 FM Seattle",
        "streamUrl": "https://kexp-mp3-128.streamguys1.com/kexp128.mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 13,  # Eclectic
        "country": "United States", "countryCode": "US",
        "homepage": "https://www.kexp.org/",
        "logoUrl": "https://www.kexp.org/static/assets/img/logo-kexp.svg",
    },
    {
        "id": "radioparadise.main",
        "name": "Radio Paradise — Main Mix",
        "streamUrl": "https://stream.radioparadise.com/aac-128",
        "streamFormat": "aac", "bitrate": 128,
        "genreId": 13,  # Eclectic
        "country": "United States", "countryCode": "US",
        "homepage": "https://radioparadise.com/",
        "logoUrl": "https://radioparadise.com/graphics/RP-2020-logo-square.png",
    },
    {
        "id": "bbc.radio1",
        "name": "BBC Radio 1",
        "streamUrl": "http://stream.live.vc.bbcmedia.co.uk/bbc_radio_one",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 26,  # Top 40 / Pop
        "country": "United Kingdom", "countryCode": "GB",
        "homepage": "https://www.bbc.co.uk/sounds/play/live:bbc_radio_one",
        "logoUrl": "https://sounds.files.bbci.co.uk/v2/networks/bbc_radio_one/colour_default.svg",
    },
    {
        "id": "bbc.radio6music",
        "name": "BBC Radio 6 Music",
        "streamUrl": "http://stream.live.vc.bbcmedia.co.uk/bbc_6music",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 5,  # Alternative Rock
        "country": "United Kingdom", "countryCode": "GB",
        "homepage": "https://www.bbc.co.uk/sounds/play/live:bbc_6music",
        "logoUrl": "https://sounds.files.bbci.co.uk/v2/networks/bbc_6music/colour_default.svg",
    },
    {
        "id": "bbc.worldservice",
        "name": "BBC World Service",
        "streamUrl": "http://stream.live.vc.bbcmedia.co.uk/bbc_world_service",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 21,  # News / Talk Radio
        "country": "United Kingdom", "countryCode": "GB",
        "homepage": "https://www.bbc.co.uk/worldserviceradio",
        "logoUrl": "https://sounds.files.bbci.co.uk/v2/networks/bbc_world_service/colour_default.svg",
    },
    {
        "id": "kcrw.eclectic24",
        "name": "KCRW Eclectic 24",
        "streamUrl": "https://kcrw.streamguys1.com/kcrw_192k_mp3_e24_internet_radio",
        "streamFormat": "mp3", "bitrate": 192,
        "genreId": 13,  # Eclectic
        "country": "United States", "countryCode": "US",
        "homepage": "https://www.kcrw.com/music/shows/eclectic24",
        "logoUrl": "https://www.kcrw.com/about/kcrw-logo-square.png",
    },
    {
        "id": "radioswiss.jazz",
        "name": "Radio Swiss Jazz",
        "streamUrl": "http://stream.srg-ssr.ch/m/rsj/mp3_128",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 20,  # Jazz
        "country": "Switzerland", "countryCode": "CH",
        "homepage": "https://www.radioswissjazz.ch/",
        "logoUrl": "https://www.radioswissjazz.ch/favicon.ico",
    },
    {
        "id": "radioswiss.classic",
        "name": "Radio Swiss Classic",
        "streamUrl": "http://stream.srg-ssr.ch/m/rsc_en/mp3_128",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 9,  # Classical
        "country": "Switzerland", "countryCode": "CH",
        "homepage": "https://www.radioswissclassic.ch/en",
        "logoUrl": "https://www.radioswissclassic.ch/favicon.ico",
    },
    {
        "id": "franceinter",
        "name": "France Inter",
        "streamUrl": "https://icecast.radiofrance.fr/franceinter-midfi.mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 21,  # News / Talk Radio
        "country": "France", "countryCode": "FR",
        "homepage": "https://www.radiofrance.fr/franceinter",
        "logoUrl": "https://www.radiofrance.fr/sites/default/files/styles/format_16_9/public/2022-11/franceinter-default-share.jpg",
    },
    {
        "id": "dw.english",
        "name": "Deutsche Welle English",
        "streamUrl": "https://dwstream301.akamaized.net/hls/live/2017839/dwstream301/index.m3u8",
        "streamFormat": "hls", "bitrate": 128,
        "genreId": 21,  # News / Talk Radio
        "country": "Germany", "countryCode": "DE",
        "homepage": "https://www.dw.com/en/live-tv/s-100825",
        "logoUrl": "https://static.dw.com/images/logos/dw_logo_2018_square.png",
    },
    {
        "id": "wnyc.fm",
        "name": "WNYC 93.9 FM",
        "streamUrl": "https://fm939.wnyc.org/wnycfm-mp3",
        "streamFormat": "mp3", "bitrate": 128,
        "genreId": 21,  # News / Talk Radio
        "country": "United States", "countryCode": "US",
        "homepage": "https://www.wnyc.org/streams/fm",
        "logoUrl": "https://media.wnyc.org/i/200/200/c/80/photologue/photos/wnyc-square-logo-rgb.png",
    },
]


def fetch_by_tag(tag: str, limit: int = 30) -> list[dict]:
    """Fetch top stations matching tag, ordered by click count, broken filtered."""
    encoded = urllib.parse.quote(tag)
    url = f"{API_BASE}/json/stations/bytag/{encoded}?order=clickcount&reverse=true&limit={limit}&hidebroken=true"
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def codec_to_format(codec: str) -> str:
    c = (codec or "").upper()
    if "MP3" in c: return "mp3"
    if "AAC" in c: return "aac"
    if "OGG" in c: return "ogg"
    if "FLAC" in c: return "flac"
    if "HLS" in c: return "hls"
    return c.lower() or "unknown"


def is_quality(s: dict) -> bool:
    """Every field we ship must be non-empty and meaningful."""
    name = (s.get("name") or "").strip()
    stream = (s.get("url_resolved") or s.get("url") or "").strip()
    logo = (s.get("favicon") or "").strip()
    homepage = (s.get("homepage") or "").strip()
    country = (s.get("country") or "").strip()
    cc = (s.get("countrycode") or "").strip()
    codec = (s.get("codec") or "").strip()
    tags = (s.get("tags") or "").strip()
    br = s.get("bitrate", 0) or 0

    if not (name and stream and logo and homepage and country and cc and codec and tags):
        return False
    # Sane bitrate band. Radio-browser occasionally reports the wrong unit
    # (e.g. 96000 for what should be 96 kbps); cap to filter those out.
    if br < 64 or br > 999:
        return False
    # Reject codecs that don't normalise to a known format.
    if codec_to_format(codec) == "unknown":
        return False
    # Stream / logo URLs that don't look like real HTTP(S) endpoints.
    if not stream.startswith(("http://", "https://")):
        return False
    if not logo.startswith(("http://", "https://")):
        return False
    if not homepage.startswith(("http://", "https://")):
        return False
    # Country codes are ISO 3166-1 alpha-2 (two chars).
    if len(cc) != 2:
        return False
    # Filter aggregator / spammy names.
    name_lower = name.lower()
    if name_lower.startswith(("#", "***", "***")):
        return False
    spam_markers = [
        "#1 hits", "24/7", "non stop", "non-stop", "nonstop", "***",
        "rdmix", "top hits", "best hits", "best of the",
        "hits 70s 80s 90s", "70s 80s 90s",
        "the greatest hits", "all hits", "music mix",
    ]
    if any(marker in name_lower for marker in spam_markers):
        return False
    return True


def map_to_schema(s: dict, genre_id: int) -> dict:
    return {
        "id": f"rb:{s['stationuuid']}",
        "name": s["name"].strip(),
        "streamUrl": (s.get("url_resolved") or s["url"]).strip(),
        "streamFormat": codec_to_format(s.get("codec", "")),
        "bitrate": int(s.get("bitrate") or 0),
        "genreId": genre_id,
        "country": s["country"].strip(),
        "countryCode": s["countrycode"].strip(),
        "homepage": s["homepage"].strip(),
        "logoUrl": s["favicon"].strip(),
        "description": s["tags"].strip(),
    }


def main() -> int:
    seen_urls: set[str] = set()
    seen_names: set[str] = set()
    stations: list[dict] = []

    # Hand-curated first — they take precedence and won't be overwritten.
    for s in HAND_CURATED:
        stations.append(s)
        seen_urls.add(s["streamUrl"])
        seen_names.add(s["name"].lower())

    for genre_id, name, tag in GENRE_DEFS:
        if tag is None:
            continue
        print(f"[{genre_id:2d} {name}] querying tag='{tag}'…", file=sys.stderr)
        try:
            raw = fetch_by_tag(tag, limit=300)
        except Exception as e:
            print(f"  ! failed: {e}", file=sys.stderr)
            continue

        kept = 0
        for r in raw:
            if kept >= PER_GENRE_TARGET:
                break
            if not is_quality(r):
                continue
            if r.get("stationuuid") in BLOCKED_UUIDS:
                continue
            stream = (r.get("url_resolved") or r["url"]).strip()
            if stream in seen_urls:
                continue
            name_key = r["name"].strip().lower()
            if name_key in seen_names:
                continue
            stations.append(map_to_schema(r, genre_id))
            seen_urls.add(stream)
            seen_names.add(name_key)
            kept += 1

        print(f"  + {kept} kept", file=sys.stderr)

    # Trim taxonomy to populated genres only.
    used = {s["genreId"] for s in stations}
    taxonomy = {
        "genres": [
            {"id": gid, "name": name}
            for gid, name, _ in GENRE_DEFS
            if gid in used
        ]
    }

    out = {"schemaVersion": 1, "taxonomy": taxonomy, "stations": stations}

    target = Path(__file__).resolve().parent.parent / "Assets" / "stations.json"
    target.write_text(json.dumps(out, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")

    print(f"\nWrote {len(stations)} stations across {len(taxonomy['genres'])} genres to {target}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
