// PaintPresets.cs — Paint Rooms foundation.
//
// Role: the starter images of the Examples menu, GENERATED IN CODE (no image assets: everything is
// our own work and costs no repository space). Each preset paints a SpectralImage with simple
// helpers (a harmonic tone, a glissando, a noise burst…) and suggests synthesis settings, so a
// beginner can press Play, hear what a picture sounds like and then edit it.
//
//   1. Chord swell        — Cmaj9 voiced over 3 octaves on a 12-tone axis, swelling in and out.
//   2. Glissando sweep    — two harmonic glides crossing (one left, one right) on a log axis.
//   3. Rhythmic texture   — a 2-bar pattern of noise bursts at 120 BPM, hard steps, noise bands.
//   4. Harmonic landscape — a drone whose spectral «hills» (formants) wander slowly, in stereo.
//
// Pure computation; deterministic (fixed random seeds). Called on the UI thread by
// PaintSynthRoom when the user picks an example (each build takes a few milliseconds).
//
// Conventions used by the helpers below:
//   * Pan is −1 (left only) … 0 (centre) … +1 (right only). It is turned into two channel weights
//     pl = min(1, 1 − pan), pr = min(1, 1 + pan): the centre is full level in BOTH channels (no
//     −3 dB pan law), a side position only attenuates the opposite channel.
//   * Cells are written with «max» (lighten-only), so overlapping partials and bursts never darken
//     each other, exactly like the Lighten blend of the brushes.
//   * SpectralImage stores columns contiguously: cell (x, y) is at index x · Height + y, row 0 at the
//     bottom (lowest pitch).

namespace CsoundVisual.Paint;

/// <summary>A starter image with its suggested synthesis settings.</summary>
/// <remarks>
/// <see cref="Build"/> receives the user's current <see cref="PaintSynthSettings"/> and returns a copy
/// (<c>with</c>) changing only what the example needs (mode, glide, envelope), so unrelated user
/// choices such as gain or sample rate survive loading an example.
/// </remarks>
/// <param name="Id">Stable identifier.</param>
/// <param name="NameEs">Spanish name.</param>
/// <param name="NameEn">English name.</param>
/// <param name="TipEs">Spanish description (what to listen for).</param>
/// <param name="TipEn">English description.</param>
/// <param name="Build">Builds the image and adjusts the given settings.</param>
public sealed record PaintPreset(string Id, string NameEs, string NameEn, string TipEs, string TipEn,
    Func<PaintSynthSettings, (SpectralImage Image, PaintSynthSettings Synth)> Build);

/// <summary>The built-in starter images.</summary>
public static class PaintPresets
{
    /// <summary>All presets, in menu order (the Examples menu lists them by <see cref="PaintPreset.NameEn"/> / <see cref="PaintPreset.NameEs"/>).</summary>
    public static IReadOnlyList<PaintPreset> All { get; } =
    [
        new("chord", "Acorde que crece", "Chord swell",
            "Un acorde de Do mayor con novena en tres octavas que entra y sale suavemente. Eje de 12 notas: cada fila es un semitono, como un piano roll.",
            "A C major 9 chord over three octaves that swells in and out. 12-tone axis: every row is a semitone, like a piano roll.",
            ChordSwell),
        new("sweep", "Glissando cruzado", "Glissando sweep",
            "Dos sonidos armónicos se deslizan en direcciones opuestas: uno sube por la izquierda y otro baja por la derecha. Eje logarítmico.",
            "Two harmonic sounds glide in opposite directions: one rises on the left, the other falls on the right. Logarithmic axis.",
            GlissandoSweep),
        new("rhythm", "Textura rítmica", "Rhythmic texture",
            "Dos compases de ráfagas de ruido a 120 BPM en distintas alturas y lados; modo bandas de ruido, sin deslizamiento (pasos duros).",
            "Two bars of noise bursts at 120 BPM at different heights and sides; noise-band mode, no glide (hard steps).",
            RhythmicTexture),
        new("landscape", "Paisaje armónico", "Harmonic landscape",
            "Un bordón sobre La 1 cuyos armónicos se iluminan como colinas que se mueven lentamente (formantes), distinto en cada oído.",
            "A drone on A1 whose harmonics light up like slowly moving hills (formants), different in each ear.",
            HarmonicLandscape),
    ];

