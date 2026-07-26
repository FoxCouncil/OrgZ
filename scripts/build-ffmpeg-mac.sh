#!/usr/bin/env bash
# Builds a portable, LGPL ffmpeg for macOS arm64 from the OFFICIAL ffmpeg.org source, for
# bundling with OrgZ. --disable-gpl keeps it LGPL: OrgZ needs only ebur128 + the native
# aac / alac / flac / pcm / image codecs (all LGPL) and never x264/x265, so nothing GPL is
# pulled in. ffmpeg's own libraries are linked statically into the binary; only macOS system
# frameworks stay dynamic (they're present on every Mac), so the result is portable across
# recent Apple-Silicon machines.
#
# Run on an Apple-Silicon Mac (e.g. build-mac) with the Xcode command-line tools installed.
# Optional, but makes the build faster / enables asm:  brew install nasm
#
#   scripts/build-ffmpeg-mac.sh
#
# It drops tools/osx-arm64/ffmpeg and prints the binary's SHA-256. Test it, then:
#   1. upload it as asset 'ffmpeg-osx-arm64' to the OrgZ 'encoders-1' GitHub release
#   2. paste the printed sha256 into the osx-arm64 entry in scripts/encoders.json
# From then on every build fetches + verifies OUR copy, exactly like win-x64 / linux-x64.
set -euo pipefail

VER="7.1"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DEST="$ROOT/tools/osx-arm64"

# Without this, clang stamps the BUILD MACHINE's OS as the minimum (a build on macOS 26
# produced `minos 26.0`), and the binary refuses to launch on anything older - which is
# most users. 11.0 is Big Sur, the first macOS to run on Apple Silicon, so it's the widest
# floor an arm64 build can have.
export MACOSX_DEPLOYMENT_TARGET=11.0
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
cd "$WORK"

url="https://ffmpeg.org/releases/ffmpeg-${VER}.tar.xz"
echo "Downloading $url"
curl -fsSL -o ffmpeg.tar.xz "$url"

# Integrity-check the source against ffmpeg.org's own published SHA-256 when available. (Pinning
# the digest in-repo would be stronger; this at least catches a corrupted / truncated download.)
if curl -fsSL -o src.sha256 "${url}.sha256" 2>/dev/null; then
  want="$(awk '{print $1}' src.sha256)"
  echo "${want}  ffmpeg.tar.xz" | shasum -a 256 -c -
else
  echo "WARNING: ffmpeg.org published no .sha256 for ${VER}; skipping source integrity check."
fi

tar -xf ffmpeg.tar.xz
cd "ffmpeg-${VER}"

echo "Configuring (LGPL; ffmpeg libs static)…"
./configure \
  --disable-gpl --disable-nonfree \
  --disable-doc --disable-htmlpages --disable-manpages --disable-podpages --disable-txtpages \
  --disable-ffplay --disable-ffprobe \
  --disable-debug \
  --enable-static --disable-shared

echo "Building…"
make -j"$(sysctl -n hw.ncpu)"

mkdir -p "$DEST"
cp ffmpeg "$DEST/ffmpeg"
chmod +x "$DEST/ffmpeg"

echo ""
"$DEST/ffmpeg" -hide_banner -version | head -1

# A binary that only runs on the build machine's macOS is worse than no binary - it fails
# at launch on the user's Mac with nothing useful to say. Check, don't assume.
minos=$(otool -l "$DEST/ffmpeg" | awk '/LC_BUILD_VERSION/{f=1} f&&/minos/{print $2; exit}')
echo "minos:  ${minos:-unknown} (deployment target ${MACOSX_DEPLOYMENT_TARGET})"
case "$minos" in
  11.*|12.*|13.*) ;;
  *) echo "WARNING: unexpected minimum macOS '${minos}' - check MACOSX_DEPLOYMENT_TARGET took effect." >&2 ;;
esac
echo "dylibs: $(otool -L "$DEST/ffmpeg" | tail -n +2 | grep -vc '/usr/lib/\|/System/Library/') non-system"

echo "Built:  $DEST/ffmpeg"
echo "sha256: $(shasum -a 256 "$DEST/ffmpeg" | awk '{print $1}')"
echo "→ upload as 'ffmpeg-osx-arm64' to the encoders-1 release, then set that sha256 in scripts/encoders.json"
