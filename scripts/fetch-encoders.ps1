#!/usr/bin/env pwsh
# Places the bundled media tools (ffmpeg, flac, lame) into tools/<rid>/ so ExecutableResolver
# finds them next to the app at runtime (AppContext.BaseDirectory/tools). Driven by encoders.json.
#
#   (default) FETCH  - download OUR vetted copies from the GitHub release named in encoders.json
#                      and verify each SHA-256. CI runs this per platform before packaging. Tools
#                      whose sha256 is still "PENDING" (or whose asset isn't published yet) are
#                      skipped with a warning - the app just falls back to a PATH install for those.
#
#   -Vendor          - MAINTAINER step: download the pinned UPSTREAM builds, extract each tool,
#                      hash it, stage the renamed asset into scripts/staged/, and write the sha256
#                      back into encoders.json. Then upload scripts/staged/* to a '<release>'
#                      GitHub release on <repo> and commit encoders.json. From then on every build
#                      fetches OUR copy and verifies it, so an upstream re-tag or compromise can't
#                      reach users - the bytes come from us and are checked.
#
# ffmpeg is the LGPL build (ebur128 + native aac/alac/pcm are all LGPL - no GPL codecs). OrgZ
# shells out to these as separate processes, so bundling them is mere aggregation.
param(
    [string]$Rid = "",
    [switch]$Vendor
)

$ErrorActionPreference = 'Stop'
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $PSScriptRoot 'encoders.json'
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json

if (-not $Rid) {
    $Rid = if ($IsWindows) { 'win-x64' } elseif ($IsLinux) { 'linux-x64' } elseif ($IsMacOS) { 'osx-arm64' } else { throw 'Cannot detect OS — pass -Rid win-x64|linux-x64|osx-arm64' }
}

$tools = $manifest.tools.$Rid
if (-not $tools -or $tools.Count -eq 0) {
    Write-Host "No bundled tools defined for $Rid — nothing to do (runtime uses a PATH install)."
    return
}

function Save-Url([string]$Url, [string]$Referer) {
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("orgz-enc-" + [Guid]::NewGuid().ToString('N'))
    $headers = @{}
    if ($Referer) { $headers['Referer'] = $Referer }
    Invoke-WebRequest -Uri $Url -OutFile $tmp -UserAgent $ua -Headers $headers -TimeoutSec 300
    return $tmp
}

function Get-Sha([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

# Extract one entry into $OutFile. zip via .NET; tar.xz via `tar` (accepts a '*/bin/x' glob).
function Extract([string]$Archive, [string]$Kind, [string]$Entry, [string]$OutFile) {
    New-Item -ItemType Directory -Force (Split-Path $OutFile) | Out-Null
    if ($Kind -eq 'zip') {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
        try {
            $e = $zip.Entries | Where-Object { $_.FullName -ieq $Entry -or $_.Name -ieq $Entry } | Select-Object -First 1
            if (-not $e) { throw "Entry '$Entry' not found in $Archive" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $OutFile, $true)
        }
        finally { $zip.Dispose() }
    }
    elseif ($Kind -eq 'tar.xz') {
        & tar -xf $Archive -C (Split-Path $OutFile) --strip-components=2 --wildcards $Entry
        if ($LASTEXITCODE -ne 0) { throw "tar extraction failed ($LASTEXITCODE) on $Archive" }
    }
    else { throw "Unknown archive kind '$Kind'" }
    if (-not $IsWindows -and (Test-Path $OutFile)) { & chmod +x $OutFile }
}

if ($Vendor) {
    # ── MAINTAINER: vet upstream, hash, stage, write hashes back into the manifest ──
    $staged = Join-Path $PSScriptRoot 'staged'
    New-Item -ItemType Directory -Force $staged | Out-Null
    foreach ($t in $tools) {
        Write-Host "vendor $Rid/$($t.out)  <-  $($t.upstreamUrl)"
        $arc = Save-Url $t.upstreamUrl $t.referer
        try {
            $outPath = Join-Path $staged $t.asset
            Extract $arc $t.archive $t.entry $outPath
            $t.sha256 = Get-Sha $outPath
            Write-Host ("  staged {0}  sha256={1}" -f $t.asset, $t.sha256)
        }
        finally { Remove-Item $arc -ErrorAction SilentlyContinue }
    }
    ($manifest | ConvertTo-Json -Depth 8) | Set-Content $manifestPath -Encoding UTF8
    Write-Host ""
    Write-Host "Staged $($tools.Count) file(s) in $staged. Next:"
    Write-Host "  1. Create GitHub release '$($manifest.release)' on $($manifest.repo); upload scripts/staged/*"
    Write-Host "  2. Commit scripts/encoders.json (now carrying the real SHA-256 values)"
    return
}

# ── FETCH: download OUR vetted copies from our release + verify each SHA-256 ──
$dest = Join-Path $root "tools/$Rid"
New-Item -ItemType Directory -Force $dest | Out-Null
$base = "https://github.com/$($manifest.repo)/releases/download/$($manifest.release)"
$got = 0; $skipped = 0
foreach ($t in $tools) {
    if ($t.sha256 -eq 'PENDING') {
        Write-Warning "skip $($t.out): not vendored yet (sha256 PENDING) — runtime falls back to a PATH install"
        $skipped++; continue
    }
    try { $tmp = Save-Url "$base/$($t.asset)" }
    catch {
        Write-Warning "skip $($t.out): download failed — is release '$($manifest.release)' published with asset '$($t.asset)'? ($($_.Exception.Message))"
        $skipped++; continue
    }
    try {
        $actual = Get-Sha $tmp
        if ($actual -ne $t.sha256.ToLowerInvariant()) {
            throw "SHA-256 mismatch for $($t.asset): manifest $($t.sha256), downloaded $actual — refusing to bundle."
        }
        Move-Item -Force $tmp (Join-Path $dest $t.out)
        if (-not $IsWindows) { & chmod +x (Join-Path $dest $t.out) }
        Write-Host ("  {0}  (verified)" -f $t.out)
        $got++
    }
    finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}
Write-Host "Done ($Rid): $got verified, $skipped skipped."
