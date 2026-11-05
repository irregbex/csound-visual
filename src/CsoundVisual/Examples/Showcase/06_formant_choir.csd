<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 06 - THE SINGING MACHINE  (~80 s)

  BIG IDEA
    FOF ("Fonctions d'Onde Formantique", IRCAM 1980s) builds a voice from tiny
    grains, one per vocal-fold pulse. Each grain is a decaying sine at a formant
    frequency; five of them (F1-F5) give a vowel, and gliding the five formants
    turns "a" into "o" into "i". With measured formant tables for soprano, alto,
    tenor and bass, Csound sings a four-part chorale and a soloist on top.

  LISTEN FOR
    - The soloist's lyrics: the vowel changes on every note (a, o, e, i, u...).
    - The choir: each part is three slightly detuned singers with their own vibrato,
      so it sounds like a group, not a machine.
    - Singer's formant (the ring around 3 kHz) in the tenor and bass.

  SLIDERS
    vowel   : vowel of the choir (0 = a, 1 = e, 2 = i, 3 = o, 4 = u; in-between glides).
    vibrato : vibrato depth of everybody (0 = organ-like, 1 = operatic).
    spread  : choir width and detune (0 = one voice per part in the centre, 1 = wide choir).
    solo    : level of the soloist.
    space   : reverb (a church).

  MIDI: no.   Headphones: optional.
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "vowel",   1, 2, 0,    0, 4
chnk "vibrato", 1, 2, 0.5,  0, 1
chnk "spread",  1, 2, 0.6,  0, 1
chnk "solo",    1, 2, 0.8,  0, 1
chnk "space",   1, 2, 0.55, 0, 1
chnset 0,    "vowel"
chnset 0.5,  "vibrato"
chnset 0.6,  "spread"
chnset 0.8,  "solo"
chnset 0.55, "space"

giSine ftgen 0, 0, 16384, 10, 1
giSigm ftgen 0, 0, 1024, 19, 0.5, 0.5, 270, 0.5      ; FOF attack shape
; Formant tables: for each vowel a, e, i, o, u -> F1..F5 (Hz), A1..A5 (dB), BW1..BW5 (Hz)
giSop ftgen 0, 0, 128, -2, \
  800,1150,2900,3900,4950,  0,-6,-32,-20,-50,  80,90,120,130,140, \
  350,2000,2800,3600,4950,  0,-20,-15,-40,-56, 60,100,120,150,200, \
  270,2140,2950,3900,4950,  0,-12,-26,-26,-44, 60,90,100,120,120, \
  450,800,2830,3800,4950,   0,-11,-22,-22,-50, 70,80,100,130,135, \
  325,700,2700,3800,4950,   0,-16,-35,-40,-60, 50,60,170,180,200
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
giTabs[] fillarray giSop, giAlt, giTen, giBas

gaRvbL init 0
gaRvbR init 0

; one formant parameter, interpolated between two neighbouring vowels
opcode VowelParam, k, ikk
  itab, kv, kj xin
  kv  limit kv, 0, 3.999
  ki  = int(kv)
  kf  = kv - ki
  xout tab:k(ki * 15 + kj, itab) * (1 - kf) + tab:k((ki + 1) * 15 + kj, itab) * kf
endop

; one formant = one fof generator
opcode Formant, a, kkikki
  kamp, kfund, itab, kv, kj, idur xin
  kform VowelParam itab, kv, kj
  kdb   VowelParam itab, kv, kj + 5
  kbw   VowelParam itab, kv, kj + 10
  aout  fof kamp * ampdb(kdb), kfund, kform, 0, kbw, 0.003, 0.02, 0.007, 60, giSine, giSigm, idur
  xout aout
endop

; a complete voice: five formants
opcode Voice, a, kkki
  kamp, kfund, kv, itab xin
  idur = 3600
  a1 Formant kamp, kfund, itab, kv, 0, idur
  a2 Formant kamp, kfund, itab, kv, 1, idur
  a3 Formant kamp, kfund, itab, kv, 2, idur
  a4 Formant kamp, kfund, itab, kv, 3, idur
  a5 Formant kamp, kfund, itab, kv, 4, idur
  xout a1 + a2 + a3 + a4 + a5
endop

; one choir singer: own tuning error, vibrato rate, slow pitch/loudness drift
opcode Singer, a, iikkki
  icps, idet, kvib, kvow, kamp, itab xin
  kv1  poscil 0.011 * kvib, random(4.6, 6.0), giSine, random(0, 1)
  kj1  jspline 0.003, 1, 4
  ka   jspline 0.15, 0.3, 1
  aout Voice kamp * (0.85 + ka), icps * cent(idet) * (1 + kv1 + kj1), kvow, itab
  xout aout
