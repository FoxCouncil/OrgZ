# OrgZ Roadmap

Quality, beauty, and simplicity - in that order when they conflict. Tests reflect the finished
product: nothing ships behind a capability flag without the conformance suite proving it.

## Now / Next

### Testing burning e2e
The CD burn pipeline (FoxOrangebook data-disc + audio burning, sector-padded WAV transcode) is
covered by argument/layout tests only. Wanted: an end-to-end burn against a rewritable disc (or an
image-backed virtual recorder if one proves workable) validating the full transcode → layout →
burn → verify chain, plus the no-media / not-blank / not-writable pre-flight paths on real drives.

### Audiobooks
The sidebar entry exists, disabled. The whole vertical: library-side audiobook detection and views,
device sync (media_type 8 write is already in the DB layer; `SupportsAudiobooks` currently just
aliases podcasts), chapters/resume where formats carry them, and its own conformance rows. Ends
with the disabled entry becoming a real view.

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
- `AreRowGroupsInitiallyCollapsed` (Avalonia PR #242): adopt when shipped, delete the
  collapse-seeding machinery
