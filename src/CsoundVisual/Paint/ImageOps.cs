// ImageOps.cs — Paint Rooms foundation.
//
// Role: the «Image» menu operations (clear, invert, normalise, blur, transpose, stretch, reverse,
// flip, noise, threshold, contrast, fades, stereo helpers) as a REGISTRY of ImageOperation
// records, so the menu is built from data and later rooms add their own operations with one
// line: ImageOps.Register(new ImageOperation(...)).
//
// Two kinds:
//   * InPlace   — modifies the cells of a region (the selection, or the whole image) of the
//                 current image; PaintSession records just those columns for undo;
//   * Replace   — returns a NEW image (e.g. a different width when stretching in time);
//                 PaintSession stores the old image object for undo.
// Pure functions of (image, region, parameter); UI thread when run through PaintSession.
//
// Shared by every Paint Room (Paint Synth, Spectrum Room, Image Filter): each room's «Image ▾»
// menu lists ImageOps.All and runs the chosen entry through its PaintSession, which takes care of
// the undo record and the change notification. Cell values are brightness 0…1 per layer; the
// image is column-major (index = x · Height + y, x = time column, y = pitch row, row 0 = lowest).
// A CellRect is half-open: columns [X0, X1), rows [Y0, Y1).
// Shaped selections (ellipse, lasso, spline — SelectionMask): an operation still receives the
// selection's bounding rectangle; PaintSession.Apply then blends its result back by each cell's
// coverage (old + (new − old) · coverage), so the implementations here never need to know the shape.
// (Consequence for the geometry operations — transpose, flip, reverse — and for normalise: they
// work on the whole rectangle, and only what lands inside the shape is kept.)
// The registry is a plain List, not thread-safe: register and read it on the UI thread.
//
// Noise layer (SpectralImage.Noise, optional per-cell noisiness 0 = tone … 1 = noise): the
// GEOMETRY operations (transpose, flip, reverse, stretch) and blur move / smooth it together with
// the levels, «clear» resets it to tonal, and «noise amount» (group 4) sets it. The LEVEL operations
// (invert, normalise, contrast, threshold, add specks, fades) and the stereo ones leave it alone:
// they change how loud a cell is, not what it is made of.

namespace CsoundVisual.Paint;

/// <summary>Whether an operation edits cells in place or produces a new image.</summary>
public enum OperationKind
{
    /// <summary>Edits the cells of the region in place (returns <c>null</c>).</summary>
    InPlace,
    /// <summary>Returns a new image that replaces the current one.</summary>
    Replace,
}

/// <summary>The single numeric parameter an operation may ask for.</summary>
/// <param name="LabelEs">Spanish label.</param>
/// <param name="LabelEn">English label.</param>
/// <param name="Min">Smallest value.</param>
/// <param name="Max">Largest value.</param>
/// <param name="Default">Initial value.</param>
/// <param name="Step">Increment of the number box.</param>
/// <param name="Format">.NET format string of the number box.</param>
public sealed record OperationParameter(string LabelEs, string LabelEn, double Min, double Max, double Default, double Step, string Format = "0.##");

/// <summary>One entry of the Image menu.</summary>
/// <param name="Id">Stable identifier («blur»).</param>
/// <param name="NameEs">Spanish menu text.</param>
/// <param name="NameEn">English menu text.</param>
/// <param name="TipEs">Spanish tooltip: what it does to the sound.</param>
/// <param name="TipEn">English tooltip.</param>
/// <param name="Parameter">Numeric parameter to ask for, or <c>null</c>.</param>
/// <param name="Kind">In place or replacement.</param>
/// <param name="Apply">
/// (image, region, parameter) → <c>null</c> for in-place operations (cells edited), or the new image.
/// </param>
/// <param name="Group">Menu group; a separator is drawn between groups.</param>
public sealed record ImageOperation(string Id, string NameEs, string NameEn, string TipEs, string TipEn,
    OperationParameter? Parameter, OperationKind Kind, Func<SpectralImage, CellRect, double, SpectralImage?> Apply, int Group = 0);

/// <summary>Registry and implementations of the image operations.</summary>
public static class ImageOps
{
    /// <summary>The operations in menu order; filled lazily with the built-ins on first use.</summary>
    static readonly List<ImageOperation> registry = [];

