#!/usr/bin/env pwsh
# Fetches the bundled CD-rip encoders (flac, lame) into tools/win-x64/.
# These binaries are gitignored - re-run on a fresh clone or in CI before packaging
# a Windows release so RipEncoder.ResolveExecutable (and the elevated rip helper)
# can find them. Paths resolve relative to the repo root regardless of CWD.
#
# NOTE: the official xiph flac.exe is dynamically linked, so libFLAC.dll (and
# libFLAC++.dll) must sit next to it - they load from the executable's directory.

$ErrorActionPreference = 'Stop'
$ua = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36'
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root 'tools/win-x64'
New-Item -ItemType Directory -Force $dest | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Save-Url([string]$Url, [string]$Referer) {
    $zip = Join-Path ([IO.Path]::GetTempPath()) ("orgz-enc-" + [Guid]::NewGuid().ToString('N') + '.zip')
    $headers = @{}
    if ($Referer) { $headers['Referer'] = $Referer }
    Invoke-WebRequest -Uri $Url -OutFile $zip -UserAgent $ua -Headers $headers -TimeoutSec 120
    return $zip
}

function Expand-Entry([string]$Zip, [string]$EntryMatch, [string]$OutFile) {
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName -ieq $EntryMatch -or $_.Name -ieq $EntryMatch } |
            Select-Object -First 1
        if (-not $entry) { throw "Entry '$EntryMatch' not found in $Zip" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $OutFile, $true)
    }
    finally { $archive.Dispose() }
    Write-Host ("  {0}  ({1:N0} bytes)" -f (Split-Path $OutFile -Leaf), (Get-Item $OutFile).Length)
}

Write-Host "Fetching Windows rip encoders into $dest"

# flac - official Win64 build (xiph, BSD). flac.exe + its libFLAC DLLs.
$flac = Save-Url 'https://downloads.xiph.org/releases/flac/flac-1.4.3-win.zip'
try {
    Expand-Entry $flac 'flac-1.4.3-win/Win64/flac.exe'      (Join-Path $dest 'flac.exe')
    Expand-Entry $flac 'flac-1.4.3-win/Win64/libFLAC.dll'   (Join-Path $dest 'libFLAC.dll')
    Expand-Entry $flac 'flac-1.4.3-win/Win64/libFLAC++.dll' (Join-Path $dest 'libFLAC++.dll')
}
finally { Remove-Item $flac -ErrorAction SilentlyContinue }

# lame - RareWares Win64 build of LAME 3.100 (LGPL). Standalone exe.
$lame = Save-Url 'https://www.rarewares.org/files/mp3/lame3.100.1-x64.zip' 'https://www.rarewares.org/mp3-lame-bundle.php'
try {
    Expand-Entry $lame 'lame.exe' (Join-Path $dest 'lame.exe')
}
finally { Remove-Item $lame -ErrorAction SilentlyContinue }

Write-Host "Done. Verify the SHA-256 of all files before shipping a public release."
