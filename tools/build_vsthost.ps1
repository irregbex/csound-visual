<#
.SYNOPSIS
  Builds the VST3 hosting bridge CsoundVisualVstHost.dll (src\VstHost) and copies it to
  src\CsoundVisual\native\, from where the app's build copies it next to CsoundVisual.exe.

.DESCRIPTION
  1. Loads the Visual Studio 2022 x64 developer environment (vcvars64.bat).
  2. Configures (CMake + Ninja, Release) into src\VstHost\build and builds the DLL. The VST3 SDK
     is the one downloaded for the plug-in (src\CsoundVST\external\vst3sdk); run
     tools\build_vst.ps1 first if it is missing.
  3. Copies the DLL to src\CsoundVisual\native\CsoundVisualVstHost.dll. Rebuild the app (or copy
     the DLL by hand next to the exe) to use it. Without the DLL the app still runs; the
     «Plug-ins» window then explains that the VST3 host is not available.

.PARAMETER Clean
  Delete src\VstHost\build before configuring (full re-configure and rebuild).

.PARAMETER Config
  CMAKE_BUILD_TYPE for the single-config Ninja generator (default "Release").

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\build_vsthost.ps1
  powershell -ExecutionPolicy Bypass -File tools\build_vsthost.ps1 -Clean

.NOTES
  Called by: developers by hand (see BUILDING.md); tools\package.ps1 only warns when the DLL is
  missing. The app loads the DLL through P/Invoke (Integration\VstHostNative.cs) to host
  third-party VST3 effects in its «Plug-ins» chain.
  Requirements: Visual Studio 2022 with the C++ workload, CMake, Ninja, and the VST3 SDK cloned
  by tools\build_vst.ps1 (this script never downloads anything).
  Also builds vsthost_native_test.exe (a console test of the bridge) but does not run it.
  Nothing is installed system-wide: outputs stay in src\VstHost\build and
  src\CsoundVisual\native. The environment variables loaded from vcvars64.bat only affect this
  PowerShell session.
#>
param(
    [switch]$Clean,
    [string]$Config = "Release"
)

# Any failing cmdlet aborts the script (native tools are checked through $LASTEXITCODE below).
$ErrorActionPreference = "Stop"
# Repository root = parent of tools\; all paths are derived from it.
$root   = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $root "src\VstHost"
$build  = Join-Path $srcDir "build"
$native = Join-Path $root "src\CsoundVisual\native"

# Prints a cyan "==> ..." progress header for each phase.
function Step($text) { Write-Host ""; Write-Host "==> $text" -ForegroundColor Cyan }

# The bridge compiles against the same SDK checkout as the plug-in (src\VstHost\CMakeLists.txt
# points there), so fail early with a clear message instead of a CMake error.
if (-not (Test-Path (Join-Path $root "src\CsoundVST\external\vst3sdk\CMakeLists.txt"))) {
    throw "VST3 SDK missing: run tools\build_vst.ps1 once (it clones the SDK into src\CsoundVST\external\vst3sdk)."
}

Step "Loading the Visual Studio x64 build environment"
# Same approach as build_vst.ps1: locate vcvars64.bat with vswhere (fallback: the default VS 2022
# Community path), run it in a child cmd.exe and import the resulting environment (cl.exe,
# INCLUDE, LIB...) into this PowerShell session so that Ninja can find the compiler.
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
$envDump = cmd /c "`"$vcvars`" >nul 2>nul && set"
foreach ($line in $envDump) {
    $i = $line.IndexOf("=")
    if ($i -gt 0) { Set-Item -Path ("env:" + $line.Substring(0, $i)) -Value $line.Substring($i + 1) }
}

# Tool locations: standard install folders first, otherwise whatever is on PATH.
$cmake = "C:\Program Files\CMake\bin\cmake.exe"
if (-not (Test-Path $cmake)) { $cmake = "cmake" }
$ninja = Join-Path $env:LOCALAPPDATA "Programs\ninja\ninja.exe"
if (-not (Test-Path $ninja)) { $ninja = "ninja" }

if ($Clean -and (Test-Path $build)) {
    Step "Removing $build"
    Remove-Item -Recurse -Force $build
}
Step "Configuring ($Config)"
& $cmake -S "$srcDir" -B "$build" -G Ninja "-DCMAKE_MAKE_PROGRAM=$ninja" "-DCMAKE_BUILD_TYPE=$Config"
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed" }

Step "Building"
# CsoundVisualVstHost = the bridge DLL; vsthost_native_test = its console test (built, not run).
& $cmake --build "$build" --target CsoundVisualVstHost vsthost_native_test
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# src\VstHost\CMakeLists.txt sends runtime outputs to build\bin (no per-config subfolder).
$dll = Join-Path $build "bin\CsoundVisualVstHost.dll"
if (-not (Test-Path $dll)) { throw "DLL not found: $dll" }

Step "Copying to $native"
# CsoundVisual.csproj includes native\CsoundVisualVstHost.dll when it exists and copies it next
# to the exe on the next build/publish; bin\ itself is not touched here.
New-Item -ItemType Directory -Force $native | Out-Null
Copy-Item -Force $dll (Join-Path $native "CsoundVisualVstHost.dll")
Write-Host "  $(Join-Path $native 'CsoundVisualVstHost.dll')"
Write-Host ""
Write-Host "Done. Rebuild Csound Visual so the DLL is copied next to CsoundVisual.exe." -ForegroundColor Green