    /// <summary>Paints one partial at frequency <paramref name="hz"/> from column x0 to x1 with an envelope (0…1 over the span) and a pan.</summary>
    /// <remarks>On continuous axes the partial is drawn on the nearest row with a faint neighbour on each side, which sounds like a pure tone but looks less thin.</remarks>
    /// <param name="img">Image to paint into (lighten-only).</param>
    /// <param name="x0">First column (inclusive; clamped to the image).</param>
    /// <param name="x1">End column (EXCLUSIVE; clamped to the image).</param>
    /// <param name="hz">Frequency of the partial (Hz); silently skipped when outside the axis range.</param>
    /// <param name="level">Peak level 0…1 (brightness).</param>
    /// <param name="pan">−1 left … 0 centre … +1 right.</param>
    /// <param name="envelope">
    /// Optional shape over the span: receives t = 0 at <paramref name="x0"/> … 1 at <c>x1 − 1</c> and
    /// returns a multiplier of <paramref name="level"/>; null = constant.
    /// </param>
    public static void Partial(SpectralImage img, int x0, int x1, double hz, double level, double pan, Func<double, double>? envelope = null)
    {
        // Fractional row of the frequency; more than half a row beyond either edge = not on this axis.
        double row = img.Axis.RowOf(hz);
        if (row < -0.5 || row > img.Height - 0.5) return;
        int r = (int)Math.Round(row);
        double pl = Math.Min(1, 1 - pan), pr = Math.Min(1, 1 + pan);
        for (int x = Math.Max(0, x0); x < Math.Min(img.Width, x1); x++)
        {
            // Normalised position in the span (a one-column span uses t = 0, avoiding a division by zero).
            double t = x1 - x0 <= 1 ? 0 : (double)(x - x0) / (x1 - x0 - 1);
            double v = level * (envelope?.Invoke(t) ?? 1);
            Max(img, x, r, v * pl, v * pr);
            if (!img.Axis.IsDiscrete)
            {
                Max(img, x, r - 1, 0.25 * v * pl, 0.25 * v * pr);
                Max(img, x, r + 1, 0.25 * v * pl, 0.25 * v * pr);
            }
        }
    }

    /// <summary>Lightens cell (x, y) to at least (l, r), clamped to 0…1; cells outside the image are ignored.</summary>
    /// <param name="img">Target image.</param>
    /// <param name="x">Column.</param>
    /// <param name="y">Row (0 = lowest pitch).</param>
    /// <param name="l">Minimum left-channel level.</param>
    /// <param name="r">Minimum right-channel level.</param>
    static void Max(SpectralImage img, int x, int y, double l, double r)
    {
        if (!img.Contains(x, y)) return;
        int i = x * img.Height + y;
        img.Left[i] = (float)Math.Clamp(Math.Max(img.Left[i], l), 0, 1);
        img.Right[i] = (float)Math.Clamp(Math.Max(img.Right[i], r), 0, 1);
    }

    /// <summary>Cmaj9 over three octaves on a 12-tone axis (C2…G4, 6 harmonics each, 1/k roll-off) with a sin^1.5 swell over 10 s.</summary>
    /// <param name="s">The user's current settings (returned modified).</param>
    /// <returns>The image and the suggested settings.</returns>
    static (SpectralImage, PaintSynthSettings) ChordSwell(PaintSynthSettings s)
    {
        // 12-TET rows from ≈ C1 (32 Hz) to ≈ C8 (4.2 kHz); 512 columns over 10 s (≈ 20 ms per column).
        var axis = PitchAxis.EqualTemperament(32, 4200, 12);
        var img = new SpectralImage(512, axis, 10);
        // C2, G2, E3, B3, D4, G4 (a spread Cmaj9) with 6 harmonics each, 1/k roll-off.
        double[] notes = [36, 43, 52, 59, 62, 67];
        foreach (double midi in notes)
        {
            double f = 440 * Math.Pow(2, (midi - 69) / 12); // MIDI note → Hz (A4 = 69 = 440 Hz)
            for (int k = 1; k <= 6; k++)
                Partial(img, 20, 500, f * k, 0.9 / k, 0, t => Math.Pow(Math.Sin(Math.PI * t), 1.5));
        }
        return (img, s with { Mode = SynthMode.Sines, Glide = true, AttackMs = 30, ReleaseMs = 120 });
    }

