// ImageFilterPresets.cs — Paint Rooms · «Image Filter»: starter filter pictures generated in code.
//
// Role: four pictures that show what an image filter can do, each with the time settings it is
// meant for. They are drawn on whatever axis/size the filter canvas has (so they also work on an
// equal-tempered or Scala axis), using frequencies in Hz and positions in fractions of the width:
//   * «Slow sweep»      — a soft band-pass (about a third of an octave wide) whose centre glides
//                         from 120 Hz up to 6 kHz and back: the classic «opening filter»;
//   * «Rhythmic gate»   — 16 steps per bar over two bars: full-range steps, low-only «kick» steps,
//                         high-only «hat» steps and silent gaps, with soft 6 % edges (no clicks);
//   * «Stereo ping-pong»— eight bands, each one lit on the opposite side from the previous one:
//                         the sound jumps left/right and up/down at the same time;
//   * «Harmonic comb»   — narrow bands on the harmonics (1…24) of a root that follows a chord
//                         progression in A minor (A, F, C, G): any noisy source starts to «sing»
//                         those chords.
//
// Pure functions of an empty SpectralImage; UI thread or any thread.
//
// Conventions shared by every Draw* method below:
//   * the image is column-major: sample (x, y) lives at index x * Height + y, row 0 = lowest
//     frequency of the axis (see SpectralImage);
//   * values are brightness 0…1 per side (Left / Right layers); the filter later maps brightness
//     to gain through ImageFilterSettings.Response (linear: 1 = 0 dB, 0.5 = −6 dB, 0 = silence);
//   * time is expressed as a fraction of the width, so the same picture fits any canvas width and
//     the Time function of the preset decides how many seconds/bars one pass of the picture lasts;
//   * band positions are computed in log2(Hz) through Axis.FrequencyOf, so they land on the right
//     rows on any pitch axis (logarithmic, equal-tempered, Scala).
// Used by ImageFilterRoom (Examples menu), ImageFilterSelfTest (the «sweep» picture) and the room's
// default canvas at start-up.

namespace CsoundVisual.Paint;

/// <summary>A starter filter picture.</summary>
/// <remarks>
/// <see cref="Draw"/> paints into an <i>empty</i> image (all zeros); <see cref="Time"/> is applied
/// to the current <see cref="ImageFilterSettings"/> with a <c>with</c> expression, so it only
/// overrides the time/feel fields (time mode, loop length, speed, glide, smoothing) and leaves the
/// FFT size, mix, gain and source untouched.
/// </remarks>
/// <param name="Id">Stable id (tests, settings).</param>
/// <param name="NameEs">Spanish name.</param>
/// <param name="NameEn">English name.</param>
/// <param name="TipEs">Spanish description (what you will hear).</param>
/// <param name="TipEn">English description.</param>
/// <param name="Draw">Paints the picture into an empty image.</param>
/// <param name="Time">Adjusts the time settings the picture is designed for.</param>
public sealed record FilterPreset(string Id, string NameEs, string NameEn, string TipEs, string TipEn,
    Action<SpectralImage> Draw, Func<ImageFilterSettings, ImageFilterSettings> Time);

