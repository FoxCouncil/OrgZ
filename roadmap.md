# OrgZ Roadmap

Quality, beauty, and simplicity - in that order when they conflict. Tests reflect the finished
product: nothing ships behind a capability flag without the conformance suite proving it.

## Now / Next

### Testing burning e2e
The CD burn pipeline (FoxOrangebook data-disc + audio burning, sector-padded WAV transcode) is
covered by argument/layout tests only. Wanted: an end-to-end burn against a rewritable disc (or an
image-backed virtual recorder if one proves workable) validating the full transcode → layout →
burn → verify chain, plus the no-media / not-blank / not-writable pre-flight paths on real drives.

### Collapsible device rows in the sidebar
Each connected iPod becomes a collapsible parent (expander chevron) over its sub-items - Music,
Podcasts, Audiobooks, Playlists - so multiple connected devices don't flood the DEVICES section.
Expansion state remembered per device; auto-expand on first connect of a session.

## Architecture

### Shared media grid v1
One `MediaDataGrid` control + optional per-view XAML header; Kind-driven column order; podcast
episodes become `MediaItem`s. Dissolves the three-grid split in MainWindow (main / radio-grouped /
podcast-grouped), the build-once column workaround, and the feed-detail grid's separate row type.
The `ViewHost` discriminator is the stepping stone already in place.

### Device playlists master view
The device Playlists node is (by spec) a navigation container today. A real master list - rows are
playlists (name, tracks, duration), double-click navigates - needs a playlist row type, which
arrives naturally with shared media grid v1.

### iPod device service
Bundled signed USB filter driver + LocalSystem service for UAC-free device operations (USB
control-transfer version reads, raw SCSI, sync) - the way iTunes does it.
macOS flavor: a privileged helper (SMAppService) for the same reads - Serial (SCSI INQUIRY
VPD 0x80) and Software Version (firmware-partition osos / USB vendor control transfer) are
unreachable from an unprivileged process there, so a blank-SysInfo classic shows "-" for
both today (macOS only surfaces the USB iSerial, which is the FireWire GUID).

## Identity read - reference-verification matrix (slice A)
Goal: exact identity (model / colour / factory-or-modded capacity / serial) for every in-scope
generation, matching libgpod, across Win/Mac/Linux read paths. Reference-verified (✅) below;
hardware-confirmed (✅✅); named gaps are the honest holes.

- **Decode (serial-suffix + model-number → model/colour/capacity):** ✅ every in-scope generation
  (1G-4G, Mini 1G/2G, Photo, Video 5G/5.5G, Nano 1G-4G, Classic 6G/6.5G/7G, Shuffle 1G-4G),
  `LookupBySerial_covers_every_in_scope_generation` using libgpod's own suffix table. Not the
  Nano 5G+ SQLite tier (out of scope).
- **Firmware formats:** both documented layouts implemented + unit-tested - board-anchored
  SysInfo record (HDD gens) and the freemyipod `SCfg` dict (NOR gens). The HDD parser is
  ✅✅ hardware-proven against a real iPod Video 5.5G byte fixture (`ScanSysCfg_reads_real_5_5G...`,
  serial 8L645KA1V9M → MA446).
- **Windows read:** ✅ WMI (`Win32_DiskDrive.SerialNumber`) surfaces the Apple serial unprivileged;
  ✅✅ on a real 5.5G + Nano 5G. Uniform mechanism across gens.
