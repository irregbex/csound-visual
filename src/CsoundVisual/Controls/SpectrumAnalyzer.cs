namespace CsoundVisual.Controls;

/// <summary>
/// Shared spectrum analysis used by the 2D <see cref="SpectrumView"/> and the 3D waterfall:
/// Hann-windowed 2048-point FFT of the newest output samples, reduced to logarithmically
/// spaced bands (20 Hz – Nyquist) on a −90…0 dBFS scale normalised to 0…1.
/// </summary>
/// <remarks>
/// <para>
/// Pure computation, no Avalonia dependency, so the 3D <c>Visual3D.WaterfallScene</c> can reuse it
/// on its own timer. Each instance owns its scratch arrays and is <b>not thread-safe</b>; every
/// consumer creates its own (the 2D view and the waterfall both do), and all calls happen on the UI
/// thread.
/// </para>
/// <para>
/// The FFT size is fixed at 2048 because that is the length of the engine's scope ring
/// (<c>CsoundEngine.ScopeSize</c>); at 48 kHz that is ≈43 ms of audio and ≈23 Hz per bin.
/// </para>
/// <para>
/// <b>Band reduction.</b> Band <c>b</c> of <c>B</c> covers
/// <c>[20·r^(b/B), 20·r^((b+1)/B))</c> Hz with <c>r = Nyquist/20</c>, i.e. equal width on a log axis.
/// The band's level is the <i>maximum</i> bin magnitude inside it (not the sum), so a pure tone reads
/// the same height wherever it falls. At the low end several narrow bands map onto the same single
/// FFT bin and therefore show identical heights: that is the resolution limit of a 2048-point FFT.
/// </para>
/// </remarks>
public sealed class SpectrumAnalyzer
{
    /// <summary>FFT size in samples.</summary>
    public const int Size = 2048;

    /// <summary>Lowest analysed frequency (Hz).</summary>
    public const double MinFrequency = 20;

    /// <summary>dB value mapped to 0.</summary>
    public const double MinDb = -90;

    /// <summary>FFT work buffers (real and imaginary parts), reused on every <see cref="Analyse"/> call so analysis never allocates.</summary>
    readonly double[] re = new double[Size], im = new double[Size];
    /// <summary>Precomputed symmetric Hann window (coefficient per sample, 0 at both ends, 1 in the middle).</summary>
    readonly double[] window = new double[Size];

    /// <summary>Normalised level of each band (0…1) from the last <see cref="Analyse"/> call.</summary>
    public double[] Levels { get; }

    /// <summary>Number of bands.</summary>
    public int Bands => Levels.Length;

    /// <summary>Sample rate of the last analysed buffer (48 kHz until the first call, or when the caller passes 0).</summary>
    public double SampleRate { get; private set; } = 48000;

    /// <summary>Creates an analyser with <paramref name="bands"/> output bands.</summary>
    /// <param name="bands">Number of log-spaced bands in <see cref="Levels"/> (96 for the 2D view, configurable in the waterfall).</param>
    public SpectrumAnalyzer(int bands)
    {
        Levels = new double[bands];
        for (int i = 0; i < Size; i++)
            window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (Size - 1));
    }

    /// <summary>Analyses the newest <see cref="Size"/> samples of <paramref name="buffer"/> (chronological, ±1.0).</summary>
    /// <param name="buffer">Mono samples, oldest first; only the last <see cref="Size"/> are used.</param>
    /// <param name="rate">Sample rate of <paramref name="buffer"/> in Hz (0 or negative = assume 48 kHz). It sets the band edges.</param>
    /// <returns>False when the buffer is too short (then <see cref="Levels"/> keeps its previous values).</returns>
    public bool Analyse(float[] buffer, double rate)
    {
        if (buffer.Length < Size) return false;
        SampleRate = rate > 0 ? rate : 48000;
        // Window the newest Size samples into the real part; the input is real, so im = 0.
        int off = buffer.Length - Size;
        for (int i = 0; i < Size; i++) { re[i] = buffer[off + i] * window[i]; im[i] = 0; }
        Fft(re, im);

        double nyquist = SampleRate / 2;
        int bands = Levels.Length;
        for (int b = 0; b < bands; b++)
        {
            // Band edges in Hz (log-spaced), then in FFT bins (bin k = k·rate/Size Hz). Bin 0 (DC) is
            // skipped and every band gets at least one bin, even when it is narrower than a bin.
            double f0 = MinFrequency * Math.Pow(nyquist / MinFrequency, (double)b / bands);
            double f1 = MinFrequency * Math.Pow(nyquist / MinFrequency, (double)(b + 1) / bands);
            int k0 = Math.Max(1, (int)(f0 / SampleRate * Size)), k1 = Math.Max(k0 + 1, (int)(f1 / SampleRate * Size));
            double max = 0;
            for (int k = k0; k < k1 && k < Size / 2; k++)
                max = Math.Max(max, Math.Sqrt(re[k] * re[k] + im[k] * im[k]));
            // 2/N for a one-sided spectrum, ×2 to compensate the Hann window's 0.5 gain, so a
            // full-scale sine reads ≈0 dBFS. The 1e-12 floor avoids Log10(0) for silence.
            double db = 20 * Math.Log10(max * 4 / Size + 1e-12);
            Levels[b] = Math.Clamp((db - MinDb) / -MinDb, 0, 1);
        }
        return true;
    }

    /// <summary>Centre frequency of band <paramref name="b"/>.</summary>
    /// <param name="b">Band index, 0…<see cref="Bands"/>−1.</param>
    /// <returns>The geometric centre of the band in Hz, at the last analysed <see cref="SampleRate"/>.</returns>
    public double BandFrequency(int b) =>
        MinFrequency * Math.Pow(SampleRate / 2 / MinFrequency, (b + 0.5) / Levels.Length);

    /// <summary>In-place iterative radix-2 FFT.</summary>
    /// <param name="re">Real parts; length must be a power of two. Replaced by the spectrum's real parts.</param>
    /// <param name="im">Imaginary parts, same length. Replaced by the spectrum's imaginary parts.</param>
    /// <remarks>
    /// Classic Cooley–Tukey decimation in time: a bit-reversal permutation, then log2(n) passes of
    /// butterflies whose twiddle factor is advanced by complex multiplication (no per-butterfly
    /// sin/cos). Forward transform (negative exponent), unscaled.
    /// </remarks>
    static void Fft(double[] re, double[] im)
    {
        int n = re.Length;
        // Bit-reversal permutation: j walks the bit-reversed counterpart of i (incremented from
        // the top bit down); each pair is swapped once (only when i < j).
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        // Butterfly passes over sub-transforms of length 2, 4, …, n. (wr, wi) is the unit step
        // e^(−2πi/len); (cr, ci) is the running twiddle e^(−2πi·j/len).
        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int j = 0; j < len / 2; j++)
                {
                    int a = i + j, c = a + len / 2;
                    double tr = re[c] * cr - im[c] * ci, ti = re[c] * ci + im[c] * cr;
                    re[c] = re[a] - tr; im[c] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
