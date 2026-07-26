# OrgZ Roadmap

Quality, beauty, and simplicity - in that order when they conflict. Tests reflect the finished
product: nothing ships behind a capability flag without the conformance suite proving it.

## v1 status (as of 0.9.18)

Working, verified: CD ripping and burning (audio + data, CD-TEXT read back off a real disc),
iPod read/write across the tiers, podcasts, audiobooks, radio, library sharing (now proven
end to end, libvlc included), and a background service hosting the privileged work. Suite:
1813 green, gated per commit.

**Closed blockers**
- Encoders ship in the installer — `encoders-1` release exists; a cold `fetch-encoders.ps1`
  pulls all five win-x64 binaries, verifies every SHA-256, and they execute. CI will now
  produce packages that can rip/burn/transcode.
- **All six Burn Tests passed on real media (2026-07-25)**, ear checks included. 3, 5 and 6 are
  automated in `BurnValidationTests`; 1, 2 and 4 were burned on the Pioneer BDR-XS07U and
  listened to. Test 2 confirms skip-to-track lands on each song's first note at Gap 0, and
  Test 4's re-burn confirms the alpha.11 punctuation fix on metal. Burn validation is closed.
- **Share playback works** (0.9.18). `ShareEndToEndTests` runs a live server on a loopback
  socket and walks the whole client journey — browse → catalogue → MediaItem → stream — with
  a real HttpClient, and has libvlc open the resulting URL and report the right duration.
  Verifying it turned up three real defects, all fixed: share rows were silently unplayable
  (every play path guarded on `FilePath`, which a share track doesn't have), a mounted share
  leaked its catalogue into Music and Bad Format, and the server's loopback bind fallback
  threw `ObjectDisposedException` because `HttpListener.Start()` disposes itself on failure —
  so a GUI-hosted share could never come up at all.

**Open before v1 — highest risk first**
1. **The background service has never been installed.** Five releases of work (0.9.3 host,
   0.9.7 disc ops, 0.9.8 sync, 0.9.13 sharing toggle, 0.9.14 job reattach) is unit-tested but
   has never run as a real Windows service. Install it from Settings > Services, then exercise
   a burn (expect zero UAC), a sync, and a relaunch mid-job to see the LCD reattach.
   Installing from a *dev* build is now survivable (0.9.19): Start/Stop park the service
   without uninstalling, which matters because a running one holds `OrgZ.exe` open and fails
   the next rebuild. The install commands themselves are finally under test — but a test
   asserting an `sc create` string is not a service that started, so this stays open.
   As of 0.9.21 the card is Debug-only and 0.9.22 moved the shipping install into the
   PerMachine MSI, so the thing to exercise is now the MSI itself: install from it, confirm
   the service appears and starts, burn something and expect no UAC, then uninstall and
   confirm the service is gone. That also settles the open `Update.exe`/Program Files question.
2. **Sharing across two real machines.** The pipe is proven; the *discovery* half still isn't.
   mDNS on loopback is not mDNS on a LAN with a firewall, and `MdnsAdvertiser` binds 5353,
   where Bonjour/Windows' own responder may already sit. Host on one box, mount from another.
3. ~~Encoders~~ **DONE on all three platforms - nothing is PENDING in `encoders.json`.**
   - win-x64: ffmpeg, flac (+ libFLAC, libFLAC++), lame.
   - linux-x64: ffmpeg from BtbN's LGPL build via WSL2 (Windows' bsdtar can't read the
     `.tar.xz`, which is what had blocked it), plus `flac` and `lame` built from upstream
     source and **statically linked** by `scripts/build-encoders-linux.sh`. Static matters:
     an AppImage has NO dependency mechanism - Velopack ships Linux as a single
     self-contained file - so "apt install flac lame" isn't something we can express and
     isn't something a user should have to work out.
   - osx-arm64: ffmpeg 7.1 built from ffmpeg.org source on build-mac (Apple clang 17),
     LGPL-clean, with AudioToolbox alac/aac available alongside the native encoders.
   Everything was verified by USE, not by filename: flac does a byte-identical lossless
   round-trip, lame writes a real mp3, and ffmpeg does both the burn path (→ 44.1k/16/stereo
   pcm_s16le) and the iPod path (ALAC) on each platform. linux-x64 additionally passed a
   COLD `fetch-encoders.ps1` from the release with every SHA-256 matching.
   REMAINING: upload `scripts/staged/ffmpeg-osx-arm64` to `encoders-1` (Fox's action), then
   a cold fetch on the Mac closes it exactly as linux was closed.

**Deferred past v1:** multi-disc burning, device playlists master view, slim custom ffmpeg
build (would cut ~90 MB off the Windows installer; the fat build is fine for v1).

## Now / Next

### Testing burning e2e — LARGELY DONE
`BurnValidationTests` automates the capacity/disc-count arithmetic, the track-boundary sector
layout, and real-ffmpeg downsampling; real burns validated the rest on a Pioneer BDR-XS07U.
Still wanted: the no-media / not-blank / not-writable pre-flight paths against real drives, and
an image-backed virtual recorder if one proves workable.

### Collapsible device rows in the sidebar — SHIPPED 0.9.5
Chevron restored on device parents, TwoWay-bound to observable expansion; per-device session
memory keyed by serial (mount fallback) so a collapse survives reconnects and drive-letter
moves; auto-expand on first connect of a session. Alignment vs the LIBRARY column may want a
pixel-nudge pass after eyeballing.

### Multi-select in media grids — SHIPPED 0.9.2, drag fixed 0.9.23
**Fixed 0.9.23:** dragging a multi-selection collapsed to a single row. The press handler
is on the TUNNEL route, so it runs before the DataGrid processes the same press and
collapses the selection to the row under the cursor - by the time PointerMoved recognised
a drag, the live selection was one row. The selection is now captured at press time (the
last moment it's intact), the payload resolves against that, and the highlight is
re-applied when the drag starts so every dragged row stays lit.

**Drag ghost, 0.9.24:** the ghost used to name a single track and appear only for row
reorders, because its overlay adorned MainDataGrid and couldn't paint anywhere else. It
now adorns the window and follows the pointer over the sidebar too, shows on EVERY drag
(playlist, device, external app - not just reorders), and reads the payload: one track by
name, several as "N tracks". The insertion line stays reorder-only - it answers "the drop
lands here", which is a question only a reorder asks - so leaving the grid retires the
line but keeps the ghost.
Still open: no per-row thumbnails in the ghost (a stack of album art, iTunes-style), and
the ghost is window-bound, so dragging out to Explorer loses it at the window edge.

Extended selection in all three grids; every verb operates on the view-ordered selection
(play next, queue, favorite, add-to-playlist, remove-from-playlist/library/device, restore,
burn, drag to playlists/devices/external apps); selection-aware menu entries show "(N)".
Revisit under shared media grid v1 to consolidate; bulk device-remove confirmation and
multi-row reorder drags are follow-ups.

### Go to current song — SHIPPED 0.9.1
Ctrl+L / album-art click / LCD double-click jump to the playing song in the view it's playing
FROM (playlist, Favorites, device, CD - not just the Music tab), centered. Never auto-follows.
Later maybe: opt-in "follow playback" toggle, foobar-style.

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

### OrgZ background service — HOST SHIPPED 0.9.3
The device-helper grew into the general service host: protocol v2 (generic payload/result
fields, capability-discovery "status" op, polite version-mismatch refusal), an op registry
features plug privileged ops into (duplicate names throw, handler crashes answer as failures),
and Settings > Services (install/uninstall/status + per-feature keep-alive toggles under
OrgZ.Services.KeepAlive.*). Remaining below - features moving onto the host:
- **Elevation, once — CD OPS SHIPPED 0.9.7**: burn / burn-data / erase / rip / firmware
  reads route through the service's cd-run op when it's installed (same spec + progress-file
  contract, terminal-event tail, single-job gate) with the per-op UAC helper as automatic
  fallback; a started disc finishes even if the GUI exits. iPod device operations (USB
  control-transfer version reads, firmware-partition reads, sync) remain to move over.
  Windows eventually adds the bundled signed USB filter driver.
  macOS flavor: privileged helper for the reads unreachable unprivileged - Serial (SCSI INQUIRY
  VPD 0x80) and Software Version (firmware-partition osos / USB vendor control transfer);
  a blank-SysInfo classic shows "-" for both today (macOS only surfaces the USB iSerial,
  which is the FireWire GUID).
- **Work that survives the GUI** — burn (0.9.7) + iPod sync (0.9.8) done: the service owns
  both jobs once handed over, so closing the window doesn't kill them. Sync is opt-in via
  Settings > Services and hands only library IDs across (the service shares the library DB,
  re-fingerprints the device, and runs the normal AddTrackAsync import). Sharing follows.
  Reattach shipped 0.9.14: a "jobs" op reports in-flight work (kind, progress file, target)
  and the GUI follows it at startup, so relaunching mid-burn picks the LCD back up instead of
  showing an idle window over a live operation.
- **Lifecycle from the app — SHIPPED 0.9.19, DEBUG-ONLY as of 0.9.21**: Settings > Services reads three states
  (Not installed / Installed, stopped / Installed and running) and offers Start and Stop
  beside Install and Uninstall. Stopped is a real place to be, and the reason is development:
  a running service holds `OrgZ.exe` open and fails the next build, so uninstall-reinstall
  used to be the only way back. The status line also reconciles the OS's view with whether a
  helper is actually answering, since `OrgZ --device-helper` run by hand is a state the rest
  of the app happily uses. Every install/uninstall/stop/start command — sc.exe arguments, the
  LaunchDaemon plist, the systemd unit — is now a pure function under test
  (`DeviceHelperInstallerTests`), which it wasn't before: this code registers a root daemon
  that issues raw SCSI, and a typo in it is a broken install found by a user under a UAC prompt.
  The card is hidden in Release builds (0.9.21): a user makes one choice - standalone, where
  each privileged disc operation asks for UAC, or installed, asked once and then silent - and
  lifecycle buttons past that point can only strand them somewhere broken, like installed-but-
  stopped wondering why burning started prompting again. The installer code stays compiled
  (the pre-commit suite runs in Release and its tests must build), just unreachable from the UI.
- **Installed with the app — SHIPPED 0.9.22**: Windows ships the **MSI only**
  (`vpk pack --msi --instLocation PerMachine`), and OrgZ's Velopack after-install hook
  registers the background service from inside it. No button to find and no second prompt:
  the MSI installs to Program Files under HKLM and is elevated by construction, so the
  hook's `sc create` just works, and EVERY Windows install gets silent disc/iPod access.
  `OnBeforeUninstallFastCallback` removes the service again, so uninstalling OrgZ can't
  leave a LocalSystem service pointing at a deleted exe.
  Velopack's default Setup.exe is per-user into `%LocalAppData%` and deliberately never
  elevates, so it can't register a service - it is built (vpk generates the MSI inside the
  same step, so `--noInst` would suppress both) and then deleted before upload. The hook
  still declines silently when it finds itself unelevated, which covers a portable or
  sideloaded copy. (Velopack's Setup.exe cannot show a checkbox or a wizard by design;
  velopack/velopack#30 requests exactly this and is open.)
  AUTO-UPDATE: works, at the cost of one UAC prompt per update. `apply_windows_impl.rs`
  tests `is_directory_writable(root)`; when it isn't (Program Files) and the process isn't
  already elevated, it re-launches `Update.exe` via `run_process_as_admin` and waits up to
  ten minutes for it. Velopack ships localised strings for that prompt ("needs administrator
  permission to install version X"), and `update_uninstall_entry` keeps the MSI's
  Add/Remove Programs entry correct across updates (it detects the `.msi-installed` marker).
  So the trade is: UAC moves from *every disc/iPod operation* to *once per update*. Older
  docs claiming privileged directories are unsupported are stale - the current text offers
  PerMachine without that caveat, and the code backs it.
- IPC groundwork already proven on the Mac testbed (device-helper daemon + client).
- **The gate and the wire — TESTED 0.9.20**: the peer-credential policy is now a pure
  function (`IsPeerAllowed`) with the fail-closed rule pinned — an unreadable "who are you"
  must never resolve to "come in", and only a legacy install with no recorded owner fails
  open. The transport is exercised over a real named pipe rather than a MemoryStream:
  round trip, capability discovery, a frame split byte-by-byte across writes, mid-frame
  hangup, a 1.5 GB declared length, junk that isn't our JSON, and eight concurrent clients.
  `ReadMessageAsync` was made symmetric while doing it — a peer vanishing mid-body is the
  same event as one that never spoke, and now reads as "no message" instead of throwing.

### Library sharing over mDNS — SHIPPED 0.9.9 (server) + 0.9.10 (client)
Client: a 30 s browse reconciles a SHARED LIBRARIES sidebar section - new shares mount
(catalogue → streamable MediaItems, namespaced per share so ids never collide), vanished ones
unmount and surrender the view; our own share is filtered out. Share views carry a playback-only
context menu because there is nothing on a remote library to mutate.
The service hosts it (share-start / share-stop / share-status ops, so a closed GUI doesn't take
the library off the air): hand-rolled mDNS/DNS-SD advertising _orgz._tcp (no new dependency -
PTR/SRV/TXT/A encode+decode, hostile-packet hardened), and an HttpListener serving GET/HEAD only
- /catalogue (JSON) and /stream/{id} (Range-capable). Read-only by construction.
Hosting is reachable as of 0.9.13: Settings > Services > Share This Library toggles it live
(name editable, status line reports the actual service state and refuses to claim sharing when
the service isn't installed).
Playback proven 0.9.18. `ShareEndToEndTests` stands a real server on a loopback socket and
walks the client's whole journey with a real HttpClient - catalogue → MediaItem → stream,
byte-for-byte - then has libvlc open the URL and read back the right duration. Stream URLs now
carry the file extension (libvlc picks a demuxer far more reliably when the location looks like
a file) and cover art has its own `/art/{id}` route, fetched on play, since a share has no local
file to read a tag out of. Three defects the separate unit tests could never have caught, all
fixed: share rows were silently unplayable (`PlayMusicItem` bailed on the missing `FilePath`), a
mounted share leaked its catalogue into Music and Bad Format (only CDs and devices were
excluded), and the loopback bind fallback threw `ObjectDisposedException` - `HttpListener.Start()`
disposes itself on failure, so a GUI-hosted share had never once come up.
REMAINING: two real machines (mDNS on a LAN with a firewall, and 5353 possibly already held by
Bonjour, is not mDNS on loopback); PIN pairing beyond the trusted-LAN default; share playlists.

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
  (build-mac.local, .NET 10), and Linux (a fresh `dotnet/sdk:10.0` container). One managed parser (byte
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

## Album artwork - ArtworkDB matrix (slice F)
Goal: OrgZ writes album artwork into a real iTunes library - the ArtworkDB + per-generation `.ithmb`
thumbnail linked to the track - such that libgpod reads it back with correct dimensions and the pixels
round-tripping. Proven via libgpod's `itdb_parse` + `itdb_artwork_get_pixbuf`
(`OrgZ.Tests/oracle/artwork_dump.c`, `ITunesDbArtworkOracleTests`).

- **iPod Video 5.5G:** ✅✅ OrgZ writes an ArtworkDB (mhfd/mhsd/mhli/mhii/mhni + filename mhod) + a
  100×100 RGB565-LE `.ithmb`; libgpod links the artwork to the track by dbid (mhii song_id), decodes the
  thumbnail to 100×100 and returns the red pixel (0xF800 → R=248) - structure, linkage, dimensions and
  pixel round-trip all confirmed. The 5.5G formats (1028=100×100, 1029=200×200) match libgpod's
  `ipod_video_cover_art_info` table exactly.
- **Into a real library:** ✅✅ adding a track + artwork to BriPod's real DB; libgpod reads the new
  track's artwork (dims + red pixel) with the 2919 originals preserved.
- **The artwork writer was already correct** - no bug found (unlike the iTunesDB writer). The only catch
  was the oracle setup: libgpod parses the ArtworkDB only for a recognised cover-art device, and its
  model lookup drops one leading letter, so the mountpoint's SysInfo needs ModelNumStr MA446 (→ A446),
  not xMA446.
- **Cross-platform:** byte-repro on Windows; live libgpod + gdk-pixbuf on Linux (WSL).

### Named gaps
- **Per-generation thumbnail formats beyond 5.5G** - `IPodCapabilities` carries formats for Nano
  1G/2G, Video 5G etc., but only the 5.5G is oracle-verified; each other generation's format
  (dimensions + RGB565 endianness) wants the same libgpod cross-check (its `Itdb_ArtworkFormat` table is
  the reference).
- **mhit `mhii_link` not set** - OrgZ links artwork by dbid (mhii song_id), which libgpod uses; iTunes
  also sets the mhit's `mhii_link` (image id). Both libgpod and the dbid path work without it, but the
  firmware may prefer it - unverified.
- **On-device art display** - libgpod decodes the thumbnail; the iPod rendering it in the now-playing /
  list views is the metal confirmation.
- **Real embedded-cover pipeline** - the tests use a synthetic solid-colour thumbnail; the ffmpeg
  extract → resize → RGB565 path (`IPodTrackImporter`) that pulls a real cover is exercised only against
  the device, not the oracle.

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
- Encoder bundling — win-x64 DONE (0.9.6 vendored, release published 0.9.17). How it works:
  `scripts/encoders.json` pins each tool's SHA-256 and points at the `encoders-1` GitHub
  release; CI runs `scripts/fetch-encoders.ps1`, which downloads from OUR release (never the
  upstream URL, which moves), verifies the hash, and drops the binaries in `tools/<rid>/`;
  `BundleMediaToolsOnPublish` copies them into `publish/tools/` so Velopack ships them INSIDE
  the installer. Users download once. Verified cold end-to-end on 2026-07-25.
  REMAINING: vendor linux-x64 from a linux host (Windows tar chokes on the .tar.xz) and
  osx-arm64 via scripts/build-ffmpeg-mac.sh — those packages ship without encoders today.
- Slim ffmpeg (post-v1): the vendored win-x64 ffmpeg is 108.9 MB, ~98% of the payload, and is
  a full build. A `--disable-everything` build carrying only the codecs OrgZ needs lands
  ~10-20 MB. Deliberately deferred - fat is fine for v1.
- CI now gates every push (Tests workflow) and every commit (pre-commit hook, `git config
  core.hooksPath .githooks` per clone)

## Polish backlog
- Multi-disc burning: when a playlist overflows one disc (the burn dialog already shows the
  "Discs: N × capacity" row and disables Burn), split the set across discs — audio by track
  boundaries, data by files — with per-disc titles ("... (2/3)") and a swap-disc prompt between
  burns
- AirPlay: real RAOP/AirPlay audio streaming (outputs currently listed disabled, "coming soon")
- Podcast store middle slot: design the feature behind the placeholder grid
- Sidebar device context menu — DONE (0.9.15 audit): the "Import Into ..." items no longer
  exist and the CD node's Rip/Eject are wired; the entry was stale. Still dead: the Artwork
  tab's Add/Delete buttons (MediaInfoDialog), which need real embedded-art writing
- Empty view states — SHIPPED 0.9.15: every grid-backed view explains its own emptiness
  (empty iPod suggests Sync, empty playlist suggests dragging, a share blames neither), and
  it's distinct from the "no search results" overlay
- Group-header count wording ("2 Items" → "2 episodes" / "50 stations") wants a custom header
  template - cheap once shared media grid v1 lands
- Ignored view — REPLACED 0.9.16 by the iTunes row tick: the view, its config, and the
  ShowIgnored setting are gone; the flag now backs a per-row checkbox (Music, Favorites,
  playlists). Unticked tracks stay visible but are skipped by play-through and sync.
  Follow-up: honour the tick in the device sync gates (playback skipping is done)
- Audiobook chapter atoms (m4b chapters) aren't parsed for in-app display - the book-detail
  Chapters card lists the file parts; on-device chapters work natively either way
- Libro.fm token persistence off-Windows (per-session sign-in until a cross-platform keychain)
- `AreRowGroupsInitiallyCollapsed` (Avalonia PR #242): adopt when shipped, delete the
  collapse-seeding machinery
