<CsoundSynthesizer>
<CsOptions>
</CsOptions>
<CsInstruments>
; =============================================================================
;  Csound Visual Instrument - default patch "Velvet Poly"
;
;  A warm stereo polysynth: three detuned saw oscillators + triangle sub per
;  voice, resonant ladder low-pass with a velocity/envelope sweep, ADSR, then a
;  shared stereo chorus and a large reverb.
;
;  Plays from the DAW's MIDI track (massign). Pitch bend = +/-2 semitones,
;  mod wheel (CC1) = vibrato, CC11 = expression, sustain pedal works.
;
;  The chnk channels below become the plug-in parameters (Param 1..16 are
;  renamed to these names in declaration order, with these ranges).
;  sr is replaced by the host's sample rate; ksmps is kept.
;
;  How this file is used:
;   * CMakeLists.txt embeds it verbatim into the plug-in as a C++ raw string
;     literal (kDefaultInstrumentCsd), so it must not contain the closing
;     delimiter of that literal, nor CMake at-sign variable references.
;   * It is the orchestra of every new Instrument instance and the fallback
;     when a saved project carries no CSD text. Sending another CSD from the
;     Csound Visual app replaces it.
;   * Before compiling, the plug-in empties the options section and adds an
;     endless f0 z event to the score, so the patch never stops inside the DAW.
;   * Signal flow: instr 1 (one instance per held MIDI note) mixes into the
;     global stereo bus gaBusL/gaBusR; instr 100 (always on) adds chorus,
;     reverb and volume, writes the plug-in output and clears the bus.
; =============================================================================

; --- header ---
; sr     : placeholder, overridden by the host rate (--sample-rate option).
; ksmps  : samples per control cycle. Also the plug-in latency reported to
;          the host (32 samples = 0.67 ms at 48 kHz) and the timing grid of
;          MIDI events and parameter changes. Keep it small (16..64).
; nchnls : stereo output, the plug-in output bus.
; 0dbfs  : 1, so audio is in the -1..1 range used by the host.
sr     = 48000
ksmps  = 32
nchnls = 2
0dbfs  = 1

; --- control channels = plug-in parameters ---
; chnk declares an input control channel with display hints. Arguments:
;   name, mode (1 = input), type (1 = integer, 2 = linear, 3 = exponential),
;   default, minimum, maximum.
; The plug-in assigns them to Param 1..6 in this order, maps the host's 0..1
; parameter value to minimum..maximum (logarithmically for type 3, used for
; frequencies and times), and resets them to their defaults on each load.
;   cutoff    : filter cutoff in Hz before velocity/envelope scaling
;   resonance : moogladder2 resonance, 0..0.9 (keeps clear of self-oscillation)
;   attack    : envelope attack in seconds (read once per note)
;   release   : envelope release in seconds (read once per note)
;   reverb    : reverb send/mix amount, 0..1
;   volume    : master output gain, linear 0..1
;        name         mode type default  min     max      (type 2 = linear, 3 = exponential)
chnk "cutoff",       1,   3,   2400,    80,     16000
chnk "resonance",    1,   2,   0.3,     0,      0.9
chnk "attack",       1,   3,   0.02,    0.001,  4
chnk "release",      1,   3,   0.9,     0.01,   8
chnk "reverb",       1,   2,   0.35,    0,      1
chnk "volume",       1,   2,   0.7,     0,      1

; --- MIDI routing ---
; The plug-in converts the DAW's note events (and pitch bend, CC1, CC2, CC11,
; CC64, aftertouch) to MIDI bytes that Csound reads as its MIDI input. Each
; note-on starts a new instance of instr 1, held until the note-off plus the
; release time of its envelopes.
massign   0, 1          ; every MIDI channel plays instr 1
pgmassign 0, 0          ; ignore program changes

; Global stereo bus: summed by every voice, consumed and cleared by instr 100.
gaBusL init 0
gaBusR init 0

