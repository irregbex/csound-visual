<#
.SYNOPSIS
  Downloads the Windows MIDI Services (MIDI 2.0) API package that Csound Visual builds against.

.DESCRIPTION
  Microsoft publishes Windows.Devices.Midi2 0.99.92-preview.11 on GitHub, not on nuget.org, and
  the file (103 MB) is above GitHub's 100 MB per-file limit, so it is not stored in this
  repository. This script puts it into src\packages-local\ (the local NuGet source declared in
  src\nuget.config) and checks its SHA-256.

  Steps:
    1. Creates src\packages-local\ if needed.
    2. If the .nupkg is not there yet, downloads it to "<name>.part" and renames it only after
       the download completed, so an interrupted download never leaves a truncated .nupkg that
       NuGet would try to use.
    3. Verifies the SHA-256 against the pinned value; on mismatch the file is deleted and the
       script fails, so a corrupted or tampered package is never restored.

  The script takes no parameters and is idempotent: running it again with a valid file present
  only re-checks the hash.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\get_midi2_package.ps1
  (from the repository root, before the first "dotnet build" / "dotnet restore")

.NOTES
  Called by: developers by hand before building the app (see BUILDING.md). Without the package
  NuGet restore of src\CsoundVisual\CsoundVisual.csproj fails.
  Nothing is installed system-wide: the package only lands in src\packages-local\ (NuGet then
  extracts it into the user's NuGet cache during restore, as with any package). The package
  provides the API metadata used by CsWinRT plus Windows.Devices.Midi2.dll/.pri, which the
  csproj copies next to CsoundVisual.exe; the Windows MIDI Services service itself is part of
  Windows and is not touched.
  To move to a newer preview, change $name, $url and $expected together, and every place the
  csproj names the version (PackageReference and the runtimes\win-x64\native paths).
#>

# Any failing cmdlet (download, hash, move) aborts the script.
$ErrorActionPreference = 'Stop'
# Repository root = parent of tools\.
$root = Split-Path -Parent $PSScriptRoot
$dir = Join-Path $root 'src\packages-local'
$name = 'Windows.Devices.Midi2.0.99.92-preview.11.nupkg'
$url = "https://github.com/microsoft/MIDI/releases/download/inbox-preview-11/$name"
# Pinned SHA-256 (lower-case hex) of the release asset above.
$expected = '13afc6e6da2adac309ab8c174352f46f1c0ebe2d79e65475bd6274defb73df74'
$target = Join-Path $dir $name

New-Item -ItemType Directory -Force $dir | Out-Null
if (-not (Test-Path $target)) {
    Write-Host "Downloading $name (103 MB) from github.com/microsoft/MIDI ..."
    # Windows PowerShell 5.1's progress bar slows Invoke-WebRequest down dramatically on large
    # files; hiding it makes the 103 MB download many times faster.
    $ProgressPreference = 'SilentlyContinue'
    Invoke-WebRequest -Uri $url -OutFile "$target.part" -UseBasicParsing
    Move-Item "$target.part" $target -Force
}
# Always verify, also when the file was already present (it may be from an older run).
# Get-FileHash returns upper-case hex, hence the ToLowerInvariant().
$hash = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $expected) {
    Remove-Item $target -Force
    throw "SHA-256 mismatch for $name (got $hash). The file was deleted; run the script again."
}
Write-Host "OK: $target"
