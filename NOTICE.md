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

### libvlc — LGPL-2.1-or-later
- Upstream: <https://www.videolan.org/vlc/>
- Source: <https://code.videolan.org/videolan/vlc>
- License text: <https://www.gnu.org/licenses/lgpl-2.1.html>
- Bundled in: **Windows** (via the `VideoLAN.LibVLC.Windows` NuGet, version
  3.0.21), **macOS** (via the project's app-bundle pack step).
- Linkage: OrgZ links libvlc **dynamically**; the system loader resolves
  `libvlc.so` / `libvlc.dll` / `libvlc.dylib` at runtime. No relinking work
  is required on the user's part — replacing the bundled `libvlc.*` with a
  compatible version of the user's choice is supported.

### FLAC — BSD-3-Clause
- Upstream: <https://xiph.org/flac/>
- Source: <https://github.com/xiph/flac>
- License text: <https://github.com/xiph/flac/blob/master/COPYING.Xiph>
- Bundled in: **Linux** AppImage builds (`tools/linux-x64/flac`, static),
  version 1.4.3.
- Build flags: `./configure --disable-shared --enable-static --disable-ogg
  --disable-doxygen-docs --disable-examples`.
- Attribution: "FLAC encoder and decoder by the Xiph.Org Foundation."

### LAME — LGPL-2.0-or-later
- Upstream: <https://lame.sourceforge.io/>
- Source: <https://sourceforge.net/projects/lame/files/lame/3.100/>
- License text: <https://www.gnu.org/licenses/old-licenses/lgpl-2.0.html>
- Bundled in: **Linux** AppImage builds (`tools/linux-x64/lame`, static),
  version 3.100.
- Build flags: `./configure --disable-shared --enable-static --disable-decoder`.
- Linkage: the `lame` CLI frontend is statically linked against `libmp3lame`.
  OrgZ invokes `lame` as a subprocess; it is not linked into the OrgZ binary
  itself.
- Source availability: the LAME upstream source (version 3.100, unmodified)
  is available indefinitely at the URL above; this serves as the written
  offer of source code required by LGPL §6. No changes were made to the
  upstream sources during the build.

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
| Microsoft.Data.Sqlite | 9.0.1 | MIT (bundles SQLite, public domain) |
| Optris.Icons.Avalonia | 12.0.4 | MIT |
| Optris.Icons.Avalonia.FontAwesome | 12.0.4 | MIT (icon glyphs under CC BY 4.0, see Assets section) |
| Serilog | 4.2.0 | Apache-2.0 |
| Serilog.Sinks.Console | 6.0.0 | Apache-2.0 |
| Serilog.Sinks.Debug | 3.0.0 | Apache-2.0 |
| Serilog.Sinks.File | 6.0.0 | Apache-2.0 |
| System.Management | 9.0.1 | MIT (Windows-only) |
| Velopack | 0.0.1298 | MIT |

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
| FoxRedbook | 1.0.0-alpha.2 | <https://github.com/FoxCouncil/FoxRainbowBooks> |
| FoxOrangebook | 1.0.0-alpha.2 | <https://github.com/FoxCouncil/FoxRainbowBooks> |

Their license terms are stated in the upstream repo.

---

## 4. Assets

### Inter font family — SIL Open Font License 1.1
- Upstream: <https://rsms.me/inter/>
- License: <https://openfontlicense.org/open-font-license-official-text/>
- Used via: `Avalonia.Fonts.Inter` NuGet; default UI typeface.

### Font Awesome Free (glyphs) — CC BY 4.0
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
network availability — no account or telemetry is required.

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
