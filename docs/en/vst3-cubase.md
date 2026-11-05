# Csound inside Cubase (VST3)

Csound Visual comes with two VST3 plug-ins that carry **Csound 7 inside**:

| Plug-in | Where it goes in Cubase | What it does |
|---|---|---|
| **Csound Visual Instrument** | Instrument track | A synth played via MIDI. Default: «Velvet Poly», a polyphonic pad with chorus and reverb. |
| **Csound Visual FX** | Insert of an audio track | An effect on the track's audio. Default: «Wide Echo Space», modulated ping-pong delay + chorus + reverb. |

The point: **any orchestra you write in Csound Visual can be sent to the plug-in** with the **Send to DAW** button, and it plays inside your Cubase project, with your tracks, your mix and your effects.

## Installation

They are already installed in `%LOCALAPPDATA%\Programs\Common\VST3\CsoundVisual.vst3` (the per-user VST3 folder, scanned by Cubase automatically). The plug-in is self-contained: it carries its own copy of Csound.

To rebuild: `powershell -ExecutionPolicy Bypass -File tools\build_vst.ps1` (with Cubase closed — Windows locks a loaded plug-in).

## Step by step in Cubase 15

1. Start Cubase. If the plug-ins don't show up: **Studio › VST Plug-in Manager**, check that `C:\Users\<you>\AppData\Local\Programs\Common\VST3` is among the VST 3 paths and press **Rescan**.
2. Create an **instrument track** and choose **Csound Visual Instrument** (vendor «Csound Visual»). Play your keyboard: the pad should sound.
3. In Csound Visual open an orchestra (the demo, an example or a showcase piece) and press **Send to DAW** › pick the track. The plug-in's Csound messages appear in the app's console.
4. The app's output card turns **green** and is titled **VST3 OUTPUT**: the scope, spectrum and meters show what plays **inside Cubase**, even when the app itself is not playing.
5. From then on **the app's sliders control the plug-in** (the status bar shows «⇄ track · orchestra»). In Cubase the same controls appear as plug-in parameters named after each channel (`cutoff`, `reverb`…): they can be **automated** and recorded.
6. For an effect: insert **Csound Visual FX** on an audio track and send an orchestra that reads the input with `inch 1, 2` (or `ain1, ain2 ins`).

## What the plug-in gives the orchestra

* **Audio**: Cubase's sample rate replaces the file's `sr`; stereo input (FX) and output.
* **MIDI**: notes, pitch bend, modulation (CC1), CC2, CC11, sustain (CC64) and aftertouch. `massign` and MIDI instruments work as in the app.
* **Tempo and transport** in channels readable with `chnget`: `HOST_BPM`, `HOST_PLAYING` (0/1), `HOST_PPQ` (position in quarter notes) and `HOST_SR`.
* **16 automatable parameters** named and ranged after the orchestra's first 16 `chnk` channels.
* **The project remembers the orchestra**: saving the Cubase project stores the code and the values.

## Limitations

* No window of its own: Cubase shows its generic parameter editor. The code is edited in Csound Visual and sent.
* Fixed latency of *ksmps* samples (Cubase compensates it): prefer a small ksmps (32 or 64).
* The score starts when the orchestra is compiled, not with Cubase's transport: use `HOST_PLAYING` and `HOST_PPQ` to sync.
* Sound-file paths inside an orchestra must be absolute.

Technical details (architecture, threads, protocol): `src\CsoundVST\README.md`.
