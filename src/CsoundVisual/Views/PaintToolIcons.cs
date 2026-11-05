// PaintToolIcons.cs — Paint Rooms: the drawn icons of the shaped selection tools.
//
// Role: the tool palettes of the three rooms show most tools as a Unicode glyph («✎», «⬚»…). The
// three shaped selection tools have no good glyph, so they get small vector icons instead: a
// dashed ellipse (ellipse select), a dashed freehand loop with its «rope» (lasso), and a smooth
// closed curve with square nodes (spline). Each is an outline geometry (StreamGeometry path data in
// a 24 × 24 box, like the app's other icons) drawn as a STROKED Path, so the dashes read as
// «selection» (the marching-ants convention) rather than as a filled shape.
//
// The palettes call PaintToolIcons.Content(glyph): a glyph that names one of these icons (the
// constants below) becomes the Path; anything else becomes the usual TextBlock. The stroke follows
// the button's foreground (checked / hover states keep working with the theme).
// UI thread only.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

namespace CsoundVisual.Views;

/// <summary>Icons and button content for the Paint Rooms tool palettes (see the file header).</summary>
public static class PaintToolIcons
{
    /// <summary>Palette «glyph» that stands for the ellipse-selection icon.</summary>
    public const string SelectEllipse = "@sel-ellipse";

    /// <summary>Palette «glyph» that stands for the lasso-selection icon.</summary>
    public const string SelectLasso = "@sel-lasso";

    /// <summary>Palette «glyph» that stands for the spline-selection icon.</summary>
    public const string SelectSpline = "@sel-spline";

    /// <summary>Path data of each icon (24 × 24 box) and whether its stroke is dashed.</summary>
    static readonly Dictionary<string, (string Data, bool Dashed)> Icons = new()
    {
        // An ellipse 18 × 13 centred in the box (two half-arcs).
        [SelectEllipse] = ("M3,12 A9,6.5 0 1 1 21,12 A9,6.5 0 1 1 3,12 Z", true),
        // A wobbly closed loop (the lasso) plus its rope hanging from the lower left.
        [SelectLasso] = ("M12,3.5 C17.5,3.5 21,6 20.5,9.5 C20,13 15.5,14.5 11,14.2 C6,13.9 3,11.5 3.6,8.3 C4.2,5.2 7.5,3.5 12,3.5 Z " +
                         "M6.5,13.2 C5.2,15.5 6.4,17.6 8.6,18.3 C10.2,18.8 10.2,20.6 8.4,21.4", true),
        // A smooth closed blob through four nodes, and the four nodes as small squares.
        [SelectSpline] = ("M4,15 C2.5,9 7,3.5 12.5,4.5 C18.5,5.5 22,11 18.5,16.5 C15.5,21 6,21 4,15 Z " +
                          "M2.8,13.8 h2.4 v2.4 h-2.4 Z M11.3,3.3 h2.4 v2.4 h-2.4 Z M19.6,10.3 h2.4 v2.4 h-2.4 Z M11.8,18.6 h2.4 v2.4 h-2.4 Z", false),
    };

    /// <summary>The content of a palette button: the drawn icon for the selection-tool «glyphs», else the text glyph.</summary>
    /// <param name="glyph">A Unicode glyph («✎») or one of <see cref="SelectEllipse"/>, <see cref="SelectLasso"/>, <see cref="SelectSpline"/>.</param>
    /// <returns>A 20 × 20 stroked <see cref="Avalonia.Controls.Shapes.Path"/> or a 17-pt centred <see cref="TextBlock"/>.</returns>
    public static Control Content(string glyph)
    {
        if (!Icons.TryGetValue(glyph, out var icon))
            return new TextBlock { Text = glyph, FontSize = 17, HorizontalAlignment = HorizontalAlignment.Center };
        var path = new Avalonia.Controls.Shapes.Path
        {
            Data = StreamGeometry.Parse(icon.Data),
            Width = 20, Height = 20, Stretch = Stretch.Uniform,
            StrokeThickness = 1.6, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            StrokeDashArray = icon.Dashed ? [2.2, 1.6] : null,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };
        // The stroke follows the button's foreground (inherited TextElement.Foreground), so the
        // icon changes colour with the checked / hover / disabled states like the text glyphs do.
        path.Bind(Shape.StrokeProperty, path.GetObservable(TextElement.ForegroundProperty));
        return path;
    }
}
