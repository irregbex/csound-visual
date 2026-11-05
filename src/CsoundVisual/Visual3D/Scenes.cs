using System.Globalization;
using System.Numerics;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Theming;

namespace CsoundVisual.Visual3D;

/// <summary>Audio data the 3D scenes read (filled by the 3D window from the engine every 16 ms, ≈ 60 times a second).</summary>
/// <remarks>
/// The hand-off from the audio thread happens inside <c>CsoundEngine.CopyScope</c>, called from
/// the 3D window's UI timer; the GPU/scene code only ever sees this UI-side copy, so it never
/// touches or waits on the performance thread. Not thread-safe by itself: writer (window timer)
/// and readers (scenes) are on the UI side.
/// </remarks>
public sealed class AudioFeed
{
    /// <summary>Latest output samples (mono mix, chronological, ±1.0), <see cref="Engine.CsoundEngine.ScopeSize"/> (2048) long.</summary>
    public float[] Scope { get; } = new float[Engine.CsoundEngine.ScopeSize];

    /// <summary>Sample rate of the running performance, Hz (needed to map FFT bins to Hz).</summary>
    public double SampleRate { get; set; } = 48000;

    /// <summary>True while Csound is playing.</summary>
    public bool Running { get; set; }

    /// <summary>Incremented whenever <see cref="Scope"/> receives new data (scenes compare it to skip re-analysing the same buffer).</summary>
    public int Version { get; set; }
}

/// <summary>
/// False-colour maps for the waterfall: a level 0…1 becomes a colour. The scientific maps
/// (Viridis, Magma, Inferno) are perceptually uniform: equal level steps look like equal colour
/// steps, so they are the most honest for reading levels.
/// </summary>
/// <remarks>
/// Each map is a few RGB stops (0…1 per component) spaced evenly over 0…1 and linearly
/// interpolated; the scientific stops are samples of the matplotlib maps. The map index is what
/// the settings store (<c>WaterfallPalette</c>), so new maps must be appended, not inserted.
/// </remarks>
public static class ColorMaps
{
    /// <summary>The maps: Spanish and English display names and colour stops (empty = built from the theme).</summary>
    static readonly (string Es, string En, Vector3[] Stops)[] maps =
    [
        ("Tema de la app", "App theme", []), // built from the active preset in Get()
        ("Ámbar", "Amber", [new(0.25f, 0.17f, 0f), new(0.60f, 0.45f, 0f), new(0.96f, 0.72f, 0f), new(0.98f, 0.52f, 0.08f), new(1f, 0.33f, 0.15f)]),
        ("Viridis", "Viridis", [new(0.267f, 0.005f, 0.329f), new(0.229f, 0.322f, 0.546f), new(0.128f, 0.567f, 0.551f), new(0.369f, 0.789f, 0.383f), new(0.993f, 0.906f, 0.144f)]),
        ("Magma", "Magma", [new(0.001f, 0f, 0.014f), new(0.316f, 0.071f, 0.485f), new(0.716f, 0.215f, 0.475f), new(0.987f, 0.535f, 0.382f), new(0.987f, 0.991f, 0.750f)]),
        ("Inferno", "Inferno", [new(0.001f, 0f, 0.014f), new(0.341f, 0.062f, 0.429f), new(0.735f, 0.216f, 0.330f), new(0.978f, 0.557f, 0.035f), new(0.988f, 0.998f, 0.645f)]),
        ("Arcoíris (Turbo)", "Rainbow (Turbo)", [new(0.19f, 0.07f, 0.23f), new(0.27f, 0.44f, 0.94f), new(0.10f, 0.81f, 0.80f), new(0.49f, 0.99f, 0.36f), new(0.91f, 0.78f, 0.15f), new(0.97f, 0.39f, 0.09f), new(0.48f, 0.02f, 0.01f)]),
        ("Hielo", "Ice", [new(0.02f, 0.03f, 0.10f), new(0.10f, 0.25f, 0.55f), new(0.30f, 0.65f, 0.90f), new(0.85f, 0.97f, 1f)]),
        ("Escala de grises", "Grayscale", [new(0.04f, 0.04f, 0.04f), new(1f, 1f, 1f)]),
    ];

    /// <summary>Theme preset name <see cref="themeStops"/> was built for (cache key).</summary>
    static string? themeStopsFor;

    /// <summary>Cached stops of the «App theme» map; rebuilt when the preset changes.</summary>
    static Vector3[] themeStops = [];

    /// <summary>Gradient of the «Tema de la app» map: very dark accent → accent → highlight → light.</summary>
    static Vector3[] ThemeStops()
    {
        if (themeStopsFor != AppTheme.Current.Name)
        {
            static Vector3 V(string role) { var v = AppTheme.Vector(role); return new(v.X, v.Y, v.Z); }
            themeStops = [V("AccentDeep") * 0.35f, V("AccentDeep"), V("Accent"), V("AccentHi"), V("AccentLight")];
            themeStopsFor = AppTheme.Current.Name;
        }
        return themeStops;
    }

