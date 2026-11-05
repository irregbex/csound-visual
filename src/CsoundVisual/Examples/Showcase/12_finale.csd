<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 12 - FINALE: "SIX ROOMS"  (about 2 min 40 s)

  BIG IDEA
    Csound is also a COMPOSITION language. This piece is one score in which
    every technique of the gallery plays a role: a Shepard-Risset riser, FM bells
    and lead, Karplus-Strong guitars with echo, a synthesised drum kit with
    euclidean rhythms, a FOF choir, and a spectral freeze of the whole mix.
    The score (at the bottom) is written in BEATS ("t 0 100" = 100 BPM) and reads
    like an arrangement: each line switches a layer on for a number of bars.

  FORM (bars of 4 beats)
    0-7    intro     : Shepard riser, pad, FM bells
    8-23   groove    : drums, Karplus-Strong guitar with dotted-eighth echo, bass
    24-39  choir     : FOF choir + FM lead melody, drums return halfway
    40-43  bridge    : the whole mix is spectrally FROZEN while a riser climbs
    44-59  climax    : everything together, lead an octave higher
    60-    coda      : final chord and the reverb tail

  SLIDERS
    space : reverb amount.
    drums : drum kit level.
    lead  : level of the FM lead melody and bells.
    vowel : vowel sung by the choir (0 a ... 4 u).

  MIDI: no.   Headphones: optional.
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "space", 1, 2, 0.5, 0, 1
chnk "drums", 1, 2, 0.8, 0, 1
chnk "lead",  1, 2, 0.8, 0, 1
chnk "vowel", 1, 2, 0,   0, 4
chnset 0.5, "space"
chnset 0.8, "drums"
chnset 0.8, "lead"
chnset 0,   "vowel"

giBPM  = 100                 ; must match the "t 0 100" line of the score
giSine ftgen 0, 0, 16384, 10, 1
giSigm ftgen 0, 0, 1024, 19, 0.5, 0.5, 270, 0.5
giShep ftgen 0, 0, 16384, 10, 1, 0.3, 0, 0.1

; ---- harmony: Am  F  C  G  (one chord per bar)
giRoot[]  fillarray 45, 41, 48, 43
giArp[][] init 4, 4
giArp fillarray 69, 72, 76, 81,  69, 72, 77, 81,  67, 72, 76, 79,  67, 71, 74, 79
giPad[][] init 4, 4
giPad fillarray 57, 60, 64, 69,  57, 60, 65, 69,  55, 60, 64, 67,  55, 59, 62, 67
; choir parts (0 bass, 1 tenor, 2 alto) per chord
giChoir[][] init 3, 4
giChoir fillarray 45, 41, 48, 43,  57, 57, 55, 55,  64, 65, 64, 62

; ---- formant tables (F1..F5, dB1..dB5, BW1..BW5 for a e i o u)
giAlt ftgen 0, 0, 128, -2, \
  800,1150,2800,3500,4950,  0,-4,-20,-36,-60,  80,90,120,130,140, \
  400,1600,2700,3300,4950,  0,-24,-30,-35,-60, 60,80,120,150,200, \
  350,1700,2700,3700,4950,  0,-20,-30,-36,-60, 50,100,120,150,200, \
  450,800,2830,3500,4950,   0,-9,-16,-28,-55,  70,80,100,130,135, \
  325,700,2530,3500,4950,   0,-12,-30,-40,-64, 50,60,170,180,200
giTen ftgen 0, 0, 128, -2, \
  650,1080,2650,2900,3250,  0,-6,-7,-8,-22,    80,90,120,130,140, \
  400,1700,2600,3200,3580,  0,-14,-12,-14,-20, 70,80,100,120,120, \
  290,1870,2800,3250,3540,  0,-15,-18,-20,-30, 40,90,100,120,120, \
  400,800,2600,2800,3000,   0,-10,-12,-12,-26, 40,80,100,120,120, \
  350,600,2700,2900,3300,   0,-20,-17,-14,-26, 40,60,100,120,120
giBas ftgen 0, 0, 128, -2, \
  600,1040,2250,2450,2750,  0,-7,-9,-9,-20,    60,70,110,120,130, \
  400,1620,2400,2800,3100,  0,-12,-9,-12,-18,  40,80,100,120,120, \
  250,1750,2600,3050,3340,  0,-30,-16,-22,-28, 60,90,100,120,120, \
  400,750,2400,2600,2900,   0,-11,-21,-20,-40, 40,80,100,120,120, \
  350,600,2400,2675,2950,   0,-20,-32,-28,-36, 40,80,100,120,120
