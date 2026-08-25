# Screenshot harness

`dotnet run --project tools/docs-screenshots` renders OrgZ views to
`docs/assets/screenshots/*.png`, seeded with fake data - consistent, no personal
library/device data, regenerable when the UI changes.

It bootstraps the real `App` headless (Skia → correct theme/fonts/icons) and
isolates `Settings`, `LibraryDb` and `App.FolderPath` to temp directories, so
nothing reads or writes the developer's own library, playlists or image cache.
Full-window shots use `new MainWindow(screenshotMode: true)` (skips LibVLC, audio
output, device detection, and the live library scan).

Music metadata is real Eurobeat (eurobeat.online, CC BY 4.0) with Mandie NRG /
DJ Nine cover art used with permission. Stations, shows and books are invented in
the same theme - no real broadcaster, podcast or book is named. Podcast artwork
renders offline by pre-seeding `RemoteImage`'s SHA-1-keyed disk cache with those
same licensed covers.

## Generated

| File | Page | Shows |
|------|------|-------|
| `library-overview.png` | Music Library | Populated library grid |
| `playlists.png` | Playlists | A playlist selected, header counts, sidebar list |
| `favorites.png` | Favorites | Favorites view |
| `podcasts.png` | Podcasts | Subscribed shows with artwork |
| `audiobooks.png` | Audiobooks | Owned-books shelf |
| `radio-browser.png` | Radio Stations | Station browser |
| `now-playing.png` | Playback | Now-playing LCD |
| `mini-player.png` | Playback | Mini-player window |
| `settings.png` | Settings | Settings dialog |
| `cd-rip-options.png` | Ripping CDs | Rip Options dialog (FLAC) |
| `cd-detected.png` | Ripping CDs | Inserted CD, generic tracks (pre-metadata) |
| `cd-metadata.png` | Ripping CDs | CD with MusicBrainz titles/album |
| `cd-rip-progress.png` | Ripping CDs | Rip in progress - LCD title/ETA/progress bar |
| `device-ipod.png` | iPods & Rockbox | Device info bar (Classic 6G identity + capacity) |
| `device-sync.png` | Playlists & Syncing | Send-to-device result in the activity panel |
| `sync-settings.png` | Playlists & Syncing | Sync settings for a connected device |
| `device-picker.png` | Playlists & Syncing | Choosing which device to send to |
| `burn-disc.png` | Burning Discs | Burn Disc dialog |
| `media-info.png` | Music Library | Get Info for a track |
| `playlist-name.png` | Playlists | Naming a new playlist |
| `airplay-password.png` | AirPlay | Receiver password prompt |
| `first-launch.png` | First Launch | Empty library on a fresh install |

## Planned (need new seed hooks)

| File | Page | Shows |
|------|------|-------|
| `sharing.png` | Library Sharing | A share visible from a second machine |
| `airplay-picker.png` | AirPlay | Output picker with receivers listed |

Both need a seeded network peer, which the harness has no hook for yet.
