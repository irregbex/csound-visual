// ImageFilterCode.cs — Paint Rooms · «Image Filter»: the Csound code that filters a sound with a picture.
//
// Role: writes the Csound instrument of the Image Filter room and the score/table code around it.
// As in Paint Synth, the SAME instrument text is used
//   * live, compiled into the running performance (the user's document or the «Paint Rooms host»),
//     with its gain tables filled straight from memory by ImageFilterPlayer, and
//   * offline / for learning, inside a standalone .csd whose tables are data files next to it
//     (WriteStandaloneCsd): «Show Csound code» opens it, «Render WAV» runs it.
//
// DSP DESIGN (and why):
//   * Phase vocoder, one chain per channel: pvsanal (Hann window, N = FFT size, hop = N/overlap) →
//     pvsmaska (multiplies the amplitude of every bin by the value at the same index of a table;
//     MEASURED: values above 1 boost, 0.5 = −6 dB, phases untouched) → pvsynth (overlap-add
//     resynthesis). With an all-ones mask the chain is transparent apart from its delay.
//     Alternatives considered: pvs2array + array multiply + pvsfromarray works too but needs an
//     interleaved (amp, freq) gain array of N + 2 values and extra copies per frame; pvsfilter
//     needs a second fsig. pvsmaska reads a plain table of N/2 + 1 gains each frame — the
//     cheapest and most readable route.
//   * The gain table (the «mask») is rewritten every CONTROL PERIOD from two big tables holding
//     the whole picture already translated to bins (ImageFilterTables): ftslice copies the two
//     columns around the play position (native table-to-table copy), they are blended (glide) and
//     smoothed per bin with a one-pole low-pass written as whole-array arithmetic (one interpreter
//     step for all bins). The smoothing (time constant «Smoothing ms») removes zipper noise when
//     the picture jumps (a hard gate pattern, a sharp edge), and the table update every control
//     period means pvsmaska always sees a fresh, smooth curve when the next frame arrives.
//   * Latency: N + 2·hop samples (measured; 1024/4 at 48 kHz = 1536 samples = 32 ms). The dry
//     signal of the mix is delayed by exactly that, so dry/wet blends without comb filtering, and
//     the play position is shifted back by N/2 (the centre of the analysis window) so a painted
//     edge filters the sound at the moment you painted it.
//   * Safety: 20 ms fade-in, 80 ms fade-out on stop, a soft clipper (transparent below about
//     −1 dBFS), and the output gain is smoothed.
//   * Source: a stereo sound file through diskin2 (sample-rate conversion and looping included) or
//     the live input (inch 1 / inch 2 — the app's engine feeds the interface input there; see
//     Settings › Audio › Input and «Input channels»).
// Cost (measured, see ImageFilterSelfTest): two 1024-point chains are a few % of one core.
//
// Table set layout: every play uses a «set» of TablesPerSet (8) consecutive table numbers starting
// at FirstTable + 8·k; only offsets 0…4 are used (TableMap), 5…7 are spare. The live player
// rotates through a ring of sets (ImageFilterPlayer.SetRing) so a new play never overwrites the
// tables a fading-out note still reads.
//
// Number formatting: every number written into Csound code goes through F / Ci (invariant
// culture), because a Spanish Windows locale would otherwise write «0,5» and break the parser.
// Pure string/file generation: no Csound calls here, safe on any thread.

using System.Globalization;
using System.Text;

namespace CsoundVisual.Paint;

/// <summary>Generates the Image Filter orchestra code, score lines and standalone .csd files.</summary>
public static class ImageFilterCode
{
    /// <summary>Instrument number of the filter.</summary>
    public const int InstrNumber = 9351;

    /// <summary>Instrument number of the helper that stops every filter (with its fade-out).</summary>
    public const int StopInstrNumber = 9352;

    /// <summary>
    /// First table number of the Image Filter (sets of <see cref="TablesPerSet"/> follow). Far from
    /// Paint Synth's 31000… so both rooms can play at once in the same performance.
    /// </summary>
    public const int FirstTable = 33500;

