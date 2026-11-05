// SpectralMask.cs — Paint Rooms: the FAITHFUL resynthesis of the «Spectrum Room» (image → sound).
//
// Idea: instead of synthesising a new sound from the picture (which loses the original phases,
// transients and noise character), keep the ORIGINAL recording and use the picture only to decide
// how loud every time-frequency point should be. The edited picture is compared with the original
// analysis; the difference becomes a gain «mask» that multiplies the recording's short-time
// spectrum, and the inverse STFT turns it back into sound. Unedited parts get a gain of exactly 1 and
// therefore come out identical to the input (measured: error < −130 dB); erased parts get 0.
//
// ------------------------------------------------------------------------------------------------
// GAIN OF ONE CELL (BuildGains)
// ------------------------------------------------------------------------------------------------
// Levels are linear in dB (SpectralAnalysis, step 6), so the edit of a cell, in dB, is simply
//     gain_dB = (level_edited − level_original) · rangeDb            (rangeDb = −floor, e.g. 90)
// with two rules on top:
//   * ERASED: a cell that had sound (original > 0) and is now 0 gets gain 0 (true silence), not
//     merely «−rangeDb» — erasing must remove, however loud the original was;
//   * CLAMPED: boosts are limited to +MaxBoostDb (default +24 dB). A mask can only re-weight what is
//     already in the recording: painting over silence amplifies the faint noise there, and without a
//     limit it would amplify it by up to 90 dB. (New material belongs to the «additive» path.)
// «Solo» (listen to / extract only the selection) sets every cell outside the selected rectangle to 0.
// FREQUENCIES OUTSIDE THE AXIS (below its lowest row or above its highest, e.g. DC, sub-bass rumble
// or the air above 16 kHz): the analysis adds their energy to the nearest EDGE ROW (see
// SpectralAnalysis, step 4), and the mask gives them that edge row's gain — so erasing a whole time
// span (all rows) really silences it, erasing the top rows also removes what lies above them, and
// untouched edges leave them unchanged.
//
// ------------------------------------------------------------------------------------------------
// APPLYING IT (Apply) — STFT → mask → inverse STFT, weighted overlap-add (WOLA)
// ------------------------------------------------------------------------------------------------
// Frames as in the analysis: frame m is centred on sample m·hop, window w (Hann), FFT size N.
//   1. X_m = FFT(w · x around m·hop).
//   2. For every bin k the gain is looked up TWICE in the mask and the two are combined:
//        g_true   at the bin's TRUE frequency (phase vocoder, as in the analysis — this is the row
//                 the bin's energy was drawn in, so erasing a partial's row removes all the bins
//                 of its window lobe even when they lie in neighbouring rows), and
//        g_centre at the bin's centre frequency k·sr/N (which catches noise bins whose phase-derived
//                 frequency wanders out of an erased band);
//      g = g_true, except that g_centre may LOWER it: g = min(g_true, g_centre) when g_centre < 1.
//      Why asymmetric: where rows are finer than bins (low frequencies on a log axis) the energy
//      of a partial is concentrated in its true row, and the bin centres of its lobe fall in DARK
//      rows (level 0 before and after the edit → gain 1, «no information»). Combining
//      symmetrically (e.g. the geometric mean, tried first) would halve every edit there in dB —
//      measured: «−12 dB on everything» gave only −9 dB. With the rule above, uniform gains are
//      exact, erasing still removes noise bins centred in the erased band (g_centre = 0), and a
//      boost is never spread to a neighbour partial whose lobe bins merely sit inside the band.
//      Solo/extract masks use g_true alone (GainMap.Solo), so a partial inside a narrow selected
//      band keeps its whole window lobe.
//      Each lookup is BILINEAR: in time between the two image columns around the frame's centre
//      time, in pitch between the two rows around the bin's fractional row. That interpolation is
//      the mask's smoothing: the gain never jumps from one frame or bin to the next, which avoids
//      clicks at the edges of a selection and the warbling «musical noise» of hard binary masks.
//   3. Y_m[k] = g · X_m[k]; the negative frequencies are the mirrored conjugates (real output).
//   4. y += w · IFFT(Y_m) at the frame's position, and finally y /= Σ_m w² (per sample).
//      WOLA normalisation: with no gains, Σ_m w·(w·x) / Σ_m w² = x exactly for ANY window and any
//      overlap where the windows cover every sample — that is why «no edits» returns the input.
// Frames whose two neighbouring columns are all-unity (so the edges, too) skip the FFT: their
// contribution w·w·x is written directly — identical result, much faster for sparse edits.
//
// Threading: pure computation on its own copies — run Apply from a background task (progress and
// cancellation supported). Frames are processed in parallel blocks; every block overlap-adds into
// its own buffer and the buffers are summed at the end (no locks in the hot loop).