giTabs[] fillarray giBas, giTen, giAlt

; ---- buses
gaMixL init 0            ; everything (dry)
gaMixR init 0
gaRvbL init 0            ; reverb send
gaRvbR init 0
gaEcho init 0            ; dotted-eighth echo send
gaDrumL init 0
gaDrumR init 0
gkShep init 0
gaFrzL init 0            ; output of the spectral freeze

; current position in beats (score time x tempo)
opcode Beat, k, 0
  xout times:k() * giBPM / 60
endop

; add a stereo signal to the mix and the reverb
opcode ToMix, 0, aak
  aL, aR, ksend xin
  gaMixL += aL
  gaMixR += aR
  gaRvbL += aL * ksend
  gaRvbR += aR * ksend
endop

; ================================================================ SOUNDS
; ---- FM bell / lead. p4 note, p5 amp, p6 ratio, p7 brightness
instr FM
  ilead chnget "lead"
  icps = mtof(p4)
  idec = (p6 > 2 ? 4 : 1.6) * (440 / icps) ^ 0.4
  aenv expsegr 0.001, 0.004, 1, idec, 0.001, 0.3, 0.0001
  kidx expseg p7 + 0.01, 0.8, p7 * 0.25 + 0.01, 1, p7 * 0.25 + 0.01
  amod poscil kidx * icps * p6, icps * p6, giSine
  asig poscil p5 * ilead, icps + amod, giSine
  asig *= aenv
  kpan jspline 0.15, 0.3, 0.8
  ToMix asig * (0.55 + kpan), asig * (0.55 - kpan), 0.5
  gaEcho += asig * 0.25
endin

; ---- pad voice: detuned saws through a slowly moving filter
instr PadVoice
  icps = mtof(p4)
  aenv linseg 0, p3 * 0.3, 1, p3 * 0.4, 1, p3 * 0.3, 0
  a1 vco2 p5, icps * cent(-7)
  a2 vco2 p5, icps * cent(7)
  kcut jspline 600, 0.2, 0.5
  aL moogladder a1, 1400 + kcut, 0.2
  aR moogladder a2, 1400 + kcut, 0.2
  ToMix aL * aenv, aR * aenv, 0.6
endin

; ---- Karplus-Strong string (see showcase 09)
opcode KarplusStrong, a, iiii
  icps, iamp, ipick, idamp xin
  iper  = 1 / icps
  anz   rand iamp
  aexc  = anz * linseg(1, iper, 1, 0, 0, 1, 0)
  aexc  = aexc - delay(aexc, iper * ipick)
  ifb   = 0.998 - 0.03 * idamp
  ismo  = 0.5 + 0.35 * idamp
  abuf  delayr 0.1
  aout  deltapi iper - ismo / sr
  afilt = aout * (1 - ismo) + delay1(aout) * ismo
  delayw aexc + afilt * ifb
  xout aout
endop

; p4 note, p5 amp, p6 pan, p7 echo send
instr Pluck
  asig KarplusStrong mtof:i(p4), p5, 0.15, 0.25
  asig dcblock2 asig
  asig += butterbp(asig, 230, 120) * 0.4
  asig *= linseg(1, p3 - 0.05, 1, 0.05, 0)
  ToMix asig * sqrt(1 - p6), asig * sqrt(p6), 0.3
  gaEcho += asig * p7
endin

; ---- FM bass. p4 note, p5 amp
instr Bass
  icps = mtof(p4)
  aenv linseg 0, 0.005, 1, p3 - 0.03, 0.7, 0.025, 0
  kidx expseg 2.5, 0.12, 0.6, 1, 0.5
  amod poscil kidx * icps, icps, giSine
  asig poscil p5, icps + amod, giSine
  asig *= aenv
  ToMix asig, asig, 0.05
endin