- **macOS/Linux read:** serial only via the raw firmware read (Apple's private iPodSBC driver
  seals off SCSI on macOS; libgpod's `sg` path is Linux-only). HDD 5.5G ✅✅ (real bytes).

### Named gaps
- **NOR `SCfg` format is code-only, not hardware-validated** - no Nano 3G/Classic NOR dump on hand.
- **HDD non-5.5G gens (Video 5G, Photo, 1G-4G, Mini, Classic) on macOS/Linux** - same board-anchored
  format assumed but unproven; need one dump each.
- **Flash gens (Nano 1G/2G, Shuffle) firmware serial on macOS/Linux** - layout unconfirmed.
- **Linux read path** - implemented, run on no generation.
- **Shuffle serial via WMI on Windows** - plausible, not hardware-confirmed (no Shuffle tested).

## Library read - reference-verification matrix (slice B)
Goal: every in-scope generation's on-device library (tracks + playlists, load-bearing fields) read
from the binary iTunesDB / iTunesSD, field-matching libgpod, proven by committed tests over real or
libgpod-generated fixtures, across Win/Mac/Linux. Reference-verified (✅); real-bytes proven (✅✅).

- **Binary iTunesDB - tracks** (1G-4G, Mini, Photo, Video 5G/5.5G, Nano 1G-4G, Classic 6/6.5/7G):
  ✅✅ `ITunesDbReader` reads id, title/artist/album/genre/composer, path, file size, duration,
  track/total, year, bitrate, sample-rate, play/skip counts, last-played, date-added, dbid,
  media-type, **rating, disc#, total-discs** - proven against a real iTunes-written iPod Video 5.5G
  database (`ITunesDbRealFixtureTests`: three untouched `mhit` records lifted off BriPod) plus
  non-circular synthetic tests whose fixture offsets ARE the real-iTunes layout. This verification
  caught two conformance bugs the old self-referential tests hid: rating was read from the 0x1C flag
  byte (fixed → byte at 0x1F) and total-discs from an int32 at 0x5E that swept up the 0x60 byte and
  returned 65536 (fixed → u16 at 0x60).
- **Binary iTunesDB - playlists:** ✅ `ReadAll` walks MHSD type 2/3, dedups the mirrored pair, skips
  the master + podcast playlists, preserves MHIP track order - synthetic tests (name, master-skip,
  order, empty). The real 5.5G database's only playlist IS the master (skipped by design), so the
  user-playlist parse is proven synthetically only - see gap.
- **iTunesSD - Shuffle 1G/2G (classic big-endian) + 3G/4G (bdhs):** ✅ `ShuffleSdWriter.Read` /
  `ShuffleBdhsWriter.Read` parse the on-device track list (path + file-type); byte-layout + round-trip
  tests assert the actual on-disk structure, not just self-consistency (`ShuffleIPodTests`).
- **The read path is proven on Win/Mac/Linux** - the iTunesDb suite (73/73, incl. the real 5.5G
  fixture) was run green on all three: Windows (Release suite via the pre-commit hook), macOS
  (foxmini.local, .NET 10), and Linux (a fresh `dotnet/sdk:10.0` container). One managed parser (byte
  math + `Path.Combine`), no per-OS branch, no P/Invoke - so there was one code path to prove, and it
  is proven, not argued. The real 5.5G bytes were also dumped and ground-truthed on macOS before being
  frozen into the fixture.

### Named gaps
- **User-playlist parse not reference-verified against real/libgpod bytes** - the on-hand 5.5G DB
  carries only the master playlist. Closing it needs a device with a user playlist, or a
  libgpod-generated DB.
- **iTunesSD proven by format-conformance + round-trip, not a real Shuffle dump** - no Shuffle in the
  fleet yet. Structure matches the spec and our hardware-validated writer.
- **Nano 5G library read is the SQLite tier** (`Nano5gLibraryReader`, covered by the nano5g fixture
  suite), not binary iTunesDB/iTunesSD - out of this slice's format scope.
- **Rockbox library is a filesystem tag-scan** (`FilesystemLibraryScanner`), no iTunes DB to parse.
- **On-device end-to-end read on Mac/Linux** - the parser itself is now run-green on all three OSes;
  what's still unproven is OrgZ reading a *live mounted* iPod's DB on macOS/Linux (mount discovery +
  file access on real hardware), the same hardware-integration gap HARDWARE.md tracks (Mac column).

## Library write - external-oracle matrix (slice C)
Goal: every in-scope generation's on-device library WRITTEN by OrgZ - add/remove tracks and playlists
with the load-bearing fields, correctly signed for the tier - such that an INDEPENDENT oracle reads
back every field and accepts it. The oracle is libgpod's own `itdb_parse` (never OrgZ's reader - that
circularity is what hid the bugs below), via the committed `OrgZ.Tests/oracle/gpod_dump.c`; the
booting device counts as the oracle where libgpod can't.

- **Plain tier (1G-4G, Mini, Photo, Video 5G/5.5G, Nano 1G/2G):** ✅✅ libgpod reads back an
  OrgZ-written iTunesDB with every field exact - id, title/artist/album/genre/composer, path, size,
  duration, track/total, disc/total, year, bitrate, sample-rate, rating, dbid, date-added - and both
  playlist forms (master + user) with correct membership and order, across add and remove
  (`ITunesDbWriterOracleTests`, two committed scenarios: emitted bytes + libgpod golden). The oracle
  caught three conformance bugs the self-round-trip never could:
  - the MHBD dataset count (0x14) was never written → libgpod (and the firmware) saw zero datasets and
    rejected the database ("no mhsd type 1"). Normalize now writes it.
  - BuildMhit dropped rating, total-tracks, disc#, total-discs and composer → now written.
  - every playlist MHIP lacked the type-100 MHOD_ID_PLAYLIST position child libgpod requires → the
    library read back as an EMPTY song list. BuildMhip now writes it.