    /// <summary>Tables per filter set; sets start every this many numbers.</summary>
    public const int TablesPerSet = 8;

    /// <summary>Channel with the dry/wet mix (0…1).</summary>
    public const string MixChannel = "PaintRooms.filter.mix";

    /// <summary>Channel with the linear output gain.</summary>
    public const string GainChannel = "PaintRooms.filter.gain";

    /// <summary>Channel with the gain-curve smoothing time constant in seconds.</summary>
    public const string SmoothChannel = "PaintRooms.filter.smooth";

    /// <summary>Offsets of the tables of one set relative to its base number.</summary>
    public static class TableMap
    {
        /// <summary>Left gains: Columns × Bins, column after column.</summary>
        public const int GainL = 0;
        /// <summary>Right gains.</summary>
        public const int GainR = 1;
        /// <summary>Left mask read by pvsmaska (Bins).</summary>
        public const int MaskL = 2;
        /// <summary>Right mask (Bins).</summary>
        public const int MaskR = 3;
        /// <summary>Scratch column (Bins).</summary>
        public const int Column = 4;
    }

    /// <summary>Invariant culture for integer fields (decimal point, no grouping).</summary>
    static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

    /// <summary>Formats a number for Csound code (invariant culture).</summary>
    /// <param name="v">The value.</param>
    /// <returns>Shared formatter of Paint Synth (<see cref="PaintCsoundCode.F"/>), so both rooms write numbers alike.</returns>
    static string F(double v) => PaintCsoundCode.F(v);