    /// <summary>All registered operations, in menu order.</summary>
    public static IReadOnlyList<ImageOperation> All
    {
        get
        {
            if (registry.Count == 0) RegisterBuiltIns();
            return registry;
        }
    }

    /// <summary>Adds an operation to the Image menu (later rooms extend the menu this way).</summary>
    /// <param name="op">The operation; one with the same <see cref="ImageOperation.Id"/> is replaced
    /// (and the new one moves to the end of the menu).</param>
    /// <remarks>The built-ins are registered first, so a room's own entries always come after them.</remarks>
    public static void Register(ImageOperation op)
    {
        if (registry.Count == 0) RegisterBuiltIns();
        registry.RemoveAll(o => o.Id == op.Id);
        registry.Add(op);
    }

    /// <summary>Finds an operation by id.</summary>
    /// <param name="id">Stable id, e.g. «blur».</param>
    /// <returns>The operation, or <c>null</c> when no such id is registered.</returns>
    public static ImageOperation? Find(string id) => All.FirstOrDefault(o => o.Id == id);

    /// <summary>Fills the registry with the built-in operations, in menu order (groups separated in the menu).</summary>
    /// <remarks>
    /// Groups: 0 = level operations (clear, invert, normalise, contrast, threshold), 1 = texture and
    /// envelopes (blur, noise, fades), 2 = geometry (transpose, flip, reverse, stretch),
    /// 3 = stereo (mono, swap, pan, pan sweeps), 4 = noise layer (noise amount). The lambdas return <c>null</c> for in-place operations.
    /// </remarks>
    static void RegisterBuiltIns()
    {
        // Plain Add (not Register): Register would recurse into RegisterBuiltIns while the list is empty.
        void Add(ImageOperation o) => registry.Add(o);
        Add(new("clear", "Borrar todo", "Clear", "Silencio en la selección (o en todo el lienzo).", "Silence in the selection (or the whole canvas).",
            null, OperationKind.InPlace, (img, r, _) => { ForEach(img, r, v => 0); SetNoise(img, r, 0, createLayer: false); return null; }));
        Add(new("invert", "Invertir", "Invert", "Lo brillante se apaga y lo oscuro suena: un acorde se vuelve un «hueco» en un muro de sonido.",
            "Bright becomes dark and dark becomes loud: a chord turns into a «hole» in a wall of sound.",
            null, OperationKind.InPlace, (img, r, _) => { ForEach(img, r, v => 1 - v); return null; }));
        Add(new("normalize", "Normalizar", "Normalize", "Escala para que el punto más brillante quede al máximo.", "Scales so the brightest cell reaches the maximum.",
            null, OperationKind.InPlace, (img, r, _) => { Normalize(img, r); return null; }));
        Add(new("contrast", "Contraste (curva)", "Contrast (curve)", "Eleva cada nivel a una potencia: >1 deja solo lo brillante (más limpio), <1 levanta lo tenue (más denso).",
            "Raises every level to a power: >1 keeps only the bright parts (cleaner), <1 lifts the faint ones (denser).",
            new("Exponente", "Exponent", 0.25, 4, 2, 0.25), OperationKind.InPlace, (img, r, p) => { ForEach(img, r, v => MathF.Pow(v, (float)p)); return null; }));
        Add(new("threshold", "Umbral", "Threshold", "Silencia todo lo que está bajo el nivel: limpia el ruido de las fotos.", "Silences everything below the level: cleans the noise of photographs.",
            new("Nivel", "Level", 0, 1, 0.3, 0.05), OperationKind.InPlace, (img, r, p) => { ForEach(img, r, v => v < p ? 0 : v); return null; }));

        Add(new("blur", "Desenfocar", "Blur", "Suaviza en tiempo y altura: los tonos se vuelven bandas y los ataques se ablandan.",
            "Softens in time and pitch: tones become bands and attacks soften.",
            new("Radio (celdas)", "Radius (cells)", 1, 24, 2, 1, "0"), OperationKind.InPlace, (img, r, p) => { Blur(img, r, (int)p); return null; }, 1));
        Add(new("noise", "Agregar ruido", "Add noise", "Agrega puntos al azar: aire, grano, crepitar.", "Adds random specks: air, grain, crackle.",
            new("Cantidad", "Amount", 0.01, 1, 0.15, 0.05), OperationKind.InPlace, (img, r, p) => { AddNoise(img, r, p); return null; }, 1));
        Add(new("fadein", "Fundido de entrada", "Fade in", "Rampa de 0 a 1 de izquierda a derecha en la selección.", "Ramp from 0 to 1 from left to right in the selection.",
            null, OperationKind.InPlace, (img, r, _) => { Fade(img, r, true); return null; }, 1));
        Add(new("fadeout", "Fundido de salida", "Fade out", "Rampa de 1 a 0 de izquierda a derecha en la selección.", "Ramp from 1 to 0 from left to right in the selection.",
            null, OperationKind.InPlace, (img, r, _) => { Fade(img, r, false); return null; }, 1));

        Add(new("shift", "Transponer (filas)", "Transpose (rows)", "Mueve todo hacia arriba (+) o abajo (−) un número de filas. En temperamento igual de 12, una fila = un semitono.",
            "Moves everything up (+) or down (−) by a number of rows. In 12-tone equal temperament one row = one semitone.",
            new("Filas", "Rows", -240, 240, 12, 1, "0"), OperationKind.InPlace, (img, r, p) => { Shift(img, r, (int)Math.Round(p)); return null; }, 2));
        Add(new("flip", "Voltear vertical", "Flip vertically", "Lo grave pasa a agudo y viceversa (una melodía se invierte).", "Low becomes high and vice versa (a melody is inverted).",
            null, OperationKind.InPlace, (img, r, _) => { Flip(img, r); return null; }, 2));
        Add(new("reverse", "Invertir en el tiempo", "Reverse", "El sonido se toca al revés.", "The sound plays backwards.",
            null, OperationKind.InPlace, (img, r, _) => { Reverse(img, r); return null; }, 2));
        Add(new("stretch", "Estirar en el tiempo", "Stretch in time", "Alarga (>1) o acorta (<1) todo el lienzo y su duración, sin cambiar la altura.",
            "Lengthens (>1) or shortens (<1) the whole canvas and its duration, without changing pitch.",
            new("Factor", "Factor", 0.1, 8, 2, 0.1), OperationKind.Replace, (img, _, p) => Stretch(img, p), 2));

        Add(new("mono", "Hacer mono", "Make mono", "Iguala las dos capas (el máximo de ambas): sonido centrado.", "Makes both layers equal (the maximum of the two): centred sound.",
            null, OperationKind.InPlace, (img, r, _) => { Stereo(img, r, swap: false); return null; }, 3));
        Add(new("swap", "Intercambiar izquierda/derecha", "Swap left/right", "Lo de la izquierda pasa a la derecha y viceversa.", "Left goes right and right goes left.",
            null, OperationKind.InPlace, (img, r, _) => { Stereo(img, r, swap: true); return null; }, 3));
        Add(new("pan", "Panorama…", "Pan…", "Pone todo lo seleccionado en una posición del estéreo: −1 = solo izquierda, 0 = centro, +1 = solo derecha. Conserva el volumen (el lado cercano queda al máximo).",
            "Places everything selected at one stereo position: −1 = left only, 0 = centre, +1 = right only. Keeps the loudness (the near side stays at full level).",
            new("Panorama (−1 izq. … +1 der.)", "Pan (−1 left … +1 right)", -1, 1, 0, 0.1), OperationKind.InPlace, (img, r, p) => { Pan(img, r, _ => p); return null; }, 3));
        Add(new("panrl", "Barrido de panorama: derecha → izquierda", "Pan sweep: right → left",
            "El sonido de la selección viaja suavemente de la derecha (al comienzo) a la izquierda (al final). Para una línea que cruza el estéreo: píntala, selecciónala y aplica esto.",
            "The selection's sound travels smoothly from the right (at its start) to the left (at its end). For a line that crosses the stereo field: paint it, select it and apply this.",
            null, OperationKind.InPlace, (img, r, _) => { Pan(img, r, t => 1 - 2 * t); return null; }, 3));
        Add(new("panlr", "Barrido de panorama: izquierda → derecha", "Pan sweep: left → right",
            "El sonido de la selección viaja suavemente de la izquierda (al comienzo) a la derecha (al final).",
            "The selection's sound travels smoothly from the left (at its start) to the right (at its end).",
            null, OperationKind.InPlace, (img, r, _) => { Pan(img, r, t => 2 * t - 1); return null; }, 3));

        Add(new("noiseamount", "Cantidad de ruido (tono ↔ ruido)", "Noise amount (tone ↔ noise)",
            "Fija cuán ruidosa suena cada celda de la selección sin cambiar su volumen: 0 = tono puro (seno), 1 = banda de ruido, entre medio = mezcla. Lo mismo que pinta el control «Ruido» del pincel.",
            "Sets how noisy each cell of the selection sounds without changing its loudness: 0 = pure tone (sine), 1 = noise band, in between = a mix. The same thing the brush's «Noise» control paints.",
            new("Ruido (0 = tono … 1 = ruido)", "Noise (0 = tone … 1 = noise)", 0, 1, 1, 0.05), OperationKind.InPlace,
            (img, r, p) => { SetNoise(img, r, (float)p, createLayer: p > 0); return null; }, 4));
    }

