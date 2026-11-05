<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 04 - TALKING CHORDS  (~94 s)

  BIG IDEA
    Cross-synthesis: take the SHAPE of one sound and the NOTES of another.
    Csound analyses a spoken voice and a synth chord with FFTs; pvsvoc keeps
    the voice's spectral envelope (the formants that make vowels and words
    intelligible) and applies it to the chord's partials. The synth literally
    speaks - the classic vocoder (Daft Punk, Kraftwerk, Herbie Hancock), but
    built from an FFT in a dozen lines.

  LISTEN FOR
    - 0-5 s: the original speaker (Max Mathews, the father of computer music).
    - Then the same words sung by a string-synth chord progression, in stereo.
    - "robot" moves from chords to a single monotone pitch (the 1970s robot).
    - "consonants" adds back the voice's hiss (s, t, k) so words stay clear.

  SLIDERS
    robot      : 0 = chord progression, 1 = one flat robotic note.
    consonants : amount of the voice's high-frequency hiss mixed back in.
    formant    : detail of the vocal envelope (low = mumbled, high = crisp).
    dry        : how much of the original voice you also hear.
    space      : reverb.

  MIDI: no.   Sample: ../Manual/Mathews.wav (15.6 s speech, 44.1 kHz, looped by diskin2).
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1

chnk "robot",      1, 2, 0,   0, 1
chnk "consonants", 1, 2, 0.4, 0, 1
chnk "formant",    1, 2, 0.6, 0, 1
chnk "dry",        1, 2, 0,   0, 1
chnk "space",      1, 2, 0.3, 0, 1
chnset 0,   "robot"
chnset 0.4, "consonants"
chnset 0.6, "formant"
chnset 0,   "dry"
chnset 0.3, "space"

; chord progression (5 notes each), 2.5 s per chord: Am9 - Fmaj7 - C/E - G6 - Dm9 - Fmaj9 - Esus4 - E7
giChords ftgen 0, 0, 64, -2, 45,57,60,64,71,  41,57,60,64,69,  40,55,60,64,67,  43,55,59,64,67, \
                             38,57,60,65,69,  41,57,64,67,72,  40,57,59,64,69,  40,56,59,62,68

; ---------------------------------------------------------------- the original voice
instr 1
  aV diskin2 "../Manual/Mathews.wav", 1
  aE linseg 1, p3 - 0.3, 1, 0.3, 0
  out aV * aE * 0.7, aV * aE * 0.7
endin

; ---------------------------------------------------------------- one side of the vocoder
opcode Excite, a, kkkkkk
  ; a detuned saw "string" chord; kDet makes the two stereo sides differ
  k1, k2, k3, k4, k5, kDet xin
  a1 vco2 0.2, cpsmidinn(k1) * kDet
  a2 vco2 0.2, cpsmidinn(k2) / kDet
  a3 vco2 0.2, cpsmidinn(k3) * kDet
  a4 vco2 0.2, cpsmidinn(k4) / kDet
  a5 vco2 0.2, cpsmidinn(k5) * kDet
  xout a1 + a2 + a3 + a4 + a5
endop

instr 2
  kRobot chnget "robot"
  kCons  chnget "consonants"
  kForm  chnget "formant"
  kDry   chnget "dry"
  kWet   chnget "space"
  kRobot portk kRobot, 0.1
  kCons  portk kCons, 0.1
  kDry   portk kDry, 0.1
  kWet   portk kWet, 0.1

  aV diskin2 "../Manual/Mathews.wav", 1, 0, 1

  ; current chord (glides 60 ms), bent towards one note (A2) by "robot"
  kRow = int(timeinsts() / 2.5) % 8
  kN1 portk tab:k(kRow * 5,     giChords), 0.03
  kN2 portk tab:k(kRow * 5 + 1, giChords), 0.03
  kN3 portk tab:k(kRow * 5 + 2, giChords), 0.03
  kN4 portk tab:k(kRow * 5 + 3, giChords), 0.03
  kN5 portk tab:k(kRow * 5 + 4, giChords), 0.03
  kN1 = kN1 + (45 - kN1) * kRobot
  kN2 = kN2 + (57 - kN2) * kRobot
  kN3 = kN3 + (57 - kN3) * kRobot
  kN4 = kN4 + (45 - kN4) * kRobot
  kN5 = kN5 + (57 - kN5) * kRobot

  aExL Excite kN1, kN2, kN3, kN4, kN5, 1.004
  aExR Excite kN1, kN2, kN3, kN4, kN5, 0.996
  aNs  pinker
  aExL += aNs * 0.03
  aExR += aNs * 0.03

  ; ---- the cross-synthesis: voice envelope x chord partials
  kCoefs = 20 + kForm * 100
  fVoice pvsanal aV,   1024, 256, 1024, 1
  fExL   pvsanal aExL, 1024, 256, 1024, 1
  fExR   pvsanal aExR, 1024, 256, 1024, 1
  fOL    pvsvoc fVoice, fExL, 1, 1, kCoefs
  fOR    pvsvoc fVoice, fExR, 1, 1, kCoefs
  aOL    pvsynth fOL
  aOR    pvsynth fOR
  ; follow the voice's loudness (so silences are silent); the gain is capped
  ; so that breaths between words cannot be blown up
  kRV    rms aV, 20
  kRO    rms (aOL + aOR) * 0.5, 20
  kG     limit kRV / (kRO + 0.0005), 0, 8
  kG     portk kG, 0.01
  aG     interp kG
  aOL   *= aG
  aOR   *= aG

  ; consonants: the voice above 4 kHz
  aHiss  buthp aV, 4000
  aHiss  buthp aHiss, 4000
  aL = aOL * 1.3 + aHiss * kCons * 2 + aV * kDry * 0.8
  aR = aOR * 1.3 + aHiss * kCons * 2 + aV * kDry * 0.8

  aRL, aRR reverbsc aL, aR, 0.85, 10000
  kEnv linseg 0, 0.5, 1, p3 - 3.5, 1, 3, 0
  ; tanh = a gentle limiter for the vocoder's sharp formant peaks
  out tanh((aL + aRL * kWet) * kEnv * 0.3), tanh((aR + aRR * kWet) * kEnv * 0.3)
endin

</CsInstruments>
<CsScore>
i1 0 5
i2 5.5 88
e
</CsScore>
</CsoundSynthesizer>