    /// <summary>
    /// The instruments: 9351 filters one source with one picture, 9352 stops all 9351 instances
    /// with their fade-out. Everything image-specific comes from p-fields and tables.
    /// </summary>
    /// <returns>Orchestra code (no header, no global statements), ready for <c>csoundCompileOrc</c> or a .csd.</returns>
    /// <remarks>
    /// <para>
    /// The text is a C# raw string literal and is shown to users as-is («Show Csound code»), hence
    /// the tutorial-style Csound comments inside it. It is compiled once per performance by
    /// <see cref="ImageFilterPlayer"/> and embedded verbatim by <see cref="WriteStandaloneCsd"/>.
    /// </para>
    /// <para>
    /// Notes on the code: <c>timeinsts</c> counts seconds since the note started, so section 5
    /// maps time → fractional column; the per-bin smoothing coefficient
    /// <c>c = 1 − e^(−1/(τ·kr))</c> is the exact one-pole equivalent of an RC time constant τ
    /// sampled at the control rate; <c>linsegr</c> provides the release segment that makes
    /// «<c>i -9351.nnnn</c>» fade out instead of cutting; <c>turnoff2 9351, 0, 1</c> in instr 9352
    /// releases every instance (mode 0 = all, 1 = allow release).
    /// </para>
    /// </remarks>
    public static string Instruments() => """
;=============================================================================
; Paint Rooms · Image Filter — a picture used as a moving stereo equaliser
; (generated by Csound Visual)
;
; The picture is a grid: columns = time, rows = pitch. At every moment the
; column under the play head is an EQ curve: a bright row lets its band
; through, a dark row removes it. The LEFT layer of the picture filters the
; left channel and the RIGHT layer the right channel (red / green when you
; import a picture), so a picture can also move the sound around.
;
; How: a phase vocoder per channel. pvsanal cuts the sound into overlapping
; FFT frames (one amplitude + frequency per «bin»; bin k sits at k*sr/N Hz),
; pvsmaska multiplies every bin's amplitude by a gain read from a table, and
; pvsynth turns the frames back into sound. Csound Visual has already turned
; the picture into one gain per bin and per column (two big tables); here we
; only pick the current column, smooth it and hand it to pvsmaska.
;=============================================================================
instr 9351
  ; ---- 1. What the score line passes -------------------------------------
  iBase   = p4          ; first table of this filter's set (map below)
  iW      = p5          ; columns of the gain tables (time steps of the picture)
  iB      = p6          ; bins per column = iN / 2 + 1
  iPass   = p7          ; seconds for one pass over the picture
  iStart  = p8          ; column where the picture starts (may be fractional)
  iLoopI  = p9          ; 1 = the picture repeats, 0 = it stops on its last column
  iSrc    = p10         ; 0 = sound file (p17), 1 = live audio input
  iN      = p11         ; FFT size (samples per analysis frame)
  iHop    = p12         ; hop: samples between frames (iN / overlap)
  iGlide  = p13         ; 1 = blend neighbouring columns, 0 = hard steps
  iSkip   = p14         ; seconds into the sound file where playback starts
  iLoopS  = p15         ; 1 = the sound file loops
  iDur    = p16         ; > 0: stop after this many seconds; 0 = until stopped
  SFile   = p17         ; the sound file (stereo WAV)

  ; ---- 2. The tables of this filter (numbers relative to iBase) -----------
  iGainL  = iBase + 0   ; left gains: iW columns of iB bins, one after the other
  iGainR  = iBase + 1   ; right gains
  iMaskL  = iBase + 2   ; the current left EQ curve, read by pvsmaska
  iMaskR  = iBase + 3   ; the current right EQ curve
  iCol    = iBase + 4   ; scratch space for one column

  ; ---- 3. Live controls (the app moves them while you listen) -------------
  kMix    chnget "PaintRooms.filter.mix"     ; 0 = dry, 1 = filtered
  kOut    chnget "PaintRooms.filter.gain"    ; linear output gain
  kSmooth chnget "PaintRooms.filter.smooth"  ; seconds: how fast the EQ follows
  iMix0   chnget "PaintRooms.filter.mix"     ; the values at the start: the glides
  iOut0   chnget "PaintRooms.filter.gain"    ; below begin there (not at 0 = dry)
  kMix    portk kMix, 0.02, iMix0            ; glide 20 ms (half-time): no clicks
  kOut    portk kOut, 0.02, iOut0
  ; one-pole smoothing coefficient per control period: c = 1 - e^(-1 / (time * kr))
  kCoef   = 1 - exp(-1 / (max(kSmooth, 0.0005) * kr))

  ; ---- 4. The source --------------------------------------------------------
  if iSrc == 0 then
    ; diskin2 converts the file's sample rate and can loop it
    aInL, aInR diskin2 SFile, 1, iSkip, iLoopS
  else
    aInL    inch 1      ; live input 1 (Settings > Audio > Input)
    if nchnls_i > 1 then
      aInR  inch 2      ; live input 2
    else
      aInR  = aInL
    endif
  endif

  ; ---- 5. Which column of the picture applies now? ------------------------
  ; A frame leaves pvsanal when its window is full, so it describes the sound
  ; that came in about iN/2 samples ago: look that far back in the picture,
  ; then a painted edge acts exactly where it was painted.
  iAlign  = iN / (2 * sr)
  kTime   timeinsts
  kPos    = iStart + (kTime - iAlign) * iW / iPass
  if iLoopI == 1 then
    kPos  = kPos - iW * floor(kPos / iW)   ; wrap around
  else
    kPos  = limit(kPos, 0, iW - 1)         ; hold the first / last column
  endif
  kC0     = floor(kPos)
  kFrac   = (kPos - kC0) * iGlide          ; how far towards the next column
  kC1     = kC0 + 1
  if kC1 >= iW then
    kC1   = (iLoopI == 1 ? 0 : iW - 1)
  endif

  ; ---- 6. Read both columns, blend, smooth (all bins at once, as arrays) --
  kA[]    init iB
  kNext[] init iB
  kTgt[]  init iB
  kGL[]   init iB                        ; current left gain of every bin
  kGR[]   init iB                        ; current right gain of every bin
  ftslice iGainL, iCol, kC0 * iB, (kC0 + 1) * iB  ; copy one column out of the picture
  copyf2array kA, iCol
  ftslice iGainL, iCol, kC1 * iB, (kC1 + 1) * iB
  copyf2array kNext, iCol
  kTgt    = kA + (kNext - kA) * kFrac
  kGL     = kGL + (kTgt - kGL) * kCoef   ; one-pole low-pass per bin
  copya2ftab kGL, iMaskL
  ftslice iGainR, iCol, kC0 * iB, (kC0 + 1) * iB
  copyf2array kA, iCol
  ftslice iGainR, iCol, kC1 * iB, (kC1 + 1) * iB
  copyf2array kNext, iCol
  kTgt    = kA + (kNext - kA) * kFrac
  kGR     = kGR + (kTgt - kGR) * kCoef
  copya2ftab kGR, iMaskR

  ; ---- 7. Phase vocoder: analyse, weight every bin, resynthesise ----------
  fL      pvsanal aInL, iN, iHop, iN, 1    ; window type 1 = Hann
  fLm     pvsmaska fL, iMaskL, 1           ; depth 1: amplitude x mask value
  aWetL   pvsynth fLm
  fR      pvsanal aInR, iN, iHop, iN, 1
  fRm     pvsmaska fR, iMaskR, 1
  aWetR   pvsynth fRm

  ; ---- 8. Dry/wet mix (dry delayed by the vocoder latency), output --------
  iLat    = (iN + 2 * iHop) / sr           ; measured latency of the chain
  aDryL   delay aInL, iLat
  aDryR   delay aInR, iLat
  aMix    interp kMix
  aGain   interp kOut
  kEnv    linsegr 0, 0.02, 1, 0.08, 0      ; 20 ms fade-in, 80 ms fade-out when stopped
  aEnv    interp kEnv
  aL      = (aDryL + (aWetL - aDryL) * aMix) * aGain * aEnv
  aR      = (aDryR + (aWetR - aDryR) * aMix) * aGain * aEnv
  ; Safety soft limit for LIVE playback (transparent below about -1 dBFS). Renders set the channel
  ; "PaintRooms.noclip" to 1: they are float and normalised afterwards, so limiting here would
  ; only add distortion. An unset channel reads 0 = limiter on.
  if chnget:k("PaintRooms.noclip") == 0 then
    aL    clip aL, 0, 0.98 * 0dbfs, 0.9
    aR    clip aR, 0, 0.98 * 0dbfs, 0.9
  endif
  if nchnls == 1 then
    out (aL + aR) * 0.5
  else
    outch 1, aL, 2, aR
  endif

  ; A one-shot play ends by itself.
  if iDur > 0 && kTime > iDur then
    turnoff
  endif
endin

; Stops every Image Filter, letting its 80 ms fade-out play.
instr 9352
  turnoff2 9351, 0, 1
  turnoff
endin
""";