; ---- drum kit (see showcase 07)
instr Kick
  aenv  expseg 1, 0.06, 0.6, p3 - 0.06, 0.001
  kfrq  expseg 160, 0.07, 50, p3 - 0.07, 44
  asig  poscil 1, kfrq, giSine
  aclk  butterhp rand:a(1), 2000
  asig  = (asig * aenv + aclk * expseg(1, 0.008, 0.001, 1, 0.001) * 0.2) * p4
  gaDrumL += asig
  gaDrumR += asig
endin

instr Snare
  aenv  expseg 1, p3, 0.001
  atone = (poscil(0.5, 185, giSine) + poscil(0.3, 330, giSine)) * expseg(1, 0.09, 0.001, 1, 0.001)
  anz   butterbp rand:a(1), 4200, 3800
  asig  = (atone + anz * aenv * 0.8) * p4
  gaDrumL += asig * 0.9
  gaDrumR += asig
  gaRvbL  += asig * 0.15
  gaRvbR  += asig * 0.15
endin

instr Hat
  a1 vco2 1, 287, 2, 0.5
  a2 vco2 1, 426, 2, 0.5
  a3 vco2 1, 517, 2, 0.5
  a4 vco2 1, 732, 2, 0.5
  a5 vco2 1, 756, 2, 0.5
  a6 vco2 1, 1120, 2, 0.5
  amet butterhp butterbp((a1 + a2 + a3 + a4 + a5 + a6) / 6, 10000, 6000), 7000
  asig = amet * expseg(1, p5, 0.001, 1, 0.001) * p4 * 2.2
  gaDrumL += asig * 0.8
  gaDrumR += asig
endin

; ---- FOF choir singer (see showcase 06)
opcode VowelParam, k, ikk
  itab, kv, kj xin
  kv  limit kv, 0, 3.999
  ki  = int(kv)
  kf  = kv - ki
  xout tab:k(ki * 15 + kj, itab) * (1 - kf) + tab:k((ki + 1) * 15 + kj, itab) * kf
endop

opcode Formant, a, kkikk
  kamp, kfund, itab, kv, kj xin
  aout fof kamp * ampdb(VowelParam(itab, kv, kj + 5)), kfund, VowelParam(itab, kv, kj), 0, \
           VowelParam(itab, kv, kj + 10), 0.003, 0.02, 0.007, 60, giSine, giSigm, 3600
  xout aout
endop

opcode Voice, a, kkki
  kamp, kfund, kv, itab xin
  a1 Formant kamp, kfund, itab, kv, 0
  a2 Formant kamp, kfund, itab, kv, 1
  a3 Formant kamp, kfund, itab, kv, 2
  a4 Formant kamp, kfund, itab, kv, 3
  a5 Formant kamp, kfund, itab, kv, 4
  xout a1 + a2 + a3 + a4 + a5
endop

opcode Singer, a, kikki
  kcps, idet, kvow, kamp, itab xin
  kv1  poscil 0.01, random(4.7, 5.9), giSine, random(0, 1)
  kj1  jspline 0.003, 1, 4
  aout Voice kamp, kcps * cent(idet) * (1 + kv1 + kj1), kvow, itab
  xout aout
endop

; ================================================================ LAYERS
; Each layer runs for the duration given in the score and follows the beat clock.

; p4 = level. One pad chord per bar.
instr Pads
  kb   = Beat()
  kbar = int(kb / 4)
  kold init -1
  if kbar != kold then
    kold = kbar
    kc   = kbar % 4
    kv = 0
    while kv < 4 do
      schedulek "PadVoice", 0, 4 * 60 / giBPM * 1.3, giPad[kc][kv], 0.05 * p4
      kv += 1
    od
  endif
endin

; p4 = intensity (1 = hats + snare, 2 = full kit)
instr Drums
  klev chnget "drums"
  klev = klev * 0.7
  kb   = Beat()
  k16  = int(kb * 4)
  kold init -1
  if k16 != kold then
    kold  = k16
    kstep = k16 % 16
    kbar  = int(k16 / 16)
    if p4 >= 2 && (kstep == 0 || kstep == 6 || kstep == 8 || kstep == 11) then
      schedulek "Kick", 0, 0.6, 0.75 * klev
    endif
    if kstep == 4 || kstep == 12 then
      schedulek "Snare", 0, 0.4, 0.6 * klev
    elseif p4 >= 2 && kstep == 15 && kbar % 2 == 1 then
      schedulek "Snare", 0, 0.2, 0.2 * klev
    endif
    if kstep % 4 == 2 then
      schedulek "Hat", 0, 0.4, 0.3 * klev, 0.3
    elseif (kstep * 11) % 16 < 11 then
      schedulek "Hat", 0, 0.1, (kstep % 4 == 0 ? 0.22 : 0.14) * klev, 0.045
    endif
  endif
