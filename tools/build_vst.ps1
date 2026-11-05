<#
.SYNOPSIS
  Builds, bundles, tests and installs the Csound Visual VST3 plug-in (src\CsoundVST).

.DESCRIPTION
  1. Clones the Steinberg VST3 SDK into src\CsoundVST\external\vst3sdk if it is missing.
  2. Loads the Visual Studio 2022 x64 developer environment (vcvars64.bat).
  3. Configures (CMake + Ninja, Release) into src\CsoundVST\build and builds the plug-in,
     the SDK validator and vsthost_test. The post-build step copies Csound 7 from
     @Source\Csound7 into CsoundVisual.vst3\Contents\Resources\Csound7.
  4. Unless -SkipTests: runs the SDK validator and vsthost_test on the built bundle.
  5. Copies the bundle to src\CsoundVST\dist\ and installs it into the per-user VST3 folder
     %LOCALAPPDATA%\Programs\Common\VST3\ (unless -NoInstall).

.PARAMETER SkipTests
  Skip step 4 (Steinberg validator + vsthost_test). Both targets are still built.

.PARAMETER NoInstall
  Do not copy the bundle into the per-user VST3 folder; it is only mirrored to
  src\CsoundVST\dist\ (which tools\package.ps1 picks up).

.PARAMETER Clean
  Delete src\CsoundVST\build before configuring (full re-configure and rebuild). The cloned
  SDK in src\CsoundVST\external\vst3sdk is kept.

.PARAMETER Config
  CMAKE_BUILD_TYPE for the single-config Ninja generator (default "Release"). The VST3 SDK
  places the bundle in build\VST3\<Config>\CsoundVisual.vst3.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build_vst.ps1
  powershell -ExecutionPolicy Bypass -File tools\build_vst.ps1 -SkipTests -NoInstall

.NOTES
  Called by: developers by hand (see BUILDING.md and src\CsoundVST\README.md). The SDK cloned
  here is also used by tools\build_vsthost.ps1, so run this script first on a fresh checkout.
  Requirements: git, Visual Studio 2022 with the C++ workload, CMake >= 3.25, Ninja
  (%LOCALAPPDATA%\Programs\ninja\ninja.exe or on PATH), and a Csound 7 copy in @Source\Csound7
  for the post-build step that embeds it into the bundle.
  What is installed outside the repository: only the bundle in the per-user folder
  %LOCALAPPDATA%\Programs\Common\VST3\CsoundVisual.vst3 (no admin rights, nothing under
  Program Files, no registry entries). Delete that folder to uninstall.
  Close any DAW that has the plug-in loaded first: Windows locks the loaded .vst3 DLL and the
  copy then fails.
#>
param(
    [switch]$SkipTests,
    [switch]$NoInstall,
    [switch]$Clean,
    [string]$Config = "Release"
)

# Any failing cmdlet aborts the script (native tools are checked through $LASTEXITCODE below).
$ErrorActionPreference = "Stop"
# Repository root = parent of tools\; all paths are derived from it.
$root    = Split-Path -Parent $PSScriptRoot
$vstDir  = Join-Path $root "src\CsoundVST"
$sdkDir  = Join-Path $vstDir "external\vst3sdk"
$build   = Join-Path $vstDir "build"
$dist    = Join-Path $vstDir "dist"

# Prints a cyan "==> ..." progress header for each phase.
function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

# --- 1. SDK ------------------------------------------------------------------
# Shallow clone (latest commit only, submodules shallow too) keeps the download small; the
# SDK's own submodules (base, pluginterfaces, public.sdk, vstgui4...) are required to build.
if (-not (Test-Path (Join-Path $sdkDir "CMakeLists.txt"))) {
    Step "Cloning the VST3 SDK (MIT) into $sdkDir"
    git clone --depth 1 --recursive --shallow-submodules https://github.com/steinbergmedia/vst3sdk.git "$sdkDir"
    if ($LASTEXITCODE -ne 0) { throw "git clone failed" }
}

