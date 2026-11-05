// PaintCsoundCode.cs — Paint Rooms foundation.
//
// Role: writes the Csound code that turns an image into sound. The SAME instrument text is used
//   * live, compiled into whatever performance is running (the user's document or the small
//     «Paint Rooms host» the app starts), with its tables filled directly from memory
//     (PaintLivePlayer), and
//   * offline / for learning, inside a standalone .csd whose tables are loaded from data files
//     written next to it (WriteStandaloneCsd): «Show Csound code» opens that .csd in the editor and
//     the WAV render runs exactly that file.
//
// HOW THE INSTRUMENT WORKS (Csound «instr 9301»):
//   * Every control period (ksmps samples) it computes the fractional column kPos that should be
//     sounding, copies the two image columns around it out of the big image table with ftslice
//     (a native table-to-table copy) into k-arrays, and blends them (glide).
//   * One-pole smoothing per row, with different coefficients for rising and falling amplitudes
//     (attack/release), written with whole-array arithmetic so it costs one interpreter step for
//     all rows instead of one per row.
//   * The result goes into small amplitude tables that adsynt2 — Csound's additive oscillator bank,
//     one oscillator per row with frequencies from a table — reads every control period, with
//     linear amplitude interpolation inside the period (no zipper noise).
//   * Noise-band mode drives TWO banks (sine and cosine waves) with independent, low-passed
//     Gaussian amplitude modulators per row: a(t)·sin + b(t)·cos with slowly varying random a, b is
//     exactly a narrow band of noise around the row frequency.
//   * Sample-loop mode uses the sound file itself as the bank's waveform.
//   * Per-cell tone/noise mix (p14 = 1, Sines mode with a noise layer — see SpectralImage.Noise):
//     each row's amplitude a is split by the cell's noisiness n with an EQUAL-POWER crossfade,
//     a·√(1−n) for the tone and a·√n for the noise band. The tone needs no bank of its own: it RIDES
//     ON the noise bands' sine oscillator, whose amplitude becomes a·√(1−n) + a·√n·N₁ (N₁, N₂ = the
//     row's slowly varying Gaussian modulators; the cosine bank keeps a·√n·N₂). Zero-mean noise
//     makes the cross term vanish, so the row's power is a²(1−n)/2 + a²n/2 = a²/2 for every n:
//     turning a cell noisier never makes it louder or softer — and the mixed path costs exactly
//     what the Noise-bands mode costs (measured: a dense 512-row stereo picture with separate tone
//     and noise banks dropped out at 10 ms periods; this way it runs like Noise bands). n is read
//     per column from its own table (ImageN), blended between columns like the levels and smoothed
//     with the attack coefficient, so a step from tone to noise is not a click.
//   * Two banks (left/right layers) only when the image is stereo; a mono image costs one bank.
// Measured (Csound CLI, 48 kHz, ksmps 32, 512 rows × 1024 columns): stereo sines ≈ 5 % of one CPU
// core, stereo noise bands ≈ 11 %.
//
// Users: PaintLivePlayer (Instruments, LiveTableLines, LiveData, ChannelInit, Fields — live play),
// PaintRenderer (WriteStandaloneCsd — «Show Csound code» and the offline WAV render) and
// PaintSelfTest (indirectly, through PaintLivePlayer.HostCsd).
//
// Design decisions:
//   * Fixed instrument numbers (9301/9302) and a fixed table range (31000 + 16·set) far above what
//     hand-written orchestras normally use, so Paint Rooms can be compiled into the USER'S running
//     document without clashing with its instruments or tables.
//   * Everything image-specific (size, duration, mode, table base…) travels in p-fields, so the
//     instrument text never changes and is compiled at most once per performance.
//   * All numbers written into code go through F() (invariant culture): a Spanish Windows would
//     otherwise write «0,5», which Csound parses as two arguments.
//   * The generated code is meant to be READ by users learning Csound: it is commented line by line
//     and the comments are part of the product (do not strip them).

using System.Globalization;
using System.Text;

namespace CsoundVisual.Paint;

/// <summary>Generates the Paint Synth orchestra code, score lines and standalone .csd files.</summary>
/// <remarks>Pure text generation plus (for <see cref="WriteStandaloneCsd"/>) file writing; no Csound
/// API calls, so it can run on any thread.</remarks>
public static class PaintCsoundCode
{
    /// <summary>Instrument number of the image player.</summary>
    /// <remarks>Live notes use fractional instances (9301.0001…) so each play can be stopped individually
    /// (see <see cref="PaintLivePlayer"/>).</remarks>
    public const int InstrNumber = 9301;

    /// <summary>Instrument number of the helper that stops every image player (with release).</summary>
    public const int StopInstrNumber = 9302;

    /// <summary>First table number used by Paint Rooms (sets of <see cref="TablesPerSet"/> follow).</summary>
    public const int FirstTable = 31000;

    /// <summary>Tables per image set (see <see cref="TableMap"/>); sets start every this many numbers.</summary>
    /// <remarks>All 16 offsets (0…15) are used since the noise layer arrived (ImageN, AmpLS, AmpRS).</remarks>
    public const int TablesPerSet = 16;

