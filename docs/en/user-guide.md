# User guide

Every button, card and control shows a **help tip** when you rest the mouse on it.

## The window

| Area | Contents |
|---|---|
| Top bar | New, Open, Save · **Tocar** (F5), Detener (Shift+F5), **Grabar** (Ctrl+Shift+R), 3D, **Tools** (piano roll, step sequencer), Renderizar (Ctrl+R) · status, time and recording clock · side panel (Ctrl+B). |
| Editor | Tabs (• = unsaved, green dot = playing), highlighted code, hover help card. |
| Console | Csound messages. Click an error with a line number to jump to that line. |
| Keyboard | On-screen piano (can be hidden from the MIDI card). |
| Side panel | Output (scope, spectrum, meters) · Live controls · Watch · Live event · MIDI · Audio · Tables · Examples. |
| Status bar | Csound version · quick help · **DSP %** and **dropouts** · line and column. |

## Shortcuts

| Key | Action |
|---|---|
| F5 / Shift+F5 | Play / stop |
| Ctrl+Enter | Live coding: evaluate the selection or the `instr` under the caret (starts playing if idle) |
| Ctrl+Shift+Enter | Send the score line (or selection) as a live event |
| Ctrl+Space | Completion |
| F1 | Manual page of the opcode under the caret |
| Ctrl+Shift+R | Record / stop recording |
| Ctrl+3 | 3D window ([3D views](visual-3d.md)) |
| Ctrl+P | Piano roll ([Music tools](music-tools.md)) |
| Ctrl+Shift+P | Step sequencer |
| Space (in the piano roll or the sequencer) | Play / stop that tool |
| Ctrl+R | Render to WAV |
| Ctrl+N / Ctrl+O / Ctrl+S / Ctrl+Shift+S | New tab / open / save / save as |
| Ctrl+B | Show or hide the side panel |
| Esc (while dragging a value) | Restore the value it had before the drag |

## Playing and controlling

* **Live controls**: every `chnk "name", …` or `chnget "name"` creates a slider. With
  `chnk … , 3, default, min, max` the range is exponential (good for frequencies).
* **MIDI learn**: click a slider's **MIDI** button and move a knob or fader on your controller.
  Right-click the button › *Quitar asignación MIDI* to remove it.
* **On-screen keyboard**: click to play (lower on the key = louder), drag for glissando. With
  focus on the keyboard: `A W S E D F T G Y H U J K O L P` play, `Z`/`X` change octave, `C`/`V`
  velocity. Notes from your controller are shown in green.
* **MIDI inputs** (MIDI card): enable the ports you want (e.g. *Impact LX61+*). The monitor shows
  what arrives. For an instrument to respond to MIDI use `massign`, and to get note and velocity
  in p4/p5 add `--midi-key=4 --midi-velocity-amp=5` to `<CsOptions>` (the demo does).
* **Live event**: type `i "Voice" 0 1 64 0.9 0.5` and press Enter.

## Music tools

The **Tools** button in the top bar opens the **piano roll** (Ctrl+P: draw notes on a grid and play them with an instrument of the document) and the **step sequencer** (Ctrl+Shift+P: beats with swing, mute/solo and Euclidean fills), plus **Open drum kit** (8 synthesised drums, instruments 1 to 8). If Csound is not in Play mode, the tool's ▶ starts it with the target document. What you make can be **inserted into the score** (a marked block that is replaced when exporting again and reloaded when the tool opens). Details in [Music tools](music-tools.md).

## Hover help and dragging values

See [Hover help & live values](editor-intelligence.md).

## Audio

Choose backend, output, latency and ksmps in the **Audio** card. Details in
[Audio engine & latency](audio-engine.md). In short:

* **WASAPI shared**: the normal choice; plays alongside everything else on Windows.
* **WASAPI exclusive**: lowest latency without ASIO (≈3 ms with the UR22mkII set to 48 kHz).
* **Csound · PortAudio**: for ASIO drivers.
* **Diagnóstico de audio**: what every device can do, without playing anything.
* **Examples** (toolbar button or Ctrl+E): browser with the ~1500 examples of the official Csound manual plus the installation's, by manual category and searchable; highlighted preview, Previous/Next, **Open in a tab**, **Play** and «Play on change» to audition many quickly. The example tab is replaced when you open another one (if unchanged), and the originals are never overwritten: «Save» asks for a new name.
* **Save** (Ctrl+S) overwrites the file; **Save as** (Ctrl+Shift+S) asks for a name and folder.
* Opening another file or example **while playing** stops the old one and starts the new one.
* **Word highlight**: with the caret on a word (e.g. `ishift`) every occurrence is marked; the status bar shows how many there are here and in other tabs; F3 / Shift+F3 jump to the next / previous one.
* **Enlarged watch**: click a watched variable to see it in the centre of the window with the rest dimmed; click outside or Esc to go back.
* **Settings** (gear button in the top bar, or Ctrl+,): **Audio** (engine, output, latency, ksmps, Sound panel and diagnostics; formerly in the side panel), interface **language** (Español / English; switches instantly and also picks the demo with comments in that language), colour theme, editor font size (Ctrl+wheel in the code too), word wrap, shortcuts to the Sound panel and audio diagnostics, recordings folder and versions.
* **Play switches Csound on**: until it is in Play mode nothing sounds, not even the MIDI keyboard. If you play notes while Csound is stopped, a bubble appears under «Play» («Csound must be in Play mode to make sound») and the button pulses.
* **Apariencia** (side-panel card): colour theme of the whole app (windows, editor, meters, 3D): **Azul deportivo** (sports blue, default), Grafito y naranja, Rojo carrera, Verde esmeralda and Ámbar clásico (the original look). Applied live and remembered.
* **Panel de sonido de Windows**: opens the classic Sound panel; *Playback › device › Properties › Advanced* changes the sample rate (48000 Hz for ≈3 ms exclusive).

Drag the window by its top bar; double-click maximizes or restores it. Size and position are remembered, and the window comes back to a visible screen if a monitor is gone.

## Keeping what you make

* **Grabar** records what you hear, live changes included, to
  `Documents\Csound Visual\Grabaciones\<file>_<date>.wav` (32-bit float). When it finishes,
  click the "✓ Grabación guardada" console line to show the file in Explorer; right-click
  **Grabar** › *Abrir carpeta de grabaciones*.
* **Renderizar** computes the whole file to WAV, faster than real time.

## Files and tabs

* Opening a file adds a tab; if it is already open, its tab is selected.
* Closing a tab or the app with changes asks *Guardar / No guardar / Cancelar*.
* Open files are reopened next time.
* Preferences live in `%APPDATA%\CsoundVisual\settings.json`.
