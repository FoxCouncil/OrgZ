#!/usr/bin/env bash
# Build a dev-mode OrgZ.app bundle on macOS that uses a native launcher shim
# (see orgz-launcher.c). End result:
#
#   bin/Debug/OrgZ.app/
#     Contents/
#       Info.plist               ← bundle identity macOS reads
#       MacOS/
#         OrgZ                   ← Mach-O launcher; execs `dotnet exec OrgZ.dll`
#         OrgZ.dll  + deps
#       Resources/
#         AppIcon.icns
#
# Run via `open bin/Debug/OrgZ.app` (or scripts/run-mac.sh, which calls this).
# Once Velopack pack signs the apphost in CI, delete this script + launcher.

set -euo pipefail

cd "$(dirname "$0")/.."

CONFIG="${ORGZ_CONFIG:-Debug}"
BIN_DIR="bin/${CONFIG}/net10.0"
APP="${BIN_DIR}/OrgZ.app"
CONTENTS="${APP}/Contents"
MACOS_DIR="${CONTENTS}/MacOS"
RES_DIR="${CONTENTS}/Resources"

echo "==> dotnet build (${CONFIG})"
dotnet build OrgZ.csproj -c "${CONFIG}" --nologo --verbosity quiet

echo "==> Stage managed assemblies into MacOS/"
mkdir -p "${MACOS_DIR}"
# Copy every dotnet build output next to the launcher. Use ditto, which is
# macOS-native and (unlike rsync) doesn't leave FinderInfo / resource-fork
# detritus that codesign refuses. Strip the apphost — we replace it with
# our shim — and PDBs.
ditto --norsrc --noextattr --noacl --noqtn "${BIN_DIR}" "${MACOS_DIR}"
rm -f "${MACOS_DIR}/OrgZ" "${MACOS_DIR}"/*.pdb
rm -rf "${MACOS_DIR}/OrgZ.app"

# Drop runtime-specific native payloads for OSes we're not targeting. dotnet
# build emits every RID a referenced package supports (Win/Linux/musl/wasm/
# maccatalyst …) — ~360 MB of Windows assets and ~150 MB of Linux variants we
# never load on macOS. Keep osx-arm64 (this Mac), the shared "osx"/"unix"
# buckets, and osx-x64 (Rosetta + cross-arch dev). Everything else: gone.
if [[ -d "${MACOS_DIR}/runtimes" ]]; then
    find "${MACOS_DIR}/runtimes" -mindepth 1 -maxdepth 1 -type d \
        ! -name 'osx-arm64' \
        ! -name 'osx-x64' \
        ! -name 'osx' \
        ! -name 'unix' \
        -exec rm -rf {} +
fi

echo "==> Compile launcher shim"
clang -O2 -arch arm64 -o "${MACOS_DIR}/OrgZ" scripts/orgz-launcher.c
xattr -cr "${MACOS_DIR}/OrgZ"

echo "==> Generate AppIcon.icns from Assets/app-icon-1024.png"
mkdir -p "${RES_DIR}"
ICONSET="$(mktemp -d -t orgz-iconset-XXXXXX)/AppIcon.iconset"
mkdir -p "$ICONSET"
# Source is the 1024×1024 Fluent Emoji rasterization (the same image the About
# box loads). sips downscales it into every slot the Apple Iconset spec needs;
# @2x variants are required for Retina rendering in Finder / Dock.
ICON_SRC="Assets/app-icon-1024.png"
for sz in 16 32 64 128 256 512; do
    sips -z "$sz" "$sz" "$ICON_SRC" --out "$ICONSET/icon_${sz}x${sz}.png" > /dev/null
    half=$((sz / 2))
    if [[ $half -ge 16 ]]; then
        sips -z "$sz" "$sz" "$ICON_SRC" --out "$ICONSET/icon_${half}x${half}@2x.png" > /dev/null
    fi
done
# 1024 slot (= 512@2x) for full Retina coverage.
cp "$ICON_SRC" "$ICONSET/icon_512x512@2x.png"
iconutil -c icns -o "${RES_DIR}/AppIcon.icns" "$ICONSET"
rm -rf "$(dirname "$ICONSET")"

echo "==> Write Info.plist"
cat > "${CONTENTS}/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleIdentifier</key>      <string>com.foxcouncil.orgz</string>
    <key>CFBundleName</key>            <string>OrgZ</string>
    <key>CFBundleDisplayName</key>     <string>OrgZ</string>
    <key>CFBundleExecutable</key>      <string>OrgZ</string>
    <key>CFBundleIconFile</key>        <string>AppIcon</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleVersion</key>         <string>0.6.0</string>
    <key>CFBundleShortVersionString</key> <string>0.6.0</string>
    <key>LSMinimumSystemVersion</key>  <string>10.15</string>
    <key>NSHighResolutionCapable</key> <true/>
    <key>LSApplicationCategoryType</key> <string>public.app-category.music</string>
    <!-- Single instance: Launch Services foregrounds the existing process when
         the user clicks the dock icon, Now Playing widget, or "open" again. -->
    <key>LSMultipleInstancesProhibited</key> <true/>
</dict>
</plist>
EOF

# Deliberately not signing the bundle here. The launcher inherits dotnet's
# IOKit grants at execv time (the running process is the Microsoft-signed
# /usr/local/share/dotnet/dotnet), so the launcher's own signature doesn't
# matter for SCSI/CD access. macOS's `bin/.../OrgZ.app` parent dir keeps
# acquiring com.apple.FinderInfo during the build, which codesign refuses,
# and an unsigned dev bundle still launches fine under Gatekeeper.

# Force Launch Services to re-read the new bundle metadata. Without this it
# may keep showing a stale icon / identity in Now Playing.
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister \
    -f "${APP}" 2>/dev/null || true

echo "==> Done: ${APP}"
