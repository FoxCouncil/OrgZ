# Hardware

OrgZ is developed and validated against real devices - every iPod generation has its own quirks, and only metal proves a write path. We gladly accept hardware donations (or loans) for testing; if you have something on this list that isn't checked off yet, open an issue.

## iPod

Color variants collapsed into one row. Win / Mac = OrgZ tested against the device on that OS. iTunes = co-habitation with iTunes verified. Rockbox = tested running Rockbox firmware.

**Identity decode (serial → model/colour/capacity) is verified against libgpod's tables for every row below** - so the model we'd *show* for any of these is a sound best guess. The **Notes** column flags what hardware would upgrade that guess to confirmed: specifically, reading the serial off that generation's own firmware on macOS/Linux (Windows already gets it from WMI). A blank Notes cell means fully confirmed.

**Transcode** is the codec fourCC OrgZ targets when a source file needs converting for that model (FLAC/OGG/etc - natively playable files always copy through untouched): `alac` = Apple Lossless, `mp4a` = AAC 256 kbps. Four models can't decode ALAC and get AAC instead: the FireWire-era iPod 1G/2G (Apple only shipped ALAC decode to dock-connector models, mid-2004 firmware) and the Shuffle 1G/2G (ALAC arrived with the Shuffle 3G) - hardware-confirmed on a real Shuffle 2G, where a valid ALAC file is silently skipped.

| Model | Released | Sync tier | Transcode | Win | Mac | iTunes | Rockbox | Notes |
|---|---|---|---|---|---|---|---|---|
| iPod 1G | 2001 | None (direct iTunesDB) | mp4a | | | | | |
| iPod 2G | 2002 | None (direct iTunesDB) | mp4a | | | | | |
| iPod 3G | 2003 | None (direct iTunesDB) | alac | | | | | |
| iPod 4G | 2004 | None (direct iTunesDB) | alac | | | | | |
| iPod Photo | 2004 | None (direct iTunesDB) | alac | | | | | |
| iPod Video 5G | 2005 | None (direct iTunesDB) | alac | | | | | |
| iPod Video 5.5G | 2006 | None (direct iTunesDB) | alac | ✅ | ✅ | | | ✅ |
| iPod Shuffle 1G | 2005 | iTunesSD | mp4a | | | | | NEEDED |
| iPod Shuffle 2G | 2006 | iTunesSD | mp4a | ✅ | | | | |
| iPod Shuffle 3G | 2009 | iTunesSD | alac | | | | | |
| iPod Shuffle 4G | 2010 | iTunesSD | alac | | | | | |
| iPod Mini 1G | 2004 | None (direct iTunesDB) | alac | | | | | Needed |
| iPod Mini 2G | 2005 | None (direct iTunesDB) | alac | | | | | |
| iPod Classic 6G | 2007 | hash58 | alac | | | | | |
| iPod Classic 6.5G | 2008 | hash58 | alac | | | | | |
| iPod Classic 7G | 2009 | hash58 | alac | | | | | |
| iPod Nano 1G | 2005 | None (direct iTunesDB) | alac | | | | | NEEDED to dump |
| iPod Nano 2G | 2006 | None (direct iTunesDB) | alac | | | | | |
| iPod Nano 3G | 2007 | hash58 | alac | ✅ | | | | |
| iPod Nano 4G | 2008 | hash58 | alac | | | | | |
| iPod Nano 5G | 2009 | hash72 + SQLite | alac | ✅ | | ✅ | | ✅ |

iPod Touch and iPhone are out of scope

## USB CD-ROM

| Drive | Connection | External power | Tested |
|---|---|---|---|
| Pioneer BD-RW BDR-XS07U | USB 3.2 Gen 1, USB-C port on the drive | Not required - USB bus-powered | ✅ CD rip + DAO audio burn (incl. CD-TEXT) |

### Burn validation pass

Six `Burn Test N` playlists live in the library for this. Test 1 is ✅ done: burned on the
BDR-XS07U with CD-TEXT, verified in foobar2000 (disc title + per-track titles/artists read
back off the disc), TOC sector-exact, plays. The rest need a person with ears and discs.

Drive quirks this drive taught us, worth re-checking on any new recorder:
- Cue sheet lead-in **and** lead-out entries must use Data Form `0x01` (device-generated);
  `0x00` is rejected with 5/26/00 INVALID FIELD IN PARAMETER LIST.
- CD-TEXT lead-in start comes from **READ ATIP**, not track 1's NWA (which reports −150, the
  pregap). Announcing CD-TEXT in the cue and then not writing the lead-in hangs the burn.
- WRITE(10) transfers must stay under the USB bridge's 64 KB cap (OrgZ uses 26 sectors).
- SAO self-finalizes: no explicit CLOSE TRACK/SESSION (5/30/05 on an already-closed disc).

Most of this pass is machine-checkable, and now is: `BurnValidationTests` executes the
capacity/disc-count arithmetic, the track-boundary sector layout, and real ffmpeg
downsamples. What's left for a human is the part a test genuinely can't have — ears, and
a disc in a tray.

| # | Playlist | What it proves | Result |
|---|---|---|---|
| 1 | Smoke (11 min) | End-to-end burn + CD-TEXT | ✅ burned; CD-TEXT verified in foobar2000 |
| 2 | Track Boundaries (5×5 s + 2 songs, **burn at Gap 0**) | Skip-to-track lands on each song's first note; last track plays to the end | ✅ sector layout automated (gapless starts, gap offsets, 4 s floor) · ✅ **ear check passed** |
| 3 | Hi-Res Downsample (192k / 96k / 48k sources) | Transcode to 44.1/16, sector-aligned | ✅ automated with real ffmpeg, validated by the burn path's own WAV parser · ⬜ optional listen for artefacts |
| 4 | Unicode CD-TEXT (Japanese / emoji titles) | Latin-1 fallback renders `?` rather than failing the burn | ✅ burned + read back in foobar2000; **re-burned after the alpha.11 punctuation fix and confirmed** — typographic punctuation now survives, genuinely unrepresentable scripts still `?` |
| 5 | Near Capacity (77.9 min) | Fits; gaps charged against the disc; burn completes | ✅ automated (fits at Gap 0; gap arithmetic can push a set over) |
| 6 | Overflow (90.2 min) | Burn refused, Discs row reads `2 × 79:57` — never reaches the drive | ✅ automated (refusal, `2 × 79:57`, round-up never undercounts) |

**All six passed on 2026-07-25**, ear checks included. Nothing in the burn validation pass is
outstanding on this drive. Tests 5 and 6 consume no media at all (test 6 should never start a
burn), so only 1-4 cost discs.

**Found by Test 4, fixed in FoxOrangebook alpha.11 (OrgZ 0.9.17):** the Latin-1 fallback also
mangled ordinary typographic punctuation - a curly apostrophe read back as `Frankie?s`, an
em-dash as `Burn Test 4 ?`. Those are Windows-1252/Unicode characters ISO-8859-1 cannot carry,
and they are common throughout real metadata. They are now transliterated to ASCII before the
encode (’→' —→- “”→" …→...), while accented Latin still passes through untouched and genuinely
unrepresentable scripts (Japanese, emoji) still fall back to `?`. **Confirmed on metal** by a
re-burn of Test 4: `Frankie's First Affair` and `Burn Test 4 - Smoke` read back correctly, with
the Japanese titles still `?` as designed.
