# OrgZ

GATTA ORGANIzE

OrgZ is a cross-platform music and radio player built with .NET 10 and Avalonia. It manages a local music library, rips CDs, streams internet radio, subscribes to podcasts, and syncs music to iPods.

Current version: 0.7.12 (pre-release).

![Music library](docs/assets/screenshots/library-overview.png)

## Features

- **Music library** - scans local audio files and reads tags, album art, and duration.
- **CD ripping** - rips audio CDs to FLAC or MP3 and fills in titles, artist, album, year, and cover art from MusicBrainz.
- **Internet radio** - browses and streams stations from Radio Browser and SHOUTcast.
- **Podcasts** - subscribes to feeds, downloads episodes on configurable rules, and resumes playback where it left off.
- **Favorites and playlists** - stars tracks and stations, and builds playlists.
- **iPod and Rockbox** - detects connected iPods (stock firmware and Rockbox) and copies tracks, playlists, and album art to supported models.
- **Playback** - keeps playing as you move between views; supports shuffle, repeat, and a compact mini-player.
- **System integration** - media keys, taskbar controls, and System Media Transport Controls on Windows; Now Playing on macOS.

## Screenshots

| Now playing | Ripping a CD |
| --- | --- |
| ![Now playing](docs/assets/screenshots/now-playing.png) | ![Ripping a CD](docs/assets/screenshots/cd-rip-progress.png) |

| Radio | Mini-player |
| --- | --- |
| ![Radio](docs/assets/screenshots/radio-browser.png) | ![Mini-player](docs/assets/screenshots/mini-player.png) |

## Platforms

Windows, macOS, and Linux.

## Building

Requires the .NET 10 SDK.

```sh
git clone https://github.com/FoxCouncil/OrgZ
cd OrgZ
dotnet run --project OrgZ.csproj
```

CD ripping uses the `flac` and `lame` command-line encoders.

## Documentation

The full manual is at <https://foxcouncil.github.io/OrgZ/>.

## License

MIT - see [LICENSE](LICENSE).

The sample library in the screenshots and manual uses cover art by Mandie NRG and DJ Nine (with permission) and Eurobeat track metadata from [eurobeat.online](https://eurobeat.online) (CC BY 4.0). See the [credits](https://foxcouncil.github.io/OrgZ/credits/).
