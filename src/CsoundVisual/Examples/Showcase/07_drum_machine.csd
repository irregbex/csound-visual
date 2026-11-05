<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 07 - DRUM MACHINE FROM SCRATCH  (endless)

  BIG IDEA
    Every sound here is synthesised from oscillators and noise - no samples:
    the kick is a sine whose pitch falls in 80 ms, the hi-hat is six detuned square
    waves (the TR-808 recipe), the snare is two tones plus filtered noise.
    The rhythms are EUCLIDEAN: "spread k hits as evenly as possible over 16 steps",
    one line of maths that produces many of the world's traditional rhythms.
    A sequencer with swing, an acid bass and chord stabs turn it into a track
    that arranges itself (intro, groove, breakdown, fills) forever.

  LISTEN FOR
    - The 16-step grid with swing (every second 16th is late).
    - Density changes the number of euclidean hits per voice: the groove thickens.
    - A tom fill at the end of every 8 bars; a breakdown without kick every 32 bars.
    - Drive saturates the whole bus like an analogue mixer pushed into the red.

  SLIDERS
    tempo   : BPM.
    swing   : 0 = straight, 0.4 = heavy shuffle.
    density : how busy the patterns are (euclidean hits per bar).
    drive   : bus saturation (0 = clean, 1 = crushed).
    space   : echo and reverb on stabs and claps.

  MIDI: no.   Headphones: optional (best on speakers with some bass).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "tempo",   1, 2, 122,  80, 160
chnk "swing",   1, 2, 0.12, 0, 0.4
chnk "density", 1, 2, 0.6,  0, 1
chnk "drive",   1, 2, 0.3,  0, 1
chnk "space",   1, 2, 0.4,  0, 1
chnset 122,  "tempo"
chnset 0.12, "swing"
chnset 0.6,  "density"
chnset 0.3,  "drive"
chnset 0.4,  "space"

giSine ftgen 0, 0, 16384, 10, 1
gaBusL init 0          ; drum + synth bus (gets the drive)
gaBusR init 0
gaFxL  init 0          ; echo/reverb send
gaFxR  init 0
gkBar  init 0

; bass line (semitones above A1) and chord roots for a 4-bar loop
giBass[] fillarray 0, 0, 12, 0, 10, 0, 7, 0,   3, 0, 15, 0, 10, 7, 0, 5
giRoots[] fillarray 0, 0, 5, 3        ; Am7, Am7, Dm7, Cmaj7-ish

; is step i a hit in a euclidean rhythm of k hits over n steps, rotated by r?
opcode Euclid, k, kkkk
  ki, kk, kn, krot xin
  xout (((ki + krot) * kk) % kn < kk ? 1 : 0)
endop

