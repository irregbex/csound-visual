using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Localization;
using CsoundVisual.Theming;

namespace CsoundVisual.Views;

/// <summary>
/// A simple modal window showing selectable, monospaced text (audio diagnostics, reports).
/// Built in code; Esc or the button closes it.
/// </summary>
/// <remarks>
/// <para>
/// The view model never references Avalonia, so it asks for reports through the
/// <c>MainViewModel.ShowReport</c> delegate; <c>MainWindow</c> implements that delegate by
/// creating this dialog and calling <see cref="Window.ShowDialog(Window)"/> with itself as owner
/// (hence <see cref="WindowStartupLocation.CenterOwner"/>).
/// </para>
/// <para>
/// Built in code rather than AXAML because it is a handful of controls with no bindings. Colours
/// come from <see cref="AppTheme"/> brushes, which are stable instances recoloured in place when
/// the theme preset changes, so the dialog follows the theme without rebuilding.
/// </para>
/// </remarks>
public sealed class InfoDialog : Window
{
    /// <summary>Creates the dialog.</summary>
    /// <param name="title">Window title.</param>
    /// <param name="text">Body text (selectable, can be copied).</param>
    public InfoDialog(string title, string text)
    {
        Title = title;
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.Brush("Bg");

        var close = new Button { Content = Loc.S("Cerrar", "Close"), Classes = { "tool", "primary" }, Padding = new Thickness(16, 8), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();

        // SelectableTextBlock (not TextBox) keeps the text read-only while still allowing
        // selection and Ctrl+C; the monospaced font keeps diagnostic tables aligned.
        var body = new SelectableTextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = AppTheme.Brush("Text"),
        };
        ToolTip.SetTip(body, Loc.S("Puedes seleccionar y copiar este texto (Ctrl+C).", "You can select and copy this text (Ctrl+C)."));

        // Layout: Close button docked at the bottom (right-aligned), the scrollable text fills
        // the rest. The last DockPanel child takes the remaining space.
        Content = new DockPanel
        {
            Margin = new Thickness(20, 16),
            Children =
            {
                new Border { Child = close, [DockPanel.DockProperty] = Dock.Bottom, Margin = new Thickness(0, 12, 0, 0) },
                new ScrollViewer { Content = body },
            },
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
