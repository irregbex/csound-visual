// PaintSelectionUi.cs — Paint Rooms: the selection part of the three rooms' tool palettes.
//
// Role: Paint Synth, the Spectrum Room and the Image Filter all offer the same four selection
// tools (rectangle «M», ellipse «Shift+M», lasso «F», spline «N»; see PaintCanvas) and the same
// «SELECTION» options under the brush settings: the feather (soft edge, screen pixels) and a
// reminder of the modifiers. It also builds the «Selection ▾» menu button placed right under the
// tool grid (select all / deselect / invert + the room's image operations). This class builds those shared pieces once so the three palettes stay
// identical: the palette entries (tool, icon «glyph», bilingual tips, shortcut) and the options panel.
// UI thread only.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>Shared selection-tool palette entries, shortcuts and options panel (see the file header).</summary>
public static class PaintSelectionUi
{
    /// <summary>The three shaped selection tools as palette entries (tool, glyph/icon key, Spanish tip, English tip, shortcut).</summary>
    /// <returns>Entries in palette order: ellipse, lasso, spline. Built on call, so the shortcut text follows the UI language.</returns>
    public static (PaintTool Tool, string Glyph, string Es, string En, string Key)[] ShapedTools() =>
    [
        (PaintTool.SelectEllipse, PaintToolIcons.SelectEllipse,
            "Selección elíptica: arrastra una caja; con Mayús durante el arrastre es un círculo. Al presionar: Mayús agrega a la selección, Alt resta. Arrastrar dentro de la selección la mueve.",
            "Ellipse selection: drag a box; hold Shift while dragging for a circle. At the press: Shift adds to the selection, Alt subtracts. Dragging inside the selection moves it.",
            Loc.S("Mayús+M", "Shift+M")),
        (PaintTool.SelectLasso, PaintToolIcons.SelectLasso,
            "Lazo (a mano alzada): dibuja cualquier forma con el mouse; se cierra sola al soltar. Mayús agrega, Alt resta.",
            "Lasso (freehand): draw any shape with the mouse; it closes by itself on release. Shift adds, Alt subtracts.",
            "F"),
        (PaintTool.SelectSpline, PaintToolIcons.SelectSpline,
            "Selección spline: haz clic para poner puntos; una curva suave y cerrada pasa por todos. Doble clic o Enter la cierra, Esc cancela, Retroceso quita el último punto. Mayús en el primer clic agrega, Alt resta.",
            "Spline selection: click to place points; a smooth closed curve runs through all of them. Double-click or Enter closes it, Esc cancels, Backspace removes the last point. Shift on the first click adds, Alt subtracts.",
            "N"),
    ];

    /// <summary>Appended to the rectangle-selection tip: the modifiers, shared by all selection tools.</summary>
    public static string ModifierHint => Loc.S(" Mayús agrega a la selección, Alt resta.", " Shift adds to the selection, Alt subtracts.");

    /// <summary>The selection tool for a plain letter key (M, Shift+M, F, N), or <c>null</c>.</summary>
    /// <param name="key">The key.</param>
    /// <param name="shift">Shift is held.</param>
    /// <returns>Select (M), SelectEllipse (Shift+M), SelectLasso (F), SelectSpline (N); <c>null</c> for other keys.</returns>
    public static PaintTool? ToolForKey(Key key, bool shift) => key switch
    {
        Key.M => shift ? PaintTool.SelectEllipse : PaintTool.Select,
        Key.F => PaintTool.SelectLasso,
        Key.N => PaintTool.SelectSpline,
        _ => null,
    };

