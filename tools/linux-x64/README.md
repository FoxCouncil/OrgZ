# Linux x64 bundled encoders

Drop static-linked `flac` and `lame` ELF binaries here. The `BundleLinuxEncoders`
MSBuild target in `OrgZ.csproj` copies anything in this directory into
`$(OutDir)tools/` during Linux builds, where `RipEncoder.ResolveExecutable`
picks them up as the fallback path when `flac` / `lame` aren't found on `PATH`.

## Recommended sources

- **flac**: build statically from <https://github.com/xiph/flac> (`./configure
  --disable-shared --enable-static` → `src/flac/flac`). Strip the binary.
- **lame**: build statically from <https://lame.sourceforge.io/> (`./configure
  --disable-shared --enable-static` → `frontend/lame`). Strip the binary.

Or grab prebuilt static binaries from a trusted source (e.g. an Alpine builder)
and verify the SHA-256.

## Why static?

Dynamically linked binaries would pull in the host's libFLAC / libmp3lame at
runtime, which defeats the point of bundling — old distros and AppImage runtimes
may not have those libs. Static binaries are typically 1-2 MB each; the AppImage
payload barely notices.

## Why not check in the binaries directly?

We don't redistribute prebuilt binaries from the public repo to keep the repo
lean and avoid arguing about which build flags / source tarball / signing
identity is canonical. The MSBuild target tolerates an empty directory, so
local dev builds still work — they just won't bundle anything, and the rip
flow falls back to `PATH` lookup.
