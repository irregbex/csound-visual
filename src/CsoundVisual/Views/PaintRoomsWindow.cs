// PaintRoomsWindow.cs — Paint Rooms: the window that hosts the rooms as tabs.
//
// Role: «Paint Rooms» (Ctrl+Shift+I, MainViewModel.OpenPaintRoomsCommand) is a family of tools to
// compose and design sound with images. Each room is one tab implementing IPaintRoom; they share
// one PaintSession (MainViewModel.PaintSession: image, history, synthesis settings, live player,
// clipboards), so a picture made in one room is available in the others.
//
// TO ADD A ROOM: write a class implementing IPaintRoom (constructor taking a PaintRoomContext) and
// add it to CreateRooms below. Nothing else needs to change.
//
// The window routes key presses (tunnelling, so Space/Ctrl+Z work wherever the focus is, except
// inside text boxes) to the active room, and on close stops the live preview and saves the
// preferences. Built in code, themed, bilingual.
//
// Undo / Redo buttons (header, right): they act on the ACTIVE room's history (IPaintRoom.EditSession
// — each room edits its own session, so each has its own ImageHistory), are greyed out when there is
// nothing to undo / redo, and their tooltips name the step that would be undone / redone. They are
// refreshed when a session reports an edit or a new image, when the tab changes, and by a light
// 4 Hz poll (a history can also change without an event, e.g. when a room clears it).

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Localization;
using CsoundVisual.Paint;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Views;

/// <summary>The Paint Rooms window: one tab per room (see the file header).</summary>
/// <remarks>
/// Non-modal, owned by the main window, which keeps at most one instance open
/// (<c>MainWindow.axaml.cs</c>, <c>vm.ShowPaintRooms</c>). Everything here runs on the UI thread.
/// The rooms are created once per window and disposed when it closes; the shared
/// <see cref="PaintSession"/> outlives the window, so reopening it shows the same picture.
/// </remarks>
public sealed class PaintRoomsWindow : Window
{
    /// <summary>The main view model: owns the shared <see cref="PaintSession"/>, the engine and the preferences.</summary>
    readonly MainViewModel vm;

    /// <summary>The rooms in tab order (from <see cref="CreateRooms"/>).</summary>
    readonly List<IPaintRoom> rooms;

    /// <summary>One <see cref="TabItem"/> per room; each item's <c>Tag</c> holds its <see cref="IPaintRoom"/>.</summary>
    readonly TabControl tabs = new() { Margin = new Thickness(6, 2, 6, 0) };

    /// <summary>The room whose tab is selected, which receives the keyboard shortcuts; <c>null</c> before opening.</summary>
    IPaintRoom? active;

    /// <summary>Header button: undo the active room's last edit (Ctrl+Z).</summary>
    readonly Button undoButton = new() { Classes = { "tool" }, Padding = new Thickness(8, 5), Content = ToolUi.Content("", "IconUndo") };

    /// <summary>Header button: save the active room's work (Ctrl+S), see <see cref="IPaintRoom.SaveChangesAsync"/>.</summary>
    readonly Button saveButton = new() { Classes = { "tool" }, Padding = new Thickness(8, 5), Content = ToolUi.Content("", "IconSave") };

    /// <summary>Header button: redo the active room's last undone edit (Ctrl+Y / Ctrl+Shift+Z).</summary>
    readonly Button redoButton = new() { Classes = { "tool" }, Padding = new Thickness(8, 5), Content = ToolUi.Content("", "IconRedo") };

