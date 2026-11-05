// PaintImportDialog.cs — Paint Rooms.
//
// Role: the dialog shown when a picture is imported (Paint Synth: File › Import picture, or a
// picture dropped on the canvas; Image Sequencer: «import picture as pattern»). Any picture of
// any size and shape is accepted. The left column holds every choice that decides how the picture
// becomes a canvas; the right side is a LIVE PREVIEW of the resulting canvas, drawn by the same
// PaintCanvas control the rooms use (same colours, same rulers), so the user sees the result —
// including stair-steps — before importing:
//   * how many columns it gets (ImportFit: FULL DETAIL = one column per picture pixel, the
//     sharpest; keep proportions → columns follow the aspect ratio and the rows; stretch to a
//     chosen number of columns; fit inside it with silent bars),
//   * the RESOLUTION: rows (vertical, the pitch axis) and columns (horizontal, time). The picture is
//     resampled to exactly columns × rows cells; detail finer than that grid turns into steps.
//     The duration does NOT add detail, it only stretches the same columns over more seconds,
//   * the pitch AXIS the rows use (Paint Synth only): the canvas's current one, logarithmic
//     (every octave equally tall: musical) or linear (equal Hz per row: lows squeezed together),
//     with its number of rows,
//   * how long it lasts (seconds),
//   * what the colours mean (ColorMapping), with a plain-words explanation of the chosen mapping
//     (also used by Paint Synth's «?» help),
// and shows the picture's size and bit depth. «Don't ask again» stores the choice
// (PaintRoomSettings.AskOnImport = false) so later drops import immediately.
//
// Number boxes: their typed text counts at once (the preview follows while typing, and the values
// are read from the text when importing), and Enter inside a box does NOT import: the Import
// button is deliberately not the dialog's default button.
//
// The preview is computed off the UI thread (ImageCodec.FromPicture) a moment after the last
// change; AskAsync waits for a running preview before returning, because the caller disposes the
// decoded picture right after. Built in code like the other Paint Rooms dialogs; UI thread only.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>The user's answer to <see cref="PaintImportDialog"/>.</summary>
/// <param name="Fit">How the picture's shape is fitted to the canvas.</param>
/// <param name="Mapping">What the colours mean.</param>
/// <param name="DurationSeconds">Duration of the resulting canvas.</param>
/// <param name="DontAskAgain">True when the user ticked «don't ask again».</param>
/// <param name="Columns">Columns (time resolution) of the result; with <see cref="ImportFit.KeepProportions"/> it follows the picture's shape and the rows.</param>
/// <param name="Axis">The pitch axis the result uses (its row count is the vertical resolution).</param>
public sealed record PaintImportChoice(ImportFit Fit, ColorMapping Mapping, double DurationSeconds, bool DontAskAgain, int Columns, PitchAxis Axis);

/// <summary>Import options for one picture, with a live preview (see the file header).</summary>
public sealed class PaintImportDialog : Window
{
    /// <summary>The answer, or <c>null</c> when cancelled (Esc, Cancel or the window's ✕).</summary>
    PaintImportChoice? result;

    /// <summary>Every preview computation started (awaited before the caller disposes the picture).</summary>
    readonly List<Task> previewTasks = [];