    /// <summary>Channel with the overall linear gain.</summary>
    /// <remarks>Read by instr 9301 with <c>chnget</c> every control period, so gain changes are heard at once.</remarks>
    public const string GainChannel = "PaintRooms.gain";

    /// <summary>Channel with the attack (rise) time in seconds.</summary>
    public const string AttackChannel = "PaintRooms.attack";

    /// <summary>Channel with the release (fall) time in seconds.</summary>
    public const string ReleaseChannel = "PaintRooms.release";

    /// <summary>Offsets of the tables of one set relative to its base number (documented in the generated code too).</summary>
    /// <remarks>
    /// Sizes are given in table points: W = image columns, H = image rows. These offsets must match
    /// the <c>iBase + n</c> lines of section 2 of <see cref="Instruments"/>.
    /// </remarks>
    public static class TableMap
    {
        /// <summary>Left amplitudes, (W+1)·H.</summary>
        public const int ImageL = 0;
        /// <summary>Right amplitudes, (W+1)·H.</summary>
        public const int ImageR = 1;
        /// <summary>Row frequencies (Hz) or loop rates, H.</summary>
        public const int Freq = 2;
        /// <summary>Bank amplitudes read by adsynt2 (left sine bank), H.</summary>
        public const int AmpLA = 3;
        /// <summary>Left cosine bank (noise), H.</summary>
        public const int AmpLB = 4;
        /// <summary>Right sine bank, H.</summary>
        public const int AmpRA = 5;
        /// <summary>Right cosine bank (noise), H.</summary>
        public const int AmpRB = 6;
        /// <summary>Scratch column, H.</summary>
        public const int Column = 7;
        /// <summary>Gaussian noise source, 65536.</summary>
        public const int Noise = 8;
        /// <summary>Noise modulator coefficients, H.</summary>
        public const int Coef = 9;
        /// <summary>Noise modulator gains, H.</summary>
        public const int Norm = 10;
        /// <summary>Waveform A: sine (16384) or the sample (2^k).</summary>
        public const int WaveA = 11;
        /// <summary>Waveform B: cosine (16384).</summary>
        public const int WaveB = 12;
        /// <summary>Per-cell noisiness 0…1, (W+1)·H when the picture mixes tone and noise, else H zeros (a placeholder).</summary>
        public const int ImageN = 13;
        /// <summary>Amplitudes of the TONAL (sine / sample) bank, left, H.</summary>
        public const int AmpLS = 14;
        /// <summary>Amplitudes of the tonal bank, right, H.</summary>
        public const int AmpRS = 15;
    }

    /// <summary>Invariant culture for every number written into Csound code (decimal point, no grouping).</summary>
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Formats a number for Csound code (invariant culture, up to 9 decimals).</summary>
    /// <param name="v">The value.</param>
    /// <returns>E.g. <c>0.5</c>, <c>440</c>, <c>0.000123457</c>; never an exponent for the magnitudes
    /// used here, never a decimal comma.</returns>
    public static string F(double v) => v.ToString("0.#########", Ci);