    /// <summary>
    /// Global code that sets the three live channels (Csound 7: a channel's default value does not
    /// initialise it, so it is written explicitly).
    /// </summary>
    /// <param name="s">Settings whose mix, output gain and smoothing are written.</param>
    /// <returns>Three <c>chnset</c> lines (global code, runs once when compiled).</returns>
    /// <remarks>
    /// Must run before the note starts: the instrument reads the channels at i-time (<c>iMix0</c>,
    /// <c>iOut0</c>) as the starting points of its <c>portk</c> glides.
    /// </remarks>
    public static string ChannelInit(ImageFilterSettings s) =>
        $"chnset {F(s.Mix)}, \"{MixChannel}\"   ; dry/wet mix (0 = dry, 1 = filtered)\n" +
        $"chnset {F(OutputGain(s))}, \"{GainChannel}\"   ; output gain ({F(s.OutputDb)} dB)\n" +
        $"chnset {F(s.SmoothingMs / 1000)}, \"{SmoothChannel}\"   ; EQ smoothing time (s)\n";

    /// <summary>Linear output gain of <paramref name="s"/>.</summary>
    /// <param name="s">Settings; <see cref="ImageFilterSettings.OutputDb"/> is clamped to −96…+12 dB.</param>
    /// <returns>10^(dB/20): amplitude factor, e.g. −6 dB → ≈ 0.5.</returns>
    public static double OutputGain(ImageFilterSettings s) => Math.Pow(10, Math.Clamp(s.OutputDb, -96, 12) / 20);