using System.Numerics;

namespace CsoundVisual.Paint;

/// <summary>
/// A gain per image cell (linear, ≥ 0) for each layer; frequencies outside the pitch axis get the
/// gain of the nearest edge row. Same geometry as the image it was built from (column-major, index x·Height + y).
/// </summary>
/// <param name="Width">Columns.</param>
/// <param name="Axis">Pitch axis (rows).</param>
/// <param name="Left">Gains of the left layer (channel 1).</param>
/// <param name="Right">Gains of the right layer (channel 2).</param>
/// <param name="Solo">
/// True for a solo/extract mask (zeros outside a selection): then only the true-frequency gain is
/// used, because the bin-centre guard would cut the window-lobe bins of a partial that lie just
/// outside a narrow selected band (measured: −0.9 dB on a note in a 45 Hz band with 23 Hz bins).
/// </param>
public sealed record GainMap(int Width, PitchAxis Axis, float[] Left, float[] Right, bool Solo = false)
{
    /// <summary>Rows.</summary>
    public int Height => Axis.RowCount;

    /// <summary>True when the two layers differ (a mono sound then becomes stereo).</summary>
    public bool IsStereo
    {
        get
        {
            for (int i = 0; i < Left.Length; i++)
                if (Left[i] != Right[i]) return true;
            return false;
        }
    }

    /// <summary>True when every gain is exactly 1 (the mask changes nothing).</summary>
    public bool IsIdentity
    {
        get
        {
            foreach (float g in Left) if (g != 1) return false;
            foreach (float g in Right) if (g != 1) return false;
            return true;
        }
    }
}

/// <summary>Faithful resynthesis of an edited analysis (see the file header).</summary>
public static class SpectralMask
{
    /// <summary>Default largest boost a mask may apply (dB).</summary>
    public const double DefaultMaxBoostDb = 24;

    /// <summary>
    /// The gain mask that turns <paramref name="original"/> into <paramref name="edited"/> (see the
    /// file header). When the two images differ in size or axis (the user resampled or retuned the
    /// edited one), the original is first resampled to the edited geometry with
    /// <see cref="SpectralImage.Retuned"/>.
    /// </summary>
    /// <param name="original">The unedited analysis.</param>
    /// <param name="edited">The edited image.</param>
    /// <param name="rangeDb">dB range of the levels (−floor of the analysis).</param>
    /// <param name="solo">When set: only the selected cells pass (with their edits), each scaled by its
    /// coverage (a soft, anti-aliased or feathered rim fades instead of cutting); everything else is silenced.</param>
    /// <param name="maxBoostDb">Largest boost (dB).</param>
    public static GainMap BuildGains(SpectralImage original, SpectralImage edited, double rangeDb, SelectionMask? solo = null,
        double maxBoostDb = DefaultMaxBoostDb)
    {
        if (original.Width != edited.Width || !original.Axis.Equals(edited.Axis))
            original = original.Retuned(edited.Width, edited.Axis);
        int w = edited.Width, h = edited.Height;
        var gl = new float[w * h];
        var gr = new float[w * h];
        for (int x = 0; x < w; x++)
            for (int y = 0; y < h; y++)
            {
                int i = x * h + y;
                float keep = solo?[x, y] ?? 1f;                   // coverage of the solo selection
                if (keep <= 0) continue;                            // outside: the gain stays 0
                gl[i] = CellGain(original.Left[i], edited.Left[i], rangeDb, maxBoostDb) * keep;
                gr[i] = CellGain(original.Right[i], edited.Right[i], rangeDb, maxBoostDb) * keep;
            }
        return new GainMap(w, edited.Axis, gl, gr, solo is not null);
    }