    /// <summary>
    /// The instruments: 9301 plays one image, 9302 stops all 9301 instances with their release.
    /// Identical in live and standalone use; everything image-specific comes from p-fields and tables.
    /// </summary>
    /// <returns>Orchestra code (Csound 7 syntax) with explanatory comments for users.</returns>
    /// <remarks>
    /// <para>Notes on the code (it lives in a C# raw string literal, so it is commented in Csound
    /// syntax inside; these are the points that are not obvious from those comments):</para>
    /// <list type="bullet">
    /// <item>k-arrays (<c>kA[] init iH</c>) keep their contents between control periods, which is
    /// what makes <c>kL</c>/<c>kR</c> (the smoothed amplitudes) and <c>kN1</c>/<c>kN2</c> (the noise
    /// low-passes) stateful one-pole filters.</item>
    /// <item><c>ftslice</c> copies table points [start, end) into the scratch table <c>iCol</c>, and
    /// <c>copyf2array</c> / <c>copya2ftab</c> move them between tables and arrays — all native loops,
    /// so per-row work costs a handful of opcodes instead of a 512-iteration interpreted loop.</item>
    /// <item>The rise/fall split: with d = target − current, (d + |d|)/2 is d when rising and 0 when
    /// falling, so <c>kL += up·kCa + (d − up)·kCr</c> applies the attack coefficient to rising rows
    /// and the release coefficient to falling ones without a per-row branch.</item>
    /// <item><c>adsynt2 kamp, kcps, ifn, ifreqfn, iampfn, icnt, iphs</c>: kcps = 1 because the
    /// frequency table already holds Hz (or loop rates); <c>iphs = 2</c> = random start phases.</item>
    /// <item>Light noise multiplies by √2 (<c>iNoiseGain</c>) so a single (sine-only) bank has the
    /// same power as the sine + cosine pair.</item>
    /// <item>Output: <c>clip … 0, 0dbfs, 0.95</c> is Bram de Jong's soft clipper, linear up to 95 %
    /// of full scale (≈ −0.45 dBFS) — a safety net; the automatic level normally peaks near −6 dBFS.</item>
    /// <item>A non-looping note turns itself off after the last column plus 6 release time constants
    /// (≈ −52 dB) plus 50 ms; live notes are held (<c>p3 = -1</c>), so this is what ends a one-shot
    /// live play (looping ones are ended by the player with <c>i -9301.nnnn</c>).</item>
    /// <item>9302 uses <c>turnoff2 9301, 0, 1</c>: all instances (mode 0), with release (1), so
    /// <c>linsegr</c>'s 80 ms fade-out plays.</item>
    /// <item>Section 7 decides at init time which banks run: <c>iTonal</c> (sines or the sample,
    /// when not mixing) and <c>iNoisy</c> (Noise bands, or the per-cell mix p14, in which the tone is
    /// added to the noise bands' sine bank — see the file header). The
    /// <c>if</c>s test i-rate values, so the opcodes of a branch that is never taken are not even
    /// initialised (a tonal picture pays nothing for the noise code). The mix weights come from
    /// whole-array maths: <c>sqrt(1 - kNs)</c> and <c>sqrt(kNs)</c> map every row at once.</item>
    /// </list>
    /// </remarks>
    public static string Instruments() => """
;=============================================================================
; Paint Rooms · Paint Synth — plays an image as sound (generated by Csound Visual)
;
; The image is a grid: columns = time (left → right), rows = pitch (bottom = low).
; Every control period (ksmps samples) this instrument looks at the column that
; should be sounding now and sets one oscillator per row to that pixel's
; amplitude. adsynt2 is Csound's additive oscillator bank: hundreds of
; oscillators for the price of one opcode, with frequencies and amplitudes
; read from tables at every control period.
;=============================================================================
instr 9301
  ; ---- 1. What the score line passes -------------------------------------
  iBase   = p4          ; first table of this image's set (map below)
  iW      = p5          ; image width = number of time columns
  iH      = p6          ; image height = number of pitch rows = oscillators per bank
  iPass   = p7          ; seconds to play the whole width once
  iStart  = p8          ; column where playback starts (may be fractional)
  iLoop   = p9          ; 1 = after the last column, start again at column 0
  iMode   = p10         ; 0 = sines, 1 = noise bands, 2 = sample loop
  iStereo = p11         ; 1 = left and right layers differ: run two banks
  iGlide  = p12         ; 1 = blend neighbouring columns, 0 = hard steps
  iNoiseBanks = p13     ; noise mode: 2 = smooth (sine + cosine bank), 1 = light (sine only)
  iNoiseGain = (iNoiseBanks == 2 ? 1 : sqrt(2)) ; one bank must carry the power of two
  iMix    = p14         ; 1 = the picture has a noise layer: mix tone and noise per cell

  ; ---- 2. The tables of this image (numbers relative to iBase) ------------
  iImgL   = iBase + 0   ; left amplitudes: (iW + 1) columns of iH rows, one column
  iImgR   = iBase + 1   ;   after the other; the extra last column is silent
  iFreq   = iBase + 2   ; oscillator frequency of every row (Hz; sample mode: loop rate)
  iAmpLA  = iBase + 3   ; amplitudes the banks read at every control period
  iAmpLB  = iBase + 4
  iAmpRA  = iBase + 5
  iAmpRB  = iBase + 6
  iCol    = iBase + 7   ; scratch space for one column
  iNoise  = iBase + 8   ; 65536 Gaussian random numbers (noise mode)
  iCoef   = iBase + 9   ; noise mode: smoothing per row (sets each band's width)
  iNorm   = iBase + 10  ; noise mode: gain per row (same loudness for every width)
  iWaveA  = iBase + 11  ; waveform of bank A: a sine (sample mode: the sound file)
  iWaveB  = iBase + 12  ; waveform of bank B: a cosine (noise mode)
  iImgN   = iBase + 13  ; noisiness of every cell, 0 = tone ... 1 = noise (same layout as iImgL)
  iAmpLS  = iBase + 14  ; amplitudes of the tonal (sine / sample) banks
  iAmpRS  = iBase + 15

  ; ---- 3. Live controls (the app moves them while you listen) -------------
  kGain   chnget "PaintRooms.gain"      ; overall linear gain (auto level × your gain)
  kAtt    chnget "PaintRooms.attack"    ; seconds for a row to rise to a brighter pixel
  kRel    chnget "PaintRooms.release"   ; seconds for a row to fall to a darker pixel
  ; one-pole smoothing coefficient per control period: c = 1 - e^(-1 / (time * kr))
  kCa     = 1 - exp(-1 / (max(kAtt, 0.0005) * kr))
  kCr     = 1 - exp(-1 / (max(kRel, 0.0005) * kr))

  ; ---- 4. Which column is sounding now? -----------------------------------
  kTime   timeinsts                     ; seconds since this note started
  kPos    = iStart + kTime * iW / iPass ; fractional column
  if iLoop == 1 then
    kPos  = kPos - iW * floor(kPos / iW)  ; wrap around to the beginning
  endif
  kC0     = floor(kPos)                 ; the column we are in
  kFrac   = (kPos - kC0) * iGlide       ; how far towards the next one (0 = no glide)
  kC1     = kC0 + 1                     ; the next column
  if iLoop == 1 then
    kC1   = (kC1 >= iW ? 0 : kC1)       ; after the last column comes the first
  endif
  kC0     = min(kC0, iW)                ; past the end: column iW, which is silent
  kC1     = min(kC1, iW)

  ; ---- 5. Read both columns and blend them (all rows at once, as arrays) --
  kA[]    init iH
  kB[]    init iH
  kTarL[] init iH                       ; what every left row should reach
  ftslice iImgL, iCol, kC0 * iH, (kC0 + 1) * iH ; copy one column out of the image
  copyf2array kA, iCol                          ; ...into an array
  ftslice iImgL, iCol, kC1 * iH, (kC1 + 1) * iH
  copyf2array kB, iCol
  kTarL   = kA + (kB - kA) * kFrac

  ; ---- 6. Smooth: rise with the attack time, fall with the release time --
  kL[]    init iH                       ; current amplitude of every left row
  kD[]    init iH
  kUp[]   init iH
  kD      = kTarL - kL                  ; distance to the target, per row
  kUp     = (kD + abs(kD)) * 0.5        ; its positive part: rows that must rise
  kL      = kL + kUp * kCa + (kD - kUp) * kCr

  kR[]    init iH                       ; right rows (a copy of the left ones in mono)
  if iStereo == 1 then
    kTarR[] init iH
    ftslice iImgR, iCol, kC0 * iH, (kC0 + 1) * iH
    copyf2array kA, iCol
    ftslice iImgR, iCol, kC1 * iH, (kC1 + 1) * iH
    copyf2array kB, iCol
    kTarR = kA + (kB - kA) * kFrac
    kD    = kTarR - kR
    kUp   = (kD + abs(kD)) * 0.5
    kR    = kR + kUp * kCa + (kD - kUp) * kCr
  endif

  ; ---- 7. Oscillator banks -------------------------------------------------
  ; Which banks run (decided once, at the start of the note):
  ;   tonal = sines (mode 0) or the sample (mode 2); noise = noise bands (mode 1)
  ;   or the per-cell mix of a picture with a noise layer (p14 = 1), where the
  ;   tone rides on the noise bands' sine oscillators (no extra bank).
  iTonal  = ((iMode != 1 && iMix == 0) ? 1 : 0)
  iNoisy  = ((iMode == 1 || iMix == 1) ? 1 : 0)
  kTL[]   init iH                       ; amplitudes for the tonal banks (left, right)
  kTR[]   init iH
  kQL[]   init iH                       ; amplitudes for the noise banks (left, right)
  kQR[]   init iH
  if iMix == 1 then
    ; Per-cell mix: n = noisiness of the cell (0 = tone, 1 = noise), read and
    ; blended like the levels, then smoothed with the attack time. Equal-power
    ; crossfade: the tone gets a * sqrt(1 - n), the noise band a * sqrt(n), so
    ; the row keeps the same loudness whatever n is.
    kTarN[] init iH
    kNs[]   init iH                     ; smoothed noisiness of every row
    kWt[]   init iH
    kWn[]   init iH
    ftslice iImgN, iCol, kC0 * iH, (kC0 + 1) * iH
    copyf2array kA, iCol
    ftslice iImgN, iCol, kC1 * iH, (kC1 + 1) * iH
    copyf2array kB, iCol
    kTarN   = kA + (kB - kA) * kFrac
    kNs     = kNs + (kTarN - kNs) * kCa
    kWt     = sqrt(1 - kNs)             ; tone weight, every row at once
    kWn     = sqrt(kNs)                 ; noise weight
    kTL     = kL * kWt
    kQL     = kL * kWn
    kTR     = kR * kWt
    kQR     = kR * kWn
  else
    kTL     = kL
    kTR     = kR
    kQL     = kL
    kQR     = kR
  endif

  aL      = 0
  aR      = 0
  if iTonal == 1 then
    ; Sines (mode 0) or sample loop (mode 2: iWaveA holds the sound file).
    ; The last argument 2 starts every oscillator at a random phase, so
    ; hundreds of partials do not all peak together.
    copya2ftab kTL, iAmpLS
    aT      adsynt2 kGain, 1, iWaveA, iFreq, iAmpLS, iH, 2
    aL      = aL + aT
    if iStereo == 1 then
      copya2ftab kTR, iAmpRS
      aT2   adsynt2 kGain, 1, iWaveA, iFreq, iAmpRS, iH, 2
      aR    = aR + aT2
    endif
  endif
  if iNoisy == 1 then
    ; Noise bands: per row, a*sin + b*cos where a and b are slowly varying
    ; random numbers (white noise through a one-pole low-pass per row): a
    ; narrow band of noise around the row frequency. «Light» noise (p13 = 1)
    ; keeps only a*sin: half the oscillators, a slightly more «tremolo» sound.
    kNz[]   init iH
    kN1[]   init iH
    kN2[]   init iH
    kCf[]   init iH
    kNm[]   init iH
    kX[]    init iH
    copyf2array kCf, iCoef
    copyf2array kNm, iNorm
    kNm     = kNm * iNoiseGain              ; light noise: one component carries all the power
    kOff    = int(random:k(0, 65536 - iH))  ; a random place in the noise table
    ftslice iNoise, iCol, kOff, kOff + iH
    copyf2array kNz, iCol
    kN1     = kN1 + (kNz - kN1) * kCf       ; low-passed noise, one value per row
    kX      = kQL * kN1 * kNm
    if iMix == 1 then
      kX    = kX + kTL                      ; the tone rides on this sine oscillator
    endif
    copya2ftab kX, iAmpLA
    aN      adsynt2 kGain, 1, iWaveA, iFreq, iAmpLA, iH, 2
    aL      = aL + aN
    if iNoiseBanks == 2 then
      kOff  = int(random:k(0, 65536 - iH))
      ftslice iNoise, iCol, kOff, kOff + iH
      copyf2array kNz, iCol
      kN2   = kN2 + (kNz - kN2) * kCf
      kX    = kQL * kN2 * kNm
      copya2ftab kX, iAmpLB
      aN2   adsynt2 kGain, 1, iWaveB, iFreq, iAmpLB, iH, 2
      aL    = aL + aN2
    endif
    if iStereo == 1 then
      kX    = kQR * kN1 * kNm
      if iMix == 1 then
        kX  = kX + kTR
      endif
      copya2ftab kX, iAmpRA
      aN3   adsynt2 kGain, 1, iWaveA, iFreq, iAmpRA, iH, 2
      aR    = aR + aN3
      if iNoiseBanks == 2 then
        kX  = kQR * kN2 * kNm
        copya2ftab kX, iAmpRB
        aN4 adsynt2 kGain, 1, iWaveB, iFreq, iAmpRB, iH, 2
        aR  = aR + aN4
      endif
    endif
  endif
  if iStereo == 0 then
    aR    = aL                          ; mono picture: one set of banks for both sides
  endif

  ; ---- 8. Output -----------------------------------------------------------
  kEnv    linsegr 0, 0.01, 1, 0.08, 0   ; 10 ms fade-in, 80 ms fade-out when stopped
  aEnv    interp kEnv
  aL      = aL * aEnv * 0dbfs
  aR      = aR * aEnv * 0dbfs
  ; Safety soft limit for LIVE playback (transparent below -0.5 dBFS). Renders set the channel
  ; "PaintRooms.noclip" to 1: they are written as 32-bit float and normalised afterwards, so any
  ; limiting here would only add distortion. An unset channel reads 0 = limiter on.
  if chnget:k("PaintRooms.noclip") == 0 then
    aL    clip aL, 0, 0dbfs, 0.95
    aR    clip aR, 0, 0dbfs, 0.95
  endif
  if nchnls == 1 then
    out (aL + aR) * 0.5
  else
    outch 1, aL, 2, aR
  endif

  ; Not looping: stop once the last column has played and the rows have faded.
  if iLoop == 0 && kTime > (iW - iStart) * iPass / iW + kRel * 6 + 0.05 then
    turnoff
  endif
endin

; Stops every image player, letting its 80 ms fade-out play.
instr 9302
  turnoff2 9301, 0, 1
  turnoff
endin
""";