endin

; p4 = level, p5 = octave shift. Eighth-note arpeggio with echo.
instr Guitar
  kseq[] fillarray 0, 1, 2, 1, 3, 2, 1, 2
  kb   = Beat()
  k8   = int(kb * 2)
  kold init -1
  if k8 != kold then
    kold = k8
    kc   = int(k8 / 8) % 4
    kn   = giArp[kc][kseq[k8 % 8]] + p5
    schedulek "Pluck", 0, 2, kn, 0.44 * p4 * (k8 % 2 == 0 ? 1 : 0.8), 0.7, 0.9
  endif
endin

; p4 = level. Eighth-note bass on the chord root.
instr Bassline
  kpat[] fillarray 1, 0, 1, 1, 0, 1, 1, 0.5
  kb   = Beat()
  k8   = int(kb * 2)
  kold init -1
  if k8 != kold then
    kold = k8
    kacc = kpat[k8 % 8]
    if kacc > 0 then
      kc = int(k8 / 8) % 4
      schedulek "Bass", 0, 0.28, giRoot[kc] - 12 + (k8 % 8 == 7 ? 12 : 0), 0.14 * p4 * kacc
    endif
  endif
endin

; p4 = level, p5 = chord (-1 = follow the bars, 0..3 = hold that chord).
; Three-part FOF choir, two singers per part, legato chord changes.
instr Choir
  kvow chnget "vowel"
  kvow port kvow, 0.2
  ic   = (p5 < 0 ? int(times:i() * giBPM / 60 / 4) % 4 : p5)
  kb   = Beat()
  kc   = (p5 < 0 ? int(kb / 4) % 4 : p5)
  kn0  portk giChoir[0][kc], 0.15, giChoir[0][ic]
  kn1  portk giChoir[1][kc], 0.15, giChoir[1][ic]
  kn2  portk giChoir[2][kc], 0.15, giChoir[2][ic]
  aenv linseg 0, 2, 1, p3 - 4, 1, 2, 0
  ia   = 0.05 * p4
  aB1  Singer mtof:k(kn0), -6, kvow, ia * 1.2, giBas
  aB2  Singer mtof:k(kn0),  5, kvow, ia * 1.2, giBas
  aT1  Singer mtof:k(kn1), -5, kvow, ia, giTen
  aT2  Singer mtof:k(kn1),  6, kvow, ia, giTen
  aA1  Singer mtof:k(kn2), -4, kvow, ia * 0.9, giAlt
  aA2  Singer mtof:k(kn2),  5, kvow, ia * 0.9, giAlt
  aL = (aB1 * 0.6 + aB2 * 0.4 + aT1 * 0.8 + aT2 * 0.2 + aA1 * 0.3 + aA2 * 0.7) * aenv
  aR = (aB1 * 0.4 + aB2 * 0.6 + aT1 * 0.2 + aT2 * 0.8 + aA1 * 0.7 + aA2 * 0.3) * aenv
  ToMix aL, aR, 0.8
endin

; p4 = level. Shepard-Risset riser: ten octave-spaced tones gliding up.
instr Shepard
  iv = 0
  while iv < 10 do
    schedule "ShepTone", 0, p3, iv, p4
    iv += 1
  od
  gkShep = gkShep + 1 / 6 / kr                  ; one octave every 6 seconds
  gkShep = gkShep - floor(gkShep)
endin

instr ShepTone
  koct = p4 + gkShep
  koct = koct - floor(koct / 10) * 10
  kd   = (koct - 6) / 1.5
  aenv linseg 0, p3 * 0.4, 1, p3 * 0.5, 1, p3 * 0.1, 0
  asig poscil3 exp(-0.5 * kd * kd) * 0.05 * p5, 20 * 2 ^ koct, giShep
  ipan = 0.5 + (p4 - 4.5) / 12
  ToMix asig * aenv * sqrt(1 - ipan), asig * aenv * sqrt(ipan), 0.6
endin

