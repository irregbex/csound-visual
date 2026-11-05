<Cabbage>
form caption("Knob Synth") size(620, 330), colour(14, 20, 34), pluginId("cvk1")
groupbox bounds(10, 10, 300, 130), text("Oscillators"), colour(22, 32, 54), outlineColour(40, 70, 120), fontColour(200, 220, 255) {
  combobox bounds(12, 32, 92, 24), channel("wave"), items("Saw", "Square", "Pulse 25%", "Triangle"), value(1)
  rslider bounds(110, 28, 90, 96), channel("detune"), range(0, 30, 7, 0.6, 0.1), text("Detune ct"), trackerColour(90, 170, 255)
  rslider bounds(205, 28, 90, 96), channel("sub"), range(0, 1, 0.35), text("Sub osc"), trackerColour(90, 170, 255)
  checkbox bounds(12, 66, 92, 20), channel("chorus"), text("Chorus"), value(1), colour:1(90, 170, 255)
  label bounds(12, 100, 92, 14), text("3 voices + sub"), fontColour(120, 140, 180)
}
groupbox bounds(320, 10, 290, 130), text("Filter"), colour(22, 32, 54), outlineColour(40, 70, 120), fontColour(200, 220, 255) {
  rslider bounds(5, 28, 90, 96), channel("cutoff"), range(60, 12000, 1800, 0.3, 1), text("Cutoff Hz"), trackerColour(255, 150, 40)
  rslider bounds(100, 28, 90, 96), channel("res"), range(0, 0.9, 0.35), text("Resonance"), trackerColour(255, 150, 40)
  rslider bounds(195, 28, 90, 96), channel("envamt"), range(-1, 1, 0.5), text("Env amount"), trackerColour(255, 150, 40)
}
groupbox bounds(10, 148, 480, 70), text("Envelope"), colour(22, 32, 54), outlineColour(40, 70, 120), fontColour(200, 220, 255) {
  hslider bounds(10, 26, 225, 20), channel("attack"), range(0.002, 2, 0.01, 0.4, 0.001), text("Attack")
  hslider bounds(245, 26, 225, 20), channel("release"), range(0.02, 4, 0.6, 0.4, 0.01), text("Release")
  hslider bounds(10, 46, 225, 20), channel("decay"), range(0.02, 3, 0.4, 0.4, 0.01), text("Decay")
  hslider bounds(245, 46, 225, 20), channel("sustain"), range(0, 1, 0.6), text("Sustain")
}
rslider bounds(500, 148, 110, 70), channel("volume"), range(0, 1, 0.7), text("Volume"), trackerColour(80, 220, 140)
keyboard bounds(10, 226, 600, 96), value(48)
</Cabbage>
<CsoundSynthesizer>
<CsOptions>
-n -d -+rtmidi=NULL -M0 --midi-key-cps=4 --midi-velocity-amp=5
</CsOptions>
<CsInstruments>
/*
  CABBAGE 01 - KNOB SYNTH  (playable; a short demo phrase starts by itself)

  A Cabbage instrument: the <Cabbage> section above declares knobs, sliders, a
  combobox, a checkbox and a keyboard. Csound Visual draws them in the «Cabbage UI»
  card (and in a resizable window with «Window»). Each widget writes a channel that
  the orchestra reads with chnget, exactly as inside Cabbage, so this same file also
  opens in Cabbage and can be exported there as a VST3/AU plugin.

  PLAY IT
    Press Play, then click the on-screen keyboard of the Cabbage UI (or play your
    MIDI keyboard). Turn Cutoff and Resonance while notes sound; Env amount sweeps the
    filter with the envelope. Double-click a knob to reset it; Ctrl+drag = fine.
*/
sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

massign 0, "Voice"
giSine ftgen 0, 0, 8192, 10, 1
gaMix init 0

; One synth voice: three detuned oscillators + sub, a resonant ladder filter with its own envelope.
instr Voice
  ifreq = p4
  iamp  = p5
  iwave chnget "wave"
  iatt  chnget "attack"
  idec  chnget "decay"
  isus  chnget "sustain"
  irel  chnget "release"
  kdet  chnget "detune"
  ksub  chnget "sub"
  kcut  chnget "cutoff"
  kres  chnget "res"
  kenv  chnget "envamt"
  kvol  chnget "volume"

  ; vco2 modes: 0 saw, 10 square, 2 pulse (pw), 12 triangle
  imode = (iwave == 2 ? 10 : (iwave == 3 ? 2 : (iwave == 4 ? 12 : 0)))
  aenv madsr iatt, idec, isus, irel
  kcents = kdet / 1200
  a1 vco2 0.33, ifreq, imode, 0.25
  a2 vco2 0.33, ifreq * powoftwo(kcents), imode, 0.25
  a3 vco2 0.33, ifreq * powoftwo(-kcents), imode, 0.25
  asub poscil ksub * 0.5, ifreq / 2, giSine
  amix = a1 + a2 + a3 + asub

  ; Filter envelope: the same ADSR shape scales the cutoff up (or down with a negative amount).
  kfenv madsr iatt, idec, isus, irel
  kfc = limit(kcut * powoftwo(kenv * kfenv * 4), 30, 16000)
  afilt moogladder2 amix, kfc, kres
  asig = afilt * aenv * iamp * kvol * 0.5
  gaMix += asig
endin

; Output: optional stereo chorus, then to the speakers.
instr Out
  aL = gaMix
  aR = gaMix
  gaMix = 0
  kon chnget "chorus"
  kon port kon, 0.05
  ad1 lfo 0.003, 0.31
  ad2 lfo 0.003, 0.23
  acL vdelay3 aL, (0.012 + ad1) * 1000, 50
  acR vdelay3 aR, (0.015 + ad2) * 1000, 50
  aoL = aL + acL * kon * 0.7
  aoR = aR + acR * kon * 0.7
  out clip(aoL, 1, 0.98), clip(aoR, 1, 0.98)
endin

; Demo phrase so the patch is heard right away (then it waits for the keyboard).
instr Demo
  inotes[] fillarray 48, 55, 60, 63, 67, 63, 60, 55, 51, 58, 63, 67
  ii = 0
  while ii < lenarray(inotes) do
    schedule "Voice", ii * 0.25, 0.22, cpsmidinn(inotes[ii]), 0.7
    ii += 1
  od
  schedule "Voice", 3, 2.5, cpsmidinn(36), 0.6
  schedule "Voice", 3, 2.5, cpsmidinn(48), 0.5
  schedule "Voice", 3, 2.5, cpsmidinn(55), 0.5
  schedule "Voice", 3, 2.5, cpsmidinn(63), 0.5
endin
</CsInstruments>
<CsScore>
i "Out" 0 z
i "Demo" 0.1 1
</CsScore>
</CsoundSynthesizer>
