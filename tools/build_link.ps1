<#
.SYNOPSIS
  Builds CsoundVisualLink.dll (Ableton Link for Csound Visual) and copies it to src\CsoundVisual\native\.

.DESCRIPTION
  1. Downloads the tagged Ableton Link release (default Link-3.1.5) and the asio-standalone commit
     that release references as its git submodule (GitHub zips do not include submodules) into
     src\AbletonLink\external\link and src\AbletonLink\external\asio, if they are missing.
     The archives are only extracted; nothing inside them is executed. The build uses our own
     src\AbletonLink\CMakeLists.txt, not Link's.
  2. Configures and builds with CMake + Visual Studio 2022 (x64, Release, static CRT) into
     src\AbletonLink\build: CsoundVisualLink.dll and link_peer_test.exe.
  3. Unless -SkipTests: runs two link_peer_test peers for a few seconds and checks they see each
     other (peers = 1) and agree on the tempo (no sound is made).
  4. Copies CsoundVisualLink.dll to src\CsoundVisual\native\ (the app's csproj copies it next to
     CsoundVisual.exe). Without the DLL the app still runs; only Link is unavailable.

  Windows Firewall may ask once whether link_peer_test.exe / CsoundVisual.exe may use the
  network (Link uses UDP multicast on port 20808). Peers on the same computer work either way.

.PARAMETER SkipTests
  Do not run the two-peer smoke test (step 3). Use it on machines without a usable network
  adapter or when the firewall prompt is unwanted.

.PARAMETER Clean
  Delete src\AbletonLink\build before configuring, forcing a full CMake re-configure and rebuild.
  The downloaded sources in src\AbletonLink\external are kept (delete that folder by hand to
  re-download them, e.g. after changing -LinkTag).

.PARAMETER LinkTag
  Git tag of github.com/Ableton/link to download (default "Link-3.1.5"). It also selects the
  asio-standalone commit, because that commit is read from the tag's "modules" submodule entry.
  Only used when the sources are missing.

.PARAMETER Config
  CMake/MSBuild configuration to build (default "Release"). Also selects the output folder
  src\AbletonLink\build\bin\<Config>.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build_link.ps1
  powershell -ExecutionPolicy Bypass -File tools\build_link.ps1 -SkipTests -Clean

.NOTES
  Called by: developers by hand (see BUILDING.md); tools\package.ps1 only warns when the DLL is
  missing, it does not build it.
  Requirements: Visual Studio 2022 with the C++ workload, CMake (C:\Program Files\CMake or on
  PATH), internet access for the first run. Unlike build_vst.ps1 this script uses the Visual
  Studio generator, which finds the compiler by itself, so vcvars64.bat is not loaded.
  Nothing is installed system-wide: all outputs stay inside the repository
  (src\AbletonLink\external, src\AbletonLink\build, src\CsoundVisual\native).
  The app loads CsoundVisualLink.dll through P/Invoke; Link itself is GPL-2.0-or-later, which is
  compatible with the app's GPL-3.0 licence.
#>
param(
    [switch]$SkipTests,
    [switch]$Clean,
    [string]$LinkTag = "Link-3.1.5",
    [string]$Config = "Release"
)

# Any failing cmdlet aborts the script (native tools are checked through $LASTEXITCODE below).
$ErrorActionPreference = "Stop"
# Repository root = parent of tools\; every path below is derived from it, so the script works
# from any current directory.
$root     = Split-Path -Parent $PSScriptRoot
$linkRoot = Join-Path $root "src\AbletonLink"
$external = Join-Path $linkRoot "external"
$build    = Join-Path $linkRoot "build"
$native   = Join-Path $root "src\CsoundVisual\native"
# Prefer the standard CMake install location; otherwise rely on cmake being on PATH (e.g. the
# copy bundled with Visual Studio when run from a developer prompt).
$cmake    = "C:\Program Files\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) { $cmake = "cmake" }

# Prints a cyan "==> ..." progress header so each phase is easy to spot in the console output.
function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

# Downloads $url into a fresh temporary folder, extracts it there and moves the single top-level
# folder of the archive to $dest.
# GitHub source zips always wrap their content in one folder named "<repo>-<ref>"; moving that
# folder (instead of extracting straight into $dest) gives $dest a predictable layout whatever the
# tag or commit. The temporary folder lives under external\ (same volume) so Move-Item is a rename.
function Fetch($url, $dest) {
    $tmp = Join-Path $external ("dl_" + [guid]::NewGuid().ToString("N").Substring(0, 8))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp "src.zip"
    Write-Host "    $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath (Join-Path $tmp "x")
    $top = Get-ChildItem (Join-Path $tmp "x") -Directory | Select-Object -First 1
    Move-Item $top.FullName $dest
    Remove-Item -Recurse -Force $tmp
}

