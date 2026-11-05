<CsoundSynthesizer>
<CsOptions>
-odac
</CsOptions>
<CsInstruments>
/*
  AUDIO FX 01 - THE TIME MICROSCOPE  (~96 s)

  BIG IDEA
    Speed and pitch are two independent knobs. The phase vocoder (mincer) cuts
    the recording into tiny overlapping FFT frames, so Csound can walk through
    the sound at ANY speed - 20x slower, frozen on one vowel, backwards - while
    the pitch stays put; or transpose it an octave without changing its length.
    (Like Cubase's AudioWarp / elastique, but you control the read head directly.)

  LISTEN FOR
    - 0-6 s: the original sentence ("the quick brown fox...").
    - 6-34 s: 4x and then 20x slower - consonants become textures, vowels organs.
    - 34-42 s: speed 0 = the sound is FROZEN on one instant, yet it keeps sounding.
    - 42-50 s: played backwards at normal speed, at the right pitch.
    - 50-66 s: normal speed, pitch down an octave, up a fifth, then a glide.
    - 66-88 s: slow motion + "harmony": the voice becomes a choir on a 5th + octaves.

  SLIDERS
    auto    : 1 = the piece drives itself (tour above), 0 = YOU drive with the sliders.
    speed   : read-head speed: 1 = normal, 0.05 = 20x slower, 0 = freeze, -1 = reverse.
    pitch   : transposition in semitones (-12..+12), independent of speed.
    harmony : adds a fifth above and octaves around the voice (0 = solo voice).
    space   : reverb amount.

  MIDI: no.   Sample: ../Manual/fox.wav (44.1 kHz mono, resampled by mincer).
*/

sr = 48000
ksmps = 64
nchnls = 2
0dbfs = 1

chnk "auto",    1, 2, 1,    0, 1
chnk "speed",   1, 2, 0.25, -1, 1
chnk "pitch",   1, 2, 0,    -12, 12
chnk "harmony", 1, 2, 0,    0, 1
chnk "space",   1, 2, 0.35, 0, 1
chnset 1,    "auto"
chnset 0.25, "speed"
chnset 0,    "pitch"
chnset 0,    "harmony"
chnset 0.35, "space"

; the whole recording in a table (GEN01, not normalised, channel 1)
giVoice ftgen 0, 0, 0, -1, "../Manual/fox.wav", 0, 0, 1

gkAutoSpeed init 1
gkAutoPitch init 0
gkAutoHarm  init 0

; ---------------------------------------------------------------- conductor (the "tour")
instr 1
  gkAutoSpeed linseg 1, 6, 1, 2, 0.25, 10, 0.25, 2, 0.05, 14, 0.05, 1, 0, 7, 0, 1, -1, 7, -1, 1, 1, 15, 1, 2, 0.08, 20, 0.08, 4, 1, 4, 1
  gkAutoPitch linseg 0, 50, 0, 0.01, -12, 6, -12, 0.01, 7, 6, 7, 4, -5, 0.01, 0, 1, 0
  gkAutoHarm  linseg 0, 66, 0, 4, 1, 18, 1, 4, 0, 1, 0
endin

; ---------------------------------------------------------------- the microscope
instr 2
  iLen  = ftlen(giVoice)
  iFsr  = ftsr(giVoice)                 ; 44100: the file's own rate
  kAuto chnget "auto"
  kSpd  chnget "speed"
  kSemi chnget "pitch"
  kHarm chnget "harmony"
  kWet  chnget "space"
  if kAuto > 0.5 then
    kSpd  = gkAutoSpeed
    kSemi = gkAutoPitch
    kHarm = gkAutoHarm
  endif
  kSpd  portk kSpd, 0.15
  kSemi portk kSemi, 0.05
  kHarm portk kHarm, 0.3
  kWet  portk kWet, 0.1

  ; read head: a phasor that runs through the file once per (file length / speed);
  ; negative frequency = backwards, 0 = frozen
  aPh   phasor kSpd * iFsr / iLen
  aTime = aPh * iLen / sr               ; mincer wants the time in table seconds
  kRat  = 2 ^ (kSemi / 12) * iFsr / sr  ; pitch ratio, corrected for 44.1 -> 48 kHz

  aRoot mincer aTime, 0.9, kRat, giVoice, 1, 2048, 4
  aFif  mincer aTime, 0.55 * kHarm, kRat * 1.4983, giVoice, 1, 2048, 4
  aLow  mincer aTime, 0.6 * kHarm, kRat * 0.5, giVoice, 1, 2048, 4
  aHigh mincer aTime, 0.3 * kHarm, kRat * 2, giVoice, 1, 2048, 4

  ; stereo picture: root centre, fifth left, octaves spread with short delays
  aFifD delay aFif, 0.023
  aHigD delay aHigh, 0.031
  aL = aRoot + 0.8 * aFifD + 0.6 * aLow + 0.3 * aHigD
  aR = aRoot + 0.3 * aFif  + 0.6 * aLow + 0.8 * aHigh
  aL dcblock2 aL
  aR dcblock2 aR

  aRL, aRR reverbsc aL, aR, 0.88, 9000
  kEnv linseg 0, 0.05, 1, p3 - 2.05, 1, 2, 0
  aOutL = (aL * (1 - 0.5 * kWet) + aRL * kWet) * 0.77 * kEnv
  aOutR = (aR * (1 - 0.5 * kWet) + aRR * kWet) * 0.77 * kEnv
  out aOutL, aOutR
endin

</CsInstruments>
<CsScore>
i1 0 96
i2 0 96
e
</CsScore>
</CsoundSynthesizer>