    /// <summary>
    /// Gain of one cell from its original and edited levels: 1 when equal, 0 when erased, otherwise
    /// 10^((edited − original)·rangeDb / 20) clamped to +<paramref name="maxBoostDb"/>.
    /// </summary>
    public static float CellGain(float original, float edited, double rangeDb, double maxBoostDb = DefaultMaxBoostDb)
    {
        if (edited == original) return 1f;                 // exact: unedited cells must give exactly 1
        if (edited <= 0 && original > 0) return 0f;        // erased → silence
        double db = Math.Min(maxBoostDb, (edited - original) * rangeDb);
        return (float)Math.Pow(10, db / 20);
    }

    /// <summary>
    /// Applies <paramref name="gains"/> to the analysed sound by STFT → mask → inverse STFT
    /// (see the file header). Output: 2 channels when the sound is stereo or the mask is stereo,
    /// else 1; same sample rate and length as the input.
    /// </summary>
    /// <param name="analysis">The analysis (its sound and STFT settings are used).</param>
    /// <param name="gains">The mask (any width/axis: column ↔ time and row ↔ frequency are mapped).</param>
    /// <param name="progress">Receives 0…1 (from worker threads).</param>
    /// <param name="cancel">Stops the work (throws <see cref="OperationCanceledException"/>).</param>
    /// <param name="forceFullPath">Tests only: run the FFT path even for all-unity frames.</param>
    /// <returns>The resynthesised sound (new arrays; the analysis is not modified).</returns>
    public static AudioData Apply(SpectrumAnalysis analysis, GainMap gains, IProgress<double>? progress = null,
        CancellationToken cancel = default, bool forceFullPath = false)
    {
        var audio = analysis.Audio;
        int outCh = audio.Channels.Length >= 2 || gains.IsStereo ? 2 : 1;
        var result = new float[outCh][];
        long total = outCh, finished = 0;
        for (int c = 0; c < outCh; c++)
        {
            // A mono sound with a stereo mask: both output channels start from the one input channel.
            var input = audio.Channels[Math.Min(c, audio.Channels.Length - 1)];
            var layer = c == 0 ? gains.Left : gains.Right;
            var sub = progress is null ? null : new Progress<double>(p => progress.Report((finished + p) / total));
            result[c] = ApplyChannel(input, audio.SampleRate, analysis.Settings, gains, layer, sub, cancel, forceFullPath);
            finished++;
        }
        progress?.Report(1);
        return new AudioData(result, audio.SampleRate);
    }