    /// <summary>Two 6-partial glides on a log axis: 80 Hz → 1 kHz panned left, 900 → 70 Hz panned right (exponential = even in pitch).</summary>
    /// <param name="s">The user's current settings (returned modified).</param>
    /// <returns>The image and the suggested settings.</returns>
    static (SpectralImage, PaintSynthSettings) GlissandoSweep(PaintSynthSettings s)
    {
        // Each column is painted on its own (x0 = x, x1 = x + 1) because the frequency changes per column.
        var axis = PitchAxis.Logarithmic(40, 12000, 512);
        var img = new SpectralImage(1024, axis, 10);
        for (int x = 0; x < img.Width; x++)
        {
            double t = (double)x / (img.Width - 1);
            double up = 80 * Math.Pow(1000.0 / 80, t);    // 80 Hz → 1 kHz, exponential = even in pitch
            double down = 900 * Math.Pow(70.0 / 900, t);  // 900 Hz → 70 Hz
            double env = Math.Min(1, Math.Min(t * 8, (1 - t) * 8)); // linear fade in/out over the first/last 1/8
            for (int k = 1; k <= 6; k++)
            {
                Partial(img, x, x + 1, up * k, 0.85 * env / k, -0.8);
                Partial(img, x, x + 1, down * k, 0.85 * env / Math.Sqrt(k), 0.8);
            }
        }
        return (img, s with { Mode = SynthMode.Sines, Glide = true, AttackMs = 8, ReleaseMs = 40 });
    }

    /// <summary>Two bars at 120 BPM on a 16th-note grid (8 columns per step): low thumps, snare-like mid bursts and random hats, for the noise-band mode without glide.</summary>
    /// <param name="s">The user's current settings (returned modified).</param>
    /// <returns>The image and the suggested settings.</returns>
    static (SpectralImage, PaintSynthSettings) RhythmicTexture(PaintSynthSettings s)
    {
        var axis = PitchAxis.Logarithmic(40, 12000, 256);
        // 2 bars of 4/4 at 120 BPM = 8 beats = 4 s; 256 columns / 8 beats = 32 columns per beat,
        // i.e. 8 columns per 16th note (62.5 ms).
        var img = new SpectralImage(256, axis, 4);
        var rng = new Random(7);
        // 16th-note grid: 32 steps of 8 columns. Pattern of (step, low Hz, high Hz, level, pan).
        (int Step, double Lo, double Hi, double Level, double Pan)[] hits =
        [
            (0, 40, 160, 1.0, 0), (8, 40, 160, 0.9, 0), (16, 40, 160, 1.0, 0), (24, 40, 160, 0.9, 0),       // kick-like low thumps
            (4, 900, 3500, 0.8, -0.3), (12, 900, 3500, 0.85, 0.3), (20, 900, 3500, 0.8, -0.3), (28, 900, 3500, 0.9, 0.3), // snare-like
        ];
        // 7 of the 8 columns of a step: the last column stays dark so consecutive hits stay separate.
        foreach (var h in hits) Burst(img, h.Step * 8, 7, h.Lo, h.Hi, h.Level, h.Pan, sharp: true);
        // Hats: every odd 16th step plus ~30 % random extra ones, short (3 columns), alternating sides.
        for (int step = 0; step < 32; step++)
            if (step % 2 == 1 || rng.NextDouble() < 0.3)
                Burst(img, step * 8, 3, 6000, 11000, 0.45 + 0.3 * rng.NextDouble(), step % 4 == 1 ? -0.7 : 0.7, sharp: true); // hats
        return (img, s with { Mode = SynthMode.NoiseBands, Glide = false, AttackMs = 2, ReleaseMs = 25, NoiseWidth = 1.5 });
    }

