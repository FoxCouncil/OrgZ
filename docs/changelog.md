# Changelog

## v0.13.1 (Current)

The first release cut from a tag: every installer is built, signed, and published
by the pipeline itself.

### Playback

- Replay Gain normalizes to -14 LUFS without clipping, so quiet and loud albums
  sit at the same level and neither distorts
- Rescan the whole library's Replay Gain in one pass from Settings

## v0.12.0

The first release with AirPlay, and the first one packaged as a per-machine
installer. It covers everything since v0.4.20.

### AirPlay

- Stream to AirPlay 2 receivers - HomePods, Apple TVs, and Macs - alongside or
  instead of the local sound device
- Pairing with password-protected receivers, with the password stored in the
  operating system's own protected store
- Now-playing tile on the speaker: title, artist, album, elapsed time and cover art
- The speaker's own buttons and Siri drive playback back in OrgZ - play, pause,
  next, previous, stop
- The volume slider adopts the speaker's current level when you select it, and
  follows it afterwards
- Receivers show live in the output picker as idle or playing, and OrgZ asks
  before taking over one somebody else is using
- Local and remote outputs are kept in step, so playing to both at once stays
  aligned

### Discs

- Rip audio CDs to WAV, FLAC, or MP3 with AccurateRip verification, MusicBrainz
  metadata, and embedded cover art
- Burn audio and data discs, with CD-TEXT, erase, and Red Book guards
- CD-TEXT is read straight off the disc, so recordables identify themselves
  without a lookup

### iPods and devices

- Read and write across the iPod generations, including artwork, playlists,
  podcasts, and audiobooks
- Rockbox devices are detected and handled on their own terms
- Sync runs in the background and survives closing the window

### Library, radio, podcasts, audiobooks

- Podcasts with per-show sync, category browsing, add-by-RSS, and OPML import/export
- Audiobooks that resume at the furthest chapter and show shelf progress
- A curated radio catalogue, plus adding and editing your own stations
- Star ratings that round-trip to POPM and Vorbis tags
- Bit-perfect FLAC playback at the file's native rate
- Share your library with another copy of OrgZ over the network, encrypted, with
  playlists and favourites

### Installing

- Windows ships an MSI that installs for all users and registers a background
  service, so disc and iPod access no longer prompt for consent every time
- Updates are checked quietly and applied only when you choose them from the
  **Help** menu
- The encoders OrgZ needs are bundled in the installer on every platform

## v0.4.20

- Abstracted list view system with config-driven columns and context menus
- Playback context - navigate freely while music continues from where you started
- Click album art / track info to jump to the currently playing item
- iTunes-style two-line track display with synchronized marquee scrolling
- Animated indeterminate seek bar for live radio streams
- Search clear button
- View state persistence across restarts (scroll position, selection, search, filters)
- Radio tags formatted with middot separators
- Consolas font baseline compensation for Fluent theme controls
- Darkened status bar background for better contrast

## v0.4.0

- Initial release
- Local music library scanning with metadata extraction
- Radio Browser and SHOUTcast integration
- Favorites system
- Windows SMTC and taskbar integration
- File system watching for library changes
