<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  SHOWCASE 04 - SPECTRAL MAGIC  (endless)

  BIG IDEA
    The phase vocoder (pvs opcodes) turns sound into a moving spectrum: hundreds
    of frequency bins updated every few milliseconds. Once sound is a spectrum you
    can do impossible things to it: morph a singing voice into wind, freeze one
    instant forever, smear it in time (blur), or transpose it while keeping the
    vowels in place - and build a choir out of a single voice.

  LISTEN FOR
    - A lone synthetic singer whose harmonics slowly turn into wind and back (morph).
    - Every ~20 s the last vowel is frozen and hangs in the air like a sustained chord.
    - "harmony" adds two transposed copies (a fifth and an octave up) with the same
      vowel formants: one voice becomes a small choir.
    - "shift" transposes everything without the chipmunk effect (formants are kept).

  SLIDERS
    morph   : 0 = pure voice, 1 = pure wind; in between the voice is made of wind.
    blur    : smears the spectrum in time (0 = crisp, 1 = a ghostly wash).
    shift   : transposition in semitones, formants preserved.
    harmony : level of the spectral choir (+7 and +12 semitones).
    motion  : how much morph/blur drift by themselves and how often freezes happen.
    space   : reverb amount.

  MIDI: no.   Headphones: optional.
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1
seed 0

chnk "morph",   1, 2, 0.25, 0, 1
chnk "blur",    1, 2, 0.1,  0, 1
chnk "shift",   1, 2, 0,   -12, 12
chnk "harmony", 1, 2, 0.5,  0, 1
chnk "motion",  1, 2, 0.6,  0, 1
chnk "space",   1, 2, 0.5,  0, 1
chnset 0.25, "morph"
chnset 0.1,  "blur"
chnset 0,    "shift"
chnset 0.5,  "harmony"
chnset 0.6,  "motion"
chnset 0.5,  "space"

giSine ftgen 0, 0, 16384, 10, 1
; melody: MIDI note, duration (s), vowel (0 a, 1 e, 2 i, 3 o, 4 u)
giNote[] fillarray 62, 65, 64, 62, 60, 62, 57,   58, 60, 62, 65, 64, 69, 62
giDur[]  fillarray 1.5, 1.5, 1, 1, 2, 1.5, 3,    1.5, 1.5, 1, 1, 2, 2, 4
giVow[]  fillarray 0, 3, 1, 0, 4, 2, 0,          3, 0, 1, 2, 0, 3, 0
; vowel formants (Hz) and gains: 5 vowels x (F1 F2 F3 A2 A3)
giForm[] fillarray 800, 1150, 2800, 0.5, 0.1, \
                   400, 1600, 2700, 0.06, 0.05, \
                   350, 1700, 2700, 0.1, 0.05, \
                   450,  800, 2830, 0.35, 0.08, \
                   325,  700, 2530, 0.25, 0.03

gaVoice init 0
gaWind  init 0
gkVowel init 0
gkFreeze init 0
gaRvbL init 0
gaRvbR init 0

; ---------------------------------------------------------------- the singer
instr Melody
  kidx init 0
  knext init 0
  ktime timeinsts
  if ktime >= knext then
    kn = kidx % lenarray(giNote)
    schedulek "Singer", 0, giDur[kn] + 0.15, giNote[kn], giVow[kn]
    knext = ktime + giDur[kn]
    ; at the end of each phrase: a spectral freeze, if "motion" allows it
    if kn == lenarray(giNote) - 1 || kn == 6 then
      if random:k(0, 1) < chnget:k("motion") * 1.4 then
        schedulek "Freeze", giDur[kn] - 0.8, 4.5
      endif
    endif
    kidx += 1
  endif
endin

