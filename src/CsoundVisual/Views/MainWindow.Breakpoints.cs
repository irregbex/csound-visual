using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Rendering;
using CsoundVisual.Editor;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Breakpoint UI of the main window: the gutter (<see cref="BreakpointMargin"/>), the paused-line
/// band, the <see cref="DebugPanel"/> over the editor, the status bar hint and the keys
/// (F9 toggle, Shift+F9 clear all; F5 continue and F10 next cycle only while paused, F5 is Play otherwise).
/// </summary>
/// <remarks>
/// <para>
/// This partial file keeps the debugger UI out of <c>MainWindow.axaml.cs</c>. Everything is
/// added in code to the existing AXAML tree: the gutter goes into AvaloniaEdit's
/// <c>TextArea.LeftMargins</c>, the line band into <c>TextView.BackgroundRenderers</c>, the
/// <see cref="DebugPanel"/> into the code-area panel that also hosts the hover overlay, and the
/// hint into the status bar grid. The breakpoint state itself lives in the view model
/// (<c>MainViewModel.Debug.cs</c>, per-document <c>DocumentViewModel.Breakpoints</c>); the
/// gutter and renderer read it through delegates, so they always show the selected document.
/// </para>
/// <para>
/// Threading: the engine's debugger callback runs on the Csound performance thread, but the view
/// model does not listen to it directly: it polls the breakpoint session once per UI frame
/// (<c>DebugTick</c>), so every notification handled here arrives on the UI thread.
/// <see cref="MainViewModel.DebugLineRequested"/> is raised right after the VM switches
/// <c>SelectedDocument</c> to the paused document, so it is re-posted here at
/// <see cref="DispatcherPriority.Background"/> to scroll only after the editor has loaded
/// that document and laid it out.
/// </para>
/// </remarks>
public partial class MainWindow
{
    /// <summary>Editor gutter that draws breakpoint dots and the paused arrow and toggles on click.</summary>
    BreakpointMargin? breakpointMargin;
    /// <summary>Floating variables panel shown over the editor while paused (created on first view-model attach).</summary>
    DebugPanel? debugPanel;
    /// <summary>The view model the debug UI is attached to; guards against attaching twice.</summary>
    MainViewModel? debugViewModel;

    /// <summary>The debug panel (for tests), or <c>null</c> before the view model is attached.</summary>
    internal DebugPanel? DebugPanelView => debugPanel;

    /// <summary>Installs the gutter, the renderer and the keys. Called once from the constructor.</summary>
    void SetupBreakpoints()
    {
        // The delegates are evaluated at draw/click time (not captured values), so the gutter
        // always reflects the currently selected tab and the current pause, and works even
        // before the view model is attached (vm is null → nothing drawn).
        breakpointMargin = new BreakpointMargin(
            () => vm?.SelectedDocument?.Breakpoints,
            () => vm?.PausedLineIn(vm.SelectedDocument),
            line => vm?.ToggleBreakpoint(line));
        ToolTip.SetTip(breakpointMargin, Loc.S("Clic: poner o quitar un breakpoint (F9). Shift+F9 quita todos.",
                                               "Click: set or remove a breakpoint (F9). Shift+F9 removes them all."));
        // Index 0: leftmost, before the line numbers (like Visual Studio / VS Code).
        Editor.TextArea.LeftMargins.Insert(0, breakpointMargin);
        // Background renderer: tints breakpoint lines and the paused line behind the text.
        Editor.TextArea.TextView.BackgroundRenderers.Add(new BreakpointLineRenderer(
            () => vm?.SelectedDocument?.Breakpoints,
            () => vm?.PausedLineIn(vm.SelectedDocument)));
        // Tunnel: runs before the window's KeyBindings, so F5 can mean «continue» while paused.
        AddHandler(KeyDownEvent, OnDebugKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => AttachDebugView(DataContext as MainViewModel);
    }

    /// <summary>Connects the debug UI to the view model (panel over the editor, status bar hint, events).</summary>
    /// <param name="model">The new <c>DataContext</c>; ignored when null or already attached.</param>
    /// <remarks>
    /// Runs once in practice (the main window's DataContext is set once). The subscriptions to
    /// the view model are never removed because the view model and the window share a lifetime.
    /// </remarks>
    void AttachDebugView(MainViewModel? model)
    {
        if (model is null || model == debugViewModel)
            return;
        debugViewModel = model;

        // The panel floats over the right side of the code area (same cell as the hover overlay).
        if (Overlay.Parent is Panel codeArea)
        {
            debugPanel = new DebugPanel(model);
            codeArea.Children.Add(debugPanel);
        }

        // Status bar: a hint over the middle column while paused or after toggling a breakpoint.
        // The status bar is found structurally (the Grid in row 2 of the window's root Grid, see
        // MainWindow.axaml); if the layout changes and it is not found, the hint is simply absent.
        if (Content is Grid root && root.Children.OfType<Grid>().FirstOrDefault(g => Grid.GetRow(g) == 2) is { } statusBar)
        {
            var text = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            var hint = new Border
            {
                Child = text, Padding = new Thickness(10, 2), Margin = new Thickness(14, 3, 18, 3), CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch, IsVisible = false,
            };
            Grid.SetColumn(hint, 1);
            statusBar.Children.Add(hint);
            // Hidden while DebugStatusText is empty. Paused = inverted "warning" pill (dark text on
            // the Warn colour) so it stands out; otherwise a quiet hint in Warn text.
            void Update()
            {
                hint.IsVisible = model.DebugStatusText.Length > 0;
                text.Text = model.DebugStatusText;
                hint.Background = model.IsDebugPaused ? AppTheme.Brush("Warn") : AppTheme.Brush("BgDeep");
                text.Foreground = model.IsDebugPaused ? AppTheme.Brush("BgDeep") : AppTheme.Brush("Warn");
                text.FontWeight = model.IsDebugPaused ? FontWeight.SemiBold : FontWeight.Normal;
            }
            model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(MainViewModel.DebugStatusText) or nameof(MainViewModel.IsDebugPaused))
                    Update();
            };
        }

