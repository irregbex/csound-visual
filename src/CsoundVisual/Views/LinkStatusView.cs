using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Compact Ableton Link indicator/toggle for the title bar: a status dot plus
/// «Link · 2 peers · 120.0 BPM». Click = turn Link on/off (<see cref="MainViewModel.ToggleLinkCommand"/>);
/// the tooltip explains the <c>LINK_*</c> channels. Built in code; place it anywhere with
/// <c>new LinkStatusView { DataContext = vm }</c> or <c>LinkStatusView.For(vm)</c>.
/// </summary>
/// <remarks>
/// Dot colours: dim = off or unavailable, accent = on and alone, «Good» (green) = on with peers.
/// The text has a fixed minimum width so the bar does not move when the peer count changes.
/// <para>
/// The control is a <see cref="Button"/> subclass rather than a templated control: it reuses the
/// app's <c>Button.tool</c> style (see <see cref="StyleKeyOverride"/>) and simply listens to the
/// view model's <see cref="INotifyPropertyChanged.PropertyChanged"/> instead of using bindings,
/// because it combines several properties (<c>IsLinkEnabled</c>, <c>LinkPeers</c>,
/// <c>LinkStatusText</c>, <c>LinkStatusTip</c>) into one visual state.
/// </para>
/// <para>
/// The static <see cref="SettingsCard"/> builds the «SYNC» card of the settings window, which
/// embeds another instance of this indicator as its live status row.
/// </para>
/// </remarks>
public sealed class LinkStatusView : Button
{
    /// <summary>Round status dot (9 px circle); its colour encodes off / alone / with peers (see <see cref="Refresh"/>).</summary>
    readonly Border dot = new() { Width = 9, Height = 9, CornerRadius = new CornerRadius(4.5), VerticalAlignment = VerticalAlignment.Center };