/// <summary>The built-in starter filter pictures (see the file header).</summary>
public static class ImageFilterPresets
{
    /// <summary>All presets, in menu order.</summary>
    public static IReadOnlyList<FilterPreset> All { get; } =
    [
        new("sweep", "Barrido lento", "Slow sweep",
            "Una banda suave (un tercio de octava) cuyo centro sube de 120 Hz a 6 kHz y vuelve a bajar en 8 segundos: el clásico filtro que «se abre». Ideal sobre un loop de batería o un pad.",
            "A soft band (a third of an octave) whose centre rises from 120 Hz to 6 kHz and falls back in 8 seconds: the classic «opening filter». Great on a drum loop or a pad.",
            DrawSweep, s => s with { TimeMode = FilterTimeMode.Seconds, LoopSeconds = 8, Speed = 1, Glide = true, SmoothingMs = 25 }),
        new("gate", "Compuerta rítmica", "Rhythmic gate",
            "Dos compases de 16 pasos a 120 BPM: pasos completos, pasos solo graves («bombo»), pasos solo agudos («hi-hat») y silencios. Convierte un pad o una voz en un patrón rítmico.",
            "Two bars of 16 steps at 120 BPM: full steps, low-only «kick» steps, high-only «hat» steps and gaps. Turns a pad or a voice into a rhythmic pattern.",
            DrawGate, s => s with { TimeMode = FilterTimeMode.Bars, Bars = 2, Bpm = 120, Speed = 1, Glide = false, SmoothingMs = 6 }),
        new("pingpong", "Ping-pong estéreo", "Stereo ping-pong",
            "Ocho bandas que se encienden una tras otra, alternando izquierda (rojo) y derecha (verde): el sonido salta de lado y de altura. Escúchalo con audífonos.",
            "Eight bands that light up one after another, alternating left (red) and right (green): the sound jumps from side to side and up and down. Listen with headphones.",
            DrawPingPong, s => s with { TimeMode = FilterTimeMode.Bars, Bars = 1, Bpm = 120, Speed = 1, Glide = false, SmoothingMs = 15 }),
        new("comb", "Peine armónico", "Harmonic comb",
            "Bandas angostas en los armónicos de La, Fa, Do y Sol (La menor): un ruido, la lluvia o un loop de batería empiezan a «cantar» esos acordes. Prueba FFT 2048 para bandas más finas.",
            "Narrow bands on the harmonics of A, F, C and G (A minor): noise, rain or a drum loop starts to «sing» those chords. Try FFT 2048 for finer bands.",
            DrawComb, s => s with { TimeMode = FilterTimeMode.Seconds, LoopSeconds = 8, Speed = 1, Glide = false, SmoothingMs = 40 }),
    ];

    /// <summary>
    /// Adds a Gaussian band (in octaves) centred at <paramref name="hz"/> to column
    /// <paramref name="x"/>, with per-side levels; keeps the maximum with what is there.
    /// </summary>
    /// <param name="img">Target image.</param>
    /// <param name="x">Column.</param>
    /// <param name="hz">Band centre (Hz).</param>
    /// <param name="widthOct">
    /// Standard deviation σ of the bell in octaves. 0.17 puts the ±σ points 0.34 octave apart
    /// (≈ a third of an octave), where the bell is at e^−½ ≈ 0.61 of its peak (−4.3 dB with the
    /// linear response); ±√2·σ (≈ 0.48 octave apart) is the −8.7 dB (one neper) width.
    /// </param>
    /// <param name="left">Peak level on the left layer (brightness 0…1).</param>
    /// <param name="right">Peak level on the right layer (brightness 0…1).</param>
    /// <remarks>
    /// «Keeps the maximum» means bands can overlap (several calls on the same column, as in the
    /// harmonic comb) without summing above 1. Public so tests and other generators can reuse it.
    /// </remarks>
    public static void Band(SpectralImage img, int x, double hz, double widthOct, double left, double right)
    {
        int h = img.Height;
        double lc = Math.Log2(hz);
        for (int y = 0; y < h; y++)
        {
            // Distance from the centre in units of σ, measured in octaves (log2 Hz).
            double d = (Math.Log2(img.Axis.FrequencyOf(y)) - lc) / widthOct;
            // Beyond 4σ the bell is below e^−8 ≈ 3·10⁻⁴ (−70 dB): skip to save work.
            if (Math.Abs(d) > 4) continue;
            double g = Math.Exp(-0.5 * d * d);
            int i = x * h + y;
            img.Left[i] = Math.Max(img.Left[i], (float)(left * g));
            img.Right[i] = Math.Max(img.Right[i], (float)(right * g));
        }
    }

    /// <summary>Slow sweep: a third-of-an-octave band going 120 Hz → 6 kHz → 120 Hz (log-sinusoidal path).</summary>
    /// <param name="img">Empty image to paint into (any size and axis).</param>
    static void DrawSweep(SpectralImage img)
    {
        double lo = Math.Log2(120), hi = Math.Log2(6000);
        for (int x = 0; x < img.Width; x++)
        {
            // Column centre as a fraction 0…1 of one pass of the picture.
            double t = (x + 0.5) / img.Width;
            // Raised cosine from 0 to 1 and back: slow at the ends, fastest in the middle.
            double u = 0.5 - 0.5 * Math.Cos(2 * Math.PI * t);
            double hz = Math.Pow(2, lo + (hi - lo) * u);
            Band(img, x, hz, 0.17, 1, 1);
            // A faint floor keeps a little of the rest of the sound (−26 dB in linear response).
            for (int y = 0; y < img.Height; y++)
            {
                int i = x * img.Height + y;
                img.Left[i] = Math.Max(img.Left[i], 0.05f);
                img.Right[i] = Math.Max(img.Right[i], 0.05f);
            }
        }
    }