; p4 = level. Accelerating snare roll (16ths -> 32nds -> 64ths).
instr Roll
  istart = times:i() * giBPM / 60
  ilen   = p3 * giBPM / 60                      ; p3 arrives in seconds: convert to beats
  kb   = Beat()
  krel = kb - istart
  kdiv = (krel < ilen * 0.5 ? 4 : (krel < ilen * 0.8 ? 8 : 16))
  kn   = int(kb * kdiv)
  kold init -1
  if kn != kold then
    kold = kn
    schedulek "Snare", 0, 0.15, p4 * (0.15 + 0.6 * krel / ilen)
  endif
endin

; ---- spectral freeze of the whole mix (p4 = level)
instr Freeze
  ain  = (gaMixL + gaMixR + gaDrumL + gaDrumR) * 0.5
  fsig pvsanal ain, 2048, 512, 2048, 1
  kfrz = (timeinsts() > 0.3 ? 1 : 0)
  ffrz pvsfreeze fsig, kfrz, kfrz
  fblr pvsblur ffrz, 0.25, 0.5
  aout pvsynth fblr
  aenv linseg 0, 0.3, 1, p3 - 1.3, 1, 1, 0
  ; only start sounding once frozen
  kgate port kfrz, 0.15
  aout *= aenv * kgate * p4
  gaFrzL = aout
endin

; ================================================================ MIX
instr Mixer
  kspace chnget "space"
  kspace port kspace, 0.1
  ; dotted-eighth echo
  idt  = 0.75 * 60 / giBPM
  abuf delayr 2
  aeL  deltap idt
  aeR  deltap idt * 2
  delayw gaEcho + aeR * 0.4
  aeL  butterlp aeL, 5000
  aeR  butterlp aeR, 4000
  aL   = gaMixL + gaDrumL + aeL * 0.5 + gaFrzL
  aR   = gaMixR + gaDrumR + aeR * 0.5 + gaFrzL
  arL, arR reverbsc gaRvbL + aeL * 0.2 + gaFrzL * 0.5, gaRvbR + aeR * 0.2 + gaFrzL * 0.5, 0.82, 9000
  aL  += arL * kspace * 1.2
  aR  += arR * kspace * 1.2
  out tanh(aL * 1.1), tanh(aR * 1.1)
  clear gaMixL, gaMixR, gaRvbL, gaRvbR, gaEcho, gaDrumL, gaDrumR, gaFrzL
endin