    /// <summary>
    /// Status text («Link · 2 peers · 120.0 BPM»). <c>MinWidth</c> 150 keeps the title bar from
    /// shifting when the text gets shorter or longer.
    /// </summary>
    readonly TextBlock text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 12, MinWidth = 150 };

    /// <summary>The main view model currently observed (taken from <see cref="StyledElement.DataContext"/>), or null.</summary>
    MainViewModel? vm;

    /// <summary>Creates the control (bind it by setting <see cref="StyledElement.DataContext"/> to the main view model).</summary>
    public LinkStatusView()
    {
        Classes.Add("tool");
        Padding = new Thickness(10, 4);
        VerticalAlignment = VerticalAlignment.Center;
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { dot, text } };
        AutomationProperties.SetName(this, "Ableton Link");
    }

    /// <summary>Convenience factory.</summary>
    public static LinkStatusView For(MainViewModel viewModel) => new() { DataContext = viewModel };

    /// <summary>
    /// Makes Avalonia style this subclass exactly like a plain <see cref="Button"/> (selectors such
    /// as <c>Button.tool</c> match by style key, not by CLR type, so without this override the
    /// app's button styles and the Fluent template would not apply).
    /// </summary>
    protected override Type StyleKeyOverride => typeof(Button);

    /// <summary>
    /// Swaps the observed view model: unsubscribes from the previous one, subscribes to the new one,
    /// wires the click to <see cref="MainViewModel.ToggleLinkCommand"/> and refreshes the visuals.
    /// </summary>
    /// <param name="e">Unused event data.</param>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Detach first so a re-parented control never keeps two subscriptions alive.
        if (vm is not null) vm.PropertyChanged -= OnVmChanged;
        vm = DataContext as MainViewModel;
        if (vm is not null)
        {
            // Subscribe only while on screen: the main view model lives for the whole app, so a
            // subscription made by a control that was removed (e.g. the Settings › Sync card of a
            // closed settings window) would keep that control alive and refreshing forever.
            // OnAttachedToVisualTree/OnDetachedFromVisualTree add and remove it as the control
            // enters and leaves the tree.
            if (isAttached) vm.PropertyChanged += OnVmChanged;
            Command = vm.ToggleLinkCommand;
        }
        Refresh();
    }

    /// <summary>True while the control is part of a visual tree (between attach and detach).</summary>
    bool isAttached;

    /// <summary>
    /// (Re)subscribes to the view model when the control appears on screen and refreshes, so a
    /// control that was detached and re-attached shows the current Link state.
    /// </summary>
    /// <param name="e">Attachment event data.</param>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isAttached = true;
        if (vm is not null)
        {
            vm.PropertyChanged -= OnVmChanged; // never subscribe twice
            vm.PropertyChanged += OnVmChanged;
        }
        Refresh();
    }

    /// <summary>
    /// Fix for a leak: unsubscribes from the long-lived view model when the control leaves the visual
    /// tree (window closed, card removed), so the view model no longer references it.
    /// </summary>
    /// <param name="e">Detachment event data.</param>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        isAttached = false;
        if (vm is not null) vm.PropertyChanged -= OnVmChanged;
    }

    /// <summary>
    /// View-model change filter: refreshes only for Link-related properties (every property whose
    /// name starts with «Link», plus <c>IsLinkEnabled</c>) or for an "everything changed"
    /// notification (null/empty name). Raised on the UI thread by <c>MainViewModel.Link.cs</c>.
    /// </summary>
    void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName.StartsWith("Link", StringComparison.Ordinal) || e.PropertyName == nameof(MainViewModel.IsLinkEnabled))
            Refresh();
    }

    /// <summary>
    /// Copies the view model's Link state into the text, tooltip, dot colour and text opacity.
    /// Brushes come from <see cref="AppTheme"/> (stable per-role brushes that are recoloured in place
    /// when the theme preset changes, so a cached brush stays correct).
    /// </summary>
    void Refresh()
    {
        if (vm is null) return;
        text.Text = vm.LinkStatusText;
        ToolTip.SetTip(this, vm.LinkStatusTip);
        dot.Background = !vm.IsLinkEnabled ? AppTheme.Brush("TextDim")
            : vm.LinkPeers > 0 ? AppTheme.Brush("Good") : AppTheme.Brush("Accent");
        text.Opacity = vm.IsLinkEnabled ? 1 : 0.75;
    }

    /// <summary>
    /// The «Sync / Sincronización» card of the settings window: enable, quantum, start/stop sync,
    /// live status and a short how-to.
    /// </summary>
    /// <remarks>
    /// Layout, top to bottom: section title, explanation (Link vs Cubase's HOST_* channels),
    /// «Enable» check box (two-way bound to <c>IsLinkEnabled</c>; disabled when the native
    /// <c>CsoundVisualLink.dll</c> is missing), quantum box, start/stop-sync check box (two-way bound
    /// to <c>LinkStartStopSync</c>), a live <see cref="LinkStatusView"/>, a monospace Csound snippet
    /// listing the <c>LINK_*</c> channels and, when unavailable, a hint on how to build the DLL.
    /// Bindings are created in code with an explicit <c>Source</c> because the card has no
    /// DataContext of its own.
    /// </remarks>
    /// <param name="vm">The main view model that owns the Link session and its settings.</param>
    /// <returns>A <c>Border.card</c> ready to be added to the settings window.</returns>
    public static Control SettingsCard(MainViewModel vm)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = Loc.S("SINCRONIZACIÓN", "SYNC"), Classes = { "section" } });
        stack.Children.Add(Dim(Loc.S(
            "Ableton Link sincroniza tempo, pulso y fase con Ableton Live, Bitwig y apps del celular en la misma red. (Cubase no tiene Link: con Cubase usa el plugin VST3 y sus canales HOST_*.)",
            "Ableton Link syncs tempo, beat and phase with Ableton Live, Bitwig and phone apps on the same network. (Cubase has no Link: with Cubase use the VST3 plug-in and its HOST_* channels.)")));

        var enable = new CheckBox { Content = Loc.S("Activar Ableton Link", "Enable Ableton Link"), IsEnabled = vm.IsLinkAvailable };
        enable.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsLinkEnabled)) { Source = vm, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(enable, vm.LinkStatusTip);
        stack.Children.Add(enable);

        var quantum = new NumericUpDown { Minimum = 1, Maximum = 16, Increment = 1, FormatString = "0", Width = 120, VerticalAlignment = VerticalAlignment.Center, Value = (decimal)vm.LinkQuantum };
        // Box → VM push. The VM may round/clamp the value (e.g. 2.5 → 2, empty box → unchanged), so
        // after pushing, the box is re-read from the VM (see the VM → box handler below).
        quantum.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v) vm.LinkQuantum = (double)v;
            if (quantum.Value != (decimal)vm.LinkQuantum) quantum.Value = (decimal)vm.LinkQuantum;
        };
        // VM → box: previously the box was only initialised once, so a quantum changed elsewhere (or
        // normalised by the VM) was not reflected. The subscription is tied to the box's life on
        // screen so the long-lived view model does not keep a closed settings window alive.
        System.ComponentModel.PropertyChangedEventHandler onQuantum = (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.LinkQuantum) && quantum.Value != (decimal)vm.LinkQuantum)
                quantum.Value = (decimal)vm.LinkQuantum;
        };
        quantum.AttachedToVisualTree += (_, _) =>
        {
            vm.PropertyChanged -= onQuantum;
            vm.PropertyChanged += onQuantum;
            quantum.Value = (decimal)vm.LinkQuantum;
        };
        quantum.DetachedFromVisualTree += (_, _) => vm.PropertyChanged -= onQuantum;
        ToolTip.SetTip(quantum, Loc.S("Pulsos por compás (quantum). Define LINK_PHASE (0…quantum) y en qué pulso empiezan el piano roll y el secuenciador. Usa el mismo valor que la cuantización de Live (normalmente 4).",
                                      "Beats per bar (quantum). Defines LINK_PHASE (0…quantum) and the beat the piano roll and step sequencer start on. Use the same value as Live's launch quantisation (usually 4)."));
        stack.Children.Add(Row(Loc.S("Pulsos por compás", "Beats per bar"), quantum));

        var startStop = new CheckBox { Content = Loc.S("Compartir tocar/detener (Start Stop Sync)", "Share play/stop (Start Stop Sync)") };
        startStop.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.LinkStartStopSync)) { Source = vm, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(startStop, Loc.S("Si está activo (aquí y en Live), Tocar en Live arranca el piano roll/secuenciador abiertos y viceversa.",
                                        "When on (here and in Live), pressing Play in Live starts the open piano roll/step sequencer and vice versa."));
        stack.Children.Add(startStop);

        var status = For(vm);
        status.HorizontalAlignment = HorizontalAlignment.Left;
        stack.Children.Add(Row(Loc.S("Estado", "Status"), status));

        stack.Children.Add(new TextBlock
        {
            Text = "kbpm chnget \"LINK_BPM\"   ; LINK_BEAT · LINK_PHASE · LINK_PLAYING · LINK_PEERS",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            Foreground = AppTheme.Brush("AccentHi"),
        });
        if (!vm.IsLinkAvailable)
            stack.Children.Add(Dim(Loc.S("No disponible: falta CsoundVisualLink.dll junto al programa (tools\\build_link.ps1 lo compila).",
                                         "Unavailable: CsoundVisualLink.dll is missing next to the program (tools\\build_link.ps1 builds it).")));
        return new Border { Classes = { "card" }, Child = stack };
    }

    /// <summary>A two-column settings row: fixed 170 px caption on the left, the control left-aligned on the right.</summary>
    /// <param name="label">Caption text (already localised).</param>
    /// <param name="control">The editor shown in the second column.</param>
    /// <returns>The row grid.</returns>
    static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A small, wrapping explanatory paragraph using the theme's <c>dim</c> text class.</summary>
    /// <param name="text">The (localised) text.</param>
    /// <returns>The text block.</returns>
    static TextBlock Dim(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Classes = { "dim" } };
}