    /// <summary>The mask of one channel (one layer of gains). See the file header, steps 1–4.</summary>
    /// <param name="x">Input samples of the channel.</param>
    /// <param name="sampleRate">Their sample rate (Hz), to map FFT bins to Hz.</param>
    /// <param name="s">STFT settings of the analysis (frame size, hop).</param>
    /// <param name="gains">The gain map (its axis and width map rows/columns to Hz/time).</param>
    /// <param name="layer">The gains of this channel (<see cref="GainMap.Left"/> or <see cref="GainMap.Right"/>).</param>
    /// <param name="progress">Receives 0…1 for this channel, or <c>null</c>.</param>
    /// <param name="cancel">Stops the work.</param>
    /// <param name="forceFullPath">Run the FFT path even where the mask is all unity.</param>
    /// <returns>The processed channel, same length as <paramref name="x"/>.</returns>
    static float[] ApplyChannel(float[] x, int sampleRate, SpectrumAnalysisSettings s, GainMap gains, float[] layer,
        IProgress<double>? progress, CancellationToken cancel, bool forceFullPath)
    {
        int n = s.FrameSize, hop = s.HopSize, bins = n / 2 + 1, len = x.Length;
        int w = gains.Width, h = gains.Height;
        int frameCount = len / hop + 1;
        double seconds = (double)len / sampleRate, colSec = seconds / w;
        var window = WindowFunction.Create(s.Window, n);
        var lookup = new RowLookup(gains.Axis, sampleRate, n);
        bool solo = gains.Solo;                           // solo: true-frequency gain only (see GainMap.Solo)
        var stft = new StftSettings(n, hop, s.Window);   // for Stft.TrueFrequency

        // Fractional row of every bin centre (for g_centre), computed once.
        var centreRow = new double[bins];
        for (int k = 0; k < bins; k++) centreRow[k] = k == 0 ? double.NegativeInfinity : lookup.RowOf((double)k * sampleRate / n);

        // Columns whose gains are all exactly 1: frames between two such columns skip the FFT.
        var unityColumn = new bool[w];
        for (int c = 0; c < w; c++)
        {
            bool u = true;
            for (int y = 0, o = c * h; y < h && u; y++) u = layer[o + y] == 1f;
            unityColumn[c] = u;
        }

        // Parallel blocks of frames. Each block overlap-adds into its own buffer covering the samples
        // its frames touch; the buffers are summed afterwards (in block order, deterministic).
        int blocks = Math.Clamp(Environment.ProcessorCount * 4, 1, Math.Max(1, frameCount / 16));
        var parts = new (int Start, double[] Data)[blocks];
        long framesDone = 0;
        var options = new ParallelOptions { CancellationToken = cancel };
        Parallel.For(0, blocks, options, b =>
        {
            int m0 = (int)((long)frameCount * b / blocks), m1 = (int)((long)frameCount * (b + 1) / blocks);
            int start = m0 * hop - n / 2;
            var acc = new double[Math.Max(0, (m1 - m0 - 1) * hop + n)];
            parts[b] = (start, acc);
            if (m1 <= m0) return;
            var buf = new Complex[n];
            var rowGain = new double[h];
            var prevPhase = new double[bins];
            var phase = new double[bins];
            bool havePrev = false;
            if (m0 > 0)
            {
                SpectralAnalysis.Transform(x, (m0 - 1) * hop - n / 2, window, buf);
                for (int k = 0; k < bins; k++) prevPhase[k] = buf[k].Phase;
                havePrev = true;
            }
            for (int m = m0; m < m1; m++)
            {
                cancel.ThrowIfCancellationRequested();
                int fs = m * hop - n / 2;    // first sample of the frame
                // Time position of the frame in column units (centres at c + 0.5): u = t·W/T − 0.5.
                double u = m * (double)hop / sampleRate / colSec - 0.5;
                int ca = Math.Clamp((int)Math.Floor(u), 0, w - 1), cb = Math.Min(ca + 1, w - 1);
                double tu = Math.Clamp(u - Math.Floor(u), 0, 1);
                if (u < 0) { cb = ca; tu = 0; }
                bool unity = !forceFullPath && unityColumn[ca] && unityColumn[cb];
                if (unity)
                {
                    // Gain 1 everywhere: IFFT(FFT(w·x)) = w·x, so the WOLA contribution is w²·x directly.
                    for (int i = 0; i < n; i++)
                    {
                        int j = fs + i;
                        if ((uint)j < (uint)len) acc[j - start] += x[j] * window[i] * window[i];
                    }
                    havePrev = false;   // the next processed frame recomputes this frame's phases
                    Interlocked.Increment(ref framesDone);
                    continue;
                }

                if (!havePrev && m > 0)
                {
                    // The previous frame was skipped (unity): compute it once for its phases, so the
                    // true frequencies here are as accurate as in the analysis.
                    SpectralAnalysis.Transform(x, fs - hop, window, buf);
                    for (int k = 0; k < bins; k++) prevPhase[k] = buf[k].Phase;
                    havePrev = true;
                }
                SpectralAnalysis.Transform(x, fs, window, buf);
                // Gains of every row at this frame time (linear interpolation between the two columns).
                int oa = ca * h, ob = cb * h;
                for (int y = 0; y < h; y++) rowGain[y] = layer[oa + y] + (layer[ob + y] - layer[oa + y]) * tu;

                for (int k = 0; k < bins; k++)
                {
                    var X = buf[k];
                    double ph = X.Phase;
                    phase[k] = ph;
                    double gCentre = RowGain(rowGain, centreRow[k]);
                    double g;
                    if (k == 0 || k == bins - 1)
                        g = gCentre;                     // DC / Nyquist: no meaningful true frequency
                    else
                    {
                        double hz = havePrev ? Stft.TrueFrequency(prevPhase[k], ph, k, stft, sampleRate) : (double)k * sampleRate / n;
                        double gTrue = RowGain(rowGain, lookup.RowOf(hz));
                        g = !solo && gCentre < 1 && gCentre < gTrue ? gCentre : gTrue;   // see the header, step 2
                    }
                    buf[k] = X * g;
                }
                (prevPhase, phase) = (phase, prevPhase);
                havePrev = true;

                // Rebuild the negative frequencies (mirrored conjugates) and invert.
                for (int k = 1; k < n / 2; k++) buf[n - k] = Complex.Conjugate(buf[k]);
                buf[0] = new Complex(buf[0].Real, 0);
                buf[n / 2] = new Complex(buf[n / 2].Real, 0);
                Fft.Transform(buf, inverse: true);
                for (int i = 0; i < n; i++)
                {
                    int j = fs + i;
                    if ((uint)j < (uint)len) acc[j - start] += buf[i].Real * window[i];
                }
                long d = Interlocked.Increment(ref framesDone);
                if (progress is not null && d % 256 == 0) progress.Report((double)d / frameCount);
            }
        });

        // Sum the block buffers, then divide by Σ w² of the frames covering each sample.
        var sum = new double[len];
        foreach (var (start, data) in parts)
        {
            if (data is null) continue;
            for (int i = 0; i < data.Length; i++)
            {
                int j = start + i;
                if ((uint)j < (uint)len) sum[j] += data[i];
            }
        }
        var norm = new double[len];
        for (int m = 0; m < frameCount; m++)
        {
            int fs = m * hop - n / 2;
            for (int i = 0; i < n; i++)
            {
                int j = fs + i;
                if ((uint)j < (uint)len) norm[j] += window[i] * window[i];
            }
        }
        var output = new float[len];
        for (int i = 0; i < len; i++) output[i] = norm[i] > 1e-12 ? (float)(sum[i] / norm[i]) : 0f;
        return output;
    }