        // Repaint the gutter/band when breakpoints change or another tab is shown; scroll to the
        // line when the engine stops at a breakpoint.
        model.BreakpointsChanged += RepaintBreakpoints;
        model.DebugLineRequested += (doc, line) => Dispatcher.UIThread.Post(() => ShowPausedLine(doc, line), DispatcherPriority.Background);
        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.SelectedDocument))
                RepaintBreakpoints();
        };
    }

    /// <summary>
    /// Redraws the breakpoint gutter and the editor's background layer (where
    /// <see cref="BreakpointLineRenderer"/> paints). Neither is invalidated automatically because
    /// their data come from delegates, not from Avalonia properties.
    /// </summary>
    void RepaintBreakpoints()
    {
        breakpointMargin?.InvalidateVisual();
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    /// <summary>Scrolls the paused line into view (centred) without stealing the selection.</summary>
    /// <param name="doc">Document that contains the breakpoint; ignored unless it is the shown tab.</param>
    /// <param name="line">1-based editor line of the pause.</param>
    /// <remarks>The caret is moved to column 1 of the line but the keyboard focus stays where it was.</remarks>
    void ShowPausedLine(DocumentViewModel doc, int line)
    {
        if (vm?.SelectedDocument != doc || line < 1 || line > doc.Document.LineCount)
            return;
        Editor.ScrollTo(line, 1);
        Editor.TextArea.Caret.Line = line;
        Editor.TextArea.Caret.Column = 1;
        RepaintBreakpoints();
    }

    /// <summary>F9 / Shift+F9 always; F5 / F10 only while paused at a breakpoint.</summary>
    /// <remarks>
    /// Registered as a tunnelling handler on the window, so it sees the key before the editor and
    /// before the window's <c>KeyBindings</c>. Setting <c>e.Handled</c> stops the normal F5 (Play)
    /// binding while paused; when not paused F5/F10 fall through untouched.
    /// </remarks>
    void OnDebugKeyDown(object? sender, KeyEventArgs e)
    {
        if (vm is null)
            return;
        switch (e.Key)
        {
            case Key.F9 when e.KeyModifiers == KeyModifiers.Shift:
                vm.ClearAllBreakpointsCommand.Execute(null);
                RepaintBreakpoints();
                e.Handled = true;
                break;
            case Key.F9 when e.KeyModifiers == KeyModifiers.None:
                vm.ToggleBreakpoint(Editor.TextArea.Caret.Line);
                RepaintBreakpoints();
                e.Handled = true;
                break;
            case Key.F5 when vm.IsDebugPaused && e.KeyModifiers == KeyModifiers.None:
                vm.DebugContinueCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.F10 when vm.IsDebugPaused:
                vm.DebugStepCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }
}