    /// <summary>Names in display order (index = map id).</summary>
    public static IReadOnlyList<string> Names => maps.Select(m => Loc.S(m.Es, m.En)).ToList();

    /// <summary>Colour of level <paramref name="t"/> (0…1) in map <paramref name="map"/>.</summary>
    /// <param name="map">Map index (clamped to the valid range).</param>
    /// <param name="t">Normalised level; clamped to 0…1.</param>
    /// <param name="alpha">Opacity of the returned colour.</param>
    /// <returns>RGBA, components 0…1.</returns>
    public static Vector4 Get(int map, float t, float alpha)
    {
        // Position x along the stop list; segment i is [stops[i], stops[i+1]] and x − i the
        // fraction inside it (i capped so t = 1 lands at the end of the last segment).
        var stops = maps[Math.Clamp(map, 0, maps.Length - 1)].Stops;
        if (stops.Length == 0)
            stops = ThemeStops();
        float x = Math.Clamp(t, 0, 1) * (stops.Length - 1);
        int i = Math.Min((int)x, stops.Length - 2);
        return new Vector4(Vector3.Lerp(stops[i], stops[i + 1], x - i), alpha);
    }
}

/// <summary>Colour helpers shared by the scenes (they follow the app's colour preset).</summary>
/// <remarks>Read every frame (not cached), so a theme change shows up immediately.</remarks>
static class Palette
{
    /// <summary>Grid lines (theme role <c>Line</c>, 75 % opaque).</summary>
    public static Vector4 Grid => AppTheme.Vector("Line", 0.75f);
    /// <summary>Box edges and reference axes (role <c>Accent</c>).</summary>
    public static Vector4 Edge => AppTheme.Vector("Accent", 0.9f);
    /// <summary>Faint helper lines such as projections (role <c>Text</c>, 35 %).</summary>
    public static Vector4 Soft => AppTheme.Vector("Text", 0.35f);
    /// <summary>Highlights: controllable objects, newest spectrum row (role <c>AccentHi</c>).</summary>
    public static Vector4 Accent => AppTheme.Vector("AccentHi", 1f);
    /// <summary>"Active/sound" colour: trail, sound source (role <c>Good</c>).</summary>
    public static Vector4 Green => AppTheme.Vector("Good", 1f);

    /// <summary>Dark amber → amber → orange-red as <paramref name="t"/> goes 0 → 1.</summary>
    public static Vector4 Heat(float t, float alpha)
    {
        t = Math.Clamp(t, 0, 1);
        Vector3 a = new(0.25f, 0.17f, 0.0f), b = new(0.96f, 0.72f, 0.0f), c = new(1.0f, 0.33f, 0.15f);
        var rgb = t < 0.6f ? Vector3.Lerp(a, b, t / 0.6f) : Vector3.Lerp(b, c, (t - 0.6f) / 0.4f);
        return new Vector4(rgb, alpha);
    }
}

/// <summary>
/// 3D waterfall spectrogram: each frame the current spectrum becomes a new row at the front, and
/// older rows recede into the distance and fade. Height and colour show the level of each
/// frequency band (logarithmic axis, 20 Hz – Nyquist).
/// </summary>
/// <remarks>
/// <para><b>Analysis.</b> <see cref="SpectrumAnalyzer"/> (shared with the 2D spectrum view) runs a
/// fixed 2048-point FFT on the newest <see cref="AudioFeed.Scope"/> samples and folds the bins into
/// <see cref="Bands"/> logarithmically spaced bands, each a level normalised 0…1 over
/// <c>MinDb</c>…0 dBFS. The FFT length equals the engine's scope ring, so a longer FFT would need a
/// longer ring.</para>
/// <para><b>History.</b> A circular buffer of rows (<c>history[row, band]</c>, newest at
/// <see cref="head"/>); "age" counts rows back from the head. Rows are added at most
/// <see cref="RowsPerSecond"/> times a second and only when the feed has new samples. When Csound
/// stops, new rows decay the previous one by 0.92 per row instead of dropping to silence.</para>
/// <para><b>Geometry.</b> The surface spans X −1.6…1.6 (frequency), Z +1.6 (now) … −1.6 (oldest)
/// and Y from <see cref="Floor"/> up by <see cref="Height"/> (level). Each cell is a quad; older
/// rows get lower alpha. Cost is about Bands × Rows quads per frame, hence <see cref="MaxCells"/>.</para>
/// </remarks>
public sealed class WaterfallScene(AudioFeed feed) : IScene3D
{
    /// <summary>World Y of the −∞ dB floor, height of the 0 dBFS level above it, and half-width of the surface.</summary>
    const float Floor = -0.6f, Height = 1.3f, Half = 1.6f;

