# VST3 plug-ins inside Csound Visual

Csound Visual can **host VST3 plug-ins**: Csound's output goes through a chain of your own effects (FabFilter, iZotope, Native Instruments, Plugin Alliance, Steinberg…) before it reaches the meters, the recorder and the audio device — like the inserts of a mixer channel. VST3 **instruments** can also be put in the chain: they add their sound to Csound's and play the same MIDI as Csound (MIDI ports and the on-screen keyboard). Each plug-in opens its **own editor window**.

This is the opposite direction of [Csound in Cubase](vst3-cubase.md): there Cubase hosts Csound; here Csound Visual hosts the plug-ins.

## Using it

1. **Ctrl+Shift+V** (or the «Plug-ins» entry, once it is in the toolbar/menu) opens the **VST3 plug-ins** window.
2. The first time, press **Scan**. Every installed VST3 module is inspected in a **separate process**, so a plug-in that crashes or hangs cannot take the app down (it is listed with an error instead). The result is cached; later scans only look at new or updated plug-ins. Right-click **Scan** › *Rescan everything* to start over. On the development PC the first full scan took about 3 minutes for 190 modules.
3. Search by name, vendor or type (*Fx*, *EQ*, *Reverb*, *Instrument*…), choose **All / Effects / Instruments**, pick a plug-in and press **Add to chain →** (or double-click it).
4. In the chain: **Bypass** skips a plug-in, **▲/▼** change the order (top = first), **✕** removes it, **Edit** opens the plug-in's own window. Click a plug-in row to see its **parameters** as sliders below (useful when a plug-in has no editor; double-click a slider = default value).
5. Press **Play** as usual. The console shows the chain at start: `🎛 VST3 plug-ins: Raum → Saturn 2 · 48000 Hz`.

The chain — order, bypass and **each plug-in's full state** (everything you set in its editor) — is saved in the settings and comes back at the next start. Plug-in windows stay open when the chain window is closed; they belong to the main window.

## Where the plug-ins act

```
Csound (csoundPerformKsmps) ─► 0dbfs scaling ─► [VST3 chain, channels 1-2] ─► meters · scope · spectrum · WAV recorder ─► device
```

* The chain runs **once per control block** (`ksmps` frames) on the audio thread, right after Csound computes the block. Meters, scope, spectrum, 3D views and **Record** all see the processed sound.
* **Only with the app's own engine «Windows · WASAPI»** (shared or exclusive). With Csound's own audio modules (PortAudio, ASIO, Csound WASAPI) the audio never passes through the app, and **Render to WAV** is an offline Csound run: in those cases the chain is skipped and the console says so.
* Channels 1 and 2 go through the chain. A mono orchestra is processed as dual mono and folded back; channels 3 and above are left as they are.
* **Instruments** add their output at their place in the chain (an effect after them processes them too) and receive note on/off, CC, pitch bend and channel pressure from the app's MIDI hub. CCs/pitch bend reach the plug-in through its MIDI mapping (IMidiMapping) when it offers one.
* Plug-in **latency** (look-ahead limiters, linear-phase EQs) is shown in the chain but not compensated: there is nothing to align it with.
* The host reports 120 BPM, 4/4, playing, to plug-ins that read the transport.
* Tiny `ksmps` values (1–8) make each plug-in run on tiny blocks: it works, but costs more CPU. 32–128 is a good range.

## Plug-ins tested on the development PC (2026-11-04)

All loaded, processed a 440 Hz sine, saved and restored their state, opened their editor window (checked visible and sized, captured with PrintWindow, closed with WM_CLOSE):

| Plug-in | Type | Notes |
|---|---|---|
| FabFilter Pro-Q 3 | EQ | +24 dB band set from the host → editor shows it, output −15.1 → −5.0 dBFS RMS |
| FabFilter Saturn 2 | Distortion | |
| Native Instruments Supercharger, Raum | Dynamics, reverb | single-component plug-ins |
| iZotope Neoverb | Reverb | 272 samples latency |
| PSP VintageWarmer2 | Dynamics | 639 samples latency |
| Plugin Alliance bx_clipper | Clipper | |
| Steinberg Retrologue | **Instrument** | note from the host → −11 dBFS peak |
| Sonic Charge Microtonic | **Instrument** | |
| Xfer Serum 2 FX | Effect | Serum 2 (instrument) is in the same module |

Through the real WASAPI engine with the demo (volume at 5 %), Raum → Saturn 2 raised the recorded RMS from −50.4 to −47.0 dBFS (the reverb tail), DSP 6 %, 0 dropouts. In the real app: the saved chain is restored at start, «Edit» (invoked through UI Automation) opens Raum's window owned by the main window, and closing the app saves the plug-in state.

**Known problem: Sylenth1** (LennarDigital 3.0.7) crashes inside the app's process with `0xC0000409`, fail-fast code 57 (most likely the **CET shadow stack** that .NET 9/10 apps enable by default rejecting its copy protection's return-address tricks) while the Steinberg validator and the native test host load it fine. The scanner isolates it («the plug-in crashed while being inspected»), so it never reaches the list. Building the app with `<CETCompat>false</CETCompat>` in `CsoundVisual.csproj` would most likely make it work (DAWs do not enable CET), at the cost of that hardening for the whole app — it is not done by default; the plug-in manager (below) switches Sylenth1 off automatically.