instr Singer
  icps = mtof(p4)
  iv   = p5
  aenv linseg 0, 0.12, 1, p3 - 0.27, 0.9, 0.15, 0
  ; glide in from the previous pitch region, natural vibrato + jitter
  kvd  linseg 0, 0.35, 0, 0.6, 1
  kvib poscil 0.012 * kvd, 5.4, giSine
  kjit jspline 0.004, 2, 6
  asaw vco2 1, icps * (1 + kvib + kjit)
  ; vowel formants
  i1 = giForm[iv * 5]
  i2 = giForm[iv * 5 + 1]
  i3 = giForm[iv * 5 + 2]
  ia2 = giForm[iv * 5 + 3]
  ia3 = giForm[iv * 5 + 4]
  af1 butterbp asaw, i1, 80
  af2 butterbp asaw, i2, 90
  af3 butterbp asaw, i3, 120
  abr butterbp rand:a(1), i2, 300
  asig = (af1 + af2 * ia2 * 2 + af3 * ia3 * 4 + abr * 0.05) * aenv
  gaVoice += asig * 0.9
endin

; while this note is active it raises the freeze flag (Spectral clears it every cycle)
instr Freeze
  gkFreeze = 1
endin

; ---------------------------------------------------------------- the wind
instr Wind
  awave jspline 0.5, 0.08, 0.25
  kcf   jspline 1, 0.05, 0.2
  an1   pinker
  an2   pinker
  an    = an1 * 0.7 + an2 * 0.3
  an    butterbp an, 900 * 2 ^ (kcf * 1.2), 1200
  an   += butterhp(an2, 3000) * 0.15
  gaWind = an * (0.6 + awave) * 3
endin

; ---------------------------------------------------------------- the spectral engine
instr Spectral
  kmorph chnget "morph"
  kblur  chnget "blur"
  kshift chnget "shift"
  kharm  chnget "harmony"
  kmot   chnget "motion"
  ; slow automatic drift on top of the sliders
  kdrift1 poscil 1, 1 / 37, giSine
  kdrift2 poscil 1, 1 / 23, giSine, 0.25
  kmorph = kmorph + kmot * 0.35 * (kdrift1 * 0.5 + 0.5)
  kblur  = kblur + kmot * 0.25 * (kdrift2 * 0.5 + 0.5)
  kmorph limit kmorph, 0, 1
  kblur  limit kblur, 0, 1
  kmorph port kmorph, 0.1
  kblur  port kblur, 0.1
  kshift port kshift, 0.1
  kharm  port kharm, 0.1
  kfrz   port gkFreeze, 0.02

  ifft = 2048
  iolap = 512
  fvoice pvsanal gaVoice, ifft, iolap, ifft, 1
  fwind  pvsanal gaWind, ifft, iolap, ifft, 1
  ; morph: amplitudes and frequencies glide from voice (0) to wind (1)
  fmorph pvsmorph fvoice, fwind, kmorph, kmorph ^ 0.7
  ffrz   pvsfreeze fmorph, (kfrz > 0.5 ? 1 : 0), (kfrz > 0.5 ? 1 : 0)
  fblur  pvsblur ffrz, kblur * 0.8 + 0.001, 1
  fmain  pvscale fblur, semitone(kshift), 1
  ffifth pvscale fblur, semitone(kshift + 7), 1
  focta  pvscale fblur, semitone(kshift + 12), 1
  amain  pvsynth fmain
  afifth pvsynth ffifth
  aocta  pvsynth focta
  ; spread the choir: main in the centre, harmonies left and right, slightly delayed
  afifth delay afifth, 0.023
  aocta  delay aocta, 0.031
  aL = amain + kharm * (afifth * 0.75 + aocta * 0.35)
  aR = amain + kharm * (afifth * 0.35 + aocta * 0.75)
  out aL, aR
  gaRvbL += aL
  gaRvbR += aR
  gaVoice = 0
  gkFreeze = 0
endin

instr Space
  kspace chnget "space"
  kspace port kspace, 0.2
  aL, aR reverbsc gaRvbL, gaRvbR, 0.8 + 0.15 * kspace, 9000
  out aL * kspace, aR * kspace
  clear gaRvbL, gaRvbR
endin

instr Master
  aL, aR monitor
  out tanh(aL * 1.3) - aL, tanh(aR * 1.3) - aR
endin

</CsInstruments>
<CsScore>
i "Melody"   0 z
i "Wind"     0 z
i "Spectral" 0 z
i "Space"    0 z
i "Master"   0 z
</CsScore>
</CsoundSynthesizer>