    /// <summary>A block of noise from lo to hi Hz, <paramref name="length"/> columns long, decaying when sharp.</summary>
    /// <param name="img">Image to paint into (lighten-only).</param>
    /// <param name="x0">First column.</param>
    /// <param name="length">Number of columns.</param>
    /// <param name="lo">Lowest frequency (Hz), mapped to the nearest row and clamped to the axis.</param>
    /// <param name="hi">Highest frequency (Hz), likewise.</param>
    /// <param name="level">Initial level 0…1.</param>
    /// <param name="pan">−1 left … +1 right.</param>
    /// <param name="sharp">True = quadratic decay (1 − progress)² over the burst (a percussive hit); false = flat.</param>
    static void Burst(SpectralImage img, int x0, int length, double lo, double hi, double level, double pan, bool sharp)
    {
        int r0 = (int)Math.Max(0, Math.Round(img.Axis.RowOf(lo))), r1 = (int)Math.Min(img.Height - 1, Math.Round(img.Axis.RowOf(hi)));
        double pl = Math.Min(1, 1 - pan), pr = Math.Min(1, 1 + pan);
        for (int x = x0; x < Math.Min(img.Width, x0 + length); x++)
        {
            double env = sharp ? Math.Pow(1 - (double)(x - x0) / length, 2) : 1;
            for (int y = r0; y <= r1; y++)
                Max(img, x, y, level * env * pl, level * env * pr);
        }
    }

    /// <summary>A drone on A1 (harmonics up to 11 kHz) whose spectral envelope is two Gaussian «hills» per ear wandering on slow sines.</summary>
    /// <param name="s">The user's current settings (returned modified).</param>
    /// <returns>The image and the suggested settings.</returns>
    static (SpectralImage, PaintSynthSettings) HarmonicLandscape(PaintSynthSettings s)
    {
        var axis = PitchAxis.Logarithmic(40, 12000, 512);
        var img = new SpectralImage(1024, axis, 20);
        double f0 = 55; // A1
        for (int x = 0; x < img.Width; x++)
        {
            double t = (double)x / (img.Width - 1);
            // Two formant «hills» per ear, wandering on slow sines (log-frequency centres).
            double cL1 = 300 * Math.Pow(2, 1.5 * Math.Sin(2 * Math.PI * t * 0.7));
            double cL2 = 2000 * Math.Pow(2, Math.Sin(2 * Math.PI * (t * 0.45 + 0.3)));
            double cR1 = 300 * Math.Pow(2, 1.5 * Math.Sin(2 * Math.PI * (t * 0.6 + 0.5)));
            double cR2 = 2000 * Math.Pow(2, Math.Sin(2 * Math.PI * (t * 0.5 + 0.8)));
            double env = Math.Min(1, Math.Min(t * 10, (1 - t) * 6));
            for (int k = 1; k <= 120; k++)
            {
                double f = f0 * k;
                if (f > 11000) break;
                // Gaussian in log-frequency: 1 at the centre c, 1/e at ± width octaves from it.
                double Hill(double c, double width) => Math.Exp(-Math.Pow(Math.Log2(f / c) / width, 2));
                // A faint 1/√k harmonic floor keeps the drone audible between the hills.
                double l = env * (0.25 / Math.Sqrt(k) + 0.9 * Hill(cL1, 0.5) + 0.6 * Hill(cL2, 0.4));
                double r = env * (0.25 / Math.Sqrt(k) + 0.9 * Hill(cR1, 0.5) + 0.6 * Hill(cR2, 0.4));
                int row = (int)Math.Round(axis.RowOf(f));
                if (row < 0 || row >= img.Height) continue;
                Max(img, x, row, Math.Min(1, l), Math.Min(1, r));
            }
        }
        return (img, s with { Mode = SynthMode.Sines, Glide = true, AttackMs = 60, ReleaseMs = 200 });
    }
}