    /// <summary>Sets the noisiness of every cell of <paramref name="r"/> to <paramref name="amount"/> (clamped to 0…1).</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region.</param>
    /// <param name="amount">0 = tonal, 1 = noise band.</param>
    /// <param name="createLayer">True: create the noise layer if the image has none (needed for amount &gt; 0).
    /// False: an image without a layer is left alone (it is already «all tonal»).</param>
    public static void SetNoise(SpectralImage img, CellRect r, float amount, bool createLayer)
    {
        var n = createLayer ? img.EnsureNoise() : img.Noise;
        if (n is null) return;
        amount = Math.Clamp(amount, 0f, 1f);
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
            Array.Fill(n, amount, x * h + r.Y0, r.Height);   // the region's rows of one column are contiguous
    }

    /// <summary>The image's layers that geometry operations must move together: left, right and (when present) noise.</summary>
    /// <param name="img">The image.</param>
    /// <returns>Two or three column-major arrays of the same layout.</returns>
    static float[][] MovingLayers(SpectralImage img) => img.Noise is { } n ? [img.Left, img.Right, n] : [img.Left, img.Right];

    // ------------------------------------------------------------------------------------------
    //  Implementations (public so tests and other rooms can call them directly)
    // ------------------------------------------------------------------------------------------

    /// <summary>Applies <paramref name="f"/> to every cell of both layers inside <paramref name="r"/> (results clamped to 0…1).</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region (columns × rows, half-open).</param>
    /// <param name="f">Level → level function, applied independently to the left and right layers.</param>
    public static void ForEach(SpectralImage img, CellRect r, Func<float, float> f)
    {
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                int i = x * h + y;
                img.Left[i] = Math.Clamp(f(img.Left[i]), 0f, 1f);
                img.Right[i] = Math.Clamp(f(img.Right[i]), 0f, 1f);
            }
    }

    /// <summary>Scales the region so its maximum (over both layers) becomes 1.</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region.</param>
    /// <remarks>One common gain for both layers, so the stereo balance is preserved. An all-black region is left untouched.</remarks>
    public static void Normalize(SpectralImage img, CellRect r)
    {
        float max = 0;
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
                max = Math.Max(max, Math.Max(img.Left[x * h + y], img.Right[x * h + y]));
        if (max <= 0) return;
        float g = 1 / max;
        ForEach(img, r, v => v * g);
    }

    /// <summary>
    /// Approximately Gaussian blur of radius <paramref name="radius"/> cells: three passes of a box
    /// filter in each direction (time and pitch), separable, inside the region.
    /// </summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region; cells outside it are neither read nor written (its edges are replicated).</param>
    /// <param name="radius">Box half-width in cells, clamped to 1…64 (box length 2·radius + 1).</param>
    /// <remarks>
    /// By the central limit theorem three box passes are already close to a Gaussian; the
    /// running sum makes each pass O(cells) whatever the radius.
    /// </remarks>
    public static void Blur(SpectralImage img, CellRect r, int radius)
    {
        radius = Math.Clamp(radius, 1, 64);
        // The noise layer is smoothed too, so a blurred tone/noise boundary becomes a gradual mix.
        foreach (var layer in MovingLayers(img))
            for (int pass = 0; pass < 3; pass++)
            {
                BoxPass(layer, img.Height, r, radius, alongTime: true);
                BoxPass(layer, img.Height, r, radius, alongTime: false);
            }
    }

    /// <summary>One running-sum box filter pass along time (rows fixed) or pitch (columns fixed), clamped at the region edges.</summary>
    /// <param name="a">One layer (Left or Right) of the image, column-major.</param>
    /// <param name="h">Image height (rows per column).</param>
    /// <param name="r">Region.</param>
    /// <param name="rad">Box half-width in cells.</param>
    /// <param name="alongTime">True: filter each row across columns; false: each column across rows.</param>
    static void BoxPass(float[] a, int h, CellRect r, int rad, bool alongTime)
    {
        // «outer» walks the lines to filter, «inner» walks the cells along one line.
        int outer0 = alongTime ? r.Y0 : r.X0, outer1 = alongTime ? r.Y1 : r.X1;
        int inner0 = alongTime ? r.X0 : r.Y0, inner1 = alongTime ? r.X1 : r.Y1;
        int len = inner1 - inner0;
        if (len <= 1) return;
        var line = new float[len];
        for (int o = outer0; o < outer1; o++)
        {
            int Idx(int i) => alongTime ? i * h + o : o * h + i;
            // Copy the line first: the output overwrites the array the running sum still needs.
            for (int i = 0; i < len; i++) line[i] = a[Idx(inner0 + i)];
            // Window sum centred on i = 0 (indices clamped = edge cells replicated).
            double sum = 0;
            for (int k = -rad; k <= rad; k++) sum += line[Math.Clamp(k, 0, len - 1)];
            for (int i = 0; i < len; i++)
            {
                // Write the mean, then slide the window one cell: add the entering cell, drop the leaving one.
                a[Idx(inner0 + i)] = (float)(sum / (2 * rad + 1));
                sum += line[Math.Clamp(i + rad + 1, 0, len - 1)] - line[Math.Clamp(i - rad, 0, len - 1)];
            }
        }
    }

    /// <summary>Adds random specks: each cell gets +amount·u³ (u uniform 0…1), so most specks are faint and a few bright.</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region.</param>
    /// <param name="amount">Largest speck added (0…1); results are capped at 1.</param>
    /// <remarks>The same speck goes to both layers (centred grain). Unseeded: every run differs.</remarks>
    public static void AddNoise(SpectralImage img, CellRect r, double amount)
    {
        var rng = new Random();
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                int i = x * h + y;
                double u = rng.NextDouble();
                float add = (float)(amount * u * u * u);
                img.Left[i] = Math.Min(1f, img.Left[i] + add);
                img.Right[i] = Math.Min(1f, img.Right[i] + add);
            }
    }

    /// <summary>Linear fade across the region's columns: in = 0→1, out = 1→0.</summary>
    /// <param name="img">Image edited in place (levels multiplied).</param>
    /// <param name="r">Region; the ramp spans its columns exactly (first column 0, last column 1 for a fade-in).</param>
    /// <param name="fadeIn">True for 0→1, false for 1→0.</param>
    public static void Fade(SpectralImage img, CellRect r, bool fadeIn)
    {
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
        {
            // t = 0 at the first column, 1 at the last (a one-column region gets t = 1).
            float t = r.Width <= 1 ? 1 : (float)(x - r.X0) / (r.Width - 1);
            float g = fadeIn ? t : 1 - t;
            for (int y = r.Y0; y < r.Y1; y++)
            {
                int i = x * h + y;
                img.Left[i] *= g;
                img.Right[i] *= g;
            }
        }
    }

    /// <summary>Moves the region's content up by <paramref name="rows"/> (down when negative); what leaves the region is lost, what enters is silent.</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region.</param>
    /// <param name="rows">Rows to move (+ = up = higher pitch). On a 12-TET axis one row is a semitone;
    /// on a logarithmic axis the interval per row depends on the axis resolution.</param>
    public static void Shift(SpectralImage img, CellRect r, int rows)
    {
        if (rows == 0) return;
        int h = img.Height, n = r.Height;
        var col = new float[n];
        // Every layer (left, right and the optional noise) moves by the same number of rows.
        foreach (var layer in MovingLayers(img))
            for (int x = r.X0; x < r.X1; x++)
            {
                // Copy the column's region slice first, then write it back shifted (source row = y − rows).
                int o = x * h + r.Y0;
                Array.Copy(layer, o, col, 0, n);
                for (int y = 0; y < n; y++)
                {
                    int src = y - rows;
                    layer[o + y] = src >= 0 && src < n ? col[src] : 0;
                }
            }
    }

    /// <summary>Flips the region upside down (pitch inversion).</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region; rows Y0…Y1−1 are mirrored around their middle.</param>
    public static void Flip(SpectralImage img, CellRect r)
    {
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
        {
            // A column's rows are contiguous, so the flip is a plain Array.Reverse of the slice.
            int o = x * h;
            foreach (var layer in MovingLayers(img))
                Array.Reverse(layer, o + r.Y0, r.Height);
        }
    }

    /// <summary>Reverses the region in time.</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region; its columns are mirrored, only within rows Y0…Y1−1.</param>
    public static void Reverse(SpectralImage img, CellRect r)
    {
        int h = img.Height;
        var layers = MovingLayers(img);
        // Swap columns pairwise from both ends towards the middle.
        for (int a = r.X0, b = r.X1 - 1; a < b; a++, b--)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                int i = a * h + y, j = b * h + y;
                foreach (var layer in layers)
                    (layer[i], layer[j]) = (layer[j], layer[i]);
            }
    }

    /// <summary>
    /// A new image <paramref name="factor"/> times longer: the width is scaled too (same time
    /// resolution) up to <see cref="SpectralImage.MaxWidth"/>; beyond that only the duration grows.
    /// </summary>
    /// <param name="img">Source image (not modified).</param>
    /// <param name="factor">Time factor, clamped to 0.05…16 (the menu offers 0.1…8).</param>
    /// <returns>A new image: resampled to the new width by <see cref="SpectralImage.Retuned"/>, same axis, duration × factor.</returns>
    /// <remarks>Ignores the selection: it always acts on the whole canvas (a Replace operation).</remarks>
    public static SpectralImage Stretch(SpectralImage img, double factor)
    {
        factor = Math.Clamp(factor, 0.05, 16);
        int w = (int)Math.Clamp(Math.Round(img.Width * factor), SpectralImage.MinWidth, SpectralImage.MaxWidth);
        var result = img.Retuned(w);
        result.DurationSeconds = img.DurationSeconds * factor;
        return result;
    }

    /// <summary>Make mono (both layers = their maximum) or swap the layers, inside the region.</summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region.</param>
    /// <param name="swap">True swaps left/right; false makes both layers equal to their maximum
    /// (maximum rather than average, so a cell lit on one side only keeps its level).</param>
    public static void Stereo(SpectralImage img, CellRect r, bool swap)
    {
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                int i = x * h + y;
                if (swap) (img.Left[i], img.Right[i]) = (img.Right[i], img.Left[i]);
                else img.Left[i] = img.Right[i] = Math.Max(img.Left[i], img.Right[i]);
            }
    }

    /// <summary>
    /// Re-places the cells of <paramref name="r"/> in the stereo field: each cell's level (the louder
    /// of its two sides) is split again with the pan returned by <paramref name="panAt"/> for its
    /// column, using the brushes' balance law (<see cref="BrushSettings.Targets"/>: the near side
    /// keeps full level up to the centre, the far side fades linearly), so a pan sweep keeps the
    /// loudness constant while it moves.
    /// </summary>
    /// <param name="img">Image edited in place.</param>
    /// <param name="r">Region.</param>
    /// <param name="panAt">Column position inside the region (0 = its first column … 1 = its last) → pan −1…+1.</param>
    public static void Pan(SpectralImage img, CellRect r, Func<double, double> panAt)
    {
        int h = img.Height;
        for (int x = r.X0; x < r.X1; x++)
        {
            // Position of the column's centre inside the region: 0 … 1 (a single column is 0.5).
            double t = r.Width <= 1 ? 0.5 : (x - r.X0) / (double)(r.Width - 1);
            double p = Math.Clamp(panAt(t), -1, 1);
            float gl = (float)Math.Min(1, 1 - p), gr = (float)Math.Min(1, 1 + p);
            for (int y = r.Y0; y < r.Y1; y++)
            {
                int i = x * h + y;
                float m = Math.Max(img.Left[i], img.Right[i]);
                img.Left[i] = m * gl;
                img.Right[i] = m * gr;
            }
        }
    }
}