- **hash58 (Classic 6/6.5/7G, Nano 3G/4G):** ✅ the hash VALUE is now cross-checked - OrgZ's
  `ITunesDbHash58` output is byte-identical to an INDEPENDENT from-spec implementation
  (`OrgZ.Tests/oracle/hash58_independent.py`, which generates the AES S-box from GF(2^8) and uses
  Python's stdlib HMAC-SHA1, sharing nothing with OrgZ but the documented `Fixed[]` constant), over the
  committed plain fixture + a FireWire GUID (`ITunesDbHash58OracleTests`: committed golden + gated live
  run). That rules out a porting bug in the y-derivation, zeroed regions or HMAC construction - what the
  old self-consistency tests couldn't. The canonical libgpod-binary cross-check
  (`itdb_hash58_write_hash`) is the further confirmation, deferred while the local Docker daemon is
  wedged; OrgZ's tables are also independently confirmed to be the standard AES S-box / inverse.

### Tiers still to close
- **hash72 + SQLite (Nano 5G):** the write path is ✅✅ hardware-validated (a stock Nano 5G plays
  OrgZ-added tracks - the booting device is the oracle). A libgpod cross-parse of the compressed CDB is
  the software-oracle follow-up (libgpod 0.8.x CDB support unconfirmed).
- **iTunesSD + bdhs (Shuffle 1G/2G, 3G/4G):** byte-layout + round-trip verified (slice B); no Shuffle
  in the fleet and libgpod's iTunesSD read path is thin, so device acceptance is the metal gap.

## Real-library mutation - co-habitation matrix (slice D)
Goal: OrgZ mutates a REAL iTunes-written iTunesDB (not one `CreateEmpty` authored) - add/remove tracks
and playlists, re-sign for the tier - without corrupting everything iTunes put there that OrgZ doesn't
model, and an independent oracle reads back BOTH the untouched iTunes content AND the OrgZ changes.
Proven against a real iPod Video 5.5G database (`ITunesDbRealMutationTests`, gated on
`ORGZ_REAL_ITUNESDB` + `ORGZ_GPOD_DUMP`; hermetic in CI).

- **Preservation:** ✅✅ starting from BriPod's real DB (5 datasets, 2919 tracks, 274 albums), an
  add-track + add-user-playlist + remove-track leaves the type-4 album table and the type-5 built-in
  playlists **byte-for-byte identical** - the datasets `ITunesDbChunkTree` preserves as opaque survive a
  mutation. Run green on Windows AND macOS (the writer is OS-agnostic; the mutation is byte-identical).
- **Acceptance:** ✅✅ libgpod's `itdb_parse` accepts the mutated real DB and reads back iTunes's
  untouched content plus the edits - 2919 tracks (2919 +1 −1), the original master "BriPod 5G"
  preserved, the removed track gone, the new track field-exact, and a new user playlist over it. Run on
  Linux (WSL libgpod) with the committed test.
- **Oracle tooling:** libgpod comes from a `debian` Docker container OR - when Docker is down and
  Homebrew has dropped the formula - `libgpod-dev` in WSL Ubuntu (`wsl -u root apt-get install`), which
  is how this slice was proven.

### Named gaps
- **hash58 real-DB mutation + re-sign** - BriPod is the plain tier (unsigned), so re-sign was a no-op
  here. Mutating a REAL hash58 database (Classic / Nano 3G-4G) and re-signing needs one of those
  devices' DBs - none in the fleet. (hash58's value itself is already proven, slice C.)
- **Device boots the mutated DB** - libgpod-accepts is proven; the firmware actually mounting an
  OrgZ-mutated iTunes library is the metal confirmation.
- **Removing a track that sits in a type-5 built-in playlist** - `RemoveTrack` cleans MHIPs from the
  type-2/3 playlists only; a music track (not in Audiobooks/Podcasts) was removed here, so no orphan
  arose, but type-5 MHIP cleanup is unverified.