# --- 2. Visual Studio environment ---------------------------------------------
Step "Loading the Visual Studio x64 build environment"
# Ninja (unlike the Visual Studio generator) needs cl.exe, link.exe, INCLUDE and LIB in the
# environment. vswhere.exe (always installed with VS 2017+) finds the newest VS that has the x64
# C++ tools; the hard-coded Community path is only a fallback.
$vcvars = $null
$vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if ($vsPath) { $vcvars = Join-Path $vsPath "VC\Auxiliary\Build\vcvars64.bat" }
}
if (-not $vcvars -or -not (Test-Path $vcvars)) {
    $vcvars = "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat"
}
if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found (install Visual Studio with the C++ workload)" }
# A batch file cannot change the environment of the PowerShell process, so run it in a child
# cmd.exe, print the resulting environment with "set" and copy every NAME=value line into this
# process. The change only lasts for this PowerShell session; nothing is persisted.
$envDump = cmd /c "`"$vcvars`" >nul 2>nul && set"
foreach ($line in $envDump) {
    $i = $line.IndexOf("=")
    if ($i -gt 0) { Set-Item -Path ("env:" + $line.Substring(0, $i)) -Value $line.Substring($i + 1) }
}

# Tool locations: the standard install folders first, otherwise whatever is on PATH (the VS
# environment loaded above also puts VS's bundled CMake and Ninja on PATH when the "C++ CMake
# tools" component is installed).
$cmake = "C:\Program Files\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) { $cmake = "cmake" }
$ninja = Join-Path $env:LOCALAPPDATA "Programs\ninja\ninja.exe"
if (-not (Test-Path $ninja)) { $ninja = "ninja" }

# --- 3. configure + build ---------------------------------------------------------
# Targets: CsoundVisual = the plug-in bundle (its post-build step, defined in
# src\CsoundVST\CMakeLists.txt, copies Csound 7 into Contents\Resources\Csound7); validator = the
# SDK's conformance checker; vsthost_test = src\CsoundVST\test\vsthost_test.cpp.
if ($Clean -and (Test-Path $build)) {
    Step "Removing $build"
    Remove-Item -Recurse -Force $build
}
Step "Configuring ($Config)"
& $cmake -S "$vstDir" -B "$build" -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

Step "Building"
& $cmake --build "$build" --target CsoundVisual validator vsthost_test
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

$bundle = Join-Path $build "VST3\$Config\CsoundVisual.vst3"
if (-not (Test-Path $bundle)) { throw "Bundle not found: $bundle" }

# --- 4. tests ---------------------------------------------------------------------
if (-not $SkipTests) {
    Step "Steinberg VST3 validator"
    $validator = Join-Path $build "bin\$Config\validator.exe"
    # The SDK's output layout has changed between versions; fall back to searching the tree.
    if (-not (Test-Path $validator)) { $validator = Get-ChildItem -Recurse -Filter validator.exe $build | Select-Object -First 1 -ExpandProperty FullName }
    & $validator "$bundle"
    if ($LASTEXITCODE -ne 0) { throw "Validator reported failures" }

    Step "vsthost_test"
    # Loads the bundle in-process and runs the audio/parameter/state/IPC checks; exit code 0 means
    # all passed. It creates and removes descriptor files under
    # %LOCALAPPDATA%\CsoundVisual\vst-instances while it runs (no sound is played).
    $test = Get-ChildItem -Recurse -Filter vsthost_test.exe $build | Select-Object -First 1 -ExpandProperty FullName
    & $test "$bundle"
    if ($LASTEXITCODE -ne 0) { throw "vsthost_test failed" }
}

# --- 5. dist + install ------------------------------------------------------------
# Copies the bundle folder $from to <$toParent>\CsoundVisual.vst3 and returns the target path.
# A .vst3 bundle is a folder (Contents\x86_64-win\CsoundVisual.vst3 + Resources), so it is
# mirrored with robocopy rather than copied as a single file.
function Copy-Bundle($from, $toParent) {
    New-Item -ItemType Directory -Force $toParent | Out-Null
    $target = Join-Path $toParent "CsoundVisual.vst3"
    # robocopy /MIR mirrors the bundle (removes stale files); exit codes < 8 mean success.
    robocopy "$from" "$target" /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copy to $target failed (is Cubase running with the plug-in loaded?)" }
    # Reset robocopy's non-zero "success" code (1 = files copied) so the script does not end
    # with a misleading non-zero exit code.
    $global:LASTEXITCODE = 0
    return $target
}

Step "Copying to dist"
$d = Copy-Bundle $bundle $dist
Write-Host "  $d"

if (-not $NoInstall) {
    Step "Installing into the per-user VST3 folder"
    # Per-user VST3 location defined by the VST3 spec on Windows; hosts such as Cubase scan it in
    # addition to C:\Program Files\Common Files\VST3. Writing here needs no admin rights.
    $vst3User = Join-Path $env:LOCALAPPDATA "Programs\Common\VST3"
    $i = Copy-Bundle $bundle $vst3User
    Write-Host "  $i"
}

Write-Host ""
Write-Host "Done. In Cubase: Studio > VST Plug-in Manager > Rescan, then look for 'Csound Visual Instrument' (Instruments) and 'Csound Visual FX' (Insert effects), vendor 'Csound Visual'." -ForegroundColor Green