    /// <summary>Upper limit of drawn cells (bands × rows) so the GPU upload stays light.</summary>
    public const int MaxCells = 60000;

    /// <summary>Frequencies (Hz) that get a grid line and a label (those below Nyquist).</summary>
    static readonly int[] FrequencyTicks = [50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];

    /// <summary>FFT + band folding; replaced by <see cref="Configure"/>.</summary>
    SpectrumAnalyzer analyzer = new(64);

    /// <summary>Ring of band levels (0…1), [row, band].</summary>
    float[,] history = new float[90, 64];

    /// <summary>Wall-clock time (s, <see cref="clock"/>) at which each row was added; 0 = never filled.</summary>
    double[] rowTime = new double[90];

    /// <summary>Time base for row pacing and the rows/s counter.</summary>
    readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Index of the newest row in the ring, and the last <see cref="AudioFeed.Version"/> analysed.</summary>
    int head, lastVersion = -1;

    /// <summary>Rows added since <see cref="rowCountStart"/>.</summary>
    int rowsThisSecond;

    /// <summary>Start (s) of the current one-second window of <see cref="MeasuredRowsPerSecond"/>.</summary>
    double rowCountStart;

    /// <summary>Frequency bands across (left→right).</summary>
    public int Bands { get; private set; } = 64;

    /// <summary>Rows of history (front = now).</summary>
    public int Rows { get; private set; } = 90;

    /// <summary>Spectra analysed per second (new rows per second).</summary>
    public int RowsPerSecond { get; private set; } = 30;

    /// <summary>Rows actually added during the last second (limited by the window's 60 Hz feed and the frame rate).</summary>
    public int MeasuredRowsPerSecond { get; private set; }

    /// <summary>Seconds of history the surface shows.</summary>
    public double Seconds => (double)Rows / RowsPerSecond;

    /// <summary>
    /// Sets the resolution: <paramref name="bands"/> frequency bands, <paramref name="rowsPerSecond"/>
    /// spectra per second and <paramref name="seconds"/> of history (clamped so bands × rows stays
    /// under <see cref="MaxCells"/>). Clears the history.
    /// </summary>
    public void Configure(int bands, int rowsPerSecond, double seconds)
    {
        Bands = Math.Clamp(bands, 16, 512);
        RowsPerSecond = Math.Clamp(rowsPerSecond, 1, 60);
        Rows = Math.Clamp((int)Math.Round(RowsPerSecond * seconds), 10, MaxCells / Bands);
        analyzer = new SpectrumAnalyzer(Bands);
        history = new float[Rows, Bands];
        rowTime = new double[Rows];
        head = 0;
    }

    /// <summary>One line describing the analysis, for the status bar.</summary>
    public string Info
    {
        get
        {
            double ms = SpectrumAnalyzer.Size / analyzer.SampleRate * 1000;
            double binHz = analyzer.SampleRate / SpectrumAnalyzer.Size;
            return Loc.S(
                string.Create(CultureInfo.InvariantCulture, $"FFT {SpectrumAnalyzer.Size} muestras ({ms:0} ms, {binHz:0.#} Hz por bin) · {Bands} bandas · {MeasuredRowsPerSecond}/{RowsPerSecond} filas/s · {Seconds:0.#} s de historia"),
                string.Create(CultureInfo.InvariantCulture, $"FFT {SpectrumAnalyzer.Size} samples ({ms:0} ms, {binHz:0.#} Hz per bin) · {Bands} bands · {MeasuredRowsPerSecond}/{RowsPerSecond} rows/s · {Seconds:0.#} s of history"));
        }
    }

    /// <summary>False-colour map used for the surface (index into <see cref="ColorMaps.Names"/>).</summary>
    public int ColorMap { get; set; }

    /// <summary>X coordinate of frequency <paramref name="hz"/> on the logarithmic axis.</summary>
    /// <remarks>Inverse of the analyser's band mapping: band index = log(f / fmin) / log(fNyquist / fmin)
    /// × Bands, minus half a band because each band's vertex sits at its centre; then band 0…Bands−1
    /// maps linearly to −Half…+Half like the vertices in <see cref="Render"/>.</remarks>
    float FrequencyX(double hz)
    {
        double nyquist = analyzer.SampleRate / 2;
        double band = Math.Log(hz / SpectrumAnalyzer.MinFrequency) / Math.Log(nyquist / SpectrumAnalyzer.MinFrequency) * Bands - 0.5;
        return -Half + 2 * Half * (float)(band / (Bands - 1));
    }

    /// <summary>Y coordinate of a level in dBFS (<c>MinDb</c> → <see cref="Floor"/>, 0 dB → Floor + Height).</summary>
    static float LevelY(double db) => Floor + Height * (float)((db - SpectrumAnalyzer.MinDb) / -SpectrumAnalyzer.MinDb);