## Podcasts + audiobooks - content-type matrix (slice E)
Goal: OrgZ writes podcasts and audiobooks (into a real iTunes library) with correct media kinds, the
Podcasts grouping, Audiobooks placement and the bookmark/unplayed flags, verified by libgpod
(`ITunesDbWriterOracleTests` podcast scenario + `ITunesDbRealMutationTests`, run live via WSL libgpod).

- **Media kinds:** ✅ libgpod reads mediatype 4 (podcast) and 8 (audiobook) back.
- **Podcasts membership + grouping:** ✅✅ two oracle-caught bugs fixed -
  - episode MHIPs lacked the type-100 position MHOD → libgpod counted zero members → an EMPTY Podcasts
    menu (the same bug class as the music library). Fixed; libgpod reads members [1,2].
  - podcast enclosure/feed URLs (mhod 15/16) were UTF-16 not plain UTF-8 → libgpod read a stray marker
    byte. Fixed; the real URLs read back.
  Grouping (show → episodes) is pinned structurally
  (`Podcast_grouping_nests_episodes_under_the_show_header`: one group-header MHIP, every episode nested
  via its 0x20 groupref + carrying the membership MHOD).
- **Bookmark/unplayed flags:** ✅ libgpod reads skip-when-shuffling, remember-position, mark-unplayed=2
  (new/blue dot) and flag4 for episodes; skip + remember-position for audiobooks.
- **Into a real library:** ✅✅ adding a podcast + audiobook to BriPod's real DB creates the Podcasts
  list by cloning the real master; libgpod confirms kinds + membership + the 2919 originals preserved.
- **Cross-platform:** byte-repro on Windows; live libgpod on Linux (WSL); the writer is OS-agnostic.
  (WSL `libgpod-dev` is the reliable Docker-free oracle now that Homebrew dropped the formula.)

### Named gaps
- **On-device menu placement + show→episode nesting DISPLAY** - libgpod accepts the grouping and lists
  the members; the firmware rendering Podcasts as shows→episodes (and the Audiobooks menu) is metal.
- **Resume playback (bookmark)** - the flags are written and read back; the firmware honouring the saved
  position is metal.
- **Audiobook excluded from Music/Library** - the audiobook currently also sits in the master playlist
  (media_type 8 still routes it to the Audiobooks menu); whether iTunes keeps audiobooks out of Music
  is unverified against a real iTunes audiobook.

## Hardware validation pass
The conformance suite proves these against synthetic devices; one session with the fleet closes
them against metal:
- Shuffle classic (1G/2G) iTunesSD write - the path that never worked before the recursion fix
- Shuffle playlist-replace + podcasts-as-tracks semantics on-device
- hash58 signing of an OrgZ-created (fresh/erased) iTunesDB - the header-overwrite fix
- Rockbox erase on a real box
- Nano 5G ALAC/AAC audio-format codes (MP3 proven)
- hash58 known-answer vector captured from a real Classic (boot test)
- Nano 5G CDB user-playlist form (research pending - playlists live in SQLite only until iTunes
  accepts a user-playlist mhyp in the CDB)
- Audiobooks land in the on-device Audiobooks menu (media_type 8 on Binary, media_kind 8 on
  Nano 5G) with the firmware honoring the remember-position flag - the conformance suite proves
  the writes; the menus need metal

## Release
- Velopack must bundle flac + lame + ffmpeg on Windows before release (rip + burn + iPod transcode
  all shell out to them)
- CI now gates every push (Tests workflow) and every commit (pre-commit hook, `git config
  core.hooksPath .githooks` per clone)

## Polish backlog
- AirPlay: real RAOP/AirPlay audio streaming (outputs currently listed disabled, "coming soon")
- Podcast store middle slot: design the feature behind the placeholder grid
- Sidebar device context menu: wire or remove the disabled "Import Into Library..." / "Import Into
  iPod..." items; CD node's "Rip CD..." / "Eject" (both services exist)
- Empty states for empty device nodes (blank grid → a quiet explanatory line)
- Group-header count wording ("2 Items" → "2 episodes" / "50 stations") wants a custom header
  template - cheap once shared media grid v1 lands
- The Ignored view has no producer (ignore-on-remove retired when Remove from Library became a
  real delete) - an explicit Hide gesture, or fold the view
- Audiobook chapter atoms (m4b chapters) aren't parsed for in-app display - the book-detail
  Chapters card lists the file parts; on-device chapters work natively either way
- Libro.fm token persistence off-Windows (per-session sign-in until a cross-platform keychain)
- `AreRowGroupsInitiallyCollapsed` (Avalonia PR #242): adopt when shipped, delete the
  collapse-seeding machinery
