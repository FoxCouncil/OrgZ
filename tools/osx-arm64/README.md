# tools/osx-arm64

Bundled command-line tools for the macOS (Apple Silicon) build, dropped here at build time and
copied next to the app by the `BundleMediaToolsOnPublish` target in `OrgZ.csproj`.

- **ffmpeg** - built from official ffmpeg.org source as a minimal **LGPL** binary via
  [`scripts/build-ffmpeg-mac.sh`](../../scripts/build-ffmpeg-mac.sh), then uploaded to the
  `encoders-1` GitHub release and SHA-256-pinned in [`scripts/encoders.json`](../../scripts/encoders.json).
  `scripts/fetch-encoders.ps1` pulls + verifies it at build time.

flac / lame on macOS come from a PATH install (Homebrew), same as Linux.

Binaries here are gitignored - see `.gitignore`.