    /// <summary>
    /// Global code that sets the three live channels (Csound 7: a channel's default value does not
    /// initialise it, so it is written explicitly).
    /// </summary>
    /// <param name="t">The tables (gain from <see cref="PaintTables.OutputGain"/>; attack/release in ms
    /// from its settings, converted here to seconds).</param>
    /// <returns>Three <c>chnset</c> lines (instr-0 / global code). Without them a note started
    /// before the host's first <c>SetControl</c> would read 0 = silence and instant envelopes.</returns>
    public static string ChannelInit(PaintTables t) =>
        $"chnset {F(t.OutputGain)}, \"{GainChannel}\"   ; overall gain (auto level x your gain)\n" +
        $"chnset {F(t.Settings.AttackMs / 1000)}, \"{AttackChannel}\"   ; attack time (s)\n" +
        $"chnset {F(t.Settings.ReleaseMs / 1000)}, \"{ReleaseChannel}\"   ; release time (s)\n";

    /// <summary>
    /// The p4…p13 fields of an instr 9301 score line (see the instrument's section 1).
    /// </summary>
    /// <param name="t">The tables.</param>
    /// <param name="baseTable">First table number of the set.</param>
    /// <param name="startColumn">Column where playback starts.</param>
    /// <param name="loop">Repeat forever.</param>
    /// <returns>
    /// Eleven space-separated values: p4 base table, p5 width, p6 height, p7 pass seconds, p8 start
    /// column (clamped to 0…width), p9 loop, p10 mode (the <see cref="SynthMode"/> value: 0 sines,
    /// 1 noise, 2 sample), p11 stereo, p12 glide, p13 noise banks (2 smooth, 1 light), p14 per-cell
    /// tone/noise mix (<see cref="PaintTables.Mixed"/>).
    /// </returns>
    /// <remarks>The order must match section 1 of <see cref="Instruments"/>; the caller prepends
    /// «i 9301[.nnnn] start duration».</remarks>
    public static string Fields(PaintTables t, int baseTable, double startColumn, bool loop) =>
        string.Join(' ',
            baseTable.ToString(Ci), t.Width.ToString(Ci), t.Height.ToString(Ci), F(t.PassSeconds),
            F(Math.Clamp(startColumn, 0, t.Width)), loop ? "1" : "0", ((int)t.Settings.Mode).ToString(Ci),
            t.Stereo ? "1" : "0", t.Settings.Glide ? "1" : "0", t.Settings.SmoothNoise ? "2" : "1", t.Mixed ? "1" : "0");