    /// <summary>Rhythmic gate: 32 sixteenth steps; F = full range, K = lows only, H = highs only, . = gap.</summary>
    /// <param name="img">Empty image to paint into (any size and axis).</param>
    /// <remarks>Both sides get the same value (a centred, mono gate).</remarks>
    static void DrawGate(SpectralImage img)
    {
        // Two bars of 16 sixteenths (the preset's Time sets Bars = 2, so one pass = 32 steps).
        const string pattern = "F.HKF.H.K.HFF.HK" + "F.HKF.H.KKH.FHFH";
        int steps = pattern.Length;
        for (int x = 0; x < img.Width; x++)
        {
            // pos: position in steps; step: which step; within: 0…1 progress inside that step.
            double pos = (x + 0.5) / img.Width * steps;
            int step = Math.Min(steps - 1, (int)pos);
            double within = pos - step;
            // Soft edges: 6 % of a step to open, the last 25 % to close (a gated «chop»).
            double env = Math.Min(1, within / 0.06) * Math.Min(1, (1 - within) / 0.25);
            char c = pattern[step];
            for (int y = 0; y < img.Height; y++)
            {
                double f = img.Axis.FrequencyOf(y);
                double level = c switch
                {
                    'F' => 1,
                    'K' => f < 180 ? 1 : f < 360 ? 1 - (f - 180) / 180 : 0,        // lows, fading out by 360 Hz
                    'H' => f > 5000 ? 1 : f > 2500 ? (f - 2500) / 2500 : 0,      // highs, fading in from 2.5 kHz
                    _ => 0,
                };
                float v = (float)(level * env);
                img.Left[x * img.Height + y] = v;
                img.Right[x * img.Height + y] = v;
            }
        }
    }

    /// <summary>Stereo ping-pong: 8 steps, band centre walking a melody of octaves/fifths, alternating sides.</summary>
    /// <param name="img">Empty image to paint into (any size and axis).</param>
    static void DrawPingPong(SpectralImage img)
    {
        // Band centres in Hz, one per step (eighth notes when the preset's Time sets one 4/4 bar).
        double[] centres = [200, 1600, 400, 3200, 300, 2400, 600, 4800];
        int steps = centres.Length;
        for (int x = 0; x < img.Width; x++)
        {
            double pos = (x + 0.5) / img.Width * steps;
            int step = Math.Min(steps - 1, (int)pos);
            double within = pos - step;
            // Fast 5 % attack, long 40 % release: each band «pings» and decays like an echo.
            double env = Math.Min(1, within / 0.05) * Math.Min(1, (1 - within) / 0.4);
            // Even steps on the left layer (red), odd steps on the right layer (green).
            bool left = step % 2 == 0;
            // 0.35 octave σ: a wide, clearly audible band.
            Band(img, x, centres[step], 0.35, left ? env : 0, left ? 0 : env);
        }
    }

    /// <summary>Harmonic comb: 24 harmonics of A2, F2, C3, G2 (two seconds each in an 8 s pass).</summary>
    /// <param name="img">Empty image to paint into (any size and axis).</param>
    static void DrawComb(SpectralImage img)
    {
        // Equal-tempered roots in Hz: A2, F2, C3, G2 (i – VI – III – VII in A minor).
        double[] roots = [110.0, 87.307, 130.813, 97.999];
        for (int x = 0; x < img.Width; x++)
        {
            double pos = (x + 0.5) / img.Width * roots.Length;
            int seg = Math.Min(roots.Length - 1, (int)pos);
            double within = pos - seg;
            // Short 3 % fade-in / 6 % fade-out so chord changes do not click.
            double env = Math.Min(1, within / 0.03) * Math.Min(1, (1 - within) / 0.06);
            for (int k = 1; k <= 24; k++)
            {
                double hz = roots[seg] * k;
                // Harmonics above the top of the canvas axis have no row to land on.
                if (hz > img.Axis.HighHz) break;
                // Narrow bells (σ = 0.025 octave = 30 cents), slightly softer towards the top
                // (level ∝ k^−¼) so it is not shrill.
                double level = env * Math.Pow(k, -0.25);
                Band(img, x, hz, 0.025, level, level);
            }
        }
    }
}