endop

; ---------------------------------------------------------------- choir
; p4 = MIDI note, p5 = part (0 soprano, 1 alto, 2 tenor, 3 bass), p6 = amplitude
instr Choir
  icps  = mtof(p4)
  itab  = giTabs[p5]
  ispr  chnget "spread"
  aenv  linseg 0, 0.6, 1, p3 - 1.4, 1, 0.8, 0
  kvow  chnget "vowel"
  kvib  chnget "vibrato"
  kvow  port kvow, 0.15
  kvib  port kvib, 0.1
  ; three singers per part (the spread slider sets their detune and stereo width)
  a1 Singer icps, -ispr * 9 + random(-2, 2), kvib, kvow, p6, itab
  a2 Singer icps, random(-2, 2),             kvib, kvow, p6, itab
  a3 Singer icps,  ispr * 9 + random(-2, 2), kvib, kvow, p6, itab
  ipart = (p5 - 1.5) * 0.12 * ispr                  ; parts slightly placed too
  ip1 = 0.5 - 0.42 * ispr + ipart
  ip3 = 0.5 + 0.42 * ispr + ipart
  ip2 = 0.5 + ipart
  aL = (a1 * sqrt(1 - ip1) + a2 * sqrt(1 - ip2) + a3 * sqrt(1 - ip3)) * aenv
  aR = (a1 * sqrt(ip1) + a2 * sqrt(ip2) + a3 * sqrt(ip3)) * aenv
  out aL, aR
  gaRvbL += aL
  gaRvbR += aR
endin

; ---------------------------------------------------------------- soloist
; legato soprano reading a melody (MIDI note, beats, vowel) from arrays
giMel[] fillarray \
  74,2,0, 73,1,3, 74,1,0,  78,3,1, 76,1,0,  74,2,2, 71,2,3,  73,4,0, \
  74,1,3, 76,1,0, 78,2,1,  79,2,0, 78,1,4, 76,1,0,  74,2,2, 76,2,3,  76,3,0, 73,1,1, \
  74,2,0, 78,2,3,  81,3,0, 78,1,1,  79,2,2, 74,2,0,  76,4,3, \
  78,1,0, 79,1,1, 81,2,0,  83,2,3, 81,1,0, 79,1,2,  78,2,0, 76,1,4, 73,1,0,  74,7,0

instr Solo
  kidx  init 0
  knext init 0
  knote init giMel[0]
  kvow  init giMel[2]
  kvenv init 0                       ; vibrato envelope: grows on long notes
  karti init 1                       ; loudness dip at every new syllable
  ktime timeinsts
  if ktime >= knext && kidx < lenarray(giMel) / 3 then
    knote = giMel[kidx * 3]
    kvow  = giMel[kidx * 3 + 2]
    knext = ktime + giMel[kidx * 3 + 1]          ; one beat = one second
    kvenv = 0
    karti = 0.55
    kidx += 1
  endif
  kvenv  = min(1, kvenv + 0.8 / kr)
  karti  = karti + (1 - karti) * 0.02
  kpitch portk knote, 0.06
  kvsm   portk kvow, 0.08
  kvibd  chnget "vibrato"
  ksolo  chnget "solo"
  ksolo  port ksolo, 0.1
  kvib   poscil 0.016 * kvibd * (0.25 + 0.75 * kvenv), 5.6, giSine
  kjit   jspline 0.003, 2, 5
  kf     = mtof(kpitch) * (1 + kvib + kjit)
  aenv   linseg 0, 0.3, 1, p3 - 1.8, 1, 1.5, 0
  asig   Voice 0.13 * karti, kf, kvsm, giSop
  asig  *= aenv * ksolo
  out asig * 0.75, asig * 0.8
  gaRvbL += asig * 0.7
  gaRvbR += asig * 0.75
endin

; ---------------------------------------------------------------- church + master
instr Church
  kspace chnget "space"
  kspace port kspace, 0.2
  aL, aR reverbsc gaRvbL, gaRvbR, 0.85 + 0.12 * kspace, 7000
  out aL * kspace * 0.9, aR * kspace * 0.9
  clear gaRvbL, gaRvbR
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.2) - aL, tanh(aR * 1.2) - aR
endin