    /// <summary>Re-checks the Undo / Redo buttons 4 times per second while the window is open.</summary>
    readonly DispatcherTimer historyTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Creates the window for <paramref name="viewModel"/>.</summary>
    /// <param name="viewModel">The application's main view model (source of the shared paint session).</param>
    public PaintRoomsWindow(MainViewModel viewModel)
    {
        vm = viewModel;
        Title = "Csound Visual · Paint Rooms";
        Width = 1440;
        Height = 880;
        MinWidth = 900;
        MinHeight = 560;
        Background = AppTheme.Brush("BgDeep");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // Everything a room needs from the outside world travels in one context object, so rooms do
        // not depend on this window class.
        var context = new PaintRoomContext(vm.PaintSession, vm, this);
        rooms = CreateRooms(context);
        foreach (var room in rooms)
        {
            // The room's Content control is built once by the room and kept alive while its tab is
            // hidden, so switching tabs keeps each room's selections and field values.
            var header = new TextBlock { Text = room.Title, FontSize = 14, FontWeight = FontWeight.SemiBold };
            ToolTip.SetTip(header, room.Tip);
            tabs.Items.Add(new TabItem { Header = header, Content = room.Content, Tag = room });
        }
        tabs.SelectionChanged += (_, _) => Activate(tabs.SelectedItem is TabItem { Tag: IPaintRoom r } ? r : null);

        var title = new TextBlock
        {
            Text = "Paint Rooms", FontSize = 18, FontWeight = FontWeight.Bold, Foreground = AppTheme.Brush("AccentHi"),
            Margin = new Thickness(14, 8, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        var subtitle = new TextBlock
        {
            Text = Loc.S("Compón y diseña sonido pintando: tiempo → derecha, altura ↑, brillo = volumen, rojo/verde = izquierda/derecha. Lo hace Csound.",
                         "Compose and design sound by painting: time → right, pitch ↑, brightness = loudness, red/green = left/right. Csound makes it."),
            FontSize = 12, Foreground = AppTheme.Brush("TextDim"), Margin = new Thickness(14, 10, 14, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // Undo / Redo of the active room (see the file header).
        Avalonia.Automation.AutomationProperties.SetName(undoButton, "Undo");
        Avalonia.Automation.AutomationProperties.SetName(redoButton, "Redo");
        Avalonia.Automation.AutomationProperties.SetName(saveButton, "Save");
        saveButton.Click += (_, _) => _ = SaveActiveAsync();
        undoButton.Click += (_, _) => { active?.Undo(); UpdateHistoryButtons(); };
        redoButton.Click += (_, _) => { active?.Redo(); UpdateHistoryButtons(); };
        // Every distinct session (Paint Synth's is shared, the others are private) refreshes the buttons.
        foreach (var session in rooms.Select(r => r.EditSession).Distinct())
        {
            session.Edited += UpdateHistoryButtons;
            session.ImageReplaced += UpdateHistoryButtons;
        }
        historyTimer.Tick += (_, _) => UpdateHistoryButtons();
        var historyButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 8, 14, 0),
            VerticalAlignment = VerticalAlignment.Center, Children = { saveButton, undoButton, redoButton },
        };
        // Layout: a header strip docked on top (title and subtitle on the left, Undo / Redo on the
        // right), the TabControl filling the rest (last DockPanel child).
        var head = new DockPanel();
        DockPanel.SetDock(historyButtons, Dock.Right);
        head.Children.Add(historyButtons);
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { title, subtitle } });
        var root = new DockPanel();
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);
        root.Children.Add(tabs);
        Content = root;

        // Tunnel = the window sees the key BEFORE the focused control does, so shortcuts such as
        // Space (play) are not swallowed by a focused button or slider.
        AddHandler(KeyDownEvent, OnKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Opened += (_, _) =>
        {
            // The TabControl may already have selected the first tab while the items were added
            // (before SelectionChanged was subscribed), so activate it explicitly.
            tabs.SelectedIndex = 0;
            Activate(rooms.Count > 0 ? rooms[0] : null);
            // A second OnActivated once layout has run (Background priority = after the first
            // measure/arrange pass), for rooms that need their real control sizes (e.g. to fit the
            // canvas to the viewport). Rooms must therefore tolerate OnActivated being called twice.
            Dispatcher.UIThread.Post(() => active?.OnActivated(), DispatcherPriority.Background);
            historyTimer.Start();
            UpdateHistoryButtons();
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            // Rooms release their own resources (timers, analysis tasks, players), then the shared
            // live preview is stopped and the paint preferences are written to settings.
            historyTimer.Stop();
            foreach (var session in rooms.Select(r => r.EditSession).Distinct())
            {
                session.Edited -= UpdateHistoryButtons;
                session.ImageReplaced -= UpdateHistoryButtons;
            }
            foreach (var room in rooms) room.Dispose();
            vm.PaintSession.Player?.Stop();
            vm.SavePaintSettings();
        };
    }

    /// <summary>Set once the unsaved work is resolved: the next <see cref="Window.Closing"/> really closes.</summary>
    bool allowClose;

    /// <summary>True while the «Save changes?» prompt is open (a second close request is ignored).</summary>
    bool closePromptOpen;