; ---------------------------------------------------------------------------
;  Voice (one instance per MIDI note)
; ---------------------------------------------------------------------------
instr 1
  ; Note number (0..127) and velocity scaled to 0..1.
  inote  notnum
  ivel   veloc 0, 1
  ; Envelope times are read at note start (i-time), with safety minimums.
  iatt   = max(chnget:i("attack"), 0.001)
  irel   = max(chnget:i("release"), 0.01)

  ; --- pitch: bend, vibrato, expression ---
  kbend  pchbend 0, 2                  ; semitones
  kwheel midictrl 1, 0, 1              ; mod wheel -> vibrato depth
  kexpr  midictrl 11, 0, 1
  kexpr  = (kexpr == 0 ? 1 : kexpr)    ; CC11 not sent yet -> full level
  ; 5.2 Hz sine vibrato: a light 0.4 % depth always, up to 1.6 % with the wheel.
  kvib   lfo 0.004 + kwheel * 0.012, 5.2
  kcps   = cpsmidinn(inote + kbend) * (1 + kvib)

  ; --- envelopes (madsr and linsegr keep the voice alive after the note-off
  ; for the release time irel, then it ends) ---
  ; aenv: amplitude ADSR (decay 0.6 s, sustain 75 %).
  ; kfeg: filter envelope, 1 -> 0.35 over 0.9 s, falling to 0.15 on release.
  aenv   madsr iatt, 0.6, 0.75, irel
  kfeg   linsegr 1, 0.9, 0.35, irel, 0.15

  ; --- oscillators: two saws detuned by about +/-6 cents, a saw one octave up,
  ; and a triangle sub-oscillator (vco2 mode 12) one octave down ---
  a1     vco2 1, kcps * 1.0035
  a2     vco2 1, kcps * 0.9965
  a3     vco2 1, kcps * 2.001
  asub   vco2 1, kcps * 0.5, 12        ; triangle one octave below
  amix   = (a1 + a2) * 0.32 + a3 * 0.1 + asub * 0.3

  ; --- filter ---
  ; The cutoff parameter is smoothed (port, 20 ms) to avoid zipper noise from
  ; automation. Effective cutoff = parameter scaled by velocity (harder =
  ; brighter) and by the filter envelope, clamped to 30..18000 Hz.
  kcut   chnget "cutoff"
  kcut   port kcut, 0.02
  kres   chnget "resonance"
  kf     limit kcut * (0.55 + 0.6 * ivel) * (0.6 + 1.4 * kfeg), 30, 18000
  afil   moogladder2 amix, kf, kres

  ; --- level and stereo placement ---
  ; Velocity also scales loudness. Each pitch class gets a fixed pan position
  ; (C left of centre ... B right of centre, within about +/-0.2), with an
  ; equal-power (square root) pan law. The voice adds itself to the bus.
  aout   = afil * aenv * (0.2 + 0.5 * ivel) * kexpr * 1.5
  ipan   = 0.5 + ((inote % 12) - 5.5) / 28
  gaBusL += aout * sqrt(1 - ipan)
  gaBusR += aout * sqrt(ipan)
endin

; ---------------------------------------------------------------------------
;  Chorus + reverb + volume (always on)
;
;  Numbered 100 so it runs after every voice (instr 1) in each control cycle:
;  Csound performs instruments in ascending number order, so the bus is
;  complete when this instrument reads it.
; ---------------------------------------------------------------------------
instr 100
  ; Parameters, smoothed over 50 ms.
  krev   chnget "reverb"
  krev   port krev, 0.05
  kvol   chnget "volume"
  kvol   port kvol, 0.05

  ; --- stereo chorus ---
  ; Two slow sine LFOs (0.23 and 0.29 Hz, a quarter cycle apart) sweep two
  ; modulated delays around 11 and 13 ms (+/-3.5 ms; vdelay3 times are in ms,
  ; 40 ms maximum). Each chorused side is mixed into the opposite channel to
  ; widen the image.
  alfo1  oscili 1, 0.23
  alfo2  oscili 1, 0.29, -1, 0.25
  achL   vdelay3 gaBusL, 11 + 3.5 * alfo1, 40
  achR   vdelay3 gaBusR, 13 + 3.5 * alfo2, 40
  adryL  = gaBusL * 0.75 + achR * 0.45
  adryR  = gaBusR * 0.75 + achL * 0.45

  ; --- reverb: feedback 0.87 (a large room), damping cutoff 9 kHz ---
  ; The reverb parameter crossfades: the dry part drops to 50 % at most while
  ; the wet part rises; volume scales the result.
  arvL, arvR reverbsc adryL, adryR, 0.87, 9000
  aoutL  = (adryL * (1 - krev * 0.5) + arvL * krev * 0.9) * kvol
  aoutR  = (adryR * (1 - krev * 0.5) + arvR * krev * 0.9) * kvol
  out    tanh(aoutL), tanh(aoutR)    ; soft safety limiter for big chords
  ; Reset the bus for the next control cycle (the voices accumulate with +=).
  clear  gaBusL, gaBusR
endin
; Start the effects instrument at time 0 with an infinite duration (-1).
schedule 100, 0, -1

</CsInstruments>
<CsScore>
f0 z
</CsScore>
</CsoundSynthesizer>
