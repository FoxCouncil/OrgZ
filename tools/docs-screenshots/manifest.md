# Screenshot harness

`dotnet run --project tools/docs-screenshots` renders OrgZ views to
`docs/assets/screenshots/*.png`, seeded with fake data — consistent, no personal
library/device data, regenerable when the UI changes.

It bootstraps the real `App` headless (Skia → correct theme/fonts/icons) and
isolates `Settings` to a temp dir. Full-window shots use
`new MainWindow(screenshotMode: true)` + an `internal Seed*ForScreenshots` hook on
the view model (skips LibVLC, audio output, device detection, and the live
library scan).

## Generated

| File | Page | Shows |
|------|------|-------|
| `cd-rip-options.png` | Ripping CDs | Rip Options dialog (FLAC) |
| `cd-detected.png` | Ripping CDs | Inserted CD, generic tracks (pre-metadata) |
| `cd-metadata.png` | Ripping CDs | CD with MusicBrainz titles/album |
| `cd-rip-progress.png` | Ripping CDs | Rip in progress — LCD title/ETA/progress bar |
| `device-ipod.png` | iPods & Rockbox | Device info bar (Classic 6G identity + capacity) |
| `device-sync.png` | Playlists & Syncing | Send-to-device result in the activity panel |
| `library-overview.png` | (Music Library, when written) | Populated library grid |

## Planned (need new seed hooks / pages)

| File | Page | Shows |
|------|------|-------|
| `now-playing.png` | Playback | Now-playing LCD with VU meter (needs audio-tap data) |
| `radio-browser.png` | Radio Stations | Radio station browser / filter panel |
| `settings.png` | Settings | Settings dialog |
| `first-launch.png` | First Launch | Empty-state / folder picker on first run |