    /// <summary>Describes where a play starts and how long it lasts (the p8, p9, p14…p17 fields).</summary>
    /// <param name="StartColumn">Gain-table column where the picture starts.</param>
    /// <param name="LoopImage">The picture repeats.</param>
    /// <param name="LiveInput">Source = live input (otherwise <paramref name="SourcePath"/>).</param>
    /// <param name="SkipSeconds">Seconds into the file.</param>
    /// <param name="LoopSource">The file loops.</param>
    /// <param name="DurationSeconds">Stop after this many seconds (0 = held until stopped).</param>
    /// <param name="SourcePath">Stereo WAV played by diskin2 (ignored for live input).</param>
    public sealed record PlaySpec(double StartColumn, bool LoopImage, bool LiveInput, double SkipSeconds, bool LoopSource,
        double DurationSeconds, string? SourcePath);

    /// <summary>The p4…p17 fields of an instr 9351 score line (see the instrument's section 1).</summary>
    /// <param name="t">Gain tables (give the column and bin counts, p5/p6).</param>
    /// <param name="s">Settings (FFT size, hop, glide).</param>
    /// <param name="baseTable">First table of the set (p4).</param>
    /// <param name="passSeconds">Seconds for one pass over the picture (p7).</param>
    /// <param name="p">Start position, looping, source and duration (p8…p10, p14…p17).</param>
    /// <returns>The fields separated by spaces, p17 quoted; the caller prefixes «i 9351[.nnnn] start dur».</returns>
    public static string Fields(ImageFilterTables t, ImageFilterSettings s, int baseTable, double passSeconds, PlaySpec p)
    {
        // Csound strings: forward slashes avoid backslash escapes; quotes cannot appear in Windows paths.
        string file = (p.SourcePath ?? "").Replace('\\', '/');
        return string.Join(' ',
            baseTable.ToString(Ci), t.Columns.ToString(Ci), t.Bins.ToString(Ci), F(passSeconds),
            F(Math.Clamp(p.StartColumn, 0, t.Columns - 1)), p.LoopImage ? "1" : "0", p.LiveInput ? "1" : "0",
            s.FftSize.ToString(Ci), s.Hop.ToString(Ci), s.Glide ? "1" : "0", F(Math.Max(0, p.SkipSeconds)),
            p.LoopSource ? "1" : "0", F(Math.Max(0, p.DurationSeconds)), "\"" + file + "\"");
    }

    /// <summary>
    /// Live use: the ftgen lines that create an EMPTY table set at <paramref name="baseTable"/>, one
    /// per table with its offset (the player creates the two big ones in separate control cycles
    /// and copies the data straight into their memory).
    /// </summary>
    /// <param name="t">Gain tables (sizes only; the data is copied separately).</param>
    /// <param name="baseTable">First table number of the set.</param>
    /// <returns>One (offset in <see cref="TableMap"/>, orchestra line) pair per table.</returns>
    /// <remarks>
    /// GEN −2 with a single 0 creates a zero-filled table of exactly the given size (the minus sign
    /// skips normalisation; the size need not be a power of two). <c>giFiltTab</c> is a
    /// throw-away global: <c>ftgen</c> needs an output variable, the number is fixed anyway.
    /// </remarks>
    public static IReadOnlyList<(int Offset, string Code)> LiveTableLines(ImageFilterTables t, int baseTable)
    {
        // big = whole picture in bins (Columns × Bins floats); b = one column.
        int big = t.Columns * t.Bins, b = t.Bins;
        return
        [
            (TableMap.GainL, $"giFiltTab ftgen {baseTable + TableMap.GainL}, 0, {big}, -2, 0   ; left gains\n"),
            (TableMap.GainR, $"giFiltTab ftgen {baseTable + TableMap.GainR}, 0, {big}, -2, 0   ; right gains\n"),
            (TableMap.MaskL, $"giFiltTab ftgen {baseTable + TableMap.MaskL}, 0, {b}, -2, 0   ; left EQ curve\n"),
            (TableMap.MaskR, $"giFiltTab ftgen {baseTable + TableMap.MaskR}, 0, {b}, -2, 0   ; right EQ curve\n"),
            (TableMap.Column, $"giFiltTab ftgen {baseTable + TableMap.Column}, 0, {b}, -2, 0   ; scratch column\n"),
        ];
    }

