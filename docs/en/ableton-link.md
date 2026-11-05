# Ableton Link

**Ableton Link** keeps several music programs in time over the local network: they share one **tempo**, one **beat grid** and the **phase inside the bar**, and (optionally) **play/stop**. Ableton Live, Bitwig Studio, Reason, Traktor, many iOS/Android apps and now Csound Visual speak it. Any participant can change the tempo and every other one follows within a few milliseconds; nobody is "master".

> **Cubase does not support Link.** With Cubase 15, use the Csound Visual VST3 plug-in instead: inside Cubase an orchestra gets the host tempo and position as `HOST_BPM`, `HOST_PLAYING` and `HOST_PPQ` (see [Csound in Cubase](vst3-cubase.md)).

## Turning it on

* **Settings › Sync** (Ctrl+,): **Enable Ableton Link**, **Beats per bar** (the *quantum*, default 4) and **Share play/stop (Start Stop Sync)**. All three are remembered; if Link was on when you closed the app, it is on again at the next start.
* The small **Link status** control (dot + «Link · 1 peer · 120.0 BPM») turns Link on/off with a click. Dot colours: grey = off, blue = on and alone, green = on with peers.

## Setup in Ableton Live (latest version)

1. **Preferences › Link, Tempo & MIDI** (Live 12; «Link/MIDI» in older versions): set **Show Link Toggle** to *Show*. Turn **Start Stop Sync** on if you want Live's Play/Stop to start/stop Csound Visual's piano roll and step sequencer (and the other way round).
2. Click the **Link** switch at the top left of Live's control bar. It shows «1 Link» when Csound Visual is in the session.
3. In Csound Visual turn Link on. The status shows «Link · 1 peer · <Live's tempo> BPM».
4. Change the tempo in Live: the status, every orchestra reading `LINK_BPM` and the open tool windows follow. Change it in the piano roll or step sequencer: Live follows.
5. Use the same **quantum** in both programs (Live's launch quantisation: «1 Bar» in 4/4 = 4 beats).

Both programs on the same computer work immediately. For another computer or a phone, they must be on the same network (Wi-Fi or cable, same subnet, multicast allowed).

## What the orchestra receives

While Csound plays and Link is on, the app writes these control channels **before every control cycle** (on Csound's own thread, without allocations):

| Channel | Value |
|---|---|
| `LINK_BPM` | session tempo; **0 while Link is off** (use it to fall back to your own tempo) |
| `LINK_BEAT` | beat position on the shared timeline |
| `LINK_PHASE` | position inside the bar: 0 … quantum (0 = the "1" Live counts) |
| `LINK_PLAYING` | 1 while the shared transport plays (with Start Stop Sync), else 0 |
| `LINK_PEERS` | number of other Link apps in the session |
| `LINK_QUANTUM` | beats per bar chosen in Settings › Sync |

The values are computed for the moment the block **will be heard**, not the moment it is computed: Link's host clock (µs) is mapped to Csound's score clock with a smoothed offset, plus the output latency the engine knows. With the app's own WASAPI engine this is the stream latency reported by Windows plus one and a half device periods; with PortAudio/ASIO (Csound drives the device) the latency is unknown and taken as 0, so sound may be a few ms late against Live.

```csound
kbpm  chnget "LINK_BPM"
kbeat chnget "LINK_BEAT"
kph   chnget "LINK_PHASE"
kbpm  = (kbpm > 0 ? kbpm : 110)         ; own tempo when Link is off
if changed(floor(kbeat)) == 1 then       ; a new beat started in this block
  schedulek "Click", 0, 0.05, (kph < 0.5 ? 1 : 0)
endif
```

The example **Showcase › 13 · Ableton Link pulse** (`Examples/Showcase/13_link_pulse.csd`) does it properly: it derives a sixteenth-note clock from `LINK_BEAT`, places every note at its exact offset inside the control block, accents the downbeat with `LINK_PHASE`, changes chord every bar and falls back to its own tempo slider when Link is off.

Note: Link shares the **phase**, not the beat number. Two peers can count bars from different origins (`LINK_BEAT` = 37.0 here, 5.0 in another app), but `LINK_PHASE` and the beat fraction always agree.

## Piano roll and step sequencer

With Link on, the tools' transport:

* **follows the session tempo** (the tempo box updates by itself) and a tempo typed in the tool becomes the session tempo — but merely opening a tool window never changes Live's tempo;
* **starts on the next bar** (phase 0 of the quantum), like a clip in Live, and stays **phase-locked**: on every scheduling pass (~4 ms) the scheduler re-anchors its beat → Csound-time mapping on Link's timeline, so tempo changes made by any peer are followed without drift;
* with **Start Stop Sync**: ▶ in the tool starts the shared transport (on the downbeat); Play in Live starts the open tools (on Live's start bar if it is still ahead, otherwise on the next bar); Stop on either side stops the other.

Notes already queued (the scheduler works 120 ms ahead) keep the old tempo if a peer changes it inside that window: at most one or two notes land slightly early or late at the exact moment of a tempo change.

## Firewall

Link uses UDP multicast (port 20808). The first time, Windows Firewall may ask whether Csound Visual may communicate on networks: allow **private networks** to sync with other computers or phones. Programs on the same computer work even without allowing it. In our tests on this PC no prompt appeared.

## Testing without Live

* `CsoundVisual.exe --link-test [seconds] [--peer path\to\link_peer_test.exe]`: silent end-to-end check (Csound plays zeros through WASAPI). A second process joins at 97 BPM, changes tempo, starts and stops the transport; the test checks peers, tempo convergence both ways, phase agreement at identical host times, the `LINK_*` channels as the orchestra sees them and a transport locked to Link.
* `link_peer_test.exe --bpm 97 --seconds 30 --tempo-at 10:133 --play-at 15 --stop-at 20 --start-stop`: a stand-alone Link peer that prints the timeline; useful with Live too.

## Building

`powershell -ExecutionPolicy Bypass -File tools\build_link.ps1` downloads Ableton Link 3.1.5 and the asio-standalone commit it references into `src\AbletonLink\external\`, builds `CsoundVisualLink.dll` (Link + its official C API `abl_link`, x64, static CRT, Release) and `link_peer_test.exe` with Visual Studio 2022, runs a two-peer smoke test and copies the DLL to `src\CsoundVisual\native\` (the csproj copies it next to the exe). **Without the DLL the app runs normally**: Link is shown as unavailable.

## Licence

Ableton Link is **GPL-2.0-or-later** (or a commercial licence from Ableton). Building it into Csound Visual for personal use is fine. Distributing the app together with `CsoundVisualLink.dll` makes that distribution subject to the GPL (source available, same licence), unless Ableton grants a proprietary licence (link-devs@ableton.com). The DLL is a separate, optional file, so the app can also be shipped without it.