    /// <summary>Z coordinate of a row <paramref name="age"/> rows old.</summary>
    float AgeZ(float age) => Half - 2 * Half * age / (Rows - 1);

    /// <inheritdoc/>
    public string Name => Loc.S("Espectrograma en cascada", "Waterfall spectrogram");

    /// <inheritdoc/>
    public string Help => Loc.S("Cada fila es el espectro de un instante: la de adelante es ahora y las antiguas se alejan hacia atrás (segundos). Izquierda→derecha = frecuencia en Hz (escala logarítmica); altura y color = nivel en dBFS (0 = máximo digital). Arrastra con el mouse para girar, rueda para acercar, doble clic para volver a la vista inicial.",
        "Each row is the spectrum of one instant: the front row is now and older rows recede towards the back (seconds). Left→right = frequency in Hz (logarithmic scale); height and colour = level in dBFS (0 = digital maximum). Drag with the mouse to rotate, wheel to zoom, double-click to return to the initial view.");

    /// <inheritdoc/>
    public bool Interactive => false;

    /// <inheritdoc/>
    public void Render(GlRenderer r, double time)
    {
        double nowSeconds = clock.Elapsed.TotalSeconds;
        if (nowSeconds - rowCountStart >= 1)
        {
            MeasuredRowsPerSecond = rowsThisSecond;
            rowsThisSecond = 0;
            rowCountStart = nowSeconds;
        }
        // A new row when new samples arrived and its time slot has come (rows per second). The 4 ms
        // tolerance absorbs frame-timing jitter so 60 rows/s is reachable at 60 fps.
        if (feed.Version != lastVersion && nowSeconds - rowTime[head] >= 1.0 / RowsPerSecond - 0.004)
        {
            lastVersion = feed.Version;
            rowsThisSecond++;
            head = (head + 1) % Rows;
            rowTime[head] = nowSeconds;
            bool ok = feed.Running && analyzer.Analyse(feed.Scope, feed.SampleRate);
            for (int b = 0; b < Bands; b++)
                history[head, b] = ok ? (float)analyzer.Levels[b] : history[(head + Rows - 1) % Rows, b] * 0.92f;
        }

        // Floor grid: one line per labelled frequency (so the numbers line up with it) and one
        // per second of history; plus the level scale on the left wall.
        foreach (int hz in FrequencyTicks)
            if (hz < analyzer.SampleRate / 2)
            {
                float x = FrequencyX(hz);
                r.Line(new(x, Floor, -Half), new(x, Floor, Half), Palette.Grid);
            }
        foreach (var (age, _) in SecondMarks())
            r.Line(new(-Half, Floor, AgeZ(age)), new(Half, Floor, AgeZ(age)), Palette.Grid);
        for (int db = -90; db <= 0; db += 30)
            r.Line(new(-Half, LevelY(db), Half), new(-Half, LevelY(db), -Half), Palette.Grid);
        r.Line(new(-Half, Floor, Half), new(-Half, LevelY(0), Half), Palette.Edge);

        // Vertex and colour of (age, band); alpha fades from 0.85 (now) towards 0.10 (oldest).
        Vector3 P(int age, int band) => new(
            -Half + 2 * Half * band / (Bands - 1),
            Floor + Height * history[(head - age + Rows) % Rows, band],
            AgeZ(age));
        Vector4 C(int age, int band) => ColorMaps.Get(ColorMap, history[(head - age + Rows) % Rows, band], 0.85f - 0.75f * age / Rows);

        for (int age = 0; age < Rows - 1; age++)
            for (int b = 0; b < Bands - 1; b++)
                r.Quad(P(age, b), P(age, b + 1), P(age + 1, b + 1), P(age + 1, b), C(age, b), C(age, b + 1), C(age + 1, b + 1), C(age + 1, b));

        // Bright outline of the newest row.
        for (int b = 0; b < Bands - 1; b++)
            r.Line(P(0, b) + new Vector3(0, 0.005f, 0), P(0, b + 1) + new Vector3(0, 0.005f, 0), Palette.Accent);
    }

    /// <summary>Rows that are whole seconds old (age in rows, seconds), measured from the real row times.</summary>
    IEnumerable<(float Age, int Seconds)> SecondMarks()
    {
        double now = rowTime[head];
        int next = 1;
        for (int age = 1; age < Rows; age++)
        {
            double t = rowTime[(head - age + Rows) % Rows];
            if (t <= 0) yield break; // history not filled yet
            if (now - t >= next)
            {
                yield return (age, next);
                next++;
            }
        }
    }

