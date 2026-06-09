# Windows x64 bundled encoders

Drop `flac.exe` and `lame.exe` here. The `BundleWindowsEncoders` MSBuild target in
`OrgZ.csproj` copies anything in this directory into `$(OutDir)tools/` during
Windows builds, where `RipEncoder.ResolveExecutable` picks them up as the fallback
when `flac` / `lame` aren't found on `PATH`.

This is **also the only path the elevated CD-rip helper can use**: the rip runs as
a UAC-elevated relaunch of `OrgZ.exe`, and that process doesn't inherit the user's
`PATH` additions — but it shares `AppContext.BaseDirectory`, so `BaseDirectory/tools/`
resolves there too.

## Populating

Run `scripts/fetch-encoders.ps1` from the repo root. It downloads and extracts:

- **flac.exe** — official Win64 build from <https://downloads.xiph.org/releases/flac/> (BSD-licensed, standalone).
- **lame.exe** — RareWares Win64 build of LAME 3.100 (<https://www.rarewares.org/mp3-lame-bundle.php>, LGPL, standalone).

Verify the SHA-256 of both before shipping a public release.

## Why not check in the binaries?

Same rationale as `tools/linux-x64/`: keep the repo lean and avoid baking a specific
build / signing identity into source control. The MSBuild target tolerates an empty
directory, so dev builds without the binaries still work — they just fall back to
`PATH` for `flac` / `lame`.