    /// <summary>Asks about unsaved room work before closing; the close is retried once it is resolved.</summary>
    /// <param name="sender">This window.</param>
    /// <param name="e">Closing arguments; cancelled while the prompt is pending.</param>
    /// <remarks>
    /// Same pattern as the main window: Closing cannot be awaited, so the first request is
    /// cancelled, the prompt runs, and on «Save» / «Don't save» the window closes again with
    /// <see cref="allowClose"/> set. When the MAIN window closes it calls
    /// <see cref="ConfirmUnsavedAsync"/> first, so nothing is left to ask here by then.
    /// </remarks>
    async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (allowClose || !rooms.Any(r => r.HasUnsavedChanges)) return;
        e.Cancel = true;
        if (closePromptOpen) return;
        closePromptOpen = true;
        try
        {
            if (await ConfirmUnsavedAsync())
            {
                allowClose = true;
                Close();
            }
        }
        finally { closePromptOpen = false; }
    }

    /// <summary>The 💾 button / Ctrl+S: saves the active room's work (the room shows the result in its status line).</summary>
    async Task SaveActiveAsync()
    {
        if (active is { } room) await room.SaveChangesAsync();
        UpdateHistoryButtons();
    }

    /// <summary>Asks «Save changes?» for every room with unsaved work and saves or discards it.</summary>
    /// <returns>False when the user chose Cancel (nothing more is asked; the close must be abandoned).</returns>
    public async Task<bool> ConfirmUnsavedAsync()
    {
        foreach (var room in rooms.Where(r => r.HasUnsavedChanges).ToList())
        {
            var choice = await new ConfirmSaveDialog(room.UnsavedName).ShowDialog<SaveChoice>(this);
            switch (choice)
            {
                case SaveChoice.Save: if (!await room.SaveChangesAsync()) return false; break;
                case SaveChoice.Discard: room.DiscardChanges(); break;
                default: return false;
            }
        }
        return true;
    }

    /// <summary>The rooms, in tab order. Add new rooms here.</summary>
    /// <param name="context">Shared session, view model and owner window handed to every room.</param>
    /// <returns>A new list; the first room is the one shown when the window opens.</returns>
    static List<IPaintRoom> CreateRooms(PaintRoomContext context) =>
    [
        new PaintSynthRoom(context),
        new SpectrumRoom(context),        // sound → picture (analysis) → edit → sound (spectral mask or additive)
        new ImageFilterRoom(context),     // an image as a time-varying stereo EQ (file, shared sound or live input)
        new ImageSequencerRoom(context),  // small painted patterns arranged on a timeline (phrases, loops)
    ];

    /// <summary>The rooms (tests drive them directly).</summary>
    public IReadOnlyList<IPaintRoom> Rooms => rooms;

    /// <summary>Switches the active room, telling the old one it was deactivated and the new one it was activated.</summary>
    /// <param name="room">The room of the newly selected tab, or <c>null</c> when no tab is selected.</param>
    /// <remarks>Idempotent: selecting the room that is already active does nothing.</remarks>
    void Activate(IPaintRoom? room)
    {
        if (ReferenceEquals(room, active)) return;
        active?.OnDeactivated();
        active = room;
        active?.OnActivated();
        // The 💾 tooltip names what this room saves (PNG, WAV, or for the next session).
        ToolTip.SetTip(saveButton, active is null ? null : Loc.S("Guardar (Ctrl+S): ", "Save (Ctrl+S): ") + active.UnsavedName);
        UpdateHistoryButtons();
    }

    /// <summary>
    /// Enables the Undo / Redo buttons from the active room's history and names, in their
    /// tooltips, the step that would be undone / redone.
    /// </summary>
    /// <remarks>
    /// History labels are operation ids («clear», «paste», «pencil»…); an id of a registered image
    /// operation is shown with its menu name in the UI language, anything else as it is.
    /// </remarks>
    void UpdateHistoryButtons()
    {
        undoButton.IsEnabled = active?.CanUndo == true;
        redoButton.IsEnabled = active?.CanRedo == true;
        static string Name(string? label) => label is null ? "" : ": " + (ImageOps.Find(label) is { } op ? Loc.S(op.NameEs, op.NameEn) : label switch
        {
            // Labels written by the session, the brushes and the rooms (anything else is shown as is).
            "select" => Loc.S("cambio de selección", "selection change"),
            "synth" => Loc.S("ajuste de sonido", "sound setting"),
            "duration" => Loc.S("duración", "duration"),
            "filter" => Loc.S("ajuste del filtro", "filter setting"),
            "spectrum" => Loc.S("ajuste del análisis o resíntesis", "analysis or resynthesis setting"),
            "sequencer" => Loc.S("ajuste del arreglo o del sonido", "arrangement or sound setting"),
            "arrangement" => Loc.S("cambio del arreglo", "arrangement change"),
            "paste" => Loc.S("pegar", "paste"),
            "move" => Loc.S("mover la selección", "move the selection"),
            "gradient" => Loc.S("degradado", "gradient"),
            "spectrum.gain" => Loc.S("ganancia", "gain"),
            "retune" => Loc.S("cambio de eje o tamaño", "axis or size change"),
            "import" => Loc.S("importar imagen", "import picture"),
            "pencil" => Loc.S("lápiz", "pencil"), "airbrush" => Loc.S("aerógrafo", "airbrush"), "spray" => "spray",
            "harmonics" => Loc.S("pincel armónico", "harmonics brush"), "line" => Loc.S("línea", "line"),
            "rectangle" => Loc.S("rectángulo", "rectangle"), "ellipse" => Loc.S("elipse", "ellipse"),
            "eraser" => Loc.S("goma", "eraser"), "blur" => Loc.S("desenfocar", "blur"), "smudge" => Loc.S("difuminar", "smudge"),
            _ => label,
        });
        ToolTip.SetTip(undoButton, Loc.S("Deshacer (Ctrl+Z)", "Undo (Ctrl+Z)") + (undoButton.IsEnabled ? Name(active!.UndoLabel) : ""));
        ToolTip.SetTip(redoButton, Loc.S("Rehacer (Ctrl+Y / Ctrl+Shift+Z)", "Redo (Ctrl+Y / Ctrl+Shift+Z)") + (redoButton.IsEnabled ? Name(active!.RedoLabel) : ""));
    }

    /// <summary>Tunnelling key handler: offers every key to the active room first (it ignores keys while a text box has focus).</summary>
    /// <param name="sender">The window.</param>
    /// <param name="e">The key event; marked handled when the room used the key, so the focused control never sees it.</param>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (active is null) return;
        // Ctrl+S saves the active room's work, wherever the focus is.
        if (e.Key == Key.S && e.KeyModifiers == KeyModifiers.Control) { _ = SaveActiveAsync(); e.Handled = true; return; }
        // The room decides what to do while typing (normally: let the text box have Space, letters,
        // Ctrl+Z…), so it is told whether the focus is in a text box (the editable part of a
        // NumericUpDown is a TextBox too, so number fields count as text).
        bool text = FocusManager?.GetFocusedElement() is TextBox;
        if (active.HandleKey(e, text)) e.Handled = true;
    }
}