    /// <inheritdoc/>
    public void AddLabels(List<Label3D> labels)
    {
        foreach (int hz in FrequencyTicks)
            if (hz < analyzer.SampleRate / 2)
                labels.Add(new(new(FrequencyX(hz), Floor - 0.08f, Half + 0.14f), hz >= 1000 ? $"{hz / 1000}k" : hz.ToString(CultureInfo.InvariantCulture)));
        labels.Add(new(new(0, Floor - 0.25f, Half + 0.32f), Loc.S("Frecuencia (Hz)", "Frequency (Hz)"), true));

        for (int db = -90; db <= 0; db += 30)
            labels.Add(new(new(-Half - 0.2f, LevelY(db), Half), $"{db} dB"));
        labels.Add(new(new(-Half - 0.25f, LevelY(0) + 0.2f, Half), Loc.S("Nivel (dBFS)", "Level (dBFS)"), true));

        labels.Add(new(new(Half + 0.25f, Floor, AgeZ(0)), Loc.S("ahora", "now")));
        foreach (var (age, seconds) in SecondMarks())
            labels.Add(new(new(Half + 0.25f, Floor, AgeZ(age)), $"−{seconds} s"));
        labels.Add(new(new(Half + 0.45f, Floor - 0.2f, 0), Loc.S("Tiempo", "Time"), true));
    }

    /// <inheritdoc/>
    public void Drag(float dx, float dy, bool shift) { }

    /// <inheritdoc/>
    public void Wheel(float delta) { }
}

/// <summary>
/// XYZ pad: a sphere inside a cube that controls three channels at once. Drag horizontally for
/// X, vertically for Y, and with Shift (or the wheel) for Z. Lines drop from the sphere to the
/// floor and walls so its position is readable from any angle; a fading trail shows the gesture.
/// </summary>
/// <remarks>
/// The scene knows nothing about channels: the 3D window maps each axis 0…1 onto a slider's range
/// (exponentially for exponential channels) when <see cref="Changed"/> fires, and calls
/// <see cref="SetPositionSilently"/> to follow sliders moved elsewhere. Pad coordinates (0…1) map to
/// world −1…1 through <see cref="World"/>.
/// </remarks>
public sealed class XyzPadScene : IScene3D
{
    /// <summary>Recent world positions of the sphere (at most 90), oldest first.</summary>
    readonly Queue<Vector3> trail = new();

    /// <summary>Backing field of <see cref="Position"/> (pad units, 0…1 per axis).</summary>
    Vector3 position = new(0.5f, 0.5f, 0.5f);

    /// <summary>
    /// Per axis (X, Y, Z): channel name and the texts of its minimum and maximum, shown as labels
    /// on the cube edges. Set by the 3D window when the channels are chosen.
    /// </summary>
    public (string Name, string Min, string Max)[] Axes { get; } = [("—", "", ""), ("—", "", ""), ("—", "", "")];

    /// <summary>Text shown above the sphere with the current values (set by the window).</summary>
    public string ValueText { get; set; } = "";

    /// <summary>Moves the sphere without notifying (used to follow the sliders).</summary>
    public void SetPositionSilently(Vector3 p) => Position = p;

    /// <summary>Raised with the new position (each axis 0…1) whenever the user moves the sphere.</summary>
    public event Action<Vector3>? Changed;

    /// <summary>Current position, each axis 0…1.</summary>
    public Vector3 Position
    {
        get => position;
        set => position = Vector3.Clamp(value, Vector3.Zero, Vector3.One);
    }

    /// <inheritdoc/>
    public string Name => "Pad XYZ";

    /// <inheritdoc/>
    public string Help => Loc.S("Mueve la esfera para controlar tres canales a la vez. Botón izquierdo: arriba/abajo = Y; izquierda/derecha = el eje que se ve de lado a lado (X de frente; Z si giras la cámara de costado). Rueda del mouse = Z. Shift + arrastrar arriba/abajo = profundidad (alejar/acercar). Botón derecho y arrastrar gira la cámara; Ctrl+rueda acerca. Los canales son los sliders del documento que se ve en la ventana principal; la esfera parte donde están.",
        "Move the sphere to control three channels at once. Left button: up/down = Y; left/right = whichever axis runs across the screen (X from the front; Z if you turn the camera sideways). Mouse wheel = Z. Shift + drag up/down = depth (away/closer). Right button and drag rotates the camera; Ctrl+wheel zooms. The channels are the sliders of the document shown in the main window; the sphere starts where they are.");

    /// <inheritdoc/>
    public bool Interactive => true;

    /// <summary>Pad coordinates (0…1) → world coordinates of the cube (−1…1).</summary>
    static Vector3 World(Vector3 p) => (p - new Vector3(0.5f)) * 2f;