; ---------------------------------------------------------------- sequencer
instr Sequencer
  ktempo chnget "tempo"
  kswing chnget "swing"
  kdens  chnget "density"
  ktempo limit ktempo, 40, 250
  kdens  limit kdens, 0, 1
  kstep  init 0           ; 0..15 inside the bar
  kbar   init 0
  kclock init 1000        ; time since last step (s); large = fire at once
  kclock += 1 / kr
  ; a pair of 16ths lasts 2 x base; swing moves the second one later
  kbase  = 15 / ktempo
  kdur   = (kstep % 2 == 0 ? kbase * (1 + kswing) : kbase * (1 - kswing))
  if kclock >= kdur then
    kclock = 0
    kstep  = (kstep + 1) % 16
    if kstep == 0 then
      kbar += 1
    endif
    gkBar  = kbar
    ksec   = kbar % 32
    kbreak = (ksec >= 16 && ksec < 20 ? 1 : 0)       ; breakdown: bars 16-19
    kintro = (kbar < 4 ? 1 : 0)
    kfill  = (kbar % 8 == 7 && kstep >= 12 ? 1 : 0)   ; last beat of every 8 bars
    kacc   = (kstep % 4 == 0 ? 1 : 0.75)

    ; ---- kick
    kkick  = 3 + round(kdens * 2)
    if kbreak == 0 && kfill == 0 && Euclid(kstep, kkick, 16, 0) == 1 then
      schedulek "Kick", 0, 0.6, 0.9
    endif
    ; ---- snare / clap backbeat + euclidean ghost notes
    if kintro == 0 && kfill == 0 then
      if kstep == 4 || kstep == 12 then
        schedulek "Snare", 0, 0.4, 0.7
        if ksec >= 8 then
          schedulek "Clap", 0, 0.6, 0.45
        endif
      elseif kdens > 0.3 && Euclid(kstep, round(kdens * 4), 16, 3) == 1 then
        schedulek "Snare", 0, 0.2, 0.12
      endif
    endif
    ; ---- hats: closed euclidean, open on the off-beats
    if kstep % 4 == 2 && kdens > 0.35 then
      schedulek "Hat", 0, 0.45, 0.35, 0.35
    elseif Euclid(kstep, 6 + round(kdens * 10), 16, 0) == 1 then
      schedulek "Hat", 0, 0.1, 0.22 * kacc + random:k(0, 0.06), 0.045
    endif
    ; ---- percussion (cowbell/rim) for dense settings
    if kdens > 0.55 && kintro == 0 && Euclid(kstep, 5, 16, 2) == 1 then
      schedulek "Perc", 0, 0.3, 0.16 * kdens
    endif
    ; ---- tom fill
    if kfill == 1 then
      schedulek "Tom", 0, 0.5, 0.6, 7 - (kstep - 12) * 4
    endif
    ; ---- acid bass (euclidean 7/16), chord stabs (3 per bar)
    kroot  = giRoots[int(kbar / 1) % 4]
    if kintro == 0 && Euclid(kstep, 5 + round(kdens * 4), 16, 1) == 1 then
      knote = 33 + kroot + giBass[kstep]
      schedulek "Bass", 0, kbase * 1.6, knote, 0.5 * kacc + 0.12, kacc
    endif
    if kbar >= 2 && Euclid(kstep, 3, 16, 2) == 1 then
      schedulek "Stab", 0, 0.3, 57 + kroot, 0.18 + kbreak * 0.08
    endif
  endif
endin

; ---------------------------------------------------------------- drum voices
instr Kick
  aenv  expseg 1, 0.06, 0.6, p3 - 0.06, 0.001
  kfrq  expseg 170, 0.07, 52, p3 - 0.07, 45
  asig  poscil 1, kfrq, giSine
  aclk  butterhp rand:a(1), 2000
  aclk *= expseg(1, 0.008, 0.001, 1, 0.001)
  asig  = (asig * aenv + aclk * 0.25) * p4
  gaBusL += asig
  gaBusR += asig
endin

instr Snare
  aenv  expseg 1, p3, 0.001
  at1   poscil 0.5, 185 * expseg(1.3, 0.02, 1, 1, 1), giSine
  at2   poscil 0.3, 330, giSine
  atone = (at1 + at2) * expseg(1, 0.09, 0.001, 1, 0.001)
  anz   butterbp rand:a(1), 4200, 3800
  asig  = (atone + anz * aenv * 0.8) * p4
  gaBusL += asig * 0.9
  gaBusR += asig
endin

instr Clap
  ; three fast bursts then a short tail, like hands clapping slightly apart
  aenv  linseg 0, 0.001, 1, 0.008, 0.1, 0.001, 1, 0.008, 0.1, 0.001, 1, 0.15, 0.08, p3 - 0.18, 0
  anz   butterbp rand:a(1), 1200, 900
  asig  = anz * aenv * p4 * 1.4
  gaBusL += asig * 0.8
  gaBusR += asig
  gaFxL  += asig * 0.4
  gaFxR  += asig * 0.4
endin