`CsoundVisual.vst3` (our own plug-in) is deliberately **not offered**: the app already is Csound, and loading the plug-in would put a second Csound into the same process.

## Building the native bridge

The hosting code is a small C++ DLL, `CsoundVisualVstHost.dll` (`src/VstHost`, VST3 SDK 3.8 under MIT, static CRT):

```
powershell -ExecutionPolicy Bypass -File tools\build_vsthost.ps1
```

It configures CMake + Ninja with the Visual Studio x64 tools, builds the DLL and `vsthost_native_test.exe`, and copies the DLL to `src\CsoundVisual\native\`, from where the app's build copies it next to `CsoundVisual.exe`. It reuses the SDK downloaded for the plug-in (`src\CsoundVST\external\vst3sdk`; run `tools\build_vst.ps1` once if it is missing). **Without the DLL the app runs normally**; the plug-in window explains that the host is disabled.

## Tests (no UI, nothing audible unless stated)

```
CsoundVisual.exe --vst3-list [full]
CsoundVisual.exe --vsthost-test "<module.vst3>" [class name] [seconds] [--set "Param title=0.9"] [--editor shot.bmp]
CsoundVisual.exe --vsthost-engine-test "<a.vst3>|<b.vst3>" [seconds ≤ 3]
src\VstHost\build\bin\vsthost_native_test.exe "<module.vst3>" [class id]
```

* `--vst3-list` scans (cache) and prints every plug-in.
* `--vsthost-test` loads a plug-in, processes a sine in memory and prints peak/RMS before/after, its parameters, a state round trip into a second instance and, with `--editor`, opens its window, captures it and closes it with WM_CLOSE.
* `--vsthost-engine-test` plays the demo **quietly** (5 % volume) twice through the real WASAPI engine — without and with the chain — records both runs and compares the levels.
* `vsthost_native_test` does the same load/process without .NET (to tell a plug-in problem from a host problem).

## How it works (for developers)

* **`src/VstHost/source/vsthost.cpp`** — plain C API (`vh_*`) over the SDK hosting helpers: module loading (shared per path), component + controller (single-component or separate, connected through `IConnectionPoint`, controller synced with `setComponentState`), stereo bus arrangement with fallback to what the plug-in proposes, `HostProcessData` buffers, an `IComponentHandler` whose `performEdit` queues parameter changes for the processor, MIDI events through `IEventList`, state blob `CVS1` (component + controller state), editor window (`IPlugView` attached to a top-level `HWND` owned by the main window, `IPlugFrame::resizeView`, content scale from the monitor DPI). `process()` and plug-in creation run under SEH: a plug-in that crashes while processing is disabled («crashed while processing») instead of closing the app.
* **Threads**: everything but `vh_process_stereo` runs on the UI thread (Avalonia's UI thread pumps the editor windows — no extra UI thread). Parameter changes and MIDI go through small spin-locked inboxes that the audio thread drains with a *try*-lock, so it never waits.
* **`Integration/VstChain.cs`** — the ordered slots; every change publishes an immutable array that the audio thread reads once per block; a removed plug-in is freed only after the audio thread left the block that might use it. When a run starts at another sample rate, the performance thread asks the UI thread (dispatcher, ≤ 5 s) to re-prepare the plug-ins.
* **`Engine/CsoundEngine.Inserts.cs`** — `IAudioInsertChain`, `PrepareInserts` (app WASAPI path), `SkipInserts` (Csound-driven audio), `ApplyInserts` (in `Step`, before the meters and the recorder).
* **`Integration/VstScanner.cs`** — folders `%CommonProgramFiles%\VST3` and `%LOCALAPPDATA%\Programs\Common\VST3`, worker processes (`--vst3-scan-worker`), cache `%LOCALAPPDATA%\CsoundVisual\vst3-cache.json`.
* **`Views/PluginChainWindow.cs`**, **`ViewModels/MainViewModel.Plugins.cs`** (`OpenPluginChainCommand`, `ShowPluginChain`, `InitPluginHost`, `ShutdownPluginHost`), settings `PluginChain` in `settings.json`.

## Plug-in manager (black list)

Like Cubase, Csound Visual keeps a list of plug-ins to ignore: **Settings › VST3 plug-ins** lists every module found by the scan with an *active* check box.

* A module that **fails the scan** (for example Sylenth1, which crashes inside .NET 10 apps) is switched off automatically and shows why.
* A module that **crashes the app** while loading or while its window opens is switched off at the next start (a marker file `%LOCALAPPDATA%\CsoundVisual\vst-loading.txt` survives the crash), and the console says so.
* Switched-off modules are not offered in the chain window and are skipped when the saved chain is restored. Tick the box to try one again (a module that failed is re-inspected at the next *Scan*); «Switch all on» resets the list.
