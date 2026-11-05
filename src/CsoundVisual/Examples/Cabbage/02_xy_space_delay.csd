<Cabbage>
form caption("XY Space Delay") size(560, 330), colour(10, 14, 24), pluginId("cvx2")
image bounds(0, 0, 560, 330), colour(16, 24, 40), outlineColour(50, 90, 160), outlineThickness(2), shape("rounded")
xypad bounds(12, 12, 300, 306), channel("time", "feedback"), rangeX(0.03, 1.2, 0.36, 0.5, 0.001), rangeY(0, 0.95, 0.55, 1, 0.001), text("X: delay time  ·  Y: feedback"), colour(6, 10, 20), ballColour(255, 140, 40)
label bounds(325, 14, 225, 15), text("Ping-pong delay, spinning echoes"), fontColour(140, 170, 220), align("left")
rslider bounds(325, 40, 75, 85), channel("tone"), range(300, 12000, 3500, 0.3, 1), text("Echo tone"), trackerColour(255, 140, 40)
rslider bounds(400, 40, 75, 85), channel("spin"), range(0, 3, 0.4, 0.5, 0.01), text("Spin Hz"), trackerColour(255, 140, 40)
rslider bounds(475, 40, 75, 85), channel("mix"), range(0, 1, 0.45), text("Wet"), trackerColour(80, 200, 255)
button bounds(325, 140, 110, 34), channel("freeze"), text("Freeze", "Frozen!"), colour:0(30, 44, 70), colour:1(255, 140, 40), fontColour:1(20, 10, 0)
button bounds(440, 140, 110, 34), channel("kick"), text("Ping!", "Ping!"), latched(0), colour:0(30, 44, 70), colour:1(80, 200, 255)
checkbox bounds(325, 186, 225, 18), channel("useinput"), text("Use the audio input"), value(0)
vslider bounds(325, 212, 40, 106), channel("level"), range(0, 1, 0.8), text("Level")
combobox bounds(375, 222, 175, 24), channel("source"), items("Plucked loop", "Drum loop", "Noise bursts"), value(1)
label bounds(375, 258, 175, 14), text("Drag the ball:"), fontColour(120, 140, 180), align("left")
label bounds(375, 276, 175, 14), text("right = longer delay,"), fontColour(120, 140, 180), align("left")
label bounds(375, 294, 175, 14), text("up = more echoes."), fontColour(120, 140, 180), align("left")
</Cabbage>
<CsoundSynthesizer>
<CsOptions>
-n -d -+rtmidi=NULL -M0
</CsOptions>
<CsInstruments>
/*
  CABBAGE 02 - XY SPACE DELAY  (an effect with an XY pad)

  Drag the ball of the XY pad: left/right sets the delay time, up/down the feedback.
  Freeze holds the echoes forever, Ping! fires a click into the delay, Spin turns the
  echoes around the stereo field. With «Process the audio input» checked it works on
  the live input (Settings › Audio › Input); otherwise it plays a built-in loop.
  It reads its widgets with cabbageGetValue, a Cabbage-only opcode: Csound Visual
  provides it automatically (see the console when you press Play).
*/
sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

giSine ftgen 0, 0, 8192, 10, 1
gaIn init 0

; Built-in sources so the effect is heard without an input.
instr Source
  ksrc cabbageGetValue "source"
  kuse cabbageGetValue "useinput"
  kping cabbageGetValue "kick"
  kmetro metro 4
  kmetro *= 1 - kuse            ; the built-in loop pauses while the input is used
  asrc = 0
  if ksrc == 1 then
    ; Plucked pentatonic loop
    knote = 0
    if kmetro == 1 then
      knote = 57 + int(random:k(0, 5)) * 3 + int(random:k(0, 2)) * 12
      event "i", "Pluck", 0, 0.8, knote
    endif
  elseif ksrc == 2 then
    ; Kick + hat pattern
    kstep init 0
    if kmetro == 1 then
      if kstep % 4 == 0 then
        event "i", "Kick", 0, 0.4
      endif
      event "i", "Hat", 0, 0.08
      kstep += 1
    endif
  else
    asrc = noise:a(0.3, 0) * lfo:k(1, 0.5, 3) * (1 - kuse)
  endif
  ain inch 1
  if trigger(kping, 0.5, 0) == 1 then
    event "i", "Pluck", 0, 0.5, 93
  endif
  gaIn = asrc + ain * kuse
endin

instr Pluck
  a1 pluck 0.35, cpsmidinn(p4), cpsmidinn(p4), 0, 1
  gaIn += a1 * linseg:k(1, p3, 0)
endin

instr Kick
  a1 poscil expon:k(0.9, p3, 0.001), expon:k(150, p3, 40), giSine
  gaIn += a1
endin

instr Hat
  a1 buthp noise:a(0.25, 0), 7000
  gaIn += a1 * expon:k(1, p3, 0.001)
endin

; The delay: ping-pong taps with a filtered feedback path and a slow stereo rotation.
instr Delay
  ktime   cabbageGetValue "time"
  kfb     cabbageGetValue "feedback"
  ktone   cabbageGetValue "tone"
  kspin   cabbageGetValue "spin"
  kmix    cabbageGetValue "mix"
  kfreeze cabbageGetValue "freeze"
  klevel  cabbageGetValue "level"
  ktime port ktime, 0.15
  ain = gaIn * (1 - kfreeze)
  gaIn = 0
  kfbk = kfreeze == 1 ? 1 : kfb
  afbL init 0
  afbR init 0
  adL vdelay3 ain + afbR * kfbk, a(ktime) * 1000, 2500
  adR vdelay3 afbL * kfbk, a(ktime) * 1000, 2500
  afbL tone adL, ktone
  afbR tone adR, ktone
  kpan = 0.5 + 0.5 * lfo:k(1, kspin)
  awL = afbL * kpan + afbR * (1 - kpan)
  awR = afbR * kpan + afbL * (1 - kpan)
  aL = (ain * (1 - kmix) + awL * kmix) * klevel
  aR = (ain * (1 - kmix) + awR * kmix) * klevel
  out clip(aL, 1, 0.98), clip(aR, 1, 0.98)
endin
</CsInstruments>
<CsScore>
i "Source" 0 z
i "Delay" 0 z
</CsScore>
</CsoundSynthesizer>
