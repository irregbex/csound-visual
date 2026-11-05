<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 01 - GENERATIVE AMBIENT  (endless)

  BIG IDEA
    Csound is not only a synthesizer, it is a programming language: this piece
    composes itself while it plays. A "conductor" instrument chooses chords with a
    Markov chain (each chord has its own likely successors), writes a melody with a
    random walk over the scale, and schedules pads, plucks, bass and bells as new
    notes. No two runs are the same, and it never ends.

  LISTEN FOR
    - Chords that change every two bars but always sound "logical" (Markov choices).
    - A pluck melody that wanders step by step and lands on chord tones on the beat.
    - Rare FM bells and wind swells that keep the texture alive.

  SLIDERS
    density    : how many notes the melody and bells play (0 = almost only pads).
    brightness : opens the filters of pads and plucks (dark -> shimmering).
    tempo      : speed of the musical clock in BPM (chords and melody follow it).
    space      : amount and length of the reverb and echo.

  MIDI: no.   Headphones: optional.
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

; ---------------------------------------------------------------- controls
chnk "density",    1, 2, 0.55, 0, 1
chnk "brightness", 1, 2, 0.5,  0, 1
chnk "tempo",      1, 2, 72,   40, 120
chnk "space",      1, 2, 0.6,  0, 1
; chnk declares the slider; chnset gives the channel its starting value
chnset 0.55, "density"
chnset 0.5,  "brightness"
chnset 72,   "tempo"
chnset 0.6,  "space"

; ---------------------------------------------------------------- tables
giScale[] fillarray 0, 2, 4, 5, 7, 9, 11      ; major scale (D major, root below)
giRoot = 50                                   ; D3
; Markov matrix: row = current chord degree (I ii iii IV V vi), columns = probability
; of moving to each degree. Rows sum to 1.
giMarkov ftgen 0, 0, 64, -2, \
  0.00, 0.15, 0.10, 0.35, 0.15, 0.25, \
  0.15, 0.00, 0.10, 0.20, 0.35, 0.20, \
  0.10, 0.10, 0.00, 0.35, 0.10, 0.35, \
  0.35, 0.20, 0.10, 0.00, 0.20, 0.15, \
  0.45, 0.10, 0.10, 0.15, 0.00, 0.20, \
  0.15, 0.25, 0.10, 0.35, 0.15, 0.00
giSine ftgen 0, 0, 16384, 10, 1

; global effect buses
gaRvbL init 0
gaRvbR init 0
gaDlyL init 0
gaDlyR init 0
gkChord init 0          ; current chord degree (0..5)

; scale degree (can be > 7 or negative) -> MIDI note
opcode Deg2Midi, k, k
  kdeg xin
  koct = floor(kdeg / 7)
  kidx = kdeg - koct * 7
  kidx = (kidx < 0 ? kidx + 7 : kidx)
  xout giRoot + koct * 12 + giScale[kidx]
endop

; is a scale degree part of the current chord (root, 3rd, 5th, 7th)?
opcode IsChordTone, k, kk
  kdeg, kchord xin
  kd = (kdeg - kchord) % 7
  kd = (kd < 0 ? kd + 7 : kd)
  xout (kd == 0 || kd == 2 || kd == 4 || kd == 6 ? 1 : 0)
endop

; ---------------------------------------------------------------- the conductor
instr Conductor
  ktempo  chnget "tempo"
  kdens   chnget "density"
  ktempo  limit ktempo, 30, 200
  kstep   metro ktempo / 60 * 2            ; eighth-note clock
  kcount  init 0
  kchord  init 0
  kmel    init 9                           ; melody position in scale degrees
  kstepdur = 30 / ktempo

  if kstep == 1 then
    ; ---- new chord every 16 eighths (two bars)
    if kcount % 16 == 0 then
      if kcount > 0 then
        ; Markov choice of the next chord
        kpick random 0, 1
        kacc = 0
        knext = 0
        kj = 0
        while kj < 6 do
          kacc += tab:k(kchord * 6 + kj, giMarkov)
          if kpick < kacc then
            knext = kj
            kj = 6
          endif
          kj += 1
        od
        kchord = knext
      endif
      gkChord = kchord
      kcdur = kstepdur * 16
      ; pad voicing: root, 3rd, 5th, 7th, 9th spread over two octaves
      kv = 0
      while kv < 5 do
        kdeg = kchord + 7 + kv * 2
        knote Deg2Midi kdeg
        schedulek "Pad", random:k(0, 0.4), kcdur * 1.35, knote, 0.055
        kv += 1
      od
      kbass Deg2Midi kchord - 7
      schedulek "Bass", 0, kcdur * 1.1, kbass, 0.16
      ; a wind swell every 4 chords
      if kcount % 64 == 0 then
        schedulek "Wind", 0, kcdur * 2, 0.05
      endif
    endif

    ; ---- melody: random walk, probability set by density
    kplay = (kcount % 2 == 0 ? 0.25 + 0.6 * kdens : 0.05 + 0.55 * kdens * kdens)
    if random:k(0, 1) < kplay then
      kmove = round(gauss:k(1.6))
      kmove = (kmove == 0 ? (random:k(0, 1) < 0.5 ? -1 : 1) : kmove)
      kmel += kmove
      kmel = (kmel < 4 ? 6 : kmel)
      kmel = (kmel > 20 ? 17 : kmel)
      ; on strong beats pull the melody onto a chord tone
      if kcount % 4 == 0 then
        if IsChordTone(kmel, kchord) == 0 then
          kmel += 1
        endif
      endif
      knote Deg2Midi kmel + 7
      kacc = (kcount % 4 == 0 ? 1 : 0.7)
      schedulek "Pluck", 0, 4, knote, 0.22 * kacc
      ; sometimes an echo an octave up
      if random:k(0, 1) < 0.15 * kdens then
        schedulek "Pluck", kstepdur, 3, knote + 12, 0.08
      endif
    endif

    ; ---- rare bells on chord tones
    if random:k(0, 1) < 0.03 + 0.1 * kdens then
      kbd = kchord + 14 + 2 * int(random:k(0, 3.99))
      kbell Deg2Midi kbd
      schedulek "Bell", 0, 7, kbell, 0.06
    endif
    kcount += 1
  endif
