<CsoundSynthesizer>
<CsOptions>
</CsOptions>
<CsInstruments>
; =============================================================================
;  Csound Visual FX - default patch "Wide Echo Space"
;
;  Stereo effect on the track's audio: modulated ping-pong delay with a
;  darkening feedback filter, a gentle chorus on the echoes and a large
;  stereo reverb, mixed with the dry signal.
;
;  The chnk channels below become the plug-in parameters (Param 1..16 are
;  renamed to these names in declaration order, with these ranges).
;  sr is replaced by the host's sample rate; ksmps is kept.
;
;  How this file is used:
;   * CMakeLists.txt embeds it verbatim into the plug-in as a C++ raw string
;     literal (kDefaultFxCsd), so it must not contain the closing delimiter of
;     that literal, nor CMake at-sign variable references.
;   * It is the orchestra of every new FX instance and the fallback when a
;     saved project carries no CSD text. Sending another CSD from the Csound
;     Visual app replaces it.
;   * Before compiling, the plug-in empties the options section and adds an
;     endless f0 z event to the score, so the effect never stops inside the
;     DAW.
;   * Signal flow: a single always-on instrument reads the track's stereo
;     input (inch), processes it and writes the plug-in output (out).
; =============================================================================

; --- header ---
; sr       : placeholder, overridden by the host rate (--sample-rate option).
; ksmps    : samples per control cycle. Also the plug-in latency reported to
;            the host (32 samples = 0.67 ms at 48 kHz); the host compensates it
;            and the bypass path delays the dry signal by the same amount.
; nchnls   : stereo output, the plug-in output bus.
; nchnls_i : stereo input, fed from the plug-in input bus.
; 0dbfs    : 1, so audio is in the -1..1 range used by the host.
sr       = 48000
ksmps    = 32
nchnls   = 2
nchnls_i = 2
0dbfs    = 1

; --- control channels = plug-in parameters ---
; chnk declares an input control channel with display hints. Arguments:
;   name, mode (1 = input), type (1 = integer, 2 = linear, 3 = exponential),
;   default, minimum, maximum.
; The plug-in assigns them to Param 1..5 in this order, maps the host's 0..1
; parameter value to minimum..maximum (logarithmically for type 3), and resets
; them to their defaults on each load.
;   mix      : dry/wet balance, 0 = dry only, 1 = wet only
;   time     : left delay time in seconds (the right side uses 1.5 times it)
;   feedback : echo feedback amount, below 1 so the echoes always die out
;   tone     : low-pass cutoff in Hz applied to every repeat and to the reverb
;   size     : reverb feedback (room size), 0.3..0.97
;        name         mode type default  min     max      (type 2 = linear, 3 = exponential)
chnk "mix",          1,   2,   0.35,    0,      1
chnk "time",         1,   3,   0.375,   0.02,   2
chnk "feedback",     1,   2,   0.45,    0,      0.95
chnk "tone",         1,   3,   5500,    300,    18000
chnk "size",         1,   2,   0.82,    0.3,    0.97

; ---------------------------------------------------------------------------
;  The effect (one instance, started below and running forever)
; ---------------------------------------------------------------------------
instr 1
  ; Track audio: input channels 1 and 2 of the plug-in.
  ainL, ainR inch 1, 2

  ; Parameters. mix and time are smoothed (port) because jumps in them would
  ; click; feedback, tone and size are used as they come.
  kmix   chnget "mix"
  kmix   port kmix, 0.05
  ktime  chnget "time"
  ktime  port ktime, 0.2               ; glide instead of clicks when the time moves
  kfb    chnget "feedback"
  ktone  chnget "tone"
  ksize  chnget "size"

  ; --- modulated ping-pong delay (feedback crosses channels) ---
  ; afbL/afbR hold the filtered echoes of the previous control cycle; they are
  ; written further down and read here, which closes the feedback loop.
  ; amod1/amod2: slow LFOs (0.37 and 0.41 Hz, starting half a cycle apart)
  ; that wobble the delay times by +/-2.1 ms for a tape-like movement.
  afbL   init 0
  afbR   init 0
  amod1  oscili 0.0021, 0.37
  amod2  oscili 0.0021, 0.41, -1, 0.5
  adt    = a(ktime)

  ; Delay line lengths follow the longest tap each one must hold:
  ;   left : time (max 2 s) + modulation (2.1 ms) + offset (2.5 ms) = 2.0046 s
  ;          -> 2.2 s;
  ;   right: 1.5 * time (max 3 s) + 2.1 ms + 2.5 ms = 3.0046 s -> 3.1 s.
  ; (The right line used to be 2.2 s too, so any time above ~1.46 s asked
  ; deltap3 for more delay than the buffer holds and the right echo stopped
  ; following the parameter.) deltap3 reads with cubic interpolation, so the
  ; moving delay time stays smooth; the extra 2.5 ms is a small safety offset
  ; that keeps the modulated tap away from zero delay.
  ; The right tap is 1.5 times longer (dotted rhythm against the left), and
  ; each line is fed by its own input plus the opposite side's echo: the
  ; repeats bounce between left and right.
  abufL  delayr 2.2
  atapL  deltap3 adt + amod1 + 0.0025
         delayw ainL + afbR * kfb
  abufR  delayr 3.1
  atapR  deltap3 adt * 1.5 + amod2 + 0.0025
         delayw ainR + afbL * kfb

  ; One-pole low-pass (tone) in the feedback path, then a DC blocker so no
  ; offset can build up in the loop.
  afbL   tone atapL, ktone              ; each repeat gets darker
  afbR   tone atapR, ktone
  afbL   dcblock2 afbL
  afbR   dcblock2 afbR

  ; --- chorus on the echoes ---
  ; Short modulated delays (9 and 12 ms, +/-3 ms, in ms for vdelay3) cross-mixed
  ; into the opposite channel thicken and widen the echoes only, not the dry
  ; signal.
  alfo1  oscili 1, 0.21
  alfo2  oscili 1, 0.27, -1, 0.25
  achL   vdelay3 afbL, 9 + 3 * alfo1, 30
  achR   vdelay3 afbR, 12 + 3 * alfo2, 30
  aechoL = afbL * 0.7 + achR * 0.4
  aechoR = afbR * 0.7 + achL * 0.4

  ; --- reverb fed by dry + echoes ---
  ; size is the reverbsc feedback (decay length); tone doubles as its damping
  ; cutoff in Hz, so darker echoes also mean a darker room.
  arvL, arvR reverbsc ainL * 0.5 + aechoL * 0.6, ainR * 0.5 + aechoR * 0.6, ksize, ktone

  ; --- output: linear crossfade between the dry input and the wet signal ---
  awetL  = aechoL * 0.8 + arvL * 0.9
  awetR  = aechoR * 0.8 + arvR * 0.9
  aoutL  = ainL * (1 - kmix) + awetL * kmix
  aoutR  = ainR * (1 - kmix) + awetR * kmix
  out    aoutL, aoutR
endin
; Start the effect at time 0 with an infinite duration (-1): an FX processes
; its input continuously, no MIDI note is needed.
schedule 1, 0, -1

</CsInstruments>
<CsScore>
f0 z
</CsScore>
</CsoundSynthesizer>