    /// <summary>
    /// Live use: global code that creates an EMPTY table set at <paramref name="baseTable"/> (the
    /// host then copies the data straight into the tables' memory) plus the waveforms, which
    /// Csound computes itself (GEN10 sine, GEN09 cosine).
    /// </summary>
    /// <param name="t">The tables (only their sizes are used).</param>
    /// <param name="baseTable">First table number of the set.</param>
    /// <returns>A comment line plus one <c>ftgen</c> line per table, in <see cref="TableMap"/> order.</returns>
    public static string LiveTables(PaintTables t, int baseTable) =>
        $"; Paint Rooms: table set {baseTable}…{baseTable + TablesPerSet - 1} ({t.Width} columns × {t.Height} rows)\n" +
        string.Concat(LiveTableLines(t, baseTable).Select(l => l.Code));

    /// <summary>
    /// The lines of <see cref="LiveTables"/>, one ftgen per table with its offset in the set, so the
    /// live player can create the two big image tables in separate control cycles.
    /// </summary>
    /// <param name="t">The tables (sizes, and whether a sample wave exists).</param>
    /// <param name="baseTable">First table number of the set.</param>
    /// <returns>(offset in the set, one line of global code ending in «\n») pairs.</returns>
    /// <remarks>
    /// <c>ftgen n, 0, size, -2, 0</c> = GEN02 without normalisation (the minus sign) holding a single
    /// 0: a zero-filled table of exactly <c>size</c> points (Csound accepts any size, not only powers
    /// of two). All lines assign to the same dummy variable <c>giPaintTab</c>; only the table numbers
    /// matter. A sample wave gets an empty table of its length here and is filled by
    /// <see cref="LiveData"/>; otherwise WaveA is a GEN10 sine.
    /// </remarks>
    public static IReadOnlyList<(int Offset, string Code)> LiveTableLines(PaintTables t, int baseTable)
    {
        var list = new List<(int, string)>();
        // img = points of one image layer: (W + 1) columns × H rows (the +1 is the silent end column).
        int img = (t.Width + 1) * t.Height, h = t.Height;
        // Adds the ftgen line of a zero-filled table of «size» points at «offset».
        void Empty(int offset, int size, string what) =>
            list.Add((offset, $"giPaintTab ftgen {baseTable + offset}, 0, {size}, -2, 0   ; {what}\n"));
        Empty(TableMap.ImageL, img, "left amplitudes");
        Empty(TableMap.ImageR, img, "right amplitudes");
        Empty(TableMap.Freq, h, "row frequencies");
        Empty(TableMap.AmpLA, h, "bank amplitudes");
        Empty(TableMap.AmpLB, h, "bank amplitudes");
        Empty(TableMap.AmpRA, h, "bank amplitudes");
        Empty(TableMap.AmpRB, h, "bank amplitudes");
        Empty(TableMap.Column, h, "scratch column");
        Empty(TableMap.Noise, PaintTables.NoiseLength, "Gaussian noise");
        Empty(TableMap.Coef, h, "noise smoothing per row");
        Empty(TableMap.Norm, h, "noise gain per row");
        if (t.SampleWave is { } wave)
            Empty(TableMap.WaveA, wave.Length, "the sample");
        else
            list.Add((TableMap.WaveA, $"giPaintTab ftgen {baseTable + TableMap.WaveA}, 0, {PaintTables.WaveLength}, 10, 1   ; sine\n"));
        // GEN09 «1, 1, 90» = partial 1, amplitude 1, phase 90° = a cosine (the quadrature partner of
        // the sine in noise-band mode). Created even when unused: it is small and keeps the map fixed.
        list.Add((TableMap.WaveB, $"giPaintTab ftgen {baseTable + TableMap.WaveB}, 0, {PaintTables.WaveLength}, 9, 1, 1, 90   ; cosine\n"));
        // Noise layer: the full (W + 1)·H table only when the picture mixes tone and noise; otherwise a
        // tiny placeholder (never read: the instrument's mix branch is skipped at init time).
        Empty(TableMap.ImageN, t.Mixed ? img : h, t.Mixed ? "noisiness per cell" : "noisiness (unused)");
        Empty(TableMap.AmpLS, h, "tonal bank amplitudes");
        Empty(TableMap.AmpRS, h, "tonal bank amplitudes");
        return list;
    }

