#!/usr/bin/env bash
# Bundle libvlc into a linux-x64 publish directory, so the AppImage is genuinely
# self-contained.
#
# WHY THIS EXISTS: no LibVLC NuGet ships Linux natives, so without this the app links against
# whatever libvlc the host happens to have. On a stock Ubuntu or Fedora desktop that is
# nothing - VLC is not preinstalled on either - and OrgZ writes one line to a log the user has
# never heard of and exits. An AppImage has no dependency mechanism to declare `vlc` with, so
# bundling is the only way the file can keep the promise its own name makes.
#
# WHERE THE BYTES COME FROM: Debian bookworm's own pool, pinned by version AND SHA-256 below.
# Debian is used rather than a VideoLAN tarball because VideoLAN does not publish a generic
# Linux libvlc build, and Debian's archive is content-addressed, stable and auditable.
#
# The digests are the ones Debian publishes in its signed Packages index for these exact
# files, NOT hashes computed over whatever we happened to download - so a compromised mirror
# cannot hand us different bytes and a matching checksum. To re-pin (new version or suite):
#
#   curl -s https://deb.debian.org/debian/dists/bookworm/main/binary-amd64/Packages.gz \
#     | gzip -d | awk -v RS= '/^Package: (libvlc5|libvlccore9|vlc-plugin-base)$/ \
#       { for (i=1;i<=NF;i++) if ($i ~ /^(Package|Version|Filename|SHA256):$/) print $i, $(i+1) }'
#
# Usage: scripts/fetch-vlc-linux.sh <publish-dir>

set -euo pipefail

PUBLISH="${1:?usage: fetch-vlc-linux.sh <publish-dir>}"
[ -d "$PUBLISH" ] || { echo "publish directory not found: $PUBLISH" >&2; exit 1; }

VLC_VERSION="3.0.23-0+deb12u1"
MIRROR="https://deb.debian.org/debian"

# package|relative path in the pool|sha256
PACKAGES=(
    "libvlc5|pool/main/v/vlc/libvlc5_${VLC_VERSION}_amd64.deb|07ad5c61dc41acf29c485224accf457b7632e68a910eee21badf30213e6ab359"
    "libvlccore9|pool/main/v/vlc/libvlccore9_${VLC_VERSION}_amd64.deb|a3114b86450777e4cbbd4620419b74d189367e5f5026286cc74c39c9e759bfa7"
    "vlc-plugin-base|pool/main/v/vlc/vlc-plugin-base_${VLC_VERSION}_amd64.deb|38b953a2a6355c5ba75e3e5d2015100d793fbf5a00211aaa78b2d705a9547cb1"
)

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

DEST_LIB="$PUBLISH/vlc/lib"
DEST_PLUGINS="$PUBLISH/vlc/plugins"
mkdir -p "$DEST_LIB" "$DEST_PLUGINS"

for entry in "${PACKAGES[@]}"; do
    name="${entry%%|*}"
    rest="${entry#*|}"
    path="${rest%%|*}"
    want="${rest##*|}"
    deb="$WORK/$name.deb"

    echo "==> $name $VLC_VERSION"
    curl -fsSL "$MIRROR/$path" -o "$deb"

    got="$(sha256sum "$deb" | cut -d' ' -f1)"
    if [ "$got" != "$want" ]; then
        echo "SHA-256 mismatch for $name" >&2
        echo "  expected $want" >&2
        echo "  actual   $got" >&2
        exit 1
    fi

    # dpkg-deb is not guaranteed present (and is absent on non-Debian hosts), but ar and tar
    # are: a .deb is an ar archive whose data member is a tarball.
    ( cd "$WORK" && ar x "$deb" && mkdir -p "extract-$name" && tar -xf data.tar.* -C "extract-$name" && rm -f data.tar.* control.tar.* debian-binary )
done

# The libraries themselves, resolved through their symlinks so the AppImage carries real
# files rather than dangling links into /usr.
for so in "$WORK"/extract-*/usr/lib/x86_64-linux-gnu/libvlc*.so.*; do
    [ -e "$so" ] || continue
    cp -L "$so" "$DEST_LIB/"
done

# Plugins. libvlc refuses to play anything without them, and it finds them through
# VLC_PLUGIN_PATH, which the app sets from its own base directory.
PLUGIN_SRC="$WORK/extract-vlc-plugin-base/usr/lib/x86_64-linux-gnu/vlc/plugins"
[ -d "$PLUGIN_SRC" ] || { echo "no plugins found in vlc-plugin-base" >&2; exit 1; }
cp -R "$PLUGIN_SRC/." "$DEST_PLUGINS/"

# Drop the categories a MUSIC player never loads - the same policy the Windows publish target
# and the macOS script already apply, for the same reason: libvlc scans this directory on
# first init, so fewer files is a faster cold start as well as a smaller download. Audio,
# access, demux, packetizer and codec all STAY (codec holds the audio decoders next to the
# video ones and cannot be dropped wholesale).
for category in video_output video_filter video_chroma video_splitter visualization spu text_renderer stream_out mux access_output gui; do
    rm -rf "${DEST_PLUGINS:?}/$category"
done

# Assert the result is actually loadable rather than merely present: a missing transitive
# dependency here is a silent "no audio" for every Linux user, discovered after release.
#
# LD_LIBRARY_PATH is set for the check because libvlc.so.5 carries a DT_NEEDED on
# libvlccore.so.9 and NO RUNPATH, so it cannot find its sibling on its own. That is not a
# packaging fault - the app loads libvlccore by absolute path first, which puts it in the
# process before libvlc asks for it - but it does mean a bare ldd reports a false failure.
# Anything ELSE reported missing here is real.
if command -v ldd >/dev/null 2>&1; then
    missing="$(LD_LIBRARY_PATH="$DEST_LIB" ldd "$DEST_LIB"/libvlc.so.5 2>/dev/null | grep 'not found' || true)"
    if [ -n "$missing" ]; then
        echo "bundled libvlc has unresolved dependencies:" >&2
        echo "$missing" >&2
        exit 1
    fi
fi

echo "bundled libvlc $VLC_VERSION:"
echo "  libs    : $(find "$DEST_LIB" -type f | wc -l) file(s), $(du -sh "$DEST_LIB" | cut -f1)"
echo "  plugins : $(find "$DEST_PLUGINS" -name '*.so' | wc -l) plugin(s), $(du -sh "$DEST_PLUGINS" | cut -f1)"