</CsInstruments>
<CsScore>
t 0 100            ; tempo: from here on p2 and p3 are in BEATS (4 beats = 1 bar)
i "Mixer" 0 268
; ---------------- intro (bars 0-7)
i "Shepard"  0  32 1.8
i "Pads"     0  96 1
i "FM"  16.00 3 81 0.17 3.5 1.5
i "FM"  17.00 3 76 0.17 3.5 1.5
i "FM"  18.00 3 72 0.17 3.5 1.5
i "FM"  19.00 3 69 0.17 3.5 1.5
i "FM"  20.00 3 77 0.17 3.5 1.5
i "FM"  21.00 3 72 0.17 3.5 1.5
i "FM"  22.00 3 69 0.17 3.5 1.5
i "FM"  23.00 3 65 0.17 3.5 1.5
i "FM"  24.00 3 79 0.17 3.5 1.5
i "FM"  25.00 3 76 0.17 3.5 1.5
i "FM"  26.00 3 72 0.17 3.5 1.5
i "FM"  27.00 3 67 0.17 3.5 1.5
i "FM"  28.00 3 79 0.17 3.5 1.5
i "FM"  28.50 3 74 0.17 3.5 1.5
i "FM"  29.00 3 71 0.17 3.5 1.5
i "FM"  30.00 3 67 0.17 3.5 1.5
; ---------------- groove (bars 8-23)
i "Drums"    32 16 1
i "Drums"    48 48 2
i "Guitar"   32 64 1 0
i "Bassline" 48 48 1
; ---------------- choir + lead (bars 24-39)
i "Choir"    96 64 1 -1
i "Bassline" 96 64 0.8
i "Drums"   128 32 2
i "Guitar"  128 32 0.8 12
i "FM"  96.00  1.90 76 0.550 1 1.2
i "FM"  98.00  0.95 74 0.550 1 1.2
i "FM"  99.00  0.95 72 0.550 1 1.2
i "FM" 100.00  2.85 72 0.550 1 1.2
i "FM" 103.00  0.95 69 0.550 1 1.2
i "FM" 104.00  0.95 67 0.550 1 1.2
i "FM" 105.00  0.95 72 0.550 1 1.2
i "FM" 106.00  1.90 76 0.550 1 1.2
i "FM" 108.00  3.80 74 0.550 1 1.2
i "FM" 112.00  0.95 76 0.550 1 1.2
i "FM" 113.00  1.90 81 0.550 1 1.2
i "FM" 115.00  0.95 79 0.550 1 1.2
i "FM" 116.00  1.90 77 0.550 1 1.2
i "FM" 118.00  0.95 76 0.550 1 1.2
i "FM" 119.00  0.95 72 0.550 1 1.2
i "FM" 120.00  1.90 76 0.550 1 1.2
i "FM" 122.00  1.90 79 0.550 1 1.2
i "FM" 124.00  2.85 74 0.550 1 1.2
i "FM" 127.00  0.95 71 0.550 1 1.2
i "FM" 128.00  1.90 76 0.550 1 1.4
i "FM" 130.00  0.95 74 0.550 1 1.4
i "FM" 131.00  0.95 72 0.550 1 1.4
i "FM" 132.00  2.85 72 0.550 1 1.4
i "FM" 135.00  0.95 69 0.550 1 1.4
i "FM" 136.00  0.95 67 0.550 1 1.4
i "FM" 137.00  0.95 72 0.550 1 1.4
i "FM" 138.00  1.90 76 0.550 1 1.4
i "FM" 140.00  3.80 74 0.550 1 1.4
i "FM" 144.00  0.95 76 0.550 1 1.4
i "FM" 145.00  1.90 81 0.550 1 1.4
i "FM" 147.00  0.95 79 0.550 1 1.4
i "FM" 148.00  1.90 77 0.550 1 1.4
i "FM" 150.00  0.95 76 0.550 1 1.4
i "FM" 151.00  0.95 72 0.550 1 1.4
i "FM" 152.00  1.90 76 0.550 1 1.4
i "FM" 154.00  1.90 79 0.550 1 1.4
i "FM" 156.00  1.90 74 0.550 1 1.4
i "FM" 158.00  1.90 76 0.550 1 1.4
; ---------------- bridge: spectral freeze + riser (bars 40-43)
i "Freeze"  159.5 16.5 1.4
i "Shepard" 160 16 1.3
i "Roll"    168  8 0.8
; ---------------- climax (bars 44-59)
i "Drums"   176 64 2
i "Guitar"  176 64 1 0
i "Bassline" 176 64 1
i "Choir"   176 64 1.1 -1
i "Pads"    176 64 0.6
i "FM" 176.00  1.90 88 0.500 1 1.8
i "FM" 176.00  2.00 88 0.175 3.5 1
i "FM" 178.00  0.95 86 0.500 1 1.8
i "FM" 178.00  1.00 86 0.175 3.5 1
i "FM" 179.00  0.95 84 0.500 1 1.8
i "FM" 179.00  1.00 84 0.175 3.5 1
i "FM" 180.00  2.85 84 0.500 1 1.8
i "FM" 180.00  3.00 84 0.175 3.5 1
i "FM" 183.00  0.95 81 0.500 1 1.8
i "FM" 183.00  1.00 81 0.175 3.5 1
i "FM" 184.00  0.95 79 0.500 1 1.8
i "FM" 184.00  1.00 79 0.175 3.5 1
i "FM" 185.00  0.95 84 0.500 1 1.8
i "FM" 185.00  1.00 84 0.175 3.5 1
i "FM" 186.00  1.90 88 0.500 1 1.8
i "FM" 186.00  2.00 88 0.175 3.5 1
i "FM" 188.00  3.80 86 0.500 1 1.8
i "FM" 188.00  4.00 86 0.175 3.5 1
i "FM" 192.00  0.95 88 0.500 1 1.8
i "FM" 192.00  1.00 88 0.175 3.5 1
i "FM" 193.00  1.90 93 0.500 1 1.8
i "FM" 193.00  2.00 93 0.175 3.5 1
i "FM" 195.00  0.95 91 0.500 1 1.8
i "FM" 195.00  1.00 91 0.175 3.5 1
i "FM" 196.00  1.90 89 0.500 1 1.8
i "FM" 196.00  2.00 89 0.175 3.5 1
i "FM" 198.00  0.95 88 0.500 1 1.8
i "FM" 198.00  1.00 88 0.175 3.5 1
i "FM" 199.00  0.95 84 0.500 1 1.8
i "FM" 199.00  1.00 84 0.175 3.5 1
i "FM" 200.00  1.90 88 0.500 1 1.8
i "FM" 200.00  2.00 88 0.175 3.5 1
i "FM" 202.00  1.90 91 0.500 1 1.8
i "FM" 202.00  2.00 91 0.175 3.5 1
i "FM" 204.00  2.85 86 0.500 1 1.8
i "FM" 204.00  3.00 86 0.175 3.5 1
i "FM" 207.00  0.95 83 0.500 1 1.8
i "FM" 207.00  1.00 83 0.175 3.5 1
i "FM" 208.00  1.90 88 0.500 1 2.0
i "FM" 208.00  2.00 88 0.175 3.5 1
i "FM" 210.00  0.95 86 0.500 1 2.0
i "FM" 210.00  1.00 86 0.175 3.5 1
i "FM" 211.00  0.95 84 0.500 1 2.0
i "FM" 211.00  1.00 84 0.175 3.5 1
i "FM" 212.00  2.85 84 0.500 1 2.0
i "FM" 212.00  3.00 84 0.175 3.5 1
i "FM" 215.00  0.95 81 0.500 1 2.0
i "FM" 215.00  1.00 81 0.175 3.5 1
i "FM" 216.00  0.95 79 0.500 1 2.0
i "FM" 216.00  1.00 79 0.175 3.5 1
i "FM" 217.00  0.95 84 0.500 1 2.0
i "FM" 217.00  1.00 84 0.175 3.5 1
i "FM" 218.00  1.90 88 0.500 1 2.0
i "FM" 218.00  2.00 88 0.175 3.5 1
i "FM" 220.00  3.80 86 0.500 1 2.0
i "FM" 220.00  4.00 86 0.175 3.5 1
i "FM" 224.00  0.95 88 0.500 1 2.0
i "FM" 224.00  1.00 88 0.175 3.5 1
i "FM" 225.00  1.90 93 0.500 1 2.0
i "FM" 225.00  2.00 93 0.175 3.5 1
i "FM" 227.00  0.95 91 0.500 1 2.0
i "FM" 227.00  1.00 91 0.175 3.5 1
i "FM" 228.00  1.90 89 0.500 1 2.0
i "FM" 228.00  2.00 89 0.175 3.5 1
i "FM" 230.00  0.95 88 0.500 1 2.0
i "FM" 230.00  1.00 88 0.175 3.5 1
i "FM" 231.00  0.95 84 0.500 1 2.0
i "FM" 231.00  1.00 84 0.175 3.5 1
i "FM" 232.00  1.90 88 0.500 1 2.0
i "FM" 232.00  2.00 88 0.175 3.5 1
i "FM" 234.00  1.90 91 0.500 1 2.0
i "FM" 234.00  2.00 91 0.175 3.5 1
i "FM" 236.00  1.90 86 0.500 1 2.0
i "FM" 236.00  2.00 86 0.175 3.5 1
i "FM" 238.00  1.90 88 0.500 1 2.0
i "FM" 238.00  2.00 88 0.175 3.5 1
; ---------------- coda (bar 60)
i "Kick"    240 1 0.6
i "Choir"   240 18 1.2 0
i "Pluck" 240.00 6 45 0.2 0.5 0.6
i "Pluck" 240.06 6 57 0.2 0.5 0.6
i "Pluck" 240.12 6 64 0.2 0.5 0.6
i "Pluck" 240.18 6 69 0.2 0.5 0.6
i "Pluck" 240.24 6 72 0.2 0.5 0.6
i "Pluck" 240.30 6 76 0.2 0.5 0.6
i "FM" 240.00 10 69 0.14 3.5 1.2
i "FM" 240.10 10 76 0.14 3.5 1.2
i "FM" 240.20 10 81 0.14 3.5 1.2
i "FM" 240.30 10 84 0.14 3.5 1.2
i "FM"      240 8 81 0.550 1 1.5
e
</CsScore>
</CsoundSynthesizer>