    /// <inheritdoc/>
    public void Render(GlRenderer r, double time)
    {
        // Cube edges: 8 corners (back face z = −1 first, then front face), 12 edges as index pairs.
        Vector3[] c = [new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1), new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)];
        int[] e = [0, 1, 1, 2, 2, 3, 3, 0, 4, 5, 5, 6, 6, 7, 7, 4, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < e.Length; i += 2)
            r.Line(c[e[i]], c[e[i + 1]], Palette.Edge with { W = 0.55f });
        // Floor grid.
        for (int i = 1; i < 8; i++)
        {
            float t = -1 + i * 0.25f;
            r.Line(new(-1, -1, t), new(1, -1, t), Palette.Grid);
            r.Line(new(t, -1, -1), new(t, -1, 1), Palette.Grid);
        }

        var w = World(position);
        // Projections onto the floor and the two back walls.
        r.Line(w, new(w.X, -1, w.Z), Palette.Soft);
        r.Line(w, new(-1, w.Y, w.Z), Palette.Soft);
        r.Line(w, new(w.X, w.Y, -1), Palette.Soft);
        r.Point(new(w.X, -1, w.Z), Palette.Accent);
        r.Point(new(-1, w.Y, w.Z), Palette.Accent);
        r.Point(new(w.X, w.Y, -1), Palette.Accent);

        // Trail of the gesture.
        Vector3? prev = null;
        int k = 0;
        foreach (var p in trail)
        {
            if (prev is { } q)
                r.Line(q, p, Palette.Green with { W = 0.15f + 0.6f * k / Math.Max(1, trail.Count) });
            prev = p;
            k++;
        }

        // Radius breathes ±10 % at 4 rad/s (≈ 0.64 Hz) so the handle looks alive.
        float pulse = 0.08f + 0.008f * MathF.Sin((float)time * 4);
        r.Sphere(w, pulse, Palette.Accent);
    }

    /// <summary>Moves the sphere by <paramref name="delta"/> pad units (clamped to the cube), records the trail and raises <see cref="Changed"/>.</summary>
    void Move(Vector3 delta)
    {
        Position += delta;
        trail.Enqueue(World(position));
        while (trail.Count > 90) trail.Dequeue();
        Changed?.Invoke(position);
    }

    /// <inheritdoc/>
    public void AddLabels(List<Label3D> labels)
    {
        // X along the front floor edge, Y up the front-left edge, Z along the right floor edge.
        labels.Add(new(new(-1, -1.12f, 1.1f), Axes[0].Min));
        labels.Add(new(new(1, -1.12f, 1.1f), Axes[0].Max));
        labels.Add(new(new(0, -1.3f, 1.25f), "X · " + Axes[0].Name, true));
        labels.Add(new(new(-1.2f, -1, 1), Axes[1].Min));
        labels.Add(new(new(-1.2f, 1, 1), Axes[1].Max));
        labels.Add(new(new(-1.35f, 0, 1), "Y · " + Axes[1].Name, true));
        labels.Add(new(new(1.18f, -1.12f, 1), Axes[2].Min));
        labels.Add(new(new(1.18f, -1.12f, -1), Axes[2].Max));
        labels.Add(new(new(1.4f, -1.3f, 0), "Z · " + Axes[2].Name, true));
        if (ValueText.Length > 0)
            labels.Add(new(World(position) + new Vector3(0, 0.25f, 0), ValueText));
    }

    /// <inheritdoc/>
    public void Drag(float dx, float dy, bool shift) => Drag(new DragContext(dx, dy, shift, 0, default, default));

    /// <summary>
    /// Camera-relative drag. Horizontal mouse motion moves along whichever world axis (X or Z)
    /// runs most left-to-right on screen, with the sign that follows the mouse; vertical motion is
    /// Y. With Shift, vertical motion moves along the other horizontal axis (depth): mouse up =
    /// away from the camera.
    /// </summary>
    /// <remarks>
    /// BUG FIXED HERE: the drag used fixed world axes, so Z could only change with Shift or the
    /// wheel and, once the camera was turned, left/right on screen no longer matched X.
    /// </remarks>
    public void Drag(in DragContext d)
    {
        // Camera right vector on the floor: (cos yaw, 0, −sin yaw); forward: (−sin yaw, 0, −cos yaw).
        // Snap the camera's right vector to the dominant world axis (X or Z); "depth" is the other
        // horizontal axis, signed so that it points away from the camera.
        float rx = MathF.Cos(d.Yaw), rz = -MathF.Sin(d.Yaw);
        bool horizontalIsX = MathF.Abs(rx) >= MathF.Abs(rz);
        var across = horizontalIsX ? new Vector3(MathF.Sign(rx), 0, 0) : new Vector3(0, 0, MathF.Sign(rz));
        var depth = horizontalIsX ? new Vector3(0, 0, -MathF.Sign(MathF.Cos(d.Yaw))) : new Vector3(-MathF.Sign(MathF.Sin(d.Yaw)), 0, 0);
        // Move works in pad units (0…1 per axis; the drawn cube spans −1…1 world units, see World).
        // Gain 1.5: dragging across two thirds of the view sweeps a whole axis.
        Move(d.Shift
            ? depth * (-d.Dy * 1.5f)
            : across * (d.Dx * 1.5f) + new Vector3(0, -d.Dy * 1.5f, 0));
    }

    /// <inheritdoc/>
    /// <remarks>Always the world Z axis (not camera-relative), 0.06 pad units per wheel notch.</remarks>
    public void Wheel(float delta) => Move(new Vector3(0, 0, delta * 0.06f));
}

