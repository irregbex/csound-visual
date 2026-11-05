using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using CsoundVisual.Engine;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>
/// Debug panel shown over the right side of the editor when a breakpoint stops the performance:
/// the instrument instance (name, p1–p3, time, which note), the buttons Continue (F5), Next
/// cycle (F10), Remove all breakpoints (Shift+F9) and Stop, and a table with every local and
/// global variable (name, type, value; a-signals as RMS/peak plus their first samples, arrays and
/// f-signals summarised). Built in code; it follows <see cref="MainViewModel.DebugHit"/>.
/// </summary>
/// <remarks>
/// <para>
/// Created and placed by <c>MainWindow.AttachDebugView</c> (<c>MainWindow.Breakpoints.cs</c>) in
/// the same grid cell as the editor's hover overlay, right-aligned with a fixed width of 400 DIP.
/// It has no bindings: it listens to <see cref="INotifyPropertyChanged.PropertyChanged"/> of the
/// view model and rebuilds its text and the variables table in <see cref="Refresh"/>. The
/// variables are a snapshot (<see cref="BreakpointHit"/>) taken by the engine at the stop, so the
/// table is rebuilt only when a new hit arrives, not continuously.
/// </para>
/// <para>
/// Visibility rules: shown when <see cref="MainViewModel.IsDebugPanelVisible"/> and a hit exists.
/// After Continue the panel stays up with "playing (last pause)" so the values can still be
/// read; it can only be closed while not paused (closing while paused would hide the controls
/// that resume the performance).
/// </para>
/// </remarks>
public sealed class DebugPanel : Border
{
    /// <summary>The application view model (debug state and commands).</summary>
    readonly MainViewModel vm;
    /// <summary>"instr N · line L · before «opcode»".</summary>
    readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    /// <summary>"note i of n · " plus the instance timing (p2/p3/time), from <see cref="MainViewModel.FormatHitTiming"/>.</summary>
    readonly TextBlock subtitle = new() { FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
    /// <summary>Header state text: paused (audio silent) or playing since the last pause.</summary>
    readonly TextBlock state = new() { FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Content of the scroller: section captions and one row per variable.</summary>
    readonly StackPanel rows = new() { Spacing = 0 };
    /// <summary>Buttons whose enabled state depends on <see cref="MainViewModel.IsDebugPaused"/>.</summary>
    readonly Button continueButton, stepButton, closeButton;

    /// <summary>Creates the panel for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The main view model; the panel subscribes to its property changes for its whole life.</param>
    public DebugPanel(MainViewModel viewModel)
    {
        vm = viewModel;
        Width = 400;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Stretch;
        Margin = new Thickness(0, 8, 14, 8);
        Padding = new Thickness(14, 10, 10, 10);
        CornerRadius = new CornerRadius(10);
        Background = AppTheme.Brush("Surface2_F2");
        BorderBrush = AppTheme.Brush("Warn");
        BorderThickness = new Thickness(1.5);
        BoxShadow = BoxShadows.Parse("0 10 30 0 #A0000000");
        IsVisible = false;

        // Row 0 — header: "⏸ DEBUG" label | state text (stretches) | close button.
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        header.Children.Add(new TextBlock
        {
            Text = Loc.S("⏸ DEPURAR", "⏸ DEBUG"), FontSize = 11, FontWeight = FontWeight.Bold, LetterSpacing = 1.2,
            Foreground = AppTheme.Brush("Warn"), VerticalAlignment = VerticalAlignment.Center,
        });
        Grid.SetColumn(state, 1);
        state.Margin = new Thickness(10, 0, 0, 0);
        header.Children.Add(state);
        closeButton = new Button { Classes = { "tool" }, Padding = new Thickness(6), Content = ToolUi.Content("", "IconClose") };
        ToolTip.SetTip(closeButton, Loc.S("Cerrar el panel (se abre de nuevo en la próxima pausa)", "Close the panel (it opens again at the next pause)"));
        closeButton.Click += (_, _) => vm.CloseDebugPanelCommand.Execute(null);
        Grid.SetColumn(closeButton, 2);
        header.Children.Add(closeButton);

        // Row 3 — action buttons in a WrapPanel so they reflow on two lines in the 400 DIP width.
        continueButton = ToolUi.Button(Loc.S("Continuar (F5)", "Continue (F5)"),
            Loc.S("Sigue tocando hasta que una nota llegue a un breakpoint donde aún no se detuvo (otra línea, otra nota o una nota nueva).",
                  "Keeps playing until a note reaches a breakpoint where it has not stopped yet (another line, another note or a new note)."),
            () => vm.DebugContinueCommand.Execute(null), primary: true);
        stepButton = ToolUi.Button(Loc.S("Siguiente ciclo (F10)", "Next cycle (F10)"),
            Loc.S("Avanza hasta el próximo breakpoint, como mucho la misma línea un ciclo de control (ksmps) después.",
                  "Runs to the next breakpoint hit: at the latest the same line one control cycle (ksmps) later."),
            () => vm.DebugStepCommand.Execute(null));
        var clearButton = ToolUi.Button(Loc.S("Quitar todos", "Remove all"),
            Loc.S("Quita todos los breakpoints de este archivo y sigue tocando (Shift+F9).", "Removes every breakpoint of this file and keeps playing (Shift+F9)."),
            () => vm.ClearAllBreakpointsCommand.Execute(null));
        var stopButton = ToolUi.Button(Loc.S("Detener", "Stop"), Loc.S("Detiene la interpretación.", "Stops the performance."),
            () => vm.DebugStopCommand.Execute(null), icon: "IconStop");
        var buttons = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 6) };
        foreach (var b in new[] { continueButton, stepButton, clearButton, stopButton })
        {
            b.Margin = new Thickness(0, 0, 6, 6);
            b.Padding = new Thickness(9, 5);
            buttons.Children.Add(b);
        }

        // Rows 1–2: title and subtitle; row 4 (star): the variables table, vertical scroll only so
        // long values wrap instead of widening the panel.
        var scroller = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,*") };
        title.Margin = new Thickness(0, 6, 0, 2);
        title.Foreground = AppTheme.Brush("Text");
        subtitle.Foreground = AppTheme.Brush("TextDim");
        Grid.SetRow(title, 1);
        Grid.SetRow(subtitle, 2);
        Grid.SetRow(buttons, 3);
        Grid.SetRow(scroller, 4);
        layout.Children.Add(header);
        layout.Children.Add(title);
        layout.Children.Add(subtitle);
        layout.Children.Add(buttons);
        layout.Children.Add(scroller);
        Child = layout;

        vm.PropertyChanged += OnViewModelChanged;
        Refresh();
    }

