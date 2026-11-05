<#
.SYNOPSIS
  Builds the portable release package of Csound Visual (folder + zip under release\).

.DESCRIPTION
  Builds the portable release package of Csound Visual:

    release\CsoundVisual-<version>-win-x64\
        CsoundVisual.exe (+ .NET runtime, self-contained: nothing to install)
        Csound7\          bin, plugins64, samples of the Csound 7 installation (LGPL-2.1)
        Examples\         the example collection
        Docs\             the HTML documentation (open Docs\index.html)
        VST3 plug-in\     CsoundVisual.vst3 for Cubase & co. (if it was built)
        LICENSE, THIRD-PARTY-NOTICES.md, licenses\, README.md
    release\CsoundVisual-<version>-win-x64.zip

  The app finds Csound in the Csound7 folder next to the exe, so the folder can be copied to any
  Windows 11 x64 PC (24H2 or later) and run directly.

  Prerequisites: a Csound 7 installation (-CsoundHome, default @Source\Csound7, else
  C:\Program Files\Csound7); optional native parts built first (tools\build_vsthost.ps1,
  tools\build_link.ps1, tools\build_vst.ps1).

  Steps: locate Csound 7 -> warn about missing optional native DLLs -> dotnet publish
  (self-contained) into the release folder -> copy Csound 7 (bin, plugins64, samples) plus its
  licence -> copy the HTML docs (docs\html, built by tools\build_docs.py), licences, README and
  CHANGELOG -> copy the VST3 bundle from src\CsoundVST\dist if present -> zip the folder.
  The release folder is deleted and recreated on every run, so nothing stale is shipped.

.PARAMETER CsoundHome
  Folder of the Csound 7 installation to bundle (must contain bin\csound7_64.dll). When empty,
  @Source\Csound7 in the repository is tried first, then %ProgramFiles%\Csound7.

.PARAMETER NoZip
  Only produce the release folder; skip the (slow) Compress-Archive step.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\package.ps1
  powershell -ExecutionPolicy Bypass -File tools\package.ps1 -CsoundHome "C:\Program Files\Csound7" -NoZip

.NOTES
  Called by: the maintainer when preparing a release (see BUILDING.md).
  The version comes from the first non-empty <Version> in src\CsoundVisual\CsoundVisual.csproj.
  docs\html must already exist (run python tools\build_docs.py first); this script does not
  regenerate it.
  Nothing is installed: all outputs go to release\ inside the repository. The VST3 bundle is
  shipped with an INSTALL.txt instead of being installed.
  The Csound copy is unmodified and loaded as a separate DLL, which is how the LGPL-2.1 terms
  are met; ABOUT.txt and LICENSE-LGPL-2.1.txt next to it state this.
