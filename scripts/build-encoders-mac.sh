#!/usr/bin/env bash
# Builds flac + lame for macOS, for bundling with OrgZ.
#
# Same reasoning as the Linux script: OrgZ ships what OrgZ needs. Ripping shells out to
# real flac/lame binaries, and telling a Mac user to "brew install flac lame" before they
# can rip a CD is exactly the thing we don't do.
#
# Versions match Windows and Linux (flac 1.4.3, lame 3.100), so rip flags, tagging and
# cover-art behaviour are identical on every platform.
#
# PORTABILITY differs from Linux: macOS has no supported way to statically link libSystem,
# so "portable" here means our own libraries are static and the only dynamic dependencies
# are Apple's own, which exist on every Mac. That's checked below via otool, and the
# deployment target is pinned so the binary doesn't demand the build machine's macOS.
#
# Run on a Mac with the Xcode command-line tools:
#   bash scripts/build-encoders-mac.sh [arm64|x86_64]
#
# Either arch can be built from either kind of Mac - clang cross-compiles, and nothing here
# is executed during the build. The functional test at the end is skipped when the binaries
# cannot run on this machine (an x86_64 build on Apple Silicon without Rosetta).
#
# It stages scripts/staged/{flac,lame}-osx-<rid> and prints each SHA-256. Then upload both
# to the 'encoders-1' release and paste the hashes into scripts/encoders.json.
set -euo pipefail

FLAC_VER="1.4.3"
LAME_VER="3.100"

ARCH="${1:-$(uname -m)}"
case "$ARCH" in
    arm64)  RID="osx-arm64" ;;
    x86_64) RID="osx-x64" ;;
    *) echo "Unsupported arch: $ARCH (expected arm64 or x86_64)" >&2; exit 1 ;;
esac

# Cross-compiling needs the target spelled out for the compiler AND for autoconf, which
# would otherwise configure for the build machine and emit the wrong arch.
export CFLAGS="-arch $ARCH ${CFLAGS:-}"
export CXXFLAGS="-arch $ARCH ${CXXFLAGS:-}"
export LDFLAGS="-arch $ARCH ${LDFLAGS:-}"
CONFIGURE_HOST="--host=${ARCH}-apple-darwin"

echo "Building for $ARCH ($RID)"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAGED="$ROOT/scripts/staged"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$STAGED"

# Big Sur: the first macOS that runs on Apple Silicon, so the widest floor an arm64 build
# can have. Without it clang stamps the build machine's OS and the binary won't launch on
# anything older.
export MACOSX_DEPLOYMENT_TARGET=11.0

UA='Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'

cd "$WORK"

echo "== flac ${FLAC_VER} =="
curl -fsSL -A "$UA" -o flac.tar.xz "https://downloads.xiph.org/releases/flac/flac-${FLAC_VER}.tar.xz"
tar -xf flac.tar.xz
cd "flac-${FLAC_VER}"
# --disable-ogg: OrgZ rips native .flac, never FLAC-in-Ogg, so this drops the libogg
# dependency entirely rather than bundling a second library.
./configure $CONFIGURE_HOST --enable-static --disable-shared --disable-ogg --disable-doxygen-docs \
            --disable-examples --disable-dependency-tracking >/dev/null
make -j"$(sysctl -n hw.ncpu)" >/dev/null
strip src/flac/flac
cp src/flac/flac "$STAGED/flac-${RID}"
cd "$WORK"