    /// <summary>Builds the dialog for a decoded picture.</summary>
    /// <param name="fileName">Picture file name (shown in the title line).</param>
    /// <param name="picture">The decoded picture (read by the preview while the dialog is open).</param>
    /// <param name="axis">The canvas's current pitch axis (the default; its rows are the default vertical resolution).</param>
    /// <param name="canvasWidth">Current canvas columns (the default for «stretch» and «fit inside»).</param>
    /// <param name="prefs">Remembered choices used as the initial values.</param>
    /// <param name="duration">Initial duration in seconds (the current canvas's).</param>
    /// <param name="showDuration">False hides the duration box (the Image Sequencer: a pattern's length follows the tempo).</param>
    /// <param name="allowAxis">False keeps the given axis (the Image Sequencer: every pattern shares the document's axis).</param>
    /// <param name="maxColumns">Upper limit for the columns (patterns are shorter than canvases).</param>
    PaintImportDialog(string fileName, DecodedPicture picture, PitchAxis axis, int canvasWidth, PaintRoomSettings prefs, double duration,
                      bool showDuration, bool allowAxis, int maxColumns)
    {
        Title = Loc.S("Importar imagen", "Import picture");
        Width = 1180;
        Height = 760;
        MinWidth = 900;
        MinHeight = 560;
        Background = AppTheme.Brush("Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ---- what the picture is
        var info = new TextBlock
        {
            Text = Loc.S($"{fileName} · {picture.OriginalWidth} × {picture.OriginalHeight} px · {picture.Format} · {Depth(picture, true)}",
                         $"{fileName} · {picture.OriginalWidth} × {picture.OriginalHeight} px · {picture.Format} · {Depth(picture, false)}"),
            Foreground = AppTheme.Brush("TextDim"), FontSize = 12, TextWrapping = TextWrapping.Wrap,
        };

        // ---- fit: three radio buttons in ImportFit order (texts refreshed by Update, they quote the sizes)
        var full = new RadioButton { GroupName = "fit" };
        ToolTip.SetTip(full, Loc.S("Una columna por cada píxel de ancho de la imagen: lo más nítido en el tiempo. La duración solo decide qué tan rápido se tocan esas columnas.",
                                   "One column for every pixel of the picture's width: the sharpest in time. The duration only decides how fast those columns are played."));
        var keep = new RadioButton { GroupName = "fit" };
        ToolTip.SetTip(keep, Loc.S("El alto de la imagen se ajusta a las filas (la altura del sonido) y el ancho se convierte en columnas según su forma: una panorámica da muchas columnas, un retrato pocas. Nada se deforma.",
                                   "The picture's height is scaled to the rows (pitch) and its width becomes columns according to its shape: a panorama gives many columns, a portrait few. Nothing is distorted."));
        var stretch = new RadioButton { GroupName = "fit" };
        var inside = new RadioButton { GroupName = "fit" };
        (prefs.ImportFit switch { ImportFit.Stretch => stretch, ImportFit.FitInside => inside, ImportFit.KeepProportions => keep, _ => full }).IsChecked = true;

        // ---- resolution: pitch axis + rows (vertical), columns (horizontal)
        var axisBox = new ComboBox
        {
            ItemsSource = new[]
            {
                Loc.S($"Igual que el lienzo: {axis.Describe()}", $"Same as the canvas: {axis.Describe()}"),
                Loc.S("Logarítmico (musical: cada octava igual de alta)", "Logarithmic (musical: every octave equally tall)"),
                Loc.S("Lineal (Hz parejos: los graves quedan apretados)", "Linear (even Hz: the lows get squeezed)"),
            },
            SelectedIndex = 0, Width = 400, IsEnabled = allowAxis,
        };
        ToolTip.SetTip(axisBox, Loc.S(
            "Cómo se reparten las filas de la imagen entre las frecuencias. Logarítmico = como un teclado (lo normal para música y fotos). Lineal = como un espectrograma en Hz. Un eje de notas (temperamento o Scala) siempre redondea a notas: los escalones verticales son parte de él.",
            "How the picture's rows are spread over the frequencies. Logarithmic = like a keyboard (the usual choice for music and photos). Linear = like a spectrogram in Hz. A note axis (temperament or Scala) always rounds to notes: vertical steps are part of it."));
        // Rows for the new continuous axes: the picture's own height (full vertical detail) up to
        // 1024 by default — more rows cost more CPU when playing live; «= picture» goes up to 2048.
        int pictureRows = Math.Clamp(picture.Bitmap.Height, PitchAxis.MinRows, PitchAxis.MaxRows);
        int defaultRows = Math.Min(pictureRows, 1024);
        // On a NOTE axis (equal temperament, Scala) a picture always comes out in semitone steps;
        // start on the logarithmic axis then, so the import is smooth unless the user wants notes.
        if (allowAxis && axis.IsDiscrete) axisBox.SelectedIndex = 1;
        var rowsBox = ToolUi.Number(PitchAxis.MinRows, PitchAxis.MaxRows, defaultRows, 64,
            Loc.S("Resolución vertical: filas (alturas) del lienzo. Más filas = curvas más suaves en altura, más CPU al tocar.",
                  "Vertical resolution: rows (pitches) of the canvas. More rows = smoother curves in pitch, more CPU when playing."), "0", 130);
        // «= picture»: one row per picture pixel of height (full vertical detail, up to 2048), on a
        // continuous axis (a note axis cannot have free rows, so it switches to logarithmic).
        var rowsFullButton = new Button { Content = Loc.S($"= imagen ({pictureRows})", $"= picture ({pictureRows})"), Padding = new Thickness(8, 4), IsEnabled = allowAxis };
        ToolTip.SetTip(rowsFullButton, Loc.S("Una fila por píxel de alto de la imagen: lo más suave en altura.", "One row per pixel of the picture's height: the smoothest in pitch."));
        rowsFullButton.Click += (_, _) =>
        {
            if (axisBox.SelectedIndex == 0) axisBox.SelectedIndex = 1;
            rowsBox.Value = pictureRows;
        };
        var columnsBox = ToolUi.Number(SpectralImage.MinWidth, maxColumns, Math.Clamp(canvasWidth, SpectralImage.MinWidth, maxColumns), 64,
            Loc.S("Resolución horizontal: columnas (instantes) del lienzo. Más columnas = líneas diagonales más suaves en el tiempo.",
                  "Horizontal resolution: columns (moments in time) of the canvas. More columns = smoother diagonal lines over time."), "0", 130);
        var seconds = ToolUi.Number(0.5, 600, Math.Clamp(duration, 0.5, 600), 0.5,
            Loc.S("Duración del sonido en segundos: el ancho de la imagen se reparte en este tiempo. No agrega detalle: solo estira las mismas columnas.",
                  "Sound duration in seconds: the picture's width is spread over this time. It adds no detail: it only stretches the same columns."), "0.0", 110);
        var gridText = new TextBlock { FontSize = 12, Foreground = AppTheme.Brush("Text"), TextWrapping = TextWrapping.Wrap };

        // ---- colours
        var colours = new ComboBox
        {
            ItemsSource = new[]
            {
                Loc.S("Brillo = volumen (mono)", "Brightness = loudness (mono)"),
                Loc.S("Rojo = izquierda, verde = derecha", "Red = left, green = right"),
                Loc.S("Brillo = volumen, color = posición estéreo", "Brightness = loudness, hue = stereo position"),
                // Items 3 and 4 = ColorMapping.RgbNoise / RgbaNoiseMask (the index IS the enum value).
                Loc.S("RGB: R = izquierda, G = derecha, B = ruido", "RGB: R = left, G = right, B = noise"),
                Loc.S("RGBA: RGB + alfa = máscara (transparente = silencio)", "RGBA: RGB + alpha = mask (transparent = silence)"),
            },
            SelectedIndex = (int)prefs.ImportMapping,
            MinWidth = 300,
        };
        // Always-visible explanation of the chosen mapping (Felipe: «this information is important,
        // it should be somewhere to remember it»).
        var explain = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = AppTheme.Brush("TextDim") };
        var dontAsk = new CheckBox { Content = Loc.S("No volver a preguntar al soltar imágenes en el lienzo", "Don't ask again when pictures are dropped on the canvas") };

        // ---- preview: the rooms' own canvas on a private session, read-only
        var previewSession = new PaintSession(new PaintRoomSettings());
        var preview = new PaintCanvas { Session = previewSession, IsHitTestVisible = false, Focusable = false };
        var previewStatus = new TextBlock { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(0, 6, 0, 0) };

        // A number box's value INCLUDING text typed but not yet committed (NumericUpDown only
        // commits on Enter or focus loss); out-of-range or unparsable text falls back to the value.
        static double Val(NumericUpDown n, double fallback)
        {
            if (decimal.TryParse(n.Text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var d)
                && d >= n.Minimum && d <= n.Maximum)
                return (double)d;
            return n.Value is { } v ? (double)v : fallback;
        }

        // The current choices, read from the controls.
        ImportFit Fit() => full.IsChecked == true ? ImportFit.FullDetail : stretch.IsChecked == true ? ImportFit.Stretch
                         : inside.IsChecked == true ? ImportFit.FitInside : ImportFit.KeepProportions;
        PitchAxis Axis()
        {
            int rows = (int)Val(rowsBox, defaultRows);
            return axisBox.SelectedIndex switch
            {
                1 => PitchAxis.Logarithmic(axis.LowHz, axis.HighHz, rows),
                2 => PitchAxis.Linear(axis.LowHz, axis.HighHz, rows),
                _ => axis,
            };
        }
        int Columns(PitchAxis a) => Fit() switch
        {
            ImportFit.FullDetail => Math.Clamp(picture.Bitmap.Width, SpectralImage.MinWidth, maxColumns),
            ImportFit.KeepProportions => Math.Min(maxColumns, ImageCodec.ColumnsFor(picture.Bitmap.Width, picture.Bitmap.Height, a.RowCount)),
            _ => (int)Val(columnsBox, canvasWidth),
        };
        double Seconds() => Val(seconds, duration);
        ColorMapping Mapping() => (ColorMapping)Math.Max(0, colours.SelectedIndex);

        // Debounced preview: each change restarts a short timer; the computation runs off the UI
        // thread and only the latest request is shown.
        int generation = 0;
        var debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        debounce.Tick += (_, _) =>
        {
            debounce.Stop();
            int gen = ++generation;
            var (a, cols, secs, map, fit) = (Axis(), 0, Seconds(), Mapping(), Fit());
            cols = Columns(a);
            previewStatus.Text = Loc.S("Calculando la vista previa…", "Computing the preview…");
            var task = Task.Run(() => ImageCodec.FromPicture(picture, cols, a, secs, map, fit == ImportFit.FitInside ? ImportFit.FitInside : ImportFit.Stretch));
            previewTasks.RemoveAll(p => p.IsCompleted);
            previewTasks.Add(task);
            task.ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (gen != generation || !IsVisible) return;
                if (t.IsFaulted) { previewStatus.Text = Loc.S("No se pudo calcular: ", "Could not compute: ") + t.Exception?.GetBaseException().Message; return; }
                previewSession.ReplaceImage(t.Result, "preview", undoable: false);
                preview.ZoomToFit();
                previewStatus.Text = Loc.S($"Vista previa: {t.Result.Width} × {t.Result.Height} celdas, tal como quedará en el lienzo.",
                                           $"Preview: {t.Result.Width} × {t.Result.Height} cells, exactly as it will be on the canvas.");
            }), TaskScheduler.Default);
        };

        void Update()
        {
            var a = Axis();
            int cols = Columns(a);
            int rows = a.RowCount;
            int fullCols = Math.Clamp(picture.Bitmap.Width, SpectralImage.MinWidth, maxColumns);
            full.Content = Loc.S($"Detalle completo (recomendado): una columna por píxel = {fullCols} columnas",
                                 $"Full detail (recommended): one column per pixel = {fullCols} columns");
            int keepCols = Math.Min(maxColumns, ImageCodec.ColumnsFor(picture.Bitmap.Width, picture.Bitmap.Height, rows));
            keep.Content = Loc.S($"Mantener proporciones con las filas: {keepCols} columnas",
                                 $"Keep proportions with the rows: {keepCols} columns");
            stretch.Content = Loc.S("Estirar a las columnas elegidas (puede deformarse)", "Stretch to the chosen columns (may distort)");
            inside.Content = Loc.S("Encajar dentro de las columnas elegidas, con bordes en silencio", "Fit inside the chosen columns, with silent borders");
            // Columns are typed for stretch / fit inside; with «keep proportions» they follow the rows.
            columnsBox.IsEnabled = Fit() is ImportFit.Stretch or ImportFit.FitInside;
            if (!columnsBox.IsEnabled) columnsBox.Value = cols;
            rowsBox.IsEnabled = allowAxis && axisBox.SelectedIndex != 0;
            if (axisBox.SelectedIndex == 0) rowsBox.Value = a.RowCount;
            double pxPerCol = (double)picture.Bitmap.Width / Math.Max(1, cols), pxPerRow = (double)picture.Bitmap.Height / Math.Max(1, rows);
            double colsPerSecond = cols / Math.Max(0.001, Seconds());
            gridText.Text = Loc.S(
                $"Resultado: {cols} × {rows} celdas ({colsPerSecond:0} columnas por segundo). Cada celda resume ≈ {pxPerCol:0.#} × {pxPerRow:0.#} píxeles de la imagen: con 1 × 1 se ve tan nítida como el original. Si ves escalones, usa «Detalle completo» (horizontal) y «= imagen» en las filas (vertical); la duración no agrega detalle, solo cambia la velocidad." + (rows > 1024 ? " Más de 1024 filas exigen más CPU al tocar en vivo (el render a WAV no tiene problema)." : "") + (a.IsDiscrete ? " Este eje es de notas: en vertical siempre redondea a notas (escalones de semitono); elige Logarítmico para curvas suaves." : ""),
                $"Result: {cols} × {rows} cells ({colsPerSecond:0} columns per second). Each cell sums up ≈ {pxPerCol:0.#} × {pxPerRow:0.#} picture pixels: at 1 × 1 it is as sharp as the original. If you see stair-steps, use «Full detail» (horizontal) and «= picture» on the rows (vertical); the duration adds no detail, it only changes the speed." + (rows > 1024 ? " More than 1024 rows need more CPU when playing live (rendering to WAV is fine)." : "") + (a.IsDiscrete ? " This is a note axis: vertically it always rounds to notes (semitone steps); choose Logarithmic for smooth curves." : ""));
            explain.Text = MappingHelp(Mapping());
            debounce.Stop();
            debounce.Start();
        }
        foreach (var r in new[] { full, keep, stretch, inside }) r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) Update(); };
        // Typing counts at once (no Enter needed): follow the text of the three number boxes.
        foreach (var n in new[] { rowsBox, columnsBox, seconds })
            n.PropertyChanged += (_, e) => { if (e.Property == NumericUpDown.TextProperty && n.IsKeyboardFocusWithin && n.IsEnabled) Update(); };
        // Leaving «same as the canvas» for a continuous axis starts at the default (detailed) rows,
        // not at the canvas's row count that the box was showing.
        int lastAxis = axisBox.SelectedIndex;
        axisBox.SelectionChanged += (_, _) =>
        {
            if (lastAxis == 0 && axisBox.SelectedIndex != 0) rowsBox.Value = defaultRows;
            lastAxis = axisBox.SelectedIndex;
            Update();
        };
        colours.SelectionChanged += (_, _) => Update();
        rowsBox.ValueChanged += (_, _) => Update();
        columnsBox.ValueChanged += (_, _) => { if (columnsBox.IsEnabled) Update(); };
        seconds.ValueChanged += (_, _) => Update();

        // NOT IsDefault: Enter inside a number box must only confirm that number, never import.
        var ok = new Button { Content = Loc.S("Importar", "Import"), Classes = { "primary" }, MinWidth = 100 };
        var cancel = new Button { Content = Loc.S("Cancelar", "Cancel"), MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) =>
        {
            var a = Axis();
            result = new PaintImportChoice(Fit(), Mapping(), Seconds(), dontAsk.IsChecked == true, Columns(a), a);
            Close();
        };
        cancel.Click += (_, _) => Close();
        Closed += (_, _) => debounce.Stop();

        static TextBlock Heading(string text) => new() { Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("TextFaint"), Margin = new Thickness(0, 6, 0, 0) };
        var left = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = Loc.S("Importar imagen", "Import picture"), FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("AccentHi") },
                info,
                new TextBlock { Text = PictureBasics, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = AppTheme.Brush("Text") },
                Heading(Loc.S("FORMA", "SHAPE")),
                new StackPanel { Spacing = 6, Children = { full, keep, stretch, inside } },
                Heading(Loc.S("RESOLUCIÓN Y EJE DE ALTURA", "RESOLUTION AND PITCH AXIS")),
                ToolUi.Captioned(Loc.S("Eje", "Axis"), axisBox),
                // A WrapPanel: the three boxes wrap to a second line instead of being cut off.
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 8,
                    Children =
                    {
                        ToolUi.Captioned(Loc.S("Filas", "Rows"), rowsBox),
                        rowsFullButton,
                        ToolUi.Captioned(Loc.S("Columnas", "Columns"), columnsBox),
                        new ContentControl { Content = ToolUi.Captioned(Loc.S("Duración (s)", "Duration (s)"), seconds), IsVisible = showDuration },
                    },
                },
                gridText,
                Heading(Loc.S("COLORES", "COLOURS")),
                ToolUi.Captioned(Loc.S("Los colores son", "Colours mean"), colours),
                new Border { Background = AppTheme.Brush("Surface"), CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 8), Child = explain },
                dontAsk,
            },
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, ok } };
        var right = new DockPanel();
        DockPanel.SetDock(previewStatus, Dock.Bottom);
        right.Children.Add(previewStatus);
        right.Children.Add(new Border { BorderBrush = AppTheme.Brush("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), ClipToBounds = true, Child = preview });

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("480,16,*"), RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(18) };
        var leftScroll = new ScrollViewer { Content = left, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        grid.Children.Add(leftScroll);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Grid.SetRow(buttons, 1);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(buttons);
        Content = grid;

        Opened += (_, _) => { Update(); ok.Focus(); };
        // Test hook (CSV_IMPORT_SNAPSHOT = PNG path; optional CSV_IMPORT_SNAPSHOT_AXIS = 1 log / 2 linear):
        // renders the dialog to a picture once the preview is shown, then closes. Never set in normal use.
        if (Environment.GetEnvironmentVariable("CSV_IMPORT_SNAPSHOT") is { Length: > 0 } snap)
            Opened += async (_, _) =>
            {
                if (int.TryParse(Environment.GetEnvironmentVariable("CSV_IMPORT_SNAPSHOT_AXIS"), out int ax)) axisBox.SelectedIndex = ax;
                await Task.Delay(3000);
                var px = new PixelSize((int)Bounds.Width, (int)Bounds.Height);
                using var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(px);
                rtb.Render(this);
                rtb.Save(snap);
                Close();
            };
        // The canvas only knows its size after layout: fit the preview again when it changes.
        preview.SizeChanged += (_, _) => preview.ZoomToFit();
    }

    /// <summary>How any imported picture becomes sound (the same for every colour mapping).</summary>
    public static string PictureBasics => Loc.S(
        "Cómo suena una imagen: de izquierda a derecha = tiempo; de abajo hacia arriba = altura (grave → agudo); brillo = volumen (negro = silencio, blanco = máximo).",
        "How a picture sounds: left to right = time; bottom to top = pitch (low → high); brightness = loudness (black = silence, white = loudest).");

    /// <summary>
    /// What each colour mapping does, in plain words, including what a GREY picture does with it.
    /// Shown under the «Colours mean» box and in Paint Synth's «?» help.
    /// </summary>
    /// <param name="mapping">The mapping to explain.</param>
    /// <returns>A short paragraph in the UI language.</returns>
    public static string MappingHelp(ColorMapping mapping) => mapping switch
    {
        ColorMapping.RedGreen => Loc.S(
            "Rojo = lo que suena por la IZQUIERDA, verde = lo que suena por la DERECHA; el azul se ignora. Amarillo (rojo + verde) suena en ambos lados. Una imagen gris suena al centro (rojo y verde son iguales).",
            "Red = what plays on the LEFT, green = what plays on the RIGHT; blue is ignored. Yellow (red + green) plays on both sides. A grey picture sounds centred (red and green are equal)."),
        ColorMapping.BrightnessHuePan => Loc.S(
            "El brillo es el volumen y el TONO del color es la posición en el estéreo: rojo/magenta = izquierda, verde/cian = derecha, amarillo/azul = centro; cuanto más saturado, más hacia el lado. Los grises no tienen tono: suenan al centro.",
            "Brightness is the loudness and the colour's HUE is the stereo position: red/magenta = left, green/cyan = right, yellow/blue = centre; the more saturated, the further to the side. Greys have no hue: they sound centred."),
        ColorMapping.RgbNoise => Loc.S(
            "Rojo = izquierda, verde = derecha, AZUL = cuánto ruido (sin azul = tono puro, azul a tope = soplo o platillo). Ojo: un gris también tiene azul, así que con esta opción las fotos normales suenan en parte a ruido; úsala para imágenes hechas a propósito (o exportadas desde aquí).",
            "Red = left, green = right, BLUE = how noisy (no blue = pure tone, full blue = breath or cymbal). Note: grey also contains blue, so with this option ordinary photos partly sound like noise; use it for pictures made on purpose (or exported from here)."),
        ColorMapping.RgbaNoiseMask => Loc.S(
            "Igual que RGB (rojo = izquierda, verde = derecha, azul = ruido) y además la TRANSPARENCIA es silencio: lo transparente no suena aunque tenga color; lo semitransparente suena más suave. Para PNG con fondo transparente.",
            "Like RGB (red = left, green = right, blue = noise) and also TRANSPARENCY is silence: transparent parts do not sound even if they have colour; semi-transparent ones are quieter. For PNGs with a transparent background."),
        _ => Loc.S(
            "Solo cuenta el brillo; el color se ignora y todo suena al centro, como tonos puros. Una imagen gris: gris claro = fuerte, gris oscuro = suave. La opción más segura para fotos.",
            "Only brightness counts; colour is ignored and everything sounds centred, as pure tones. A grey picture: light grey = loud, dark grey = soft. The safest option for photographs."),
    };

    /// <summary>Bit depth line for the info text: «16 bits» / «8 bits» (+ «con alfa»), in the UI language.</summary>
    /// <param name="picture">The decoded picture.</param>
    /// <param name="spanish">True for the Spanish text.</param>
    /// <returns>E.g. «16-bit + alpha».</returns>
    static string Depth(DecodedPicture picture, bool spanish) =>
        (picture.BitsPerChannel >= 16 ? (spanish ? "16 bits por canal" : "16-bit") : (spanish ? "8 bits por canal" : "8-bit")) +
        (picture.HasAlpha ? (spanish ? " + alfa" : " + alpha") : "");

    /// <summary>Shows the dialog modally over <paramref name="owner"/>; returns the choice or <c>null</c> when cancelled.</summary>
    /// <param name="owner">Owner window (the Paint Rooms window).</param>
    /// <param name="fileName">Picture file name.</param>
    /// <param name="picture">The decoded picture (must stay alive until this returns).</param>
    /// <param name="axis">The canvas's current pitch axis.</param>
    /// <param name="canvasWidth">Current canvas columns.</param>
    /// <param name="prefs">Remembered import choices (initial values).</param>
    /// <param name="duration">Initial duration (seconds).</param>
    /// <param name="showDuration">False hides the duration box.</param>
    /// <param name="allowAxis">False keeps <paramref name="axis"/> (no axis or rows choice).</param>
    /// <param name="maxColumns">Upper limit for the columns.</param>
    /// <returns>The choice, or <c>null</c> when cancelled.</returns>
    /// <remarks>Waits for a preview still being computed, so the caller can dispose the picture safely afterwards.</remarks>
    public static async Task<PaintImportChoice?> AskAsync(Window owner, string fileName, DecodedPicture picture, PitchAxis axis, int canvasWidth,
        PaintRoomSettings prefs, double duration, bool showDuration = true, bool allowAxis = true, int maxColumns = SpectralImage.MaxWidth)
    {
        var dialog = new PaintImportDialog(fileName, picture, axis, canvasWidth, prefs, duration, showDuration, allowAxis, maxColumns);
        await dialog.ShowDialog(owner);
        foreach (var t in dialog.previewTasks)
        {
            try { await t; } catch { /* a failed preview does not matter any more */ }
        }
        return dialog.result;
    }
}