#>
param(
    [string]$CsoundHome = "",
    [switch]$NoZip
)
# Any failing cmdlet aborts the script (dotnet is checked through $LASTEXITCODE).
$ErrorActionPreference = 'Stop'
# Repository root = parent of tools\.
$root = Split-Path -Parent $PSScriptRoot
$proj = Join-Path $root 'src\CsoundVisual\CsoundVisual.csproj'
# The csproj may have several <PropertyGroup>s; PowerShell's XML adapter returns one Version value
# per group (null where absent), so keep the first non-empty one.
$version = ([xml](Get-Content $proj)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$name = "CsoundVisual-$version-win-x64"
$out = Join-Path $root "release\$name"

# Prints a cyan "==> ..." progress header for each phase.
function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---- Csound 7
# Auto-detect: the first candidate that really contains the Csound 7 engine DLL wins.
if (-not $CsoundHome) {
    $candidates = @((Join-Path $root '@Source\Csound7'), (Join-Path $env:ProgramFiles 'Csound7'))
    $CsoundHome = $candidates | Where-Object { Test-Path (Join-Path $_ 'bin\csound7_64.dll') } | Select-Object -First 1
}
if (-not $CsoundHome -or -not (Test-Path (Join-Path $CsoundHome 'bin\csound7_64.dll'))) {
    throw "Csound 7 not found. Install it (C:\Program Files\Csound7) or pass -CsoundHome <folder with bin\csound7_64.dll>."
}
# Optional native bridges: the csproj only includes them when they exist in native\, and the app
# degrades gracefully without them, so a missing DLL is a warning, not an error.
foreach ($dll in 'CsoundVisualVstHost.dll', 'CsoundVisualLink.dll') {
    if (-not (Test-Path (Join-Path $root "src\CsoundVisual\native\$dll"))) {
        Write-Warning "$dll was not built: the package will not have that feature (see BUILDING.md)."
    }
}

# ---- App (self-contained .NET 10, win-x64)
Step "Publishing Csound Visual $version (Release, self-contained win-x64)"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
# Self-contained: the .NET runtime is copied into the folder, so the target PC needs no .NET
# install. ReadyToRun is disabled (smaller output, no extra cross-compile step); the app is
# JIT-compiled at run time. Publishing goes straight to the release folder, not to bin\.
dotnet publish $proj -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=false -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# ---- Csound 7 next to the exe (only what the app needs at run time)
Step "Copying Csound 7 from $CsoundHome"
$cs = Join-Path $out 'Csound7'
# bin = csound7_64.dll and its dependencies; plugins64 = opcode plug-in libraries; samples =
# files opened by examples through SSDIR. Headers, docs and the installer's other content are
# not needed at run time and are left out.
foreach ($sub in 'bin', 'plugins64', 'samples') {
    $src = Join-Path $CsoundHome $sub
    if (Test-Path $src) { Copy-Item $src (Join-Path $cs $sub) -Recurse -Force }
}
# Csound's own README is kept, renamed so it is not confused with the app's README.md.
foreach ($f in 'README.md') {
    $src = Join-Path $CsoundHome $f
    if (Test-Path $src) { Copy-Item $src (Join-Path $cs "CSOUND-$f") -Force }
}
Copy-Item (Join-Path $root 'licenses\LGPL-2.1-only.txt') (Join-Path $cs 'LICENSE-LGPL-2.1.txt') -Force
# The LGPL asks that the source of the exact version being distributed is available, so the note
# names the commit the bundled build was made from. csound --version prints it as
# "[commit: <40 hex digits>]" (on stderr: Out-String collects both streams as text).
# Windows PowerShell turns redirected stderr lines into errors, which 'Stop' would make fatal, so
# the preference is relaxed just for this call.
$ErrorActionPreference = 'Continue'
$csVersion = (& (Join-Path $cs 'bin\csound.exe') --version 2>&1 | Out-String)
$ErrorActionPreference = 'Stop'
$commit = if ($csVersion -match 'commit: ([0-9a-f]{40})') { $Matches[1] } else { $null }
$sourceLine = if ($commit) {
    "Exact source of this build: https://github.com/csound/csound/tree/$commit"
} else {
    "Source code: https://github.com/csound/csound"
}
if (-not $commit) { Write-Warning "Could not read the Csound commit from csound --version" }
# Licence/provenance note for the bundled Csound (written as UTF-8 with BOM by Windows PowerShell).
Set-Content -Path (Join-Path $cs 'ABOUT.txt') -Encoding utf8 -Value @"
Csound 7 (https://csound.com) is free software under the GNU Lesser General Public License 2.1
(LICENSE-LGPL-2.1.txt).
$sourceLine
This folder is an unmodified copy of the bin, plugins64 and samples folders of a Csound 7
installation, used by Csound Visual as a separate dynamic library (csound7_64.dll).
csound7_64.dll and its plug-ins include third-party libraries (libsndfile, mpg123, LAME, FLAC,
Ogg, Vorbis, Opus, liblo, PortAudio, PortMidi): see THIRD-PARTY-NOTICES.md and the licenses
folder next to CsoundVisual.exe.
"@

# ---- Documentation, licences, VST3 plug-in
Step "Copying documentation and licences"
# docs\html is the offline site generated by tools\build_docs.py (fails here if it was not built).
Copy-Item (Join-Path $root 'docs\html') (Join-Path $out 'Docs') -Recurse -Force
foreach ($f in 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'README.md', 'README.es.md', 'CHANGELOG.md') {
    Copy-Item (Join-Path $root $f) (Join-Path $out $f) -Force
}
Copy-Item (Join-Path $root 'licenses') (Join-Path $out 'licenses') -Recurse -Force
# The VST3 bundle is taken from dist\ (mirrored there by tools\build_vst.ps1), never from the
# user's installed VST3 folder. It already contains its own Csound 7 copy under Resources.
$vst = Join-Path $root 'src\CsoundVST\dist\CsoundVisual.vst3'
if (Test-Path $vst) {
    $vstOut = Join-Path $out 'VST3 plug-in'
    New-Item -ItemType Directory -Force $vstOut | Out-Null
    Copy-Item $vst (Join-Path $vstOut 'CsoundVisual.vst3') -Recurse -Force
    Set-Content -Path (Join-Path $vstOut 'INSTALL.txt') -Encoding utf8 -Value @"
Copy the CsoundVisual.vst3 folder into one of:
  %LOCALAPPDATA%\Programs\Common\VST3        (only for your user, no admin rights needed)
  C:\Program Files\Common Files\VST3          (all users)
then rescan plug-ins in your DAW (Cubase: Studio > VST Plug-in Manager > Rescan).
See Docs\en\vst3-cubase.html.
"@
} else {
    Write-Warning "The VST3 plug-in was not built (tools\build_vst.ps1): the package will not include it."
}

# ---- Zip
if (-not $NoZip) {
    Step "Compressing"
    $zip = Join-Path $root "release\$name.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # -Path is the folder itself (not its contents), so the zip contains one top-level
    # CsoundVisual-<version>-win-x64\ folder, which unzips cleanly.
    Compress-Archive -Path $out -DestinationPath $zip -CompressionLevel Optimal
    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Package: $zip ($mb MB)" -ForegroundColor Green
}
Write-Host "Folder:  $out" -ForegroundColor Green
