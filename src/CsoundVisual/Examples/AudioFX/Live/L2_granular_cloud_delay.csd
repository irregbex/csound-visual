<CsoundSynthesizer>
<CsOptions>
-odac -iadc
</CsOptions>
<CsInstruments>
/*
  LIVE FX L2 - GRANULAR CLOUD DELAY  (endless, processes the audio input)

  BIG IDEA
    The last 4 seconds of your track live in a circular buffer. Dozens of
    tiny "grains" (20-400 ms windowed snippets) are read back from random
    points of that past, each with its own pitch (unison, octaves, fifths),
    direction and stereo position. A delay that, instead of repeating the
    sound, dissolves it into a cloud; with feedback the cloud feeds on itself
    and keeps rising like a shimmer reverb.

  LISTEN FOR
    - On drums: hits smeared into rhythmic dust and pitched "ghost" echoes.
    - On a voice: a whispering crowd built from the singer's own last words.
    - High "pitch" + feedback = octave-climbing shimmer clouds.

  SLIDERS
    density  : grains per second (2-80).
    size     : grain length (20-400 ms): short = glitter, long = smeared echoes.
    pitch    : 0 = all grains at the original pitch, 1 = scattered octaves/fifths, some reversed.
    feedback : how much of the cloud is fed back into the buffer (0-0.85).
    mix      : cloud level (the dry signal is never delayed).

  MIDI: no.   Input: the track's audio. For the Csound Visual FX plugin in Cubase.
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "density",  1, 3, 20,  2, 80
chnk "size",     1, 3, 0.12, 0.02, 0.4
chnk "pitch",    1, 2, 0.4, 0, 1
chnk "feedback", 1, 2, 0.35, 0, 0.85
chnk "mix",      1, 2, 0.5, 0, 1
chnset 20,   "density"
chnset 0.12, "size"
chnset 0.4,  "pitch"
chnset 0.35, "feedback"
chnset 0.5,  "mix"

giLen = 4 * sr
giBuf ftgen 0, 0, -giLen, -2, 0           ; 4-second circular buffer (mono)
giWin ftgen 0, 0, 8193, 20, 2, 1          ; Hanning grain window
; pitch choices for scattered grains: unison, octave up/down, fifth up, two octaves up
giPitch ftgen 0, 0, 8, -2, 1, 1, 2, 0.5, 1.5, 1, 2, 4

gkWpos init 0
gaGL   init 0
gaGR   init 0
gaFb   init 0

; ---------------------------------------------------------------- input -> buffer + grain scheduler
instr 1
  ain1, ain2 in
  kDens chnget "density"
  kSize chnget "size"
  kPit  chnget "pitch"
  kFb   chnget "feedback"

  ; write (input + fed-back cloud) into the circular buffer
  aW    phasor sr / giLen
  aIdx  = aW * giLen
  aIn   = (ain1 + ain2) * 0.5 + gaFb * kFb
  aIn   = tanh(aIn)                       ; feedback can never explode
  tablew aIn, aIdx, giBuf
  gkWpos = k(aIdx)

  ; grains at random (Poisson-like) moments
  kNext init 0
  kNext -= ksmps / sr
  if kNext <= 0 then
    kNext  = -log(random:k(0.001, 1)) / kDens
    kDur   = kSize * random:k(0.7, 1.3)
    kAge   = random:k(kDur * 4 + 0.05, 3.8)          ; how far into the past
    kRat   = 1
    if random:k(0, 1) < kPit then
      kRat = tab:k(int(random:k(0, 7.99)), giPitch)
    endif
    kRev   = (random:k(0, 1) < kPit * 0.35 ? 1 : 0)
    event "i", 10, 0, kDur, kAge, kRat, kRev, random:k(0.1, 0.9)
  endif
endin

; ---------------------------------------------------------------- one grain
instr 10
  iStart = i(gkWpos) - p4 * sr
  iRate  = p5 * (p6 > 0.5 ? -1 : 1)
  aPos   line iStart, p3, iStart + iRate * p3 * sr
  aSig   table3 aPos, giBuf, 0, 0, 1     ; wrap-around read
  aEnv   oscili 1, 1 / p3, giWin
  aSig  *= aEnv
  gaGL  += aSig * sqrt(1 - p7)
  gaGR  += aSig * sqrt(p7)
endin

; ---------------------------------------------------------------- mixer
instr 99
  ain1, ain2 in
  kMix chnget "mix"
  kMix portk kMix, 0.05
  ; the cloud is gently low-passed and widened
  aL tone gaGL, 9000
  aR tone gaGR, 9000
  gaFb = (aL + aR) * 0.5
  aRL, aRR reverbsc aL, aR, 0.6, 7000
  aL = aL + aRL * 0.3
  aR = aR + aRR * 0.3
  out ain1 * (1 - 0.4 * kMix) + aL * kMix * 1.6, ain2 * (1 - 0.4 * kMix) + aR * kMix * 1.6
  clear gaGL, gaGR
endin

</CsInstruments>
<CsScore>
i1  0 z
i99 0 z
e
</CsScore>
</CsoundSynthesizer>