instr Hat
  ; six detuned square waves (TR-808 hi-hat) through a high-pass filter
  iscale = 1.4
  a1 vco2 1, 205.3 * iscale, 2, 0.5
  a2 vco2 1, 304.4 * iscale, 2, 0.5
  a3 vco2 1, 369.6 * iscale, 2, 0.5
  a4 vco2 1, 522.7 * iscale, 2, 0.5
  a5 vco2 1, 540.0 * iscale, 2, 0.5
  a6 vco2 1, 800.0 * iscale, 2, 0.5
  amet = (a1 + a2 + a3 + a4 + a5 + a6) / 6
  amet butterbp amet, 10000, 6000
  amet butterhp amet, 7000
  aenv expseg 1, p5, 0.001, 1, 0.001
  asig = amet * aenv * p4 * 2.2
  gaBusL += asig
  gaBusR += asig * 0.8
endin

instr Perc
  a1 vco2 1, 540, 2, 0.5
  a2 vco2 1, 800, 2, 0.5
  asig butterbp a1 + a2, 900, 500
  asig *= expseg(1, p3, 0.001) * p4
  gaBusL += asig * 0.5
  gaBusR += asig
endin

instr Tom
  kfrq expseg 140 * semitone(p5), 0.12, 90 * semitone(p5), p3 - 0.12, 80 * semitone(p5)
  asig poscil p4, kfrq, giSine
  asig *= expseg(1, p3, 0.001)
  ipan = 0.3 + (p5 + 5) / 30
  gaBusL += asig * sqrt(1 - ipan)
  gaBusR += asig * sqrt(ipan)
endin

; ---------------------------------------------------------------- synths
instr Bass
  icps = mtof(p4)
  kcut expseg 300 + 2500 * p6, 0.15, 220, p3, 200
  asig vco2 p5, icps
  asig moogladder asig, kcut, 0.65
  asig *= linseg(1, p3 - 0.02, 1, 0.02, 0)
  asig = tanh(asig * 1.5)
  gaBusL += asig * 0.5
  gaBusR += asig * 0.5
endin

instr Stab
  ; minor 7th chord, quick filter envelope, sent to the echo
  icps = mtof(p4)
  a1 vco2 1, icps
  a2 vco2 1, icps * semitone(3) * cent(6)
  a3 vco2 1, icps * semitone(7) * cent(-5)
  a4 vco2 1, icps * semitone(10) * cent(4)
  kcut expseg 3500, p3, 400
  aL moogladder (a1 + a3) * 0.5, kcut, 0.3
  aR moogladder (a2 + a4) * 0.5, kcut, 0.3
  aenv expseg 1, p3, 0.01
  aL *= aenv * p5
  aR *= aenv * p5
  gaBusL += aL
  gaBusR += aR
  gaFxL  += aL
  gaFxR  += aR
endin

; ---------------------------------------------------------------- mix: drive, echo, reverb
instr Mixer
  kdrive chnget "drive"
  kspace chnget "space"
  ktempo chnget "tempo"
  kdrive port kdrive, 0.05
  kspace port kspace, 0.1
  ktempo limit ktempo, 40, 250
  ; tempo-synced ping-pong echo (dotted eighth)
  kdt  port 45 / ktempo, 0.3
  adt  interp kdt
  abL  delayr 2
  aeL  deltapi adt
  abR  delayr 2
  aeR  deltapi adt
  delayw aeL * 0.9                ; right line is fed by the left echo...
  delayw gaFxL + aeR * 0.45       ; ...and feeds back into the left one
  aeL  butterhp aeL, 300
  aeR  butterhp aeR, 300
  arL, arR reverbsc gaFxL * 0.6 + aeL * 0.3, gaFxR * 0.6 + aeR * 0.3, 0.7, 8000
  ; bus saturation
  kg   = 1 + 5 * kdrive
  aL   = tanh(gaBusL * kg) / (1 + kdrive * 1.2)
  aR   = tanh(gaBusR * kg) / (1 + kdrive * 1.2)
  aL  += (aeL * 0.5 + arL * 0.7) * kspace
  aR  += (aeR * 0.5 + arR * 0.7) * kspace
  out aL * 0.85, aR * 0.85
  clear gaBusL, gaBusR, gaFxL, gaFxR
endin

</CsInstruments>
<CsScore>
i "Sequencer" 0 z
i "Mixer"     0 z
</CsScore>
</CsoundSynthesizer>
