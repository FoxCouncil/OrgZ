# OrgZ Third-Party Notices

OrgZ is built on, links against, or redistributes the following third-party
software. This document collects attributions and license obligations across
those components. The OrgZ source code itself is covered by the project
[LICENSE](LICENSE) file.

If you redistribute OrgZ (binary or source), the obligations below transfer
to you for each component you ship.

---

## 1. Native libraries bundled with release builds

These are shipped inside OrgZ release packages (installers, AppImages, .app
bundles). They are *redistributed*, so their license terms apply to anyone
who downloads OrgZ.

The full license texts ship with the app in `licenses/` (`LGPL-2.1.txt`,
`LGPL-2.0.txt`, `GPL-2.0.txt`, `BSD-3-Clause-FLAC.txt`) alongside
`licenses/THIRD-PARTY-NOTICES.txt`, which states the same obligations for the
end user. The exact vendored versions and their SHA-256 hashes are pinned in
`scripts/encoders.json`.

### libvlc - LGPL-2.1-or-later
- Upstream: <https://www.videolan.org/vlc/>
- Source: <https://code.videolan.org/videolan/vlc> (release tarballs:
  <https://get.videolan.org/vlc/3.0.21/>)
- License text: `licenses/LGPL-2.1.txt`, <https://www.gnu.org/licenses/lgpl-2.1.html>
- Bundled in: **Windows** (via the `VideoLAN.LibVLC.Windows` NuGet, version
  3.0.21), **macOS** (staged from the official VLC.app by
  `scripts/fetch-vlc-mac.sh`, version 3.0.21), and **Linux** (staged from
  Debian bookworm's `libvlc5` / `libvlccore9` / `vlc-plugin-base` packages by
  `scripts/fetch-vlc-linux.sh`, version 3.0.23-0+deb12u1, source
  <https://deb.debian.org/debian/pool/main/v/vlc/>).
- Linkage: OrgZ links libvlc **dynamically**; the loader resolves
  `libvlc.so` / `libvlc.dll` / `libvlc.dylib` at runtime. No relinking work
  is required on the user's part - replacing the bundled `libvlc.*` with a
  compatible version of the user's choice is supported: on macOS in
  `OrgZ.app/Contents/MacOS/vlc/lib/`, on Windows under `libvlc\win-x64\`, and on
  Linux in `vlc/lib/` inside the AppImage (extract it with
  `./OrgZ.AppImage --appimage-extract`).
- Plugins: all three bundles drop the plugin categories a music player never
  loads; the macOS staging also blocklists GPL-licensed plugins (x264, x265,
  libdvdread / libdvdnav / libdvdcss), and the Linux bundle takes only
  `vlc-plugin-base`, which carries none of them.

### ffmpeg - LGPL-2.1-or-later
- Upstream: <https://ffmpeg.org/>
- Source: <https://ffmpeg.org/download.html>; the prebuilt LGPL binaries come
  from <https://github.com/BtbN/FFmpeg-Builds>
- License text: `licenses/LGPL-2.1.txt`
- Bundled in: **Windows**, **Linux** and **macOS** release packages
  (`tools/<rid>/ffmpeg`). Windows and Linux use BtbN's `-lgpl` builds; macOS is
  built from ffmpeg.org 7.1 source by `scripts/build-ffmpeg-mac.sh`. No GPL
  codecs are enabled in any of them.
- Linkage: OrgZ invokes `ffmpeg` as a subprocess; nothing links against it.
- Used for: iPod transcode and artwork extraction, CD-burn and data-disc
  transcode, Sound Check (ReplayGain) loudness measurement.
- Source availability: the corresponding source is the unmodified upstream
  release at the URLs above.

### FLAC libraries (libFLAC, libFLAC++) - BSD-3-Clause
- Upstream: <https://xiph.org/flac/>
- Source: <https://github.com/xiph/flac>
- License text: `licenses/BSD-3-Clause-FLAC.txt`,
  <https://github.com/xiph/flac/blob/master/COPYING.Xiph>
- Bundled in: **Windows** (`libFLAC.dll`, `libFLAC++.dll`, version 1.4.3). The
  Linux and macOS builds link libFLAC statically into the `flac` binary.
- Attribution: "FLAC encoder and decoder by the Xiph.Org Foundation."

### FLAC `flac` command-line encoder - GPL-2.0-or-later
- Upstream splits FLAC by component: the libraries are BSD-licensed, the
  programs (`flac`, `metaflac`) are GPL. See the project README.
- Source: <https://github.com/xiph/flac> (release 1.4.3). Windows ships the
  official `flac-1.4.3-win` build from <https://downloads.xiph.org/releases/flac/>;
  Linux and macOS are built from that same unmodified source by
  `scripts/build-encoders-linux.sh` / `scripts/build-encoders-mac.sh`.
- License text: `licenses/GPL-2.0.txt`
- Bundled in: **Windows**, **Linux** and **macOS** release packages
  (`tools/<rid>/flac`), version 1.4.3.
- Build flags (Linux/macOS): `./configure --enable-static --disable-shared
  --disable-ogg --disable-doxygen-docs --disable-examples`.
- Linkage: OrgZ invokes `flac` as a separate process and never links against
  it, so the two are aggregated rather than combined.
- Source availability: the unmodified upstream flac-1.4.3 release at the URLs
  above; this serves as the written offer of source code required by GPL §3.

### LAME - LGPL-2.0-or-later
- Upstream: <https://lame.sourceforge.io/>
- Source: <https://sourceforge.net/projects/lame/files/lame/3.100/>; the
  Windows binary is the rarewares build (<https://www.rarewares.org/>) of that
  release.
- License text: `licenses/LGPL-2.0.txt`,
  <https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html>
- Bundled in: **Windows**, **Linux** and **macOS** release packages
  (`tools/<rid>/lame`), version 3.100.
- Build flags (Linux/macOS): `./configure --enable-static --disable-shared
  --disable-dependency-tracking`, then `make LDFLAGS="-all-static"` on Linux.
- Linkage: the `lame` CLI frontend is statically linked against `libmp3lame`.
  OrgZ invokes `lame` as a subprocess; it is not linked into the OrgZ binary
  itself.
- Source availability: the LAME upstream source (version 3.100, unmodified)
  is available indefinitely at the URLs above; this serves as the written
  offer of source code required by LGPL §6. No changes were made to the
  upstream sources during any of the builds shipped.

---

## 2. NuGet dependencies (.NET libraries)

Restored at build time, redistributed inside the OrgZ binary or alongside it.

### LGPL-licensed (notice + source-availability obligations apply)

| Package | Version | License | Source |
|---|---|---|---|
| LibVLCSharp | 3.9.4 | LGPL-2.1-or-later | <https://code.videolan.org/videolan/LibVLCSharp> |
| TagLibSharp | 2.3.0 | LGPL-2.1-only | <https://github.com/mono/taglib-sharp> |
| VideoLAN.LibVLC.Windows | 3.0.21 | LGPL-2.1-or-later | <https://code.videolan.org/videolan/vlc> |

OrgZ uses these libraries dynamically (managed reference / native DLL load).
Users can substitute compatible replacements at runtime by replacing the
shipped assemblies.

### Permissive licenses (attribution only)

| Package | Version | License |
|---|---|---|
| Avalonia | 12.0.3 | MIT |
| Avalonia.Controls.DataGrid | 12.0.0 | MIT |
| Avalonia.Desktop | 12.0.3 | MIT |
| Avalonia.Themes.Fluent | 12.0.3 | MIT |
| Avalonia.Fonts.Inter | 12.0.3 | MIT (package); embedded font under SIL OFL 1.1 |
| CommunityToolkit.Mvvm | 8.4.0 | MIT |
| Microsoft.Data.Sqlite | 9.0.7 | MIT (bundles SQLite, public domain) |
| Optris.Icons.Avalonia | 12.0.4 | MIT |
| Optris.Icons.Avalonia.FontAwesome | 12.0.4 | MIT (icon glyphs under CC BY 4.0, see Assets section) |
| Serilog | 4.2.0 | Apache-2.0 |
| Serilog.Sinks.Console | 6.0.0 | Apache-2.0 |
| Serilog.Sinks.Debug | 3.0.0 | Apache-2.0 |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 |
| SQLitePCLRaw.lib.e_sqlite3 | 3.50.3 | Apache-2.0 (native SQLite build; SQLite itself is public domain) |
| Svg.Skia | 3.2.1 | MIT (pulls SkiaSharp and HarfBuzzSharp native binaries, both MIT) |
| System.Management | 9.0.1 | MIT (Windows-only) |
| System.Security.Cryptography.ProtectedData | 9.0.1 | MIT |
| System.ServiceProcess.ServiceController | 9.0.1 | MIT (Windows-only) |
| Velopack | 1.2.110-ge826545 | MIT |

### Build-time-only (not redistributed)

| Package | Version | Notes |
|---|---|---|
| AvaloniaUI.DiagnosticsSupport | 2.2.0 | © AvaloniaUI OÜ. Included in Debug builds only (devtools bridge); excluded from Release via `IncludeAssets="None"` / `PrivateAssets="All"`. |

---

## 3. Sister projects (project references)

These are maintained by the same author and consumed via NuGet
(`PackageReference` in `OrgZ.csproj`).

| Project | Version | Upstream |
|---|---|---|
| FoxRedbook | 1.0.0-alpha.3 | <https://github.com/FoxCouncil/FoxRainbowBooks> |
| FoxOrangebook | 1.0.0-alpha.11 | <https://github.com/FoxCouncil/FoxRainbowBooks> |

Their license terms are stated in the upstream repo.

---

## 4. Assets

### Inter font family - SIL Open Font License 1.1
- Upstream: <https://rsms.me/inter/>
- License: <https://openfontlicense.org/open-font-license-official-text/>
- Used via: `Avalonia.Fonts.Inter` NuGet; default UI typeface.

### Font Awesome Free (glyphs) - CC BY 4.0
- Upstream: <https://fontawesome.com/>
- License: <https://fontawesome.com/license/free>
- Used via: `Optris.Icons.Avalonia.FontAwesome` NuGet for sidebar / button
  icons. Attribution: "Icons from Font Awesome Free, CC BY 4.0."

### Country flag images (PNG)
- Upstream: <https://flagcdn.com/> (image hosting)
- Source set: <https://github.com/lipis/flag-icons> (MIT license, by Panayiotis Lipiridis)
- Used via: 94 PNGs under `Assets/Flags/`, downloaded from `flagcdn.com/w80/`
  at seed time and embedded as Avalonia resources for the Radio country column.
- License: MIT for the upstream icon set. Attribution: "Flag icons from
  lipis/flag-icons, MIT License."

### Curated radio stations (`Assets/stations.json`)
- Source: <https://www.radio-browser.info/> public station directory.
- Used via: one-time seed (`tools/seed-stations.py`) that pulled the top-clicked
  English-language streams per genre into a bundled JSON shipped with the app.
- The radio-browser.info data is community-contributed and freely usable per
  the project's terms of service.

---

## 5. External services consulted at runtime

OrgZ queries the following web services for metadata enrichment when the
user inserts a CD or imports tracks. Network usage is opt-in via standard
network availability - no account or telemetry is required.

- **MusicBrainz** (<https://musicbrainz.org/>): CD lookup via Disc ID, track
  metadata, release-group lookup. Public, CC0-licensed metadata, governed
  by the MusicBrainz API usage policy.
- **Cover Art Archive** (<https://coverartarchive.org/>): album-cover image
  fetch keyed off MusicBrainz release / release-group MBIDs.
- **PodcastIndex** (<https://podcastindex.org/>): podcast feed directory,
  trending lists, category taxonomy, and episode metadata. PodcastIndex
  content is freely usable per the project's terms; attribution to
  PodcastIndex applies to all displayed feed metadata.

---

## 6. Reporting an omission

If a component is missing from this file or its terms are misrepresented,
please open an issue at <https://github.com/FoxCouncil/OrgZ/issues>.
