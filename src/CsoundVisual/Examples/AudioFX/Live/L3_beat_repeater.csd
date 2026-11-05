<CsoundSynthesizer>
<CsOptions>
-odac -iadc
</CsOptions>
<CsInstruments>
/*
  LIVE FX L3 - TEMPO-SYNCED BEAT REPEATER  (endless, processes the audio input)

  BIG IDEA
    The glitch/stutter effect of electronic music, built from scratch: Csound
    keeps recording your track into a buffer and, on random beats, grabs the
    last 1/4, 1/8, 1/16 or 1/32 note and loops it in time with the Cubase
    tempo (HOST_BPM). Each repeat can slow down like a tape stop or play
    backwards. Because the grid comes from the host tempo, every stutter lands
    in time with the rest of your mix.

  LISTEN FOR
    - On drums: rolls, machine-gun 1/32 bursts and "tape-stop" pitch drops on
      random beats; the rest of the time the track passes untouched.
    - On vocals: syllables chopped into rhythmic stutters.

  SLIDERS
    chance   : probability that a beat is replaced by a repeat (0 = effect off).
    division : slice length: 0 = 1/4 note, 1 = 1/8, 2 = 1/16, 3 = 1/32 (3.5+ = random).
    length   : how many beats a repeat lasts (1-4).
    tape     : pitch fall during the repeat (0 = none, 1 = full tape stop).
    reverse  : probability that the repeated slice plays backwards.

  MIDI: no.   Input: the track's audio. For the Csound Visual FX plugin in Cubase.
  Tempo: reads the "HOST_BPM" channel (120 if the host does not send it). The
  beat grid starts when playback starts (the plugin does not receive bar positions).
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1
seed 0

chnk "chance",   1, 2, 0.35, 0, 1
chnk "division", 1, 2, 4,    0, 4
chnk "length",   1, 2, 1,    1, 4
chnk "tape",     1, 2, 0.3,  0, 1
chnk "reverse",  1, 2, 0.2,  0, 1
chnset 0.35, "chance"
chnset 4,    "division"
chnset 1,    "length"
chnset 0.3,  "tape"
chnset 0.2,  "reverse"

giLen  = 8 * sr
giBufL ftgen 0, 0, -giLen, -2, 0          ; 8-second circular buffers
giBufR ftgen 0, 0, -giLen, -2, 0
gkWpos init 0
gkRep  init 0                             ; 1 while a repeat is playing
gaRL   init 0
gaRR   init 0

; ---------------------------------------------------------------- recorder + beat clock
instr 1
  ain1, ain2 in
  aW   phasor sr / giLen
  aIdx = aW * giLen
  tablew ain1, aIdx, giBufL
  tablew ain2, aIdx, giBufR
  gkWpos = k(aIdx)

  kBpm chnget "HOST_BPM"
  kBpm = (kBpm > 20 ? kBpm : 120)
  kChance chnget "chance"
  kDiv    chnget "division"
  kLenB   chnget "length"
  kTape   chnget "tape"
  kRev    chnget "reverse"

  kBusy init 0                            ; beats left in the current repeat
  if metro(kBpm / 60) == 1 then
    kBusy = max(kBusy - 1, 0)
    if kBusy == 0 && random:k(0, 1) < kChance then
      kD = (kDiv > 3.5 ? int(random:k(0, 3.99)) : int(kDiv + 0.5))
      kBeat  = 60 / kBpm
      kSlice = kBeat / 2 ^ kD             ; 1/4, 1/8, 1/16, 1/32 note in seconds
      kBeats = int(kLenB + 0.5)
      kBusy  = kBeats
      kR     = (random:k(0, 1) < kRev ? 1 : 0)
      kT     = (random:k(0, 1) < 0.6 ? kTape : 0)
      event "i", 10, 0, kBeats * kBeat, kSlice, kR, kT
    endif
  endif
endin

; ---------------------------------------------------------------- one repeat
; p4 = slice length (s), p5 = reverse, p6 = tape-stop amount
instr 10
  iSl    = p4 * sr
  iStart = i(gkWpos) - iSl                ; the slice that has just been played
  kRate  = 1 - p6 * 0.9 * linseg:k(0, p3, 1)   ; slows down towards the end
  aPh    phasor kRate * sr / iSl          ; position inside the slice, 0..1
  aOff   = (p5 > 0.5 ? (1 - aPh) : aPh) * iSl
  aL     table3 iStart + aOff, giBufL, 0, 0, 1
  aR     table3 iStart + aOff, giBufR, 0, 0, 1
  ; 2 ms fade at each loop point (no clicks)
  aWin   = limit((0.5 - abs(aPh - 0.5)) * iSl / (0.002 * sr), 0, 1)
  ; tape stop also darkens
  kCut   = 18000 * kRate ^ 2 + 400
  aL     tone aL * aWin, kCut
  aR     tone aR * aWin, kCut
  gaRL  += aL
  gaRR  += aR
  gkRep  = 1
endin

; ---------------------------------------------------------------- mixer: repeat replaces the dry signal
instr 99
  ain1, ain2 in
  kX   portk gkRep, 0.004                 ; quick crossfade dry <-> repeat
  aX   interp kX
  out ain1 * (1 - aX) + gaRL * aX, ain2 * (1 - aX) + gaRR * aX
  gkRep = 0
  clear gaRL, gaRR
endin

</CsInstruments>
<CsScore>
i1  0 z
i99 0 z
e
</CsScore>
</CsoundSynthesizer>