/// <summary>
/// 3D spatial panner: the listener's head sits at the centre of a floor; drag the sound source
/// around it (and up/down with Shift or the wheel). The scene reports azimuth (0° = front,
/// clockwise), elevation (−40°…90°) and distance, which the binaural template feeds to
/// <c>hrtfmove2</c> for real 3D audio on headphones.
/// </summary>
/// <remarks>
/// <para><b>Coordinates.</b> The listener is at the origin facing −Z (the "front"); +X is the
/// listener's right; Y is up. The floor is drawn at Y = −0.5 with rings every 0.5 world units up
/// to radius 2. Distances are in the same world units, which the binaural template interprets
/// as its <c>dist</c> channel.</para>
/// <para><b>Conversions</b> (<see cref="Polar"/> / <see cref="SetPolarSilently"/>): azimuth =
/// atan2(x, −z), clockwise from the front, 0…360°; elevation = atan2(y, horizontal distance),
/// clamped −40…90° (the range of Csound's HRTF data); distance = |source|.</para>
/// <para><b>Limits</b> (<c>Move</c>): horizontal radius ≤ 2, height −0.45…1.8, total distance ≥ 0.3
/// (inside the head HRTFs make no sense).</para>
/// </remarks>
public sealed class SpatialScene : IScene3D
{
    /// <summary>Sound source position, world units.</summary>
    Vector3 source = new(0.9f, 0.2f, -0.9f);

    /// <summary>Raised with (azimuth degrees, elevation degrees, distance) when the source moves.</summary>
    public event Action<float, float, float>? Changed;

    /// <inheritdoc/>
    public string Name => Loc.S("Panorámica espacial (binaural)", "Spatial panner (binaural)");

    /// <inheritdoc/>
    public string Help => Loc.S("La cabeza es quien escucha (la nariz mira al frente, 0°). Haz clic y arrastra con el botón izquierdo: la fuente sigue al mouse sobre el piso. Con Shift + arrastrar o la rueda la subes o bajas. Botón derecho y arrastrar gira la cámara; Ctrl+rueda acerca. Envía azimut, elevación y distancia a los canales az, el y dist: abre la plantilla binaural, presiona Tocar y escucha con audífonos.",
        "The head is the listener (the nose faces the front, 0°). Click and drag with the left button: the source follows the mouse over the floor. Shift + drag or the wheel raises or lowers it. Right button and drag rotates the camera; Ctrl+wheel zooms. Sends azimuth, elevation and distance to the az, el and dist channels: open the binaural template, press Play and listen on headphones.");

    /// <inheritdoc/>
    public bool Interactive => true;

    /// <summary>Azimuth in degrees (0 = front, 90 = right), elevation in degrees and distance of the source.</summary>
    public (float Azimuth, float Elevation, float Distance) Polar
    {
        get
        {
            float horizontal = MathF.Sqrt(source.X * source.X + source.Z * source.Z);
            float az = MathF.Atan2(source.X, -source.Z) * 180 / MathF.PI;
            if (az < 0) az += 360;
            float el = Math.Clamp(MathF.Atan2(source.Y, MathF.Max(horizontal, 1e-4f)) * 180 / MathF.PI, -40, 90);
            return (az, el, source.Length());
        }
    }

    /// <inheritdoc/>
    public void Render(GlRenderer r, double time)
    {
        // Floor: concentric rings every 0.5 and radial lines every 30°.
        for (int ring = 1; ring <= 4; ring++)
        {
            float rad = ring * 0.5f;
            for (int s = 0; s < 64; s++)
            {
                float a0 = s * MathF.Tau / 64, a1 = (s + 1) * MathF.Tau / 64;
                r.Line(new(rad * MathF.Sin(a0), -0.5f, -rad * MathF.Cos(a0)), new(rad * MathF.Sin(a1), -0.5f, -rad * MathF.Cos(a1)), Palette.Grid);
            }
        }
        for (int d = 0; d < 360; d += 30)
        {
            float a = d * MathF.PI / 180;
            r.Line(new(0, -0.5f, 0), new(2 * MathF.Sin(a), -0.5f, -2 * MathF.Cos(a)), d == 0 ? Palette.Edge : Palette.Grid);
        }

        // Listener: head and nose pointing to the front (−Z).
        r.Sphere(Vector3.Zero, 0.16f, new Vector4(0.85f, 0.78f, 0.55f, 1));
        r.Triangle(new(-0.05f, 0, -0.14f), new(0.05f, 0, -0.14f), new(0, 0, -0.26f), Palette.Accent, Palette.Accent, Palette.Accent);

        // Source with its floor shadow and a pulsing ring that suggests sound.
        r.Line(Vector3.Zero, source, Palette.Soft);
        r.Line(source, new(source.X, -0.5f, source.Z), Palette.Soft);
        r.Point(new(source.X, -0.5f, source.Z), Palette.Accent);
        // Expanding ring: phase 0…1 repeating at 0.8 Hz, growing and fading out.
        float wave = (float)(time * 0.8 % 1.0);
        for (int s = 0; s < 32; s++)
        {
            float a0 = s * MathF.Tau / 32, a1 = (s + 1) * MathF.Tau / 32, rr = 0.12f + wave * 0.35f;
            r.Line(source + new Vector3(rr * MathF.Cos(a0), 0, rr * MathF.Sin(a0)), source + new Vector3(rr * MathF.Cos(a1), 0, rr * MathF.Sin(a1)), Palette.Green with { W = 1 - wave });
        }
        r.Sphere(source, 0.1f, Palette.Green);
    }