    /// <summary>
    /// The data arrays of a table set as (table offset, data) pairs, in the order the live player
    /// copies them into Csound.
    /// </summary>
    /// <param name="t">The tables.</param>
    /// <returns>
    /// Every table whose contents come from C#. Not listed: the bank amplitude and scratch tables
    /// (written by the instrument itself) and the sine/cosine waves (computed by Csound's GENs).
    /// The arrays are the live ones of <paramref name="t"/>, not copies. The noisiness table is
    /// listed only for a mixed picture (it is as big as an image layer, so the live player copies it
    /// column by column together with ImageL / ImageR).
    /// </returns>
    public static IEnumerable<(int Offset, double[] Data)> LiveData(PaintTables t)
    {
        yield return (TableMap.ImageL, t.ImageL);
        yield return (TableMap.ImageR, t.ImageR);
        yield return (TableMap.Freq, t.Frequencies);
        yield return (TableMap.Noise, PaintTables.Noise);
        yield return (TableMap.Coef, t.NoiseCoef);
        yield return (TableMap.Norm, t.NoiseNorm);
        if (t.SampleWave is { } w)
            yield return (TableMap.WaveA, w);
        if (t.Mixed)
            yield return (TableMap.ImageN, t.ImageN);
    }

    /// <summary>
    /// Writes a standalone, readable .csd that plays the image once (score: one instr 9301 note),
    /// with its tables in data files next to it: <c>&lt;stem&gt;_left.wav</c> / <c>_right.wav</c>
    /// (the image as 32-bit float «sound files», loaded with GEN01 — the fastest exact way to give
    /// Csound half a million numbers), <c>_freqs.txt</c>, <c>_coef.txt</c>, <c>_norm.txt</c> (one
    /// number per line, GEN23), <c>_noise.wav</c> and, in sample mode, <c>_sample.wav</c>.
    /// </summary>
    /// <param name="t">Tables (computed for the sr/ksmps written in the header).</param>
    /// <param name="folder">Folder for the .csd and its data files (created if needed).</param>
    /// <param name="stem">File name stem, e.g. «paint».</param>
    /// <param name="absolutePaths">
    /// True: the code refers to the data files by absolute path (offline render through the app's
    /// engine, which must not change the process's working folder). False: plain file names, so
    /// the folder can be moved; the app runs a document in its own folder.
    /// </param>
    /// <param name="title">Shown in the header comment (e.g. the preset name).</param>
    /// <returns>The path of the .csd written.</returns>
    /// <remarks>
    /// The .csd always uses the FIRST table set (<see cref="FirstTable"/>): it is a performance of
    /// its own, so there is nothing to clash with. Its <c>&lt;CsOptions&gt;</c> say <c>-odac</c> for
    /// playing in the editor; the renderer strips them and passes its own file-output flags.
    /// The score note lasts one pass plus the release tail (6 release time constants + 0.2 s, the
    /// same formula PaintRenderer uses for its progress bar), and it is not looped.
    /// </remarks>
    /// <exception cref="IOException">The folder or one of the files could not be written.</exception>
    /// <param name="forRender">True for the offline renderer: turns the live safety limiter off
    /// (channel <c>PaintRooms.noclip</c> = 1), because the render is float and normalised afterwards.</param>
    public static string WriteStandaloneCsd(PaintTables t, string folder, string stem, bool absolutePaths, string title, bool forRender = false)
    {
        Directory.CreateDirectory(folder);
        // Data(): where a data file is written. Ref(): how the .csd names it — absolute with forward
        // slashes (a «\» inside a Csound string is an escape character) or a bare, relative name.
        string Data(string suffix) => Path.Combine(folder, stem + suffix);
        string Ref(string suffix) => absolutePaths ? Data(suffix).Replace('\\', '/') : stem + suffix;

        // ---- the data files (the big arrays as float WAVs for GEN01, the per-row ones as text for GEN23)

        WavIO.WriteFloat32(Data("_left.wav"), t.ImageL);
        WavIO.WriteFloat32(Data("_right.wav"), t.ImageR);
        WavIO.WriteFloat32(Data("_noise.wav"), PaintTables.Noise);
        WriteNumbers(Data("_freqs.txt"), t.Frequencies);
        WriteNumbers(Data("_coef.txt"), t.NoiseCoef);
        WriteNumbers(Data("_norm.txt"), t.NoiseNorm);
        if (t.SampleWave is { } wave)
            WavIO.WriteFloat32(Data("_sample.wav"), wave);
        if (t.Mixed)
            WavIO.WriteFloat32(Data("_noisiness.wav"), t.ImageN);

        // ---- the .csd text: header comments, orchestra header, channels, tables, instruments, score
        int b = FirstTable, h = t.Height;
        double tail = t.Settings.ReleaseMs / 1000 * 6 + 0.2; // seconds after the last column
        var s = t.Settings;
        var sb = new StringBuilder();
        sb.Append("<CsoundSynthesizer>\n<CsOptions>\n-odac\n</CsOptions>\n<CsInstruments>\n");
        sb.Append($"; {title}\n");
        sb.Append($"; Generated by Csound Visual · Paint Rooms · {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        sb.Append($"; Image: {t.Width} columns × {h} rows, {F(t.PassSeconds)} s; synthesis: {s.Mode}, {(t.Stereo ? "stereo" : "mono")}.\n");
        sb.Append($"; Level → amplitude: ((level - {F(s.Floor)}) / (1 - {F(s.Floor)}))^{F(s.Curve)}; attack {F(s.AttackMs)} ms, release {F(s.ReleaseMs)} ms.\n");
        sb.Append(";\n; Press Play (F5) to hear it. The data files next to this .csd hold the tables.\n\n");
        // sr and ksmps MUST be the ones the tables were built for: the noise coefficients depend on kr.
        sb.Append($"sr     = {F(t.SampleRate)}\nksmps  = {t.Ksmps}\nnchnls = 2\n0dbfs  = 1\n\n");
        sb.Append(ChannelInit(t)).Append('\n');
        if (forRender)
            sb.Append("chnset 1, \"PaintRooms.noclip\"   ; offline render: no safety limiter (float output, normalised afterwards)\n\n");
        sb.Append($"; ---- The tables (numbers {b}…{b + 15}; instr 9301 finds them from p4 = {b}) ----\n");
        // GEN01 arguments after the file name: skip time 0, format 0 (from the header), channel 1.
        sb.Append("; GEN01 (here -1: do not rescale) reads a sound file into a table. Size 0 = as long as the file.\n");
        sb.Append($"giImgL  ftgen {b + TableMap.ImageL}, 0, 0, -1, \"{Ref("_left.wav")}\", 0, 0, 1    ; left amplitudes, column after column\n");
        sb.Append($"giImgR  ftgen {b + TableMap.ImageR}, 0, 0, -1, \"{Ref("_right.wav")}\", 0, 0, 1   ; right amplitudes\n");
        sb.Append("; GEN23 reads numbers from a text file (one per line).\n");
        sb.Append($"giFreq  ftgen {b + TableMap.Freq}, 0, {h}, -23, \"{Ref("_freqs.txt")}\"   ; {(s.Mode == SynthMode.SampleLoop ? "loop rate" : "frequency in Hz")} of every row\n");
        sb.Append($"giAmp1  ftgen {b + TableMap.AmpLA}, 0, {h}, -2, 0   ; amplitudes read by the oscillator banks\n");
        sb.Append($"giAmp2  ftgen {b + TableMap.AmpLB}, 0, {h}, -2, 0\n");
        sb.Append($"giAmp3  ftgen {b + TableMap.AmpRA}, 0, {h}, -2, 0\n");
        sb.Append($"giAmp4  ftgen {b + TableMap.AmpRB}, 0, {h}, -2, 0\n");
        sb.Append($"giCol   ftgen {b + TableMap.Column}, 0, {h}, -2, 0   ; scratch column\n");
        sb.Append($"giNoise ftgen {b + TableMap.Noise}, 0, 0, -1, \"{Ref("_noise.wav")}\", 0, 0, 1   ; Gaussian random numbers\n");
        sb.Append($"giCoef  ftgen {b + TableMap.Coef}, 0, {h}, -23, \"{Ref("_coef.txt")}\"   ; noise band smoothing per row\n");
        sb.Append($"giNorm  ftgen {b + TableMap.Norm}, 0, {h}, -23, \"{Ref("_norm.txt")}\"   ; noise band gain per row\n");
        if (t.SampleWave is not null)
            sb.Append($"giWaveA ftgen {b + TableMap.WaveA}, 0, {t.SampleWave.Length}, -1, \"{Ref("_sample.wav")}\", 0, 0, 1   ; the sample (power-of-two length)\n");
        else
            sb.Append($"giWaveA ftgen {b + TableMap.WaveA}, 0, {PaintTables.WaveLength}, 10, 1   ; GEN10: a sine\n");
        sb.Append($"giWaveB ftgen {b + TableMap.WaveB}, 0, {PaintTables.WaveLength}, 9, 1, 1, 90   ; GEN09: a cosine (sine at 90 degrees)\n");
        if (t.Mixed)
            sb.Append($"giImgN  ftgen {b + TableMap.ImageN}, 0, 0, -1, \"{Ref("_noisiness.wav")}\", 0, 0, 1   ; noisiness of every cell (0 = tone, 1 = noise)\n");
        else
            sb.Append($"giImgN  ftgen {b + TableMap.ImageN}, 0, {h}, -2, 0   ; noisiness (unused: this picture has no noise layer)\n");
        sb.Append($"giAmp5  ftgen {b + TableMap.AmpLS}, 0, {h}, -2, 0   ; amplitudes of the tonal banks\n");
        sb.Append($"giAmp6  ftgen {b + TableMap.AmpRS}, 0, {h}, -2, 0\n\n");
        sb.Append(Instruments());
        sb.Append("\n</CsInstruments>\n<CsScore>\n");
        sb.Append("; p4 tables, p5 width, p6 height, p7 seconds, p8 start column, p9 loop, p10 mode, p11 stereo, p12 glide,\n");
        sb.Append("; p13 noise banks (2 smooth, 1 light), p14 tone/noise mix per cell (1 = the picture has a noise layer)\n");
        sb.Append($"i 9301 0 {F(t.PassSeconds + tail)} {Fields(t, b, 0, loop: false)}\n");
        sb.Append("</CsScore>\n</CsoundSynthesizer>\n");
        string csd = Path.Combine(folder, stem + ".csd");
        File.WriteAllText(csd, sb.ToString());
        return csd;
    }

    /// <summary>Writes one number per line (GEN23 format).</summary>
    /// <param name="path">Target text file (overwritten).</param>
    /// <param name="values">The numbers, formatted with <see cref="F"/> (invariant culture, «\n» line ends).</param>
    static void WriteNumbers(string path, double[] values)
    {
        var sb = new StringBuilder(values.Length * 12);
        foreach (double v in values) sb.Append(F(v)).Append('\n');
        File.WriteAllText(path, sb.ToString());
    }
}
