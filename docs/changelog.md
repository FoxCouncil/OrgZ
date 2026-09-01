# Changelog

## v0.18.0 (Current)

### iPods

- OrgZ now knows each player's hardware limits - the FAT32 file and folder ceilings,
  how many tracks and how much database a Classic can hold in memory, Rockbox's
  browser limit - and checks the device against them before and after every sync.
  Every music track typed, every cover stored, every playlist entry valid, no file
  at a ceiling: the status line reports the worst finding, the log the full list
- New tracks are spread across the fifty music folders the way iTunes does it,
  instead of all landing in one
- Cover art files roll over to a new file before they can hit the 4 GB filesystem
  ceiling, the way iTunes does it. A file that reached it silently lost every cover
  written afterwards
- New **Hardware Limits** page in the manual listing every limit and its source

- Fixed music synced to an iPod Classic, Nano 3G/4G or similar being missing from
  the Artists, Albums, Genres and Playlists menus. The tracks were on the device
  and playable, but were not marked as music, and those menus only list music.
  Playlists looked empty for the same reason. Tracks already on a device are
  repaired automatically at the start of the next sync - nothing is re-copied
- Album art is written once per cover instead of once per track. An album no
  longer stores the same picture on the device once for every song it has, which
  saves a lot of space and makes art load faster
- Some covers were silently skipped during sync (they decode fine on their own but
  ffmpeg rejects them while still inside the audio file). They're now extracted
  first and rendered from that, so those albums get their art
- Sync your entire music library. Sync Settings gains a "Music" option - every
  music track goes to the device, with playlists still choosing which named lists
  exist on it. If the library won't fit, that part is skipped with a note saying
  how much space it needs, and the rest of the sync carries on
- Sync Settings sits on the device's header bar next to Sync
- Syncing a device that's mid-eject now says so instead of doing nothing
- Running a sync plan against a classic (binary-database) iPod no longer fails
  instantly with a batching error
- The "No music on this device yet" line clears as soon as the first synced
  track lands, instead of waiting for a view switch
- The device capacity bar fills as tracks land during a sync, instead of sitting
  at 0 B until the end
- Syncing is much faster on libraries that need transcoding: several tracks are
  converted in parallel while the previous ones copy to the device, so the copy
  is all that paces a big sync

## v0.16.1

### Playlists

- Playlist rows line up with the rest of the sidebar again, and clicking a folder
  opens and closes it

## v0.16.0

### Playlists

- Folders. Group playlists into folders - nest them as deep as you like - from the
  sidebar's right-click menu or the File menu, and drag playlists in and out. The
  `.m3u8` files stay exactly where they were in your music folder; a playlist
  remembers its folder with an `#ORGZ-FOLDER:` line inside the file, which other
  players ignore
- Editing a playlist file outside OrgZ (adding a track in a text editor, say) is
  now picked up immediately instead of going unnoticed
- New Playlist and New Folder live in the File menu and the sidebar's right-click
  menu; the "New Playlist..." sidebar row is gone

## v0.15.1

### Performance

- Smoother scrolling in long lists. The rating stars are now drawn directly rather
  than built from sixteen controls per row, and the grid no longer re-measures every
  row while you scroll. Most noticeable on slower machines and high-resolution screens

## v0.15.0

### macOS

- An Intel build, alongside the Apple Silicon one. Both are signed with a Developer
  ID certificate and notarized, and each updates from its own architecture

### Playlists

- Playlists show their own running order in a `#` column, along with track number,
  duration and year - the same columns the library shows
- Sorting a playlist by any other column no longer loses that order; click `#` to
  come back to it
- Favorites gains the library's columns too
- On a device or a shared library, `#` is the track's place in the playlist rather
  than its place on its album

### Settings

- Every control that needs the background service now greys out together when the
  service is stopped, instead of only the sharing checkbox

## v0.14.0

### Playlists

- Playlists are files. Every playlist is a `.m3u8` in your music folder, and any
  `.m3u8` you put there becomes a playlist - no importing
- Favorites is written out as `Favorites.m3u8` for other software to read
- Drag a selection onto Favorites to star all of it at once
- Right-clicking a multiple selection acts on the whole selection instead of
  collapsing to one track

## v0.13.2

### Fixes

- The background service is now registered by the Windows installer as intended,
  so disc and iPod operations stay silent instead of asking for consent each time
- The library sharing checkbox follows the service when it is started or stopped,
  rather than needing OrgZ restarted

## v0.13.1

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