    /// <summary>Places the source from polar values without notifying (used to follow the sliders).</summary>
    public void SetPolarSilently(float azimuth, float elevation, float distance)
    {
        float a = azimuth * MathF.PI / 180, e = elevation * MathF.PI / 180;
        float h = distance * MathF.Cos(e);
        source = new Vector3(h * MathF.Sin(a), distance * MathF.Sin(e), -h * MathF.Cos(a));
    }

    /// <inheritdoc/>
    public void AddLabels(List<Label3D> labels)
    {
        (int Deg, string Text)[] marks =
        [
            (0, Loc.S("0° frente", "0° front")), (90, Loc.S("90° derecha", "90° right")),
            (180, Loc.S("180° atrás", "180° back")), (270, Loc.S("270° izquierda", "270° left")),
        ];
        foreach (var (deg, text) in marks)
        {
            float a = deg * MathF.PI / 180;
            labels.Add(new(new(2.4f * MathF.Sin(a), -0.5f, -2.4f * MathF.Cos(a)), text, true));
        }
        foreach (int d in new[] { 30, 60, 120, 150, 210, 240, 300, 330 })
        {
            float a = d * MathF.PI / 180;
            labels.Add(new(new(2.2f * MathF.Sin(a), -0.5f, -2.2f * MathF.Cos(a)), $"{d}°"));
        }
        for (int ring = 1; ring <= 4; ring++)
        {
            float rad = ring * 0.5f, a = 45 * MathF.PI / 180;
            labels.Add(new(new(rad * MathF.Sin(a), -0.5f, -rad * MathF.Cos(a)), string.Create(CultureInfo.InvariantCulture, $"{rad:0.0}")));
        }
        var (az, el, dist) = Polar;
        labels.Add(new(source + new Vector3(0, 0.25f, 0), string.Create(CultureInfo.InvariantCulture, $"az {az:0}°  el {el:0}°  d {dist:0.00}")));
    }

    /// <summary>Moves the source, applies the limits listed in the class remarks and raises <see cref="Changed"/>.</summary>
    void Move(Vector3 delta)
    {
        source += delta;
        var flat = new Vector2(source.X, source.Z);
        if (flat.Length() > 2) flat = Vector2.Normalize(flat) * 2;
        source = new Vector3(flat.X, Math.Clamp(source.Y, -0.45f, 1.8f), flat.Y);
        if (source.Length() < 0.3f) source = Vector3.Normalize(source + new Vector3(0.001f)) * 0.3f;
        var (az, el, dist) = Polar;
        Changed?.Invoke(az, el, dist);
    }

    /// <inheritdoc/>
    public void Drag(float dx, float dy, bool shift) =>
        Move(shift ? new Vector3(0, -dy * 3, 0) : new Vector3(dx * 4, 0, dy * 4));

    /// <summary>
    /// The source follows the pointer: the ray under the mouse is intersected with the
    /// horizontal plane at the source's height, so it moves exactly where the cursor is from any
    /// camera angle. Shift moves it up/down instead.
    /// </summary>
    /// <remarks>
    /// BUG FIXED HERE: the old drag mapped mouse X/Y to fixed world X/Z, which looked rotated
    /// (the default camera is turned ~35°) and inverted once the camera was orbited.
    /// </remarks>
    public void Drag(in DragContext d)
    {
        if (d.Shift || d.RayDirection == Vector3.Zero)
        {
            Drag(d.Dx, d.Dy, d.Shift);
            return;
        }
        if (d.HitHorizontalPlane(source.Y) is { } hit)
            Move(new Vector3(hit.X - source.X, 0, hit.Z - source.Z));
    }

    /// <inheritdoc/>
    public void Wheel(float delta) => Move(new Vector3(0, delta * 0.08f, 0));
}
