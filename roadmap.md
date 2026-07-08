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