    /// <summary>Refreshes on the four debug properties that affect the panel; ignores the rest.</summary>
    void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.DebugHit) or nameof(MainViewModel.IsDebugPaused)
            or nameof(MainViewModel.IsDebugPanelVisible) or nameof(MainViewModel.DebugLine))
            Refresh();
    }

    /// <summary>Rebuilds the panel from the view model.</summary>
    void Refresh()
    {
        IsVisible = vm.IsDebugPanelVisible && vm.DebugHit is not null;
        continueButton.IsEnabled = stepButton.IsEnabled = vm.IsDebugPaused;
        closeButton.IsEnabled = !vm.IsDebugPaused;
        state.Text = vm.IsDebugPaused ? Loc.S("en pausa · el audio está en silencio", "paused · audio is silent")
                                      : Loc.S("tocando (última pausa)", "playing (last pause)");
        state.Foreground = vm.IsDebugPaused ? AppTheme.Brush("Warn") : AppTheme.Brush("TextDim");
        if (vm.DebugHit is not { } hit)
            return;

        // Prefer the editor line (DebugLine, mapped from Csound's line by the VM); fall back to
        // Csound's own line number when the mapping failed (DebugLine == 0).
        string line = vm.DebugLine > 0 ? Loc.S($"línea {vm.DebugLine}", $"line {vm.DebugLine}") : Loc.S($"línea Csound {hit.Line}", $"Csound line {hit.Line}");
        string opcode = hit.Opcode.Length > 0 ? Loc.S($" · antes de «{hit.Opcode}»", $" · before «{hit.Opcode}»") : "";
        title.Text = $"instr {hit.Instrument} · {line}{opcode}";
        subtitle.Text = Loc.S($"nota {hit.InstanceIndex} de {hit.InstanceCount} · ", $"note {hit.InstanceIndex} of {hit.InstanceCount} · ") +
                        MainViewModel.FormatHitTiming(hit);

        rows.Children.Clear();
        AddSection(Loc.S($"Variables locales ({hit.Locals.Count})", $"Local variables ({hit.Locals.Count})"), hit.Locals);
        if (hit.Globals.Count > 0)
            AddSection(Loc.S($"Globales ({hit.Globals.Count})", $"Globals ({hit.Globals.Count})"), hit.Globals);
        if (hit.Locals.Count == 0 && hit.Globals.Count == 0)
            rows.Children.Add(new TextBlock { Text = Loc.S("Sin variables.", "No variables."), Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(0, 6) });
    }

    /// <summary>
    /// Appends a caption and one zebra-striped row per variable to <see cref="rows"/>.
    /// Each row is a 3-column grid — name (110 DIP, ellipsis + tooltip), rate letter (46 DIP,
    /// coloured like the editor) and value (wraps, selectable) — with an optional second line
    /// spanning all columns for <see cref="DebugVariableValue.Detail"/> (first samples of an
    /// a-signal, array contents…).
    /// </summary>
    /// <param name="caption">Section title, shown upper-case.</param>
    /// <param name="variables">Variables of the snapshot (locals or globals).</param>
    void AddSection(string caption, IReadOnlyList<DebugVariableValue> variables)
    {
        rows.Children.Add(new TextBlock
        {
            Text = caption.ToUpperInvariant(), FontSize = 10.5, FontWeight = FontWeight.Bold, LetterSpacing = 1,
            Foreground = AppTheme.Brush("AccentHi"), Margin = new Thickness(0, 8, 0, 4),
        });
        // Same monospaced font as the editor (resource "CodeFont" in Styles/Theme.axaml).
        var code = Application.Current?.TryFindResource("CodeFont", out var f) == true && f is FontFamily ff ? ff : FontFamily.Default;
        bool odd = false;
        foreach (var v in variables)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,46,*"), RowDefinitions = new RowDefinitions("Auto,Auto") };
            var name = new TextBlock { Text = v.Name, FontFamily = code, FontSize = 12.5, Foreground = AppTheme.Brush("Text"), TextTrimming = TextTrimming.CharacterEllipsis };
            var type = new TextBlock { Text = v.Type, FontFamily = code, FontSize = 11.5, Foreground = TypeBrush(v.Type), Margin = new Thickness(6, 0, 0, 0) };
            var value = new SelectableTextBlock { Text = v.Value, FontFamily = code, FontSize = 12.5, Foreground = AppTheme.Brush("Good"), TextWrapping = TextWrapping.Wrap };
            ToolTip.SetTip(name, v.Name);
            Grid.SetColumn(type, 1);
            Grid.SetColumn(value, 2);
            grid.Children.Add(name);
            grid.Children.Add(type);
            grid.Children.Add(value);
            if (v.Detail.Length > 0)
            {
                var detail = new SelectableTextBlock { Text = v.Detail, FontFamily = code, FontSize = 11, Foreground = AppTheme.Brush("TextDim"), TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(detail, 1);
                Grid.SetColumn(detail, 0);
                Grid.SetColumnSpan(detail, 3);
                grid.Children.Add(detail);
            }
            rows.Children.Add(new Border
            {
                Child = grid, Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(4),
                Background = odd ? AppTheme.Brush("Bg") : Brushes.Transparent,
            });
            odd = !odd;
        }
    }

    /// <summary>Same colours as the editor uses for each variable rate.</summary>
    /// <param name="type">Csound type letter: a, k, i, S or f (anything else is dimmed).</param>
    /// <returns>A theme brush (recoloured in place when the theme changes).</returns>
    static IBrush TypeBrush(string type) => type switch
    {
        "a" => AppTheme.Brush("SynARate"),
        "k" => AppTheme.Brush("SynKRate"),
        "i" => AppTheme.Brush("SynIRate"),
        "S" => AppTheme.Brush("SynSVar"),
        "f" => AppTheme.Brush("SynFVar"),
        _ => AppTheme.Brush("TextDim"),
    };
}