    /// <summary>
    /// Gain at fractional row <paramref name="r"/>: linear between the two nearest rows; beyond the
    /// axis ends, the end row's gain (r = −∞, i.e. 0 Hz / DC, counts as below the axis).
    /// </summary>
    /// <param name="rowGain">Gain per row of the current column (row 0 = lowest pitch).</param>
    /// <param name="r">Fractional row of an FFT bin (NaN for DC).</param>
    /// <returns>The interpolated linear gain.</returns>
    static double RowGain(double[] rowGain, double r)
    {
        int h = rowGain.Length;
        if (double.IsNaN(r)) return rowGain[0];
        if (r <= 0) return rowGain[0];
        if (r >= h - 1) return rowGain[h - 1];
        int i = (int)r;
        double t = r - i;
        return rowGain[i] + (rowGain[i + 1] - rowGain[i]) * t;
    }

    // ------------------------------------------------------------------------------------------
    //  Output helpers
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// Writes <paramref name="audio"/> as a 24-bit WAV. With <paramref name="normalize"/> the peak
    /// is brought to −1 dBTP (true peak); without it the level is kept, except that it is lowered if
    /// its true peak would exceed −1 dBTP (24-bit integer cannot hold values beyond ±1). Returns the gain applied (dB).
    /// </summary>
    public static double WriteWav24(string path, AudioData audio, bool normalize)
    {
        // True-peak normalisation / protection to −1 dBTP (see AudioData.RenderGain).
        double g = audio.RenderGain(normalize);
        var ch = audio.Channels.Select(c =>
        {
            var o = new float[c.Length];
            for (int i = 0; i < o.Length; i++) o[i] = (float)(c[i] * g);
            return o;
        }).ToArray();
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        WavIO.WritePcm24(path, ch, audio.SampleRate);
        return 20 * Math.Log10(g);
    }

    /// <summary>
    /// The sound as two channels (a mono sound is duplicated): the format of the preview files the
    /// Spectrum Room plays through Csound (its preview instrument always reads two channels).
    /// </summary>
    public static float[][] AsStereo(AudioData audio) =>
        audio.Channels.Length >= 2 ? [audio.Channels[0], audio.Channels[1]] : [audio.Channels[0], audio.Channels[0]];
}