</CsInstruments>
<CsScore>
i "Church" 0 84
i "Master" 0 84
; choir: p4 = note, p5 = part (0 S, 1 A, 2 T, 3 B), p6 = amplitude
i "Choir"   0.0 4.3 50 3 0.058
i "Choir"   0.0 4.3 57 2 0.047
i "Choir"   0.0 4.3 62 1 0.042
i "Choir"   0.0 4.3 66 0 0.030
i "Choir"   4.0 4.3 45 3 0.058
i "Choir"   4.0 4.3 57 2 0.047
i "Choir"   4.0 4.3 61 1 0.042
i "Choir"   4.0 4.3 64 0 0.030
i "Choir"   8.0 4.3 50 3 0.058
i "Choir"   8.0 4.3 57 2 0.047
i "Choir"   8.0 4.3 62 1 0.042
i "Choir"   8.0 4.3 66 0 0.030
i "Choir"  12.0 4.3 47 3 0.058
i "Choir"  12.0 4.3 54 2 0.047
i "Choir"  12.0 4.3 62 1 0.042
i "Choir"  12.0 4.3 66 0 0.030
i "Choir"  16.0 4.3 43 3 0.058
i "Choir"  16.0 4.3 55 2 0.047
i "Choir"  16.0 4.3 62 1 0.042
i "Choir"  16.0 4.3 67 0 0.030
i "Choir"  20.0 4.3 45 3 0.058
i "Choir"  20.0 4.3 57 2 0.047
i "Choir"  20.0 4.3 61 1 0.042
i "Choir"  20.0 4.3 64 0 0.030
i "Choir"  24.0 4.3 42 3 0.058
i "Choir"  24.0 4.3 57 2 0.047
i "Choir"  24.0 4.3 62 1 0.042
i "Choir"  24.0 4.3 69 0 0.030
i "Choir"  28.0 4.3 43 3 0.058
i "Choir"  28.0 4.3 59 2 0.047
i "Choir"  28.0 4.3 62 1 0.042
i "Choir"  28.0 4.3 67 0 0.030
i "Choir"  32.0 4.3 40 3 0.058
i "Choir"  32.0 4.3 55 2 0.047
i "Choir"  32.0 4.3 62 1 0.042
i "Choir"  32.0 4.3 67 0 0.030
i "Choir"  36.0 4.3 45 3 0.058
i "Choir"  36.0 4.3 55 2 0.047
i "Choir"  36.0 4.3 61 1 0.042
i "Choir"  36.0 4.3 64 0 0.030
i "Choir"  40.0 4.3 50 3 0.058
i "Choir"  40.0 4.3 57 2 0.047
i "Choir"  40.0 4.3 62 1 0.042
i "Choir"  40.0 4.3 66 0 0.030
i "Choir"  44.0 4.3 47 3 0.058
i "Choir"  44.0 4.3 54 2 0.047
i "Choir"  44.0 4.3 62 1 0.042
i "Choir"  44.0 4.3 66 0 0.030
i "Choir"  48.0 4.3 43 3 0.058
i "Choir"  48.0 4.3 55 2 0.047
i "Choir"  48.0 4.3 62 1 0.042
i "Choir"  48.0 4.3 67 0 0.030
i "Choir"  52.0 4.3 45 3 0.058
i "Choir"  52.0 4.3 57 2 0.047
i "Choir"  52.0 4.3 61 1 0.042
i "Choir"  52.0 4.3 64 0 0.030
i "Choir"  56.0 4.3 42 3 0.058
i "Choir"  56.0 4.3 57 2 0.047
i "Choir"  56.0 4.3 62 1 0.042
i "Choir"  56.0 4.3 69 0 0.030
i "Choir"  60.0 4.3 43 3 0.058
i "Choir"  60.0 4.3 59 2 0.047
i "Choir"  60.0 4.3 62 1 0.042
i "Choir"  60.0 4.3 67 0 0.030
i "Choir"  64.0 4.3 40 3 0.058
i "Choir"  64.0 4.3 55 2 0.047
i "Choir"  64.0 4.3 62 1 0.042
i "Choir"  64.0 4.3 67 0 0.030
i "Choir"  68.0 4.3 45 3 0.058
i "Choir"  68.0 4.3 55 2 0.047
i "Choir"  68.0 4.3 61 1 0.042
i "Choir"  68.0 4.3 64 0 0.030
i "Choir"  72.0 7.5 38 3 0.058
i "Choir"  72.0 7.5 57 2 0.047
i "Choir"  72.0 7.5 62 1 0.042
i "Choir"  72.0 7.5 66 0 0.030
; soloist enters after two chords and sings to the end
i "Solo" 8 71.5
e
</CsScore>
</CsoundSynthesizer>
