#!/usr/bin/env bash
# Fetch the official VLC.app for macOS, verify the publisher checksum, and stage
# libvlc + its plugins into the publish directory so Velopack ships them inside
# the OrgZ .app bundle.
#
# Legal notes: libvlc / libvlccore are LGPLv2.1+. Dynamic loading from a
# separate folder, with the user able to substitute their own copies, is the
# linkage model LibVLCSharp and VideoLAN explicitly support for non-GPL apps
# (OrgZ is MIT). We additionally filter out plugins whose upstream license is
# GPL (defense in depth — they are not normally present in the official
# binary, but we never want to ship them).
#
# Usage: scripts/fetch-vlc-mac.sh <publish-dir> <arch>
#   arch is one of: arm64, intel64
# Output:
#   <publish-dir>/vlc/lib/{libvlc.dylib,libvlccore.dylib,...}
#   <publish-dir>/vlc/plugins/lib*_plugin.dylib (filtered)
#   <publish-dir>/THIRDPARTY-LibVLC.txt

set -euo pipefail

VLC_VERSION="${VLC_VERSION:-3.0.21}"
PUBLISH_DIR="${1:?publish directory required}"
ARCH="${2:?arch required (arm64 | intel64)}"

case "$ARCH" in
    arm64|intel64) ;;
    *) echo "Unsupported arch: $ARCH (expected arm64 or intel64)" >&2; exit 1 ;;
esac

DMG_NAME="vlc-${VLC_VERSION}-${ARCH}.dmg"
BASE_URL="https://get.videolan.org/vlc/${VLC_VERSION}/macosx"
WORK_DIR="$(mktemp -d -t orgz-vlc-XXXXXX)"
trap 'rm -rf "$WORK_DIR" 2>/dev/null || true; [[ -n "${MOUNT_POINT:-}" ]] && hdiutil detach "$MOUNT_POINT" -quiet 2>/dev/null || true' EXIT

echo "==> Downloading $DMG_NAME"
curl --fail --silent --show-error --location \
    --output "$WORK_DIR/$DMG_NAME" \
    "$BASE_URL/$DMG_NAME"

# VideoLAN publishes a SHA-256 sidecar for every release artifact. Fetching and
# verifying it pins us to the exact bits the project signed off on, so a
# compromised mirror cannot silently swap libvlc.
echo "==> Verifying checksum"
curl --fail --silent --show-error --location \
    --output "$WORK_DIR/$DMG_NAME.sha256" \
    "$BASE_URL/$DMG_NAME.sha256"

EXPECTED_SHA="$(awk '{print $1}' "$WORK_DIR/$DMG_NAME.sha256")"
ACTUAL_SHA="$(shasum -a 256 "$WORK_DIR/$DMG_NAME" | awk '{print $1}')"
if [[ "$EXPECTED_SHA" != "$ACTUAL_SHA" ]]; then
    echo "Checksum mismatch for $DMG_NAME" >&2
    echo "  expected: $EXPECTED_SHA" >&2
    echo "  actual:   $ACTUAL_SHA" >&2
    exit 1
fi
echo "    ok ($ACTUAL_SHA)"

echo "==> Mounting dmg"
MOUNT_POINT="$WORK_DIR/mnt"
mkdir -p "$MOUNT_POINT"
hdiutil attach "$WORK_DIR/$DMG_NAME" -mountpoint "$MOUNT_POINT" -nobrowse -quiet -readonly

SRC_LIB="$MOUNT_POINT/VLC.app/Contents/MacOS/lib"
SRC_PLUGINS="$MOUNT_POINT/VLC.app/Contents/MacOS/plugins"
if [[ ! -d "$SRC_LIB" || ! -d "$SRC_PLUGINS" ]]; then
    echo "Unexpected VLC.app layout in dmg" >&2
    exit 1
fi

DEST="$PUBLISH_DIR/vlc"
mkdir -p "$DEST/lib" "$DEST/plugins"

echo "==> Copying libvlc dylibs"
cp -R "$SRC_LIB/" "$DEST/lib/"

# Plugin allow-listing strategy:
#   - Drop known-GPL plugins by name (x264/x265 encoders, libdvdcss/libdvdread).
#     These are not normally present in the official macOS build, but the
#     blocklist makes the policy explicit and survives upstream changes.
#   - Drop categories OrgZ does not need (GUI front-ends, video output, video
#     filters, visualizations, subtitle renderers). This is a size optimization,
#     not a license requirement.
echo "==> Filtering plugins"
GPL_BLOCKLIST=(
    "libx264_plugin.dylib"
    "libx265_plugin.dylib"
    "libdvdread_plugin.dylib"
    "libdvdnav_plugin.dylib"
    "libdvdcss_plugin.dylib"
)

# Filename prefixes for plugin categories we do not need on a music app.
UNUSED_PREFIXES=(
    "libvideo_filter_"
    "libvideo_chroma_"
    "libvideo_splitter_"
    "libvout_"
    "libgles_"
    "libgl_"
    "libcaopengl"
    "libcaca_"
    "libdeinterlace_"
    "libpostproc_"
    "libqt_"
    "libskins2_"
    "libmacosx_"
    "libvisual_"
    "libgoom_"
    "libprojectm_"
    "libfreetype_"
    "libsubsdec_"
    "libsubsusf_"
    "libsubstx3g_"
    "libsvcdsub_"
    "libdvbsub_"
    "libcc_"
    "libcvdsub_"
    "libtelx_"
    "libt140_"
    "liblibass_"
    "libsdl_image_"
    "libscreen_"
)

shopt -s nullglob
copied=0; skipped_gpl=0; skipped_unused=0
for src in "$SRC_PLUGINS"/lib*_plugin.dylib; do
    name="$(basename "$src")"

    for blocked in "${GPL_BLOCKLIST[@]}"; do
        if [[ "$name" == "$blocked" ]]; then
            skipped_gpl=$((skipped_gpl + 1))
            continue 2
        fi
    done

    for prefix in "${UNUSED_PREFIXES[@]}"; do
        if [[ "$name" == "$prefix"* ]]; then
            skipped_unused=$((skipped_unused + 1))
            continue 2
        fi
    done

    cp "$src" "$DEST/plugins/"
    copied=$((copied + 1))
done
shopt -u nullglob

echo "    copied=$copied  skipped_gpl=$skipped_gpl  skipped_unused=$skipped_unused"

# Drop the LGPL notice next to the binaries so it travels in the final .app.
cat > "$PUBLISH_DIR/THIRDPARTY-LibVLC.txt" <<EOF
OrgZ bundles libvlc and a filtered subset of its plugins from VLC ${VLC_VERSION},
distributed by VideoLAN at https://www.videolan.org/vlc/.

libvlc and libvlccore are licensed under the GNU Lesser General Public License,
version 2.1 or later. The full LGPLv2.1 text is available at:
    https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt

Source code for the libvlc version bundled here is available at:
    https://get.videolan.org/vlc/${VLC_VERSION}/

Users may replace the bundled libvlc.dylib / libvlccore.dylib in
OrgZ.app/Contents/MacOS/vlc/lib/ with their own builds. OrgZ will load whichever
copy of libvlc is present at that location.

OrgZ itself is licensed under the MIT License (see LICENSE).
EOF

echo "==> Done. Bundled VLC ${VLC_VERSION} (${ARCH}) into $DEST"
