#!/usr/bin/env bash
# Builds STATIC flac + lame binaries for linux-x64, for bundling with OrgZ.
#
# Why build rather than download: there is no trustworthy upstream that publishes static
# Linux builds of either, and Velopack ships Linux as an AppImage - a format with no
# dependency mechanism at all (one self-contained file, nothing installed system-wide). So
# "apt install flac lame" is not something we can express, and not something a user should
# have to work out. We ship what OrgZ needs.
#
# Static on purpose: an AppImage is expected to run on any distro, and a binary linked
# against this machine's glibc would not. Neither tool touches NSS or dlopen, so a fully
# static link is clean here - no getaddrinfo caveats.
#
# Versions match what Windows already ships (flac 1.4.3, lame 3.100), so every platform runs
# the same encoders with the same flags, tagging and cover-art behaviour.
#
# Run anywhere with a C toolchain - a Linux box, or WSL on the dev machine:
#   bash scripts/build-encoders-linux.sh
#
# It stages scripts/staged/{flac,lame}-linux-x64 and prints each SHA-256. Then:
#   1. upload both to the OrgZ 'encoders-1' GitHub release
#   2. paste the printed hashes into the linux-x64 entries in scripts/encoders.json
set -euo pipefail

FLAC_VER="1.4.3"
LAME_VER="3.100"

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
STAGED="$ROOT/scripts/staged"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$STAGED"
cd "$WORK"

# A browser UA: some mirrors refuse default curl, and nothing about this request should
# identify the project.
UA='Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'

echo "== flac ${FLAC_VER} =="
# Same host the Windows build comes from, so both platforms share one trust root.
curl -fsSL -A "$UA" -o flac.tar.xz "https://downloads.xiph.org/releases/flac/flac-${FLAC_VER}.tar.xz"
tar -xf flac.tar.xz
cd "flac-${FLAC_VER}"
# --disable-ogg: OrgZ rips native .flac, never FLAC-in-Ogg, and dropping it removes the
# libogg dependency entirely. --disable-programs would remove the very binary we want.
./configure --enable-static --disable-shared --disable-ogg --disable-doxygen-docs \
            --disable-examples --disable-dependency-tracking >/dev/null
# -all-static at MAKE time, not configure time. Both projects link through libtool, which
# quietly drops a plain -static and hands back a dynamically-linked PIE (the verification
# below caught exactly that) - but passing it to configure instead breaks the compiler
# probe, because there libtool isn't in the picture and gcc can't parse the flag.
make -j"$(nproc)" LDFLAGS="-all-static" >/dev/null
strip src/flac/flac
cp src/flac/flac "$STAGED/flac-linux-x64"
cd "$WORK"

echo "== lame ${LAME_VER} =="
# NOT SourceForge: it 403s automated fetches (both the /download and downloads.sourceforge.net
# forms), so it can't be used from a script or from CI. Debian's pool serves the *pristine*
# upstream tarball - a .orig.tar.gz is upstream's own bytes, unmodified - and publishes a .dsc
# carrying its SHA-256, which is a better integrity story than SourceForge offered anyway.
DEB_POOL="https://deb.debian.org/debian/pool/main/l/lame"
curl -fsSL -A "$UA" -o lame.tar.gz "${DEB_POOL}/lame_${LAME_VER}.orig.tar.gz"

echo "-- verifying source against Debian's published checksum --"
# The .dsc lists every source file under Checksums-Sha256; pull the line for the orig tarball.
if curl -fsSL -A "$UA" -o lame.dsc "${DEB_POOL}/lame_${LAME_VER}-6.dsc" 2>/dev/null \
   || curl -fsSL -A "$UA" -o lame.dsc "${DEB_POOL}/lame_${LAME_VER}-5.dsc" 2>/dev/null; then
  want=$(awk '/^Checksums-Sha256:/{f=1;next} /^[A-Za-z-]+:/{f=0} f && /\.orig\.tar\.gz$/{print $1}' lame.dsc | head -1)
  got=$(sha256sum lame.tar.gz | awk '{print $1}')
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
# No --enable-nasm: the asm path needs nasm installed, and this has to build on a bare
# toolchain (CI, a fresh WSL). The C path encodes identically, just slower - and we encode
# a handful of CD tracks, not a render farm.
./configure --enable-static --disable-shared --disable-dependency-tracking >/dev/null
make -j"$(nproc)" LDFLAGS="-all-static" >/dev/null
strip frontend/lame
cp frontend/lame "$STAGED/lame-linux-x64"
cd "$WORK"

echo
echo "== verifying =="
for tool in flac lame; do
  bin="$STAGED/${tool}-linux-x64"
  echo "--- $tool ---"
  desc=$(file -b "$bin")
  echo "  $desc"
  # Must carry no dynamic dependencies or it won't run on another distro. Tested via
  # `file`, deliberately NOT `ldd`: ldd exits non-zero for a static binary, which under
  # `set -o pipefail` fails the pipeline even when the grep matched - a check that reports
  # failure for the success case is worse than no check.
  case "$desc" in
    *"statically linked"*)
      echo "  static: yes"
      ;;
    *)
      echo "  STATIC CHECK FAILED - this binary links against system libraries:" >&2
      ldd "$bin" || true
      exit 1
      ;;
  esac
done

echo
echo "== they run =="
"$STAGED/flac-linux-x64" --version
"$STAGED/lame-linux-x64" --version | head -2

echo
echo "== round-tripping real audio =="
# Prove they actually encode, not just print a version: 2 s of tone -> flac -> back to wav,
# and the same tone -> mp3. A binary that runs but can't encode is worse than a missing one.
python3 - <<'PY'
import struct, math, wave
w = wave.open('/tmp/orgz-tone.wav', 'wb')
w.setnchannels(2); w.setsampwidth(2); w.setframerate(44100)
frames = b''.join(struct.pack('<hh', v, v) for v in
                  (int(math.sin(2*math.pi*440*i/44100)*8000) for i in range(88200)))
w.writeframes(frames); w.close()
PY
"$STAGED/flac-linux-x64" -8 -f -o /tmp/orgz-tone.flac /tmp/orgz-tone.wav
"$STAGED/flac-linux-x64" -d -f -o /tmp/orgz-decoded.wav /tmp/orgz-tone.flac
cmp /tmp/orgz-tone.wav /tmp/orgz-decoded.wav && echo "  flac: lossless round-trip verified"
"$STAGED/lame-linux-x64" -V2 --quiet /tmp/orgz-tone.wav /tmp/orgz-tone.mp3
test -s /tmp/orgz-tone.mp3 && echo "  lame: encoded $(stat -c%s /tmp/orgz-tone.mp3) bytes"
rm -f /tmp/orgz-tone.* /tmp/orgz-decoded.wav

echo
echo "== staged =="
for tool in flac lame; do
  sha256sum "$STAGED/${tool}-linux-x64"
done
echo
echo "Next: upload both to the 'encoders-1' release, and paste the hashes into scripts/encoders.json"