# --- 1. sources -----------------------------------------------------------------
# Each dependency is fetched only if a known header is missing, so re-runs work offline.
New-Item -ItemType Directory -Force $external | Out-Null
$linkDir = Join-Path $external "link"
$asioDir = Join-Path $external "asio"
if (-not (Test-Path (Join-Path $linkDir "include\ableton\Link.hpp"))) {
    Step "Downloading Ableton Link $LinkTag (GPL-2.0-or-later)"
    Fetch "https://github.com/Ableton/link/archive/refs/tags/$LinkTag.zip" $linkDir
}
if (-not (Test-Path (Join-Path $asioDir "asio\include\asio.hpp"))) {
    Step "Looking up the asio-standalone commit referenced by $LinkTag"
    # The GitHub contents API lists the entries of Link's "modules" folder at that tag; a
    # submodule entry's "sha" is the exact asio commit Link was tested with (unauthenticated
    # call, subject to GitHub's anonymous rate limit).
    $info = Invoke-RestMethod -Uri "https://api.github.com/repos/Ableton/link/contents/modules?ref=$LinkTag" -UseBasicParsing
    $sha = ($info | Where-Object { $_.name -eq "asio-standalone" }).sha
    if (-not $sha) { throw "could not find the asio-standalone submodule commit" }
    Write-Host "    asio commit $sha"
    Fetch "https://github.com/chriskohlhoff/asio/archive/$sha.zip" $asioDir
}

# --- 2. build ---------------------------------------------------------------------
if ($Clean -and (Test-Path $build)) { Remove-Item -Recurse -Force $build }
# Multi-config Visual Studio generator: the configuration is picked at build time (--config),
# and outputs land in build\bin\<Config>\ (set by src\AbletonLink\CMakeLists.txt).
Step "Configuring (Visual Studio 2022, x64)"
& $cmake -S $linkRoot -B $build -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { throw "cmake configure failed" }
Step "Building $Config"
# Arguments after "--" go to MSBuild: parallel build, no logo, minimal verbosity.
& $cmake --build $build --config $Config -- /m /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$bin = Join-Path $build "bin\$Config"
$dll = Join-Path $bin "CsoundVisualLink.dll"
$peer = Join-Path $bin "link_peer_test.exe"
if (-not (Test-Path $dll)) { throw "CsoundVisualLink.dll was not produced" }

# --- 3. smoke test: two peers ---------------------------------------------------
if (-not $SkipTests) {
    Step "Two-peer smoke test (4 s, silent)"
    # Peer A starts at 120 BPM; peer B starts at 90 BPM and, 2 s in, proposes 133 BPM
    # ("--tempo-at <seconds>:<bpm>"). If the session works, A must end at 133 too.
    # Each peer prints one grid line "G <time us> <peers> <tempo> <beat> <phase> <playing>" every
    # 100 ms to stdout (see src\AbletonLink\test\link_peer_test.c), redirected to
    # build\peerA.txt / peerB.txt and parsed below (field 2 = number of other peers seen,
    # field 3 = session tempo). Other line types (READY, TEMPO, END) are filtered out.
    $outA = Join-Path $build "peerA.txt"
    $outB = Join-Path $build "peerB.txt"
    $a = Start-Process -FilePath $peer -ArgumentList "--bpm 120 --seconds 4" -RedirectStandardOutput $outA -NoNewWindow -PassThru
    $b = Start-Process -FilePath $peer -ArgumentList "--bpm 90 --seconds 4 --tempo-at 2:133" -RedirectStandardOutput $outB -NoNewWindow -PassThru
    $a.WaitForExit(); $b.WaitForExit()
    # Peers are counted while both run (whichever exits first leaves the other alone at the end).
    $linesA = Get-Content $outA | Where-Object { $_ -like "G *" }
    $linesB = Get-Content $outB | Where-Object { $_ -like "G *" }
    $sawA = @($linesA | Where-Object { ($_ -split " ")[2] -eq "1" }).Count -gt 0
    $sawB = @($linesB | Where-Object { ($_ -split " ")[2] -eq "1" }).Count -gt 0
    $lastA = ($linesA | Select-Object -Last 1) -split " "
    Write-Host ("    peer A saw peer B: {0}, final tempo {1}" -f $sawA, $lastA[3])
    Write-Host ("    peer B saw peer A: {0}" -f $sawB)
    if (-not $sawA -or -not $sawB) { Write-Warning "the peers did not see each other (firewall? network adapter?)" }
    # Failures are only warnings: a blocked firewall should not prevent producing the DLL.
    elseif ([math]::Abs([double]$lastA[3] - 133) -gt 0.01) { Write-Warning "tempo did not converge to 133" }
    else { Write-Host "    OK: both peers see each other and share 133 BPM" -ForegroundColor Green }
}

# --- 4. copy to the app ---------------------------------------------------------
# CsoundVisual.csproj includes native\CsoundVisualLink.dll (only if it exists) and copies it to
# the output folder on the next build/publish; this script does not touch bin\.
New-Item -ItemType Directory -Force $native | Out-Null
Copy-Item $dll $native -Force
Step "Copied to $native\CsoundVisualLink.dll"
