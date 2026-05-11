#!/usr/bin/env bash
# Dev launcher for OrgZ on macOS.
#
# Builds a real OrgZ.app bundle whose CFBundleExecutable is a native Mach-O
# shim (see orgz-launcher.c) that `execv`s into `dotnet exec OrgZ.dll`. macOS
# reads bundle identity (icon, app name, click-to-foreground) from the
# bundle's Info.plist, and the running process is the Apple-signed `dotnet`
# host - so SCSITaskUserClient access works *and* the Now Playing widget
# shows OrgZ properly.
#
# When Velopack pack starts signing the apphost in CI, drop this whole
# script + orgz-launcher.c + build-mac-dev-bundle.sh.

set -euo pipefail

cd "$(dirname "$0")/.."

CONFIG="${ORGZ_CONFIG:-Debug}"
APP="bin/${CONFIG}/net10.0/OrgZ.app"

if [[ ! -f "${APP}/Contents/MacOS/OrgZ" ]] || [[ "$(find OrgZ.csproj App ViewModels Services Views Controls Models Helpers -newer "${APP}/Contents/MacOS/OrgZ" -type f 2>/dev/null | head -1)" ]]; then
    scripts/build-mac-dev-bundle.sh
fi

# Foreground the existing OrgZ.app instance if one is running (Launch Services
# single-instance), otherwise launch a fresh one. `-W` blocks until the app
# exits so this script is composable in pipelines. Pass the bundle as a path
# (positional), not `-a name` - the latter wants a Launch-Services-registered
# app name, not a path.
exec open -W "${APP}" --args "$@"