endin

; ---------------------------------------------------------------- instruments
instr Pad
  ibright chnget "brightness"
  icps = mtof(p4)
  iamp = p5
  aenv linseg 0, p3 * 0.35, 1, p3 * 0.3, 1, p3 * 0.35, 0
  klfo poscil 1, random(0.05, 0.15), giSine, random(0, 1)
  kbr chnget "brightness"
  kbr port kbr, 0.1
  kcut = 250 * 2 ^ (kbr * 4.5) * (1 + 0.35 * klfo) + icps
  a1 vco2 iamp, icps * cent(random(-9, -3))
  a2 vco2 iamp, icps * cent(random(3, 9))
  a3 vco2 iamp * 0.7, icps, 12             ; triangle for body
  aL moogladder a1 + a3, kcut, 0.15
  aR moogladder a2 + a3, kcut, 0.15
  aL *= aenv
  aR *= aenv
  out aL * 0.6, aR * 0.6
  gaRvbL += aL * 0.7
  gaRvbR += aR * 0.7
endin

instr Pluck
  icps = mtof(p4)
  kbr chnget "brightness"
  ipos random 0.2, 0.8
  asig wgpluck2 0.3, p5, icps, 0.3, 0.45
  asig dcblock2 asig
  asig butterlp asig, 600 + 7000 * kbr * kbr + icps
  aenv linseg 1, p3 - 0.05, 1, 0.05, 0
  asig *= aenv
  aL = asig * sqrt(1 - ipos)
  aR = asig * sqrt(ipos)
  out aL, aR
  gaDlyL += aL * 0.35
  gaDlyR += aR * 0.35
  gaRvbL += aL * 0.5
  gaRvbR += aR * 0.5
endin

instr Bass
  icps = mtof(p4)
  aenv linseg 0, p3 * 0.2, 1, p3 * 0.5, 0.8, p3 * 0.3, 0
  asig poscil p5, icps, giSine
  asig += poscil(p5 * 0.25, icps * 2, giSine)
  asig *= aenv
  out asig, asig
  gaRvbL += asig * 0.15
  gaRvbR += asig * 0.15
endin

instr Bell
  icps = mtof(p4)
  ipan random 0.15, 0.85
  kidx expseg 3, p3, 0.2
  aenv expseg 0.001, 0.005, 1, p3 - 0.005, 0.001
  amod poscil kidx * icps * 3.5, icps * 3.5, giSine
  asig poscil p5, icps + amod, giSine
  asig *= aenv
  out asig * sqrt(1 - ipan), asig * sqrt(ipan)
  gaRvbL += asig * (1 - ipan) * 1.2
  gaRvbR += asig * ipan * 1.2
  gaDlyL += asig * 0.2
endin

instr Wind
  aenv linseg 0, p3 * 0.5, 1, p3 * 0.5, 0
  kcf expseg 300, p3 * 0.5, 2500, p3 * 0.5, 400
  an1 pinker
  an2 pinker
  aL butterbp an1, kcf, kcf * 0.3
  aR butterbp an2, kcf * 1.2, kcf * 0.3
  aL *= aenv * p4
  aR *= aenv * p4
  out aL, aR
  gaRvbL += aL
  gaRvbR += aR
endin

; ---------------------------------------------------------------- effects + master
instr Effects
  kspace chnget "space"
  kspace port kspace, 0.2
  ktempo chnget "tempo"
  ktempo limit ktempo, 30, 200
  kdt = 60 / ktempo * 0.75                   ; dotted-eighth ping-pong echo
  kdt port kdt, 0.5
  adt interp kdt
  abufL delayr 2
  adL deltapi adt
  delayw gaDlyL + adL * 0.45 * kspace
  abufR delayr 2
  adR deltapi adt
  delayw gaDlyR + adL * 0.6
  adL butterlp adL, 4000
  adR butterlp adR, 4000
  arL, arR reverbsc gaRvbL + adL * 0.5, gaRvbR + adR * 0.5, 0.82 + 0.15 * kspace, 9000
  aL = adL * kspace * 0.8 + arL * (0.2 + 0.9 * kspace)
  aR = adR * kspace * 0.8 + arR * (0.2 + 0.9 * kspace)
  out aL, aR
  clear gaRvbL, gaRvbR, gaDlyL, gaDlyR
endin

instr Master
  ; make-up gain + gentle soft clipper on the final mix:
  ; monitor reads what all earlier instruments wrote to the output this cycle,
  ; and we add the difference so the result is tanh(gain * mix)
  aL, aR monitor
  aL = tanh(aL * 1.6) - aL
  aR = tanh(aR * 1.6) - aR
  out aL, aR
endin

</CsInstruments>
<CsScore>
i "Conductor" 0 z
i "Effects"   0 z
i "Master"    0 z
</CsScore>
</CsoundSynthesizer>