echo "== lame ${LAME_VER} =="
# Debian's pool, not SourceForge: SourceForge 403s automated fetches from every URL form,
# and the .orig.tar.gz here is the pristine upstream tarball with a .dsc to verify against.
DEB_POOL="https://deb.debian.org/debian/pool/main/l/lame"
curl -fsSL -A "$UA" -o lame.tar.gz "${DEB_POOL}/lame_${LAME_VER}.orig.tar.gz"
if curl -fsSL -A "$UA" -o lame.dsc "${DEB_POOL}/lame_${LAME_VER}-6.dsc" 2>/dev/null \
   || curl -fsSL -A "$UA" -o lame.dsc "${DEB_POOL}/lame_${LAME_VER}-5.dsc" 2>/dev/null; then
  want=$(awk '/^Checksums-Sha256:/{f=1;next} /^[A-Za-z-]+:/{f=0} f && /\.orig\.tar\.gz$/{print $1}' lame.dsc | head -1)
  got=$(shasum -a 256 lame.tar.gz | awk '{print $1}')
  if [ -n "$want" ] && [ "$want" != "$got" ]; then
    echo "  SOURCE MISMATCH: .dsc says $want, downloaded $got - refusing to build." >&2
    exit 1
  fi
  echo "  source sha256 ${got} matches Debian's .dsc"
else
  echo "  WARNING: no .dsc retrieved; skipping source integrity check." >&2
fi
tar -xf lame.tar.gz
cd "lame-${LAME_VER}"
./configure $CONFIGURE_HOST --enable-static --disable-shared --disable-dependency-tracking >/dev/null
make -j"$(sysctl -n hw.ncpu)" >/dev/null
strip frontend/lame
cp frontend/lame "$STAGED/lame-${RID}"
cd "$WORK"

echo
echo "== verifying =="
for tool in flac lame; do
  bin="$STAGED/${tool}-${RID}"
  echo "--- $tool ---"
  echo "  $(file -b "$bin")"

  # Only Apple's own libraries may be linked, or it won't run on a stock Mac.
  foreign=$(otool -L "$bin" | tail -n +2 | grep -v '/usr/lib/\|/System/Library/' || true)
  if [ -n "$foreign" ]; then
    echo "  NON-SYSTEM DEPENDENCY - will not run on a stock Mac:" >&2
    echo "$foreign" >&2
    exit 1
  fi
  echo "  dylibs: Apple system only"

  minos=$(otool -l "$bin" | awk '/LC_BUILD_VERSION/{f=1} f&&/minos/{print $2; exit}')
  echo "  minos:  ${minos:-unknown}"
  case "$minos" in
    11.*|12.*|13.*) ;;
    *) echo "  WARNING: unexpected minimum macOS '${minos}' - did MACOSX_DEPLOYMENT_TARGET take effect?" >&2 ;;
  esac
done

if [ "$ARCH" != "$(uname -m)" ] && ! arch -"$ARCH" true 2>/dev/null; then
  echo
  echo "== skipping the functional test: $ARCH binaries cannot run on $(uname -m) here =="
  echo
  echo "== staged =="
  for tool in flac lame; do
    shasum -a 256 "$STAGED/${tool}-${RID}"
  done
  exit 0
fi

echo
echo "== they do real work =="
"$STAGED/flac-${RID}" --version
"$STAGED/lame-${RID}" --version | head -1

python3 - /tmp/orgz-tone.wav <<'PY'
import struct, math, sys, wave
w = wave.open(sys.argv[1], 'wb')
w.setnchannels(2); w.setsampwidth(2); w.setframerate(44100)
w.writeframes(b''.join(struct.pack('<hh', v, v) for v in
              (int(math.sin(2*math.pi*440*i/44100)*8000) for i in range(88200))))
w.close()
PY

"$STAGED/flac-${RID}" -8 -f -s -o /tmp/orgz-tone.flac /tmp/orgz-tone.wav
"$STAGED/flac-${RID}" -d -f -s -o /tmp/orgz-back.wav /tmp/orgz-tone.flac
cmp /tmp/orgz-tone.wav /tmp/orgz-back.wav && echo "  flac  lossless round-trip OK"
"$STAGED/lame-${RID}" -V2 --quiet /tmp/orgz-tone.wav /tmp/orgz-tone.mp3
echo "  lame  wrote $(stat -f%z /tmp/orgz-tone.mp3) byte mp3"
rm -f /tmp/orgz-tone.* /tmp/orgz-back.wav

echo
echo "== staged =="
for tool in flac lame; do
  shasum -a 256 "$STAGED/${tool}-${RID}"
done
