<CsoundSynthesizer>
<CsOptions>
-odac -iadc
</CsOptions>
<CsInstruments>
/*
  LIVE FX L0 - INPUT CHECK  (endless, processes the audio input)

  BIG IDEA
    The first thing to try with a microphone or an audio interface: it lets
    you hear your input through Csound and proves that it arrives. The signal
    goes through a filter that opens the louder you play (an "auto-wah"), a
    ping-pong delay and a reverb. Every second the console prints the input
    level, so you can check it even without speakers.

  HOW TO USE IT IN CSOUND VISUAL
    1. Settings > Audio: engine "Windows - WASAPI shared", Input = your
       microphone or interface input (e.g. "Line (Steinberg UR22mkII)").
    2. Use HEADPHONES: with speakers the microphone hears the delay and
       reverb and feeds back.
    3. Play (F5). The IN meter under the output meters shows the input level.
    It also works in the Csound Visual FX plugin in Cubase (Send to DAW).

  LISTEN FOR
    - Soft playing sounds dark; loud playing opens the filter (more "wah"
      with a higher "wah" slider).
    - Repeats bounce left-right; "feedback" makes them last longer.
    - "monitor" is the clean input; "fx" is the processed layer.

  SLIDERS
    gain     : input gain before everything (1 = unchanged, up to +12 dB).
    monitor  : level of the clean (dry) input.
    wah      : how far the input level opens the filter (0 = fixed, dark).
    delay    : time between repeats, in seconds.
    feedback : how many repeats (keep it below 0.8 for safety).
    reverb   : amount of reverb on the processed layer.
    fx       : level of the processed layer (filter + delay + reverb).

  MIDI: no.   Input: "ain1, ain2 in" (a mono input reaches both channels).
  The output is soft-limited to -0.5 dBFS.
*/

sr = 48000
ksmps = 32
nchnls = 2
0dbfs = 1

; chnk's default value does not initialise the channel in Csound 7, hence the chnset lines.
chnk "gain",     1, 2, 1,    0,    4
chnk "monitor",  1, 2, 0.7,  0,    1
chnk "wah",      1, 2, 0.6,  0,    1
chnk "delay",    1, 2, 0.32, 0.05, 1.5
chnk "feedback", 1, 2, 0.35, 0,    0.9
chnk "reverb",   1, 2, 0.35, 0,    1
chnk "fx",       1, 2, 0.6,  0,    1
chnset 1,    "gain"
chnset 0.7,  "monitor"
chnset 0.6,  "wah"
chnset 0.32, "delay"
chnset 0.35, "feedback"
chnset 0.35, "reverb"
chnset 0.6,  "fx"

instr 1
  ain1, ain2 in

  kGain  chnget "gain"
  kMon   chnget "monitor"
  kWah   chnget "wah"
  kDelay chnget "delay"
  kFb    chnget "feedback"
  kRev   chnget "reverb"
  kFx    chnget "fx"
  ; portk uses a HALF time: these smooth the slider moves (no zipper noise)
  kGain  portk kGain, 0.02
  kMon   portk kMon, 0.02
  kFx    portk kFx, 0.02
  kDelay portk kDelay, 0.1
  kFb    limit kFb, 0, 0.9

  aL = ain1 * kGain
  aR = ain2 * kGain

  ; ---- level follower: instant attack, ~0.5 s release
  kLev   rms (aL + aR) * 0.5, 20
  kEnv   init 0
  kEnv   = (kLev > kEnv ? kLev : kEnv * 0.9955)
  ; -50 dBFS (or less) -> 0, 0 dBFS -> 1
  kNorm  limit (dbamp(max(kEnv, 0.00001)) + 50) / 50, 0, 1
  ; cutoff from 300 Hz up to 6 octaves higher (19 kHz) as the level rises
  kCut   = 300 * 2 ^ (6 * kNorm * kWah)
  kCut   portk kCut, 0.01
  aFL    moogladder aL, kCut, 0.5
  aFR    moogladder aR, kCut, 0.5

  ; ---- ping-pong delay: each side feeds the other one
  aTapL  init 0
  aTapR  init 0
  aTime  interp kDelay
  aBufL  delayr 2
  aTapL  deltap3 aTime
         delayw aFL + aTapR * kFb
  aBufR  delayr 2
  aTapR  deltap3 aTime
         delayw aFR * 0.3 + aTapL * kFb

  ; ---- reverb on the processed layer
  aRvL, aRvR reverbsc aFL + aTapL, aFR + aTapR, 0.86, 9000

  aWetL  = aFL * 0.5 + aTapL + aRvL * kRev * 1.5
  aWetR  = aFR * 0.5 + aTapR + aRvR * kRev * 1.5
  aOutL  = aL * kMon + aWetL * kFx
  aOutR  = aR * kMon + aWetR * kFx
  ; soft limiter: transparent below about -6 dBFS, never above -0.5 dBFS
  aOutL  = tanh(aOutL * 1.06) * 0.94
  aOutR  = tanh(aOutR * 1.06) * 0.94
  out aOutL, aOutR

  ; ---- console: input level once per second (proves the input arrives)
  kInL   rms ain1, 5
  kInR   rms ain2, 5
  printks "IN  L %6.1f dBFS   R %6.1f dBFS   filter %5.0f Hz\n", 1, \
          dbamp(max(kInL, 0.00001)), dbamp(max(kInR, 0.00001)), kCut
endin

</CsInstruments>
<CsScore>
i1 0 z
e
</CsScore>
</CsoundSynthesizer>