    /// <summary>
    /// The «Selection ▾» button that sits right under the tool grid: selection commands first
    /// (select all, deselect, invert), then the room's image operations, which act on the
    /// selection (or on the whole canvas when nothing is selected).
    /// </summary>
    /// <param name="session">The room's canvas session (the one whose selection the commands change).</param>
    /// <param name="addOperations">Appends the room's operations to the menu (its own «(selection)» /
    /// «(whole canvas)» scope line first, then the operations, see each room's BuildImageMenu).</param>
    /// <returns>A full-width button that builds its menu fresh on every click (so the scope line is current).</returns>
    /// <remarks>
    /// These commands used to live in an «Image ▾» menu at the top right of each room, far from the
    /// selection tools they belong to; keeping them under the tools puts «select → act» in one place.
    /// </remarks>
    public static Button SelectionMenuButton(PaintSession session, Action<MenuFlyout> addOperations)
    {
        var button = new Button
        {
            Content = Loc.S("Selección ▾", "Selection ▾"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(2, 4, 2, 0),
        };
        ToolTip.SetTip(button, Loc.S("Seleccionar todo, quitar o invertir la selección, y las operaciones (desenfocar, transponer, invertir, umbral…) sobre la selección o todo el lienzo.",
                                     "Select all, deselect or invert the selection, and the operations (blur, transpose, invert, threshold…) on the selection or the whole canvas."));
        Avalonia.Automation.AutomationProperties.SetName(button, "SelectionMenu");
        button.Click += (_, _) =>
        {
            var menu = new MenuFlyout();
            menu.Items.Add(Item(Loc.S("Seleccionar todo", "Select all"), new KeyGesture(Key.A, KeyModifiers.Control), () => session.ChangeSelection(CellRect.All(session.Image))));
            var none = Item(Loc.S("Quitar la selección", "Deselect"), new KeyGesture(Key.Escape), () => session.ChangeSelection(null));
            none.IsEnabled = session.Selection is not null;
            menu.Items.Add(none);
            // Invert = everything minus the current coverage; with no selection it selects everything.
            menu.Items.Add(Item(Loc.S("Invertir la selección", "Invert selection"), (KeyGesture?)null, () =>
                session.ChangeSelection(session.Selection is null
                    ? CellRect.All(session.Image)
                    : SelectionMask.Combine(CellRect.All(session.Image), session.Selection, SelectionCombine.Subtract))));
            menu.Items.Add(new Separator());
            addOperations(menu);
            menu.ShowAt(button);
        };
        return button;
    }

    /// <summary>A menu item with an optional shortcut shown on its right (the shortcut is only a hint; the room handles the key).</summary>
    /// <param name="header">Item text.</param>
    /// <param name="gesture">Shortcut shown on the right, or <c>null</c>.</param>
    /// <param name="click">Action run on click.</param>
    /// <remarks>
    /// The gesture is built from <see cref="Key"/> values, never parsed from text: an earlier
    /// version used <c>KeyGesture.Parse("Esc")</c>, which throws (the key is called «Escape») and,
    /// raised inside the button's click handler, closed the whole app.
    /// </remarks>
    static MenuItem Item(string header, KeyGesture? gesture, Action click)
    {
        var m = new MenuItem { Header = header, InputGesture = gesture };
        m.Click += (_, _) => click();
        return m;
    }

    /// <summary>
    /// The «SELECTION» options: the feather slider (0…40 screen pixels, applied to the canvas and
    /// stored in <paramref name="prefs"/>) and a short reminder of the modifiers.
    /// </summary>
    /// <param name="canvas">The room's canvas (its <see cref="PaintCanvas.SelectionFeather"/> is set).</param>
    /// <param name="prefs">Where the value is remembered (<see cref="PaintRoomSettings.SelectionFeather"/>).</param>
    /// <returns>The panel; the room shows it only while a selection tool is active.</returns>
    public static StackPanel OptionsPanel(PaintCanvas canvas, PaintRoomSettings prefs)
    {
        var value = new TextBlock { FontSize = 12, Foreground = AppTheme.Brush("TextDim"), HorizontalAlignment = HorizontalAlignment.Right };
        var slider = new Slider { Minimum = 0, Maximum = 40, Value = Math.Clamp(prefs.SelectionFeather, 0, 40), SmallChange = 1, LargeChange = 4 };
        ToolTip.SetTip(slider, Loc.S("Suavizado del borde de la selección, en píxeles de pantalla: 0 = borde nítido; más = la edición se desvanece gradualmente fuera de la forma (sin cortes bruscos en el sonido). Cambia en vivo la última selección dibujada; el relleno de color muestra el borde suave.",
                                     "Soft edge of the selection, in screen pixels: 0 = crisp edge; more = the edit fades out gradually around the shape (no abrupt cuts in the sound). Changes the last drawn selection live; the colour fill shows the soft edge."));
        void Sync()
        {
            prefs.SelectionFeather = slider.Value;
            canvas.SelectionFeather = slider.Value;
            value.Text = slider.Value < 0.5 ? Loc.S("nítido", "crisp") : $"{slider.Value:0} px";
        }
        slider.ValueChanged += (_, _) =>
        {
            Sync();
            // Make the change visible: the last drawn selection re-shapes live; with no such
            // selection a feathered ghost box shows the edge for a moment.
            if (!canvas.FeatherIsLive) canvas.ShowParameterPreview(PaintCanvas.ParameterPreview.Feather);
        };
        Sync();
        var label = new TextBlock { Text = Loc.S("Suavizado", "Feather"), FontSize = 12, Foreground = AppTheme.Brush("Text") };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(label);
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return new StackPanel
        {
            Spacing = 3, Margin = new Thickness(0, 8, 0, 0),
            Children =
            {
                new TextBlock { Text = Loc.S("SELECCIÓN", "SELECTION"), FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("TextFaint"), Margin = new Thickness(2, 4, 0, 2) },
                row,
                slider,
                new TextBlock
                {
                    Text = Loc.S("Mayús = agregar · Alt = restar\nArrastrar dentro = mover", "Shift = add · Alt = subtract\nDrag inside = move"),
                    FontSize = 11, Foreground = AppTheme.Brush("TextDim"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(2, 2, 0, 0),
                },
            },
        };
    }
}