/// <summary>Small modal dialog asking for one number (image operation parameters, synthesis settings).</summary>
/// <remarks>
/// Used through <see cref="AskAsync"/> only (the constructor is private). Fixed width, height sized to
/// its content, not resizable, centred over the owner.
/// </remarks>
public sealed class PaintParameterDialog : Window
{
    /// <summary>The accepted number, or <c>null</c> while the dialog was not confirmed (Cancel, Esc, close box).</summary>
    double? result;

    /// <summary>Builds the dialog: a caption, one number box and Apply/Cancel (Enter/Esc).</summary>
    /// <param name="title">Window title, also shown as the dialog's heading.</param>
    /// <param name="label">Caption of the number box (usually includes the unit).</param>
    /// <param name="min">Smallest accepted value.</param>
    /// <param name="max">Largest accepted value.</param>
    /// <param name="value">Initial value; also the result when Apply is pressed with an empty box.</param>
    /// <param name="step">Increment of the spinner arrows / mouse wheel.</param>
    /// <param name="format">.NET number format string of the box (e.g. <c>"0.##"</c>).</param>
    PaintParameterDialog(string title, string label, double min, double max, double value, double step, string format)
    {
        Title = title;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Background = AppTheme.Brush("Bg");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var box = ToolUi.Number(min, max, value, step, label, format, 140);
        var ok = new Button { Content = Loc.S("Aplicar", "Apply"), Classes = { "primary" }, MinWidth = 90, IsDefault = true };
        var cancel = new Button { Content = Loc.S("Cancelar", "Cancel"), MinWidth = 90, IsCancel = true };
        ok.Click += (_, _) => { result = box.Value is { } v ? (double)v : value; Close(); };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(18), Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, Foreground = AppTheme.Brush("Text") },
                ToolUi.Captioned(label, box),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } },
            },
        };
        Opened += (_, _) => box.Focus();
    }

    /// <summary>Shows the dialog over <paramref name="owner"/>; returns the number, or <c>null</c> when cancelled.</summary>
    /// <param name="owner">Window the dialog is modal to (its icon is reused).</param>
    /// <param name="title">Window title and heading.</param>
    /// <param name="label">Caption of the number box.</param>
    /// <param name="min">Smallest accepted value.</param>
    /// <param name="max">Largest accepted value.</param>
    /// <param name="value">Initial value.</param>
    /// <param name="step">Spinner increment.</param>
    /// <param name="format">Number format string.</param>
    /// <returns>The chosen number, or <c>null</c> when the user cancelled.</returns>
    public static async Task<double?> AskAsync(Window owner, string title, string label, double min, double max, double value, double step, string format)
    {
        var d = new PaintParameterDialog(title, label, min, max, value, step, format) { Icon = owner.Icon };
        await d.ShowDialog(owner);
        return d.result;
    }
}