    /// <summary>
    /// Writes a standalone, readable .csd that filters the source once with the picture, with its
    /// gain tables as 32-bit float «sound files» next to it (<c>&lt;stem&gt;_gain_left.wav</c>,
    /// <c>_gain_right.wav</c>, loaded with GEN01) and, for a file source, the source as
    /// <c>&lt;stem&gt;_source.wav</c> (stereo float, played by diskin2).
    /// </summary>
    /// <param name="t">Gain tables (computed for <paramref name="sampleRate"/>).</param>
    /// <param name="s">Filter settings.</param>
    /// <param name="sampleRate">sr written in the header.</param>
    /// <param name="ksmps">ksmps written in the header.</param>
    /// <param name="source">The source sound (copied next to the .csd), or <c>null</c> for live input.</param>
    /// <param name="passSeconds">Seconds of one pass over the picture.</param>
    /// <param name="durationSeconds">Length of the score note (live input: how long the .csd runs).</param>
    /// <param name="folder">Folder for the .csd and its data files (created if needed).</param>
    /// <param name="stem">File name stem, e.g. «filter».</param>
    /// <param name="absolutePaths">True: data files by absolute path (offline render); false: plain names (portable folder).</param>
    /// <param name="title">Shown in the header comment.</param>
    /// <returns>The path of the .csd written.</returns>
    public static string WriteStandaloneCsd(ImageFilterTables t, ImageFilterSettings s, double sampleRate, int ksmps, AudioData? source,
        double passSeconds, double durationSeconds, string folder, string stem, bool absolutePaths, string title, bool forRender = false)
    {
        Directory.CreateDirectory(folder);
        // Data: where a data file is written on disk. Ref: how the .csd names it (absolute with
        // forward slashes for renders, or a bare name resolved next to the .csd when it is opened).
        string Data(string suffix) => Path.Combine(folder, stem + suffix);
        string Ref(string suffix) => absolutePaths ? Data(suffix).Replace('\\', '/') : stem + suffix;

        // The gain tables are written as mono «sound files» of Columns × Bins samples (no sample
        // rate meaning; GEN01 just reads the floats back).
        WavIO.WriteFloat32(Data("_gain_left.wav"), t.GainL);
        WavIO.WriteFloat32(Data("_gain_right.wav"), t.GainR);
        if (source is not null)
            WavIO.WriteFloat32(Data("_source.wav"), StereoOf(source), source.SampleRate);

        bool live = source is null;
        bool fit = s.TimeMode == FilterTimeMode.FitToSound && !live;
        int b0 = FirstTable;
        var sb = new StringBuilder();
        sb.Append("<CsoundSynthesizer>\n<CsOptions>\n-odac\n");
        if (live) sb.Append("; Live input: inside Csound Visual choose it in Settings > Audio > Input.\n; With PortAudio/ASIO or the csound command line add -iadc here.\n");
        sb.Append("</CsOptions>\n<CsInstruments>\n");
        sb.Append($"; {title}\n");
        sb.Append($"; Generated by Csound Visual · Paint Rooms · Image Filter · {DateTime.Now:yyyy-MM-dd HH:mm}\n");
        sb.Append($"; Picture: {t.ImageWidth} columns × {t.Map.Rows} rows on a {t.Map.Axis.Describe()} axis,\n");
        sb.Append($";   translated to {t.Columns} columns × {t.Bins} FFT bins (FFT {s.FftSize}, hop {s.Hop}, latency {s.LatencySamples} samples).\n");
        sb.Append($"; Level → gain: {(s.Response == FilterResponse.Linear ? "gain = level" : $"decibels, range {F(s.RangeDb)} dB")}, white = {F(s.BoostDb)} dB.\n");
        sb.Append($"; One pass over the picture: {F(passSeconds)} s{(fit ? " (stretched over the whole sound)" : ", repeating")}.\n");
        sb.Append(live ? "; Source: the live audio input.\n" : $"; Source: {stem}_source.wav ({F(source!.Seconds)} s, {source.SampleRate} Hz).\n");
        sb.Append(";\n; Press Play (F5) to hear it. The data files next to this .csd hold the tables.\n\n");
        sb.Append($"sr     = {F(sampleRate)}\nksmps  = {ksmps}\nnchnls = 2\n0dbfs  = 1\n\n");
        sb.Append(ChannelInit(s)).Append('\n');
        if (forRender)
            sb.Append("chnset 1, \"PaintRooms.noclip\"   ; offline render: no safety limiter (float output, normalised afterwards)\n\n");
        sb.Append($"; ---- The tables (numbers {b0}…{b0 + 4}; instr 9351 finds them from p4 = {b0}) ----\n");
        sb.Append("; The picture, already translated by Csound Visual into one gain per FFT bin\n");
        sb.Append($"; ({t.Bins} bins per column, column after column), stored as 32-bit float\n");
        sb.Append("; «sound files» and loaded with GEN01 (here -1: do not rescale).\n");
        sb.Append($"giGainL ftgen {b0 + TableMap.GainL}, 0, 0, -1, \"{Ref("_gain_left.wav")}\", 0, 0, 1    ; left gains\n");
        sb.Append($"giGainR ftgen {b0 + TableMap.GainR}, 0, 0, -1, \"{Ref("_gain_right.wav")}\", 0, 0, 1   ; right gains\n");
        sb.Append($"giMaskL ftgen {b0 + TableMap.MaskL}, 0, {t.Bins}, -2, 0   ; current left EQ curve\n");
        sb.Append($"giMaskR ftgen {b0 + TableMap.MaskR}, 0, {t.Bins}, -2, 0   ; current right EQ curve\n");
        sb.Append($"giCol   ftgen {b0 + TableMap.Column}, 0, {t.Bins}, -2, 0   ; scratch column\n\n");
        sb.Append(Instruments());
        sb.Append("\n</CsInstruments>\n<CsScore>\n");
        sb.Append("; p4 tables, p5 columns, p6 bins, p7 seconds per pass, p8 start column, p9 picture loops,\n");
        sb.Append("; p10 source (0 file, 1 live), p11 FFT, p12 hop, p13 glide, p14 skip s, p15 file loops, p16 stop after s, p17 file\n");
        // One note from the start of the picture and the file. p16 = durationSeconds makes the
        // instrument turn itself off; the score duration has 0.1 s extra so that internal turnoff
        // (not the score end) ends it.
        var spec = new PlaySpec(0, !fit, live, 0, false, durationSeconds, live ? "" : Ref("_source.wav"));
        sb.Append($"i 9351 0 {F(durationSeconds + 0.1)} {Fields(t, s, b0, passSeconds, spec)}\n");
        sb.Append("</CsScore>\n</CsoundSynthesizer>\n");
        string csd = Path.Combine(folder, stem + ".csd");
        File.WriteAllText(csd, sb.ToString());
        return csd;
    }

    /// <summary>
    /// The first two channels of <paramref name="a"/> (a mono sound is duplicated): the instrument
    /// always reads a stereo file, so the left/right layers of the picture can pan even a mono source.
    /// </summary>
    /// <param name="a">Any sound.</param>
    /// <returns>Two channel arrays (shared with <paramref name="a"/>, not copied); empty arrays for a sound with no channels.</returns>
    public static float[][] StereoOf(AudioData a) =>
        a.Channels.Length switch
        {
            0 => [[], []],
            1 => [a.Channels[0], a.Channels[0]],
            _ => [a.Channels[0], a.Channels[1]],
        };
}
