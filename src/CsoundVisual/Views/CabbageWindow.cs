using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Cabbage;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Resizable window with the Cabbage UI of the shown document (pop-out of the side-panel card).
/// The widgets scale with the window; it follows the selected tab like the card does.
/// </summary>
/// <remarks>
/// <para>
/// Opened by <see cref="CabbageCard.PopOut"/> (the ⧉ button of the "Cabbage UI" card in the
/// side panel). A <see cref="CabbageTracker"/> watches
/// <see cref="MainViewModel.SelectedDocument"/> and that document's <c>&lt;Cabbage&gt;</c> form
/// and channel list; whenever any of them changes the window rebuilds its whole content with a
/// fresh <see cref="CabbagePanel"/> (cheap: a form has at most a few dozen widgets).
/// </para>
/// <para>
/// The widgets drive the document's existing <see cref="ChannelControlViewModel"/>s, so moving a
/// knob here is the same as moving the side-panel slider (MIDI learn and the VST link keep
/// working). The window also rebuilds on <see cref="AppTheme.Changed"/> and
/// <see cref="Loc.Changed"/> because the panel bakes colours and texts into its controls.
/// </para>
/// </remarks>
public sealed class CabbageWindow : Window
{
    /// <summary>The application view model (passed to each new <see cref="CabbagePanel"/>).</summary>
    readonly MainViewModel vm;
    /// <summary>Follows the shown document and raises a coalesced "rebuild" event.</summary>
    readonly CabbageTracker tracker;
    /// <summary>Single content slot: either the <see cref="CabbagePanel"/> or <see cref="empty"/>.</summary>
    readonly Border host = new() { Padding = new Thickness(10) };
    /// <summary>Placeholder message shown when the document has no Cabbage form.</summary>
    readonly TextBlock empty = new()
    {
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        Foreground = AppTheme.Brush("TextDim"),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(20),
    };
    /// <summary>
    /// True once the initial window size has been computed from a form. Later rebuilds (other
    /// tab, edited form) keep whatever size the user gave the window.
    /// </summary>
    bool sized;

    /// <summary>Creates the window for <paramref name="vm"/>.</summary>
    /// <param name="vm">Application view model whose selected document is followed.</param>
    public CabbageWindow(MainViewModel vm)
    {
        this.vm = vm;
        Background = AppTheme.Brush("Bg");
        MinWidth = 240;
        MinHeight = 160;
        Content = host;
        tracker = new CabbageTracker(vm);
        tracker.Changed += Rebuild;
        AppTheme.Changed += Rebuild;
        Loc.Changed += Rebuild;
        // Static events (AppTheme, Loc) would keep a closed window alive: unsubscribe on close.
        Closed += (_, _) =>
        {
            tracker.Dispose();
            AppTheme.Changed -= Rebuild;
            Loc.Changed -= Rebuild;
        };
        Rebuild();
    }

    /// <summary>
    /// Rebuilds the content for the currently shown document: a placeholder when there is no
    /// form, otherwise a new <see cref="CabbagePanel"/>. Also updates the title
    /// ("caption — Cabbage UI", or "— Knobs" for forms generated from <c>;@knob</c> annotations)
    /// and, on the first form only, sizes the window.
    /// </summary>
    void Rebuild()
    {
        var form = tracker.Form;
        if (form is null || tracker.Document is null)
        {
            Title = Loc.S("Interfaz Cabbage", "Cabbage UI");
            empty.Text = Loc.S("El documento que se muestra no tiene sección <Cabbage>.", "The shown document has no <Cabbage> section.");
            host.Child = empty;
            return;
        }
        Title = (form.Caption.Length > 0 ? form.Caption : tracker.Document.FileName) + " — "
                + (form.FromAnnotations ? Loc.S("Perillas", "Knobs") : Loc.S("Interfaz Cabbage", "Cabbage UI"));
        host.Child = new CabbagePanel(form, tracker.Document, vm);
        if (!sized)
        {
            // First show: the form's size, enlarged when small, within reason. Form units are
            // Cabbage pixels; the scale brings the width towards 700 DIP (never shrinks, at most
            // doubles), +20 accounts for the host padding (10 on each side).
            sized = true;
            double scale = Math.Clamp(700 / Math.Max(form.Width, 1), 1, 2);
            Width = Math.Clamp(form.Width * scale + 20, 300, 1600);
            Height = Math.Clamp(form.Height * scale + 20, 200, 1000);
        }
    }
}
