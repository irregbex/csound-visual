<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 08 - THE ENDLESS GLISSANDO (Shepard-Risset)  (endless)

  BIG IDEA
    An auditory illusion you can only make with precise synthesis: ten tones
    spaced exactly one octave apart all glide upwards together. Their loudness
    follows a bell curve over the frequency range, so tones fade in at the bottom
    and fade out at the top. Your ear follows the motion... and the sound keeps
    rising forever without ever getting higher. (Jean-Claude Risset, 1960s.)
    Here three such stacks form a major chord, and a Risset "eternal accelerando"
    rhythm keeps speeding up in the same impossible way.

  LISTEN FOR
    - Try to notice the moment it "starts again": you can't.
    - Set direction to -1 for an endless descent, 0 to freeze the illusion.
    - The pulse: it seems to accelerate forever (same trick applied to tempo).

  SLIDERS
    speed      : glide speed in octaves per minute.
    direction  : 1 = up, -1 = down, in-between = slower.
    brightness : centre of the loudness bell (dark and low -> bright and high).
    harmony    : 0 = single stack, 1 = major chord of three stacks.
    pulse      : level of the eternal-accelerando pulse.

  MIDI: no.   Headphones: recommended.
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

chnk "speed",      1, 3, 6,   0.5, 40
chnk "direction",  1, 2, 1,  -1, 1
chnk "brightness", 1, 2, 0.5, 0, 1
chnk "harmony",    1, 2, 0.6, 0, 1
chnk "pulse",      1, 2, 0.4, 0, 1
chnset 6,   "speed"
chnset 1,   "direction"
chnset 0.5, "brightness"
chnset 0.6, "harmony"
chnset 0.4, "pulse"

giN     = 10                         ; octaves in each stack
giFmin  = 20                         ; lowest frequency of the cycle (Hz)
; partials 1, 2, 4 (octaves only, so they do not break the illusion)
giTone  ftgen 0, 0, 16384, 10, 1, 0.25, 0, 0.08
gkPhase init 0                       ; position in the cycle, 0..1 (one octave)
gkCentre init 6
gaRvbL init 0
gaRvbR init 0

instr Motion
  kspeed chnget "speed"
  kdir   chnget "direction"
  kbr    chnget "brightness"
  kspeed port kspeed, 0.2
  kdir   port kdir, 0.2
  ; octaves per second; the phase wraps every octave
  gkPhase = gkPhase + kdir * kspeed / 60 / kr
  gkPhase = gkPhase - floor(gkPhase)
  gkCentre port 3.5 + kbr * 4.5, 0.2        ; centre of the bell, in octaves above 20 Hz
endin

; one component of a stack: p4 = its octave index, p5 = transposition (semitones), p6 = gain
instr Component
  koct   = p4 + gkPhase + p5 / 12
  koct   = koct - floor(koct / giN) * giN          ; wrap 0..N
  kfreq  = giFmin * 2 ^ koct
  ; gaussian bell over log-frequency
  kdist  = (koct - gkCentre) / 1.6
  kamp   = exp(-0.5 * kdist * kdist)
  ; the wrap happens at the bottom/top where amplitude is ~0, so it is inaudible
  kamp   port kamp, 0.01
  ; the third and fifth stacks follow the "harmony" slider
  kharm  chnget "harmony"
  kharm  port kharm, 0.1
  kgain  = (p5 == 0 ? 1 : kharm)
  asig   poscil3 kamp * p6 * kgain, kfreq, giTone
  ipan   = 0.5 + (p4 - 4.5) / 12
  out asig * sqrt(1 - ipan), asig * sqrt(ipan)
  gaRvbL += asig * sqrt(1 - ipan) * 0.4
  gaRvbR += asig * sqrt(ipan) * 0.4
endin

; launches the components of the three stacks
instr Stacks
  iv = 0
  while iv < giN do
    schedule "Component", 0, p3, iv, 0, 0.09
    schedule "Component", 0, p3, iv, 4, 0.06       ; major third
    schedule "Component", 0, p3, iv, 7, 0.06       ; fifth
    iv += 1
  od
endin

; ---------------------------------------------------------------- eternal accelerando
; Risset rhythm: three pulse layers at rates R, 2R and 4R. R doubles every 20 s;
; meanwhile the fastest layer fades out and the slowest fades in, so at the end
; of the cycle everything is back where it started - but it never stops speeding up.
instr Pulse
  kph    init 0          ; tempo phase 0..1 (one tempo doubling)
  kth    init 0          ; beat phase of the slowest layer (cycles, kept in 0..1)
  kprev[] init 3
  ibase  = 1.1           ; slowest pulse rate at the start of the cycle (Hz)
  kth   += ibase * 2 ^ kph / kr
  kph   += 1 / 20 / kr
  if kph >= 1 then
    ; tempo wrapped: the slow layer takes over the beat of the old middle one
    kph -= 1
    kth  = kth / 2
    kprev[2] = kprev[1]
    kprev[1] = kprev[0]
    kprev[0] = kth
  endif
  kth = kth - floor(kth)
  kl = 0
  while kl < 3 do
    kcur = kth * 2 ^ kl
    kcur = kcur - floor(kcur)
    if kcur < kprev[kl] then
      ; loudness bell across the three layers: middle loudest, edges silent
      kamp = sin($M_PI * (kph + kl) / 3)
      schedulek "Tick", 0, 0.25, kamp, kl
    endif
    kprev[kl] = kcur
    kl += 1
  od
endin

; p4 = amplitude, p5 = layer (0 slowest)
instr Tick
  kpl   chnget "pulse"
  aenv  expseg 1, p3, 0.001
  awood butterbp rand:a(1), 1700 + p5 * 500, 350
  asig  = awood * expseg(1, 0.04, 0.001, 1, 0.001) * 3
  if p5 == 0 then
    asig += poscil(0.5, expseg(110, 0.08, 55, 1, 50)) * aenv
  endif
  asig *= p4 * kpl * 0.6
  ipan = 0.5 + (p5 - 1) * 0.3
  out asig * sqrt(1 - ipan), asig * sqrt(ipan)
  gaRvbL += asig * 0.3
  gaRvbR += asig * 0.3
endin

instr Space
  aL, aR reverbsc gaRvbL, gaRvbR, 0.85, 7000
  out aL, aR
  clear gaRvbL, gaRvbR
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.3) - aL, tanh(aR * 1.3) - aR
endin

</CsInstruments>
<CsScore>
i "Motion" 0 z
i "Stacks" 0 z
i "Pulse"  0 z
i "Space"  0 z
i "Master" 0 z
</CsScore>
</CsoundSynthesizer>
