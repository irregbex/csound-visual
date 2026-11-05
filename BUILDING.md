# Building Csound Visual

Csound Visual is a .NET 10 / Avalonia app (`src/CsoundVisual`) plus three optional native C++
parts. The app builds and runs without the native parts; each one only adds a feature.

| Part | Folder | Script | Adds |
|---|---|---|---|
| **App** (C#) | `src/CsoundVisual` | `dotnet build` | everything else |
| VST3 host bridge (C++) | `src/VstHost` | `tools\build_vsthost.ps1` | VST3 plug-ins after Csound |
| VST3 plug-in (C++) | `src/CsoundVST` | `tools\build_vst.ps1` | Csound inside Cubase, *Send to DAW* |
| Ableton Link bridge (C++) | `src/AbletonLink` | `tools\build_link.ps1` | Ableton Link sync |

## 1. Prerequisites

* **Windows 11 x64, 24H2 or later** (the app targets `net10.0-windows10.0.26100.0`).
* **[.NET 10 SDK](https://dotnet.microsoft.com/download)** (x64).
* **Csound 7** — either
  * install it from the [Csound releases](https://github.com/csound/csound/releases) (default
    folder `C:\Program Files\Csound7`), **or**
  * copy an installed `Csound7` folder to **`@Source\Csound7`** at the repository root. This is the
    layout used during development and the one the VST3 plug-in build expects. `@Source/` is
    git-ignored.

  The app looks for Csound in this order: `CSOUND_VISUAL_HOME` → a `Csound7` folder next to the
  exe → `@Source\Csound7` in any parent folder → `C:\Program Files\Csound7`.
* For the native parts: **Visual Studio 2022** with *Desktop development with C++* (MSVC x64,
  Windows SDK), **CMake** 3.25+ and **Ninja** (both ship with Visual Studio), and **git**.
* For the documentation and example tools: **Python 3.10+** (standard library only).

## 2. Get the MIDI 2.0 package

The Windows MIDI Services API (`Windows.Devices.Midi2` 0.99.92-preview.11) is published by
Microsoft on GitHub, not on nuget.org, and is too large for this repository. Download it once:

```powershell
powershell -ExecutionPolicy Bypass -File tools\get_midi2_package.ps1
```

It goes to `src\packages-local\` (the local NuGet source in `src\nuget.config`) and its SHA-256 is
checked.

## 3. Build and run the app

```powershell
dotnet build src\CsoundVisual -c Debug
src\CsoundVisual\bin\Debug\net10.0\CsoundVisual.exe
```

Or open `src\CsoundVisual.slnx` in Visual Studio 2022 / Rider / VS Code (C# Dev Kit).

## 4. Optional native parts

Run from the repository root, in this order (the VST3 host reuses the SDK cloned by the plug-in
script). Close Cubase before `build_vst.ps1`: Windows locks a loaded plug-in.

```powershell
powershell -ExecutionPolicy Bypass -File tools\build_vst.ps1        # clones the VST3 SDK, builds, validates and installs CsoundVisual.vst3
powershell -ExecutionPolicy Bypass -File tools\build_vsthost.ps1    # builds CsoundVisualVstHost.dll into src\CsoundVisual\native
powershell -ExecutionPolicy Bypass -File tools\build_link.ps1       # downloads Ableton Link 3.1.5 + asio, builds CsoundVisualLink.dll
```

* `build_vst.ps1` options: `-SkipTests`, `-NoInstall`, `-Clean`. It clones the Steinberg VST3 SDK
  (MIT) into `src\CsoundVST\external\vst3sdk`, bundles `@Source\Csound7` inside the plug-in, runs
  Steinberg's validator and installs to `%LOCALAPPDATA%\Programs\Common\VST3`.
* `build_link.ps1` downloads Ableton Link (GPL-2.0-or-later) and asio-standalone (BSL-1.0) into
  `src\AbletonLink\external`.
* After building a native DLL, rebuild the app: the csproj copies `native\*.dll` next to the exe
  when they exist.

The SDKs and build folders are git-ignored; the scripts fetch them from their official sources.

## 5. Release package

```powershell
powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-CsoundHome <Csound7 folder>] [-NoZip]
```

Creates `release\CsoundVisual-<version>-win-x64\` and its `.zip`: the self-contained app (no .NET
install needed), Csound 7 in a `Csound7` folder next to the exe, the examples, the HTML docs, the
VST3 plug-in (if built), `LICENSE`, `THIRD-PARTY-NOTICES.md` and `licenses\`. Upload the zip to a
GitHub release.

## 6. Documentation and examples

* `python tools\build_docs.py` — converts `docs/en` and `docs/es` (Markdown) into `docs/html`.
* `python -I tools\import_examples.py <path to a clone of github.com/csound/manual>` — re-imports
  the manual examples into `src/CsoundVisual/Examples/Manual` and rebuilds `Examples/index.json`
  (also picks up the Showcase, AudioFX and Cabbage galleries).
* `python tools\make_icon.py` — regenerates the app icon.

## 7. Command-line test modes

`CsoundVisual.exe` has headless modes used during development, for example:

| Command | What it does |
|---|---|
| `--audio-probe` / `--audio-test <shared\|exclusive> <latency> [device]` | Lists audio devices / plays a test through the engine |
| `--input-probe <device> [s]`, `--input-test <device> [s]` | Records from an input / runs the live input through Csound |
| `--breakpoint-test <csd> <line>` | Runs a performance with a breakpoint and prints the variables |
| `--vst3-list`, `--vsthost-test <plugin.vst3>`, `--vsthost-engine-test "<A>\|<B>"` | VST3 scan and host tests |
| `--link-test [s] --peer <link_peer_test.exe>` | Ableton Link test against a second peer |

Most of them print `PASS`/`FAIL` lines; see the docs pages for details.

## Repository layout

```
src/CsoundVisual/      the app (Views, ViewModels, Engine, Audio, Midi, Music, Visual3D, Cabbage…)
src/CsoundVisual/Examples/   example collection (Manual = Csound manual, GFDL)
src/CsoundVST/         VST3 plug-in that runs Csound inside a DAW
src/VstHost/           native bridge to host VST3 plug-ins
src/AbletonLink/       native bridge to Ableton Link
docs/en, docs/es       documentation (Markdown) · docs/html generated · docs/images screenshots
tools/                 build, packaging, docs and example scripts
licenses/              licence texts of third-party components
```
