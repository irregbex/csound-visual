using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CsoundVisual.Controls;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;

namespace CsoundVisual.Cabbage;

/// <summary>
/// Draws the widgets of a Cabbage form with Avalonia controls: absolute positions on a canvas the
/// size of the form, scaled to fit by a <see cref="Viewbox"/>.
/// </summary>
/// <remarks>
/// <para>
/// Every widget channel is linked to the document's <see cref="ChannelControlViewModel"/> of the
/// same name (the ordinary sliders of the side panel). Moving a widget sets that view model's
/// value, so the value takes the usual path — the running Csound (<c>SendChannelValue</c>), the
/// VST3 plugin when linked, the initial values pushed when Csound starts — and the widget follows
/// the view model when a slider, MIDI learn or the editor's drag-to-change moves it.
/// </para>
/// <para>
/// Subscriptions are made while the panel is in a visual tree and removed when it leaves it, so a
/// rebuilt panel never leaks handlers on the view models.
/// </para>
/// <para>
/// <b>Built in code, not AXAML</b>: the control tree depends entirely on the parsed form. The panel
/// is immutable once built; when the document's <see cref="DocumentViewModel.CabbageForm"/>
/// changes, the host (the side-panel "Cabbage UI" card, <see cref="CabbageCard"/>, or
/// <c>Views/CabbageWindow</c>) creates a new panel. Widget bounds are in Cabbage pixels on the
/// canvas; the <see cref="Viewbox"/> does the scaling, so no control needs to know the zoom.
/// </para>
/// <para>
/// Threading: everything runs on the UI thread. View-model change notifications are expected
/// there too, but <see cref="OnChannelChanged"/> defensively posts to the UI thread when one
/// arrives from another thread.
/// </para>
/// </remarks>
public sealed class CabbagePanel : Decorator
{
    /// <summary>The form being drawn.</summary>
    readonly CabbageForm form;
    /// <summary>Document that owns the channel view models.</summary>
    readonly DocumentViewModel doc;
    /// <summary>Main view model for keyboard note routing; null in headless tests.</summary>
    readonly MainViewModel? main;
    /// <summary>Folder of the .csd, used to resolve relative <c>image file(…)</c> paths (null for unsaved documents).</summary>
    readonly string? folder;
    /// <summary>
    /// View model → setter pairs: when a channel view model's <c>Value</c> changes, every setter
    /// registered for it updates its widget (one channel can drive several widgets).
    /// </summary>
    readonly List<(ChannelControlViewModel Vm, Action<double> Apply)> links = [];
    /// <summary>Value controls that belong to a <c>radioGroup</c>, for mutual exclusion.</summary>
    readonly List<(CabbageWidget Widget, CabbageValueControl Control)> radio = [];
    /// <summary>Cabbage <c>keyboard</c> widgets, lit by external MIDI notes and released on detach.</summary>
    readonly List<PianoKeyboard> keyboards = [];
    /// <summary>True while subscribed to the view models (between attach and detach).</summary>
    bool attached;

    /// <summary>Builds the panel for <paramref name="form"/> of <paramref name="doc"/>.</summary>
    /// <param name="form">Parsed Cabbage section.</param>
    /// <param name="doc">Document whose channels the widgets drive.</param>
    /// <param name="main">Main view model (on-screen keyboard notes); may be null in tests.</param>
    public CabbagePanel(CabbageForm form, DocumentViewModel doc, MainViewModel? main)
    {
        this.form = form;
        this.doc = doc;
        this.main = main;
        folder = doc.FilePath is { } p ? Path.GetDirectoryName(p) : null;

        // One canvas in form coordinates (at least 10×10 so the Viewbox never divides by zero);
        // the form colour, or the theme's deepest background, fills it.
        var canvas = new Canvas
        {
            Width = Math.Max(10, form.Width),
            Height = Math.Max(10, form.Height),
            Background = CabbageColours.Brush(form.Colour, AppTheme.ColorOf("BgDeep")),
            ClipToBounds = true,
        };
        foreach (var w in form.Widgets)
            Add(canvas, w);
        // Uniform: keeps the form's aspect ratio whatever the size of the card/window.
        Child = new Viewbox { Stretch = Stretch.Uniform, Child = canvas };
    }

    /// <summary>Number of widgets drawn (placeholders included).</summary>
    public int WidgetCount { get; private set; }

    /// <summary>The value controls by channel name (used by tests).</summary>
    public Dictionary<string, Control> ControlsByChannel { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds one widget (and, recursively, its plant children), positions it on
    /// <paramref name="parent"/> at its Cabbage bounds and applies the common identifiers
    /// (alpha, active, a tooltip naming the channels).
    /// </summary>
    /// <param name="parent">Canvas in the parent's coordinate space.</param>
    /// <param name="w">Widget to add; invisible widgets (and their children) are skipped.</param>
    void Add(Canvas parent, CabbageWidget w)
    {
        if (!w.Visible) return;
        var b = w.Bounds;
        Control? c = Build(w);
        if (c is null) return;
        WidgetCount++;
        c.Width = Math.Max(1, b.Width);
        c.Height = Math.Max(1, b.Height);
        c.Opacity = w.Alpha;
        // active(0): shown but not interactive (display-only).
        if (!w.Active) c.IsHitTestVisible = false;
        Canvas.SetLeft(c, b.X);
        Canvas.SetTop(c, b.Y);
        string tip = w.Channels.Count > 0
            ? $"{(w.Label.Length > 0 ? w.Label + " · " : "")}{w.Type} «{string.Join("», «", w.Channels)}»"
            : "";
        // Comboboxes set their own tooltip in BuildCombo.
        if (tip.Length > 0 && c is not ComboBox) ToolTip.SetTip(c, tip);
        parent.Children.Add(c);

        if (w.Children.Count > 0)
        {
            // Plants: children are positioned relative to the parent and clipped by it.
            // The children go on a sibling canvas laid exactly over the parent control (added
            // after it, so on top), because most parent controls are not panels.
            var inner = new Canvas { Width = c.Width, Height = c.Height, ClipToBounds = true, Opacity = w.Alpha };
            Canvas.SetLeft(inner, b.X);
            Canvas.SetTop(inner, b.Y);
            foreach (var child in w.Children)
                Add(inner, child);
            parent.Children.Add(inner);
        }
    }

    /// <summary>
    /// Creates the Avalonia control for one widget type. Value widgets go through
    /// <see cref="Bind"/>; decorative ones are hit-test invisible; unsupported types become a
    /// dashed <see cref="CabbagePlaceholder"/> so the layout still shows where they were.
    /// </summary>
    /// <param name="w">The widget.</param>
    /// <returns>The control (never null in the current implementation).</returns>
    Control? Build(CabbageWidget w)
    {
        switch (w.Type)
        {
            case "rslider":
            case "encoder":
            {
                // valueTextBox(1) asks Cabbage to show the number permanently under the knob.
                var k = new CabbageKnob(w) { AlwaysShowValue = w.Identifiers.Any(i => i.Name == "valuetextbox" && i.Args.FirstOrDefault() == "1") };
                return Bind(w, k);
            }
            case "hslider":
            case "vslider":
            case "hrange":
            case "vrange":
            {
                // A range slider needs two channels (low, high); with only one it acts as a plain slider.
                bool isRange = w.Type.EndsWith("range") && w.Channels.Count >= 2;
                bool vertical = w.Type[0] == 'v';
                var s = new CabbageSlider(w, vertical, isRange);
                if (isRange)
                {
                    var r2 = CabbageSupport.RangeOf(w, 1);
                    s.Default2 = r2.Value;
                }
                return Bind(w, s);
            }
            case "nslider":
                return Bind(w, new CabbageNumberBox(w));
            case "button":
                return Bind(w, new CabbageButton(w, CabbageButton.Kind.Button));
            case "checkbox":
                return Bind(w, new CabbageButton(w, CabbageButton.Kind.CheckBox));
            case "optionbutton":
                return Bind(w, new CabbageButton(w, CabbageButton.Kind.Option));
            case "xypad":
                return Bind(w, new CabbageXYPad(w));
            case "combobox":
                return BuildCombo(w);
            case "keyboard":
                return BuildKeyboard(w);
            case "label":
                return BuildLabel(w);
            case "groupbox":
                return BuildGroup(w);
            case "image":
                return BuildImage(w);
            case "line":
                return new Border { Background = CabbageColours.Brush(w.Colour("colour"), AppTheme.ColorOf("TextFaint")) };
            case "csoundoutput":
                return new CabbagePlaceholder(w, Loc.S("Salida de Csound → consola", "Csound output → console"));
            case "hmeter":
            case "vmeter":
                return new CabbagePlaceholder(w, Loc.S("medidor", "meter"));
            case "infobutton":
                // Would open a help file in Cabbage; drawn as an inert button here.
                return new CabbageButton(w, CabbageButton.Kind.Button) { IsHitTestVisible = false };
            default:
                return new CabbagePlaceholder(w, w.Type + (w.Channels.Count > 0 ? $" «{w.Channels[0]}»" : ""));
        }
    }

    /// <summary>Links a drawn value control to its channel view model(s).</summary>
    /// <param name="w">The widget (channels, range, radio group).</param>
    /// <param name="control">The value control built for it.</param>
    /// <returns><paramref name="control"/>, for chaining in <see cref="Build"/>.</returns>
    /// <remarks>
    /// Two directions: view model → control through <see cref="Link"/> (applied in
    /// <see cref="OnChannelChanged"/>), and control → view model through
    /// <see cref="CabbageValueControl.ValueChanged"/> (index 0 = first channel, 1 = second channel
    /// of an XY pad or range slider). Setting the control's <c>Value</c> from the view model does not
    /// raise <c>ValueChanged</c>, so the two directions do not echo.
    /// </remarks>
    Control Bind(CabbageWidget w, CabbageValueControl control)
    {
        control.Range = CabbageSupport.RangeOf(w, 0);
        // A widget without a channel is still drawn and movable, it just drives nothing.
        if (w.Channels.Count == 0)
        {
            control.Value = control.Range.Value;
            return control;
        }

        // Radio group: switching this widget on switches the others of the same group off, both
        // on screen and in their channels. Clearing a sibling sets it to 0, which never clears
        // further, so this cannot recurse.
        void ClearRadioSiblings()
        {
            foreach (var (other, oc) in radio)
                if (other != w && other.RadioGroup == w.RadioGroup)
                {
                    oc.Value = 0;
                    if (other.Channels.Count > 0 && doc.FindChannel(other.Channels[0]) is { } ovm) ovm.Value = 0;
                }
        }

        // View model → control. Bug fix: a radio button turned on from elsewhere (the slider of
        // the same channel in the side panel, MIDI learn, a preset) used to light up without
        // switching its siblings off, leaving two buttons of the group on; it now clears them
        // exactly as a click does.
        var vm0 = Link(w.Channels[0], v =>
        {
            control.Value = v;
            if (w.RadioGroup != 0 && v >= 0.5) ClearRadioSiblings();
        });
        // Start from the live channel value (it may have been moved already), else Cabbage's default.
        control.Value = vm0?.Value ?? control.Range.Value;
        ControlsByChannel.TryAdd(w.Channels[0], control);

        ChannelControlViewModel? vm1 = null;
        if (w.Channels.Count >= 2)
        {
            ControlsByChannel.TryAdd(w.Channels[1], control);
            var r1 = CabbageSupport.RangeOf(w, 1);
            switch (control)
            {
                case CabbageXYPad xy:
                    xy.RangeY = r1;
                    vm1 = Link(w.Channels[1], v => xy.ValueY = v);
                    xy.ValueY = vm1?.Value ?? r1.Value;
                    break;
                case CabbageSlider s:
                    vm1 = Link(w.Channels[1], v => s.Value2 = v);
                    s.Value2 = vm1?.Value ?? r1.Value;
                    break;
            }
        }

        if (w.RadioGroup != 0) radio.Add((w, control));
        control.ValueChanged += (index, v) =>
        {
            var vm = index == 0 ? vm0 : vm1;
            if (vm is not null) vm.Value = v;
            // Radio group (user click): clear the siblings. Also needed here, not only in the
            // link above, because a widget whose channel the document does not declare (vm0 is
            // null) never receives the view model → control callback.
            if (index == 0 && w.RadioGroup != 0 && v >= 0.5)
                ClearRadioSiblings();
        };
        return control;
    }

    /// <summary>Registers <paramref name="apply"/> to run when the channel's value changes.</summary>
    /// <param name="channel">Csound channel name.</param>
    /// <param name="apply">Setter that shows a new value on the widget (called on the UI thread).</param>
    /// <returns>
    /// The document's view model for the channel, or <c>null</c> when the document has no such
    /// channel (the widget then works locally only).
    /// </returns>
    ChannelControlViewModel? Link(string channel, Action<double> apply)
    {
        var vm = doc.FindChannel(channel);
        if (vm is not null) links.Add((vm, apply));
        return vm;
    }

    /// <summary>
    /// A combobox is a real Avalonia <see cref="ComboBox"/> (its drop-down needs a popup), not a
    /// <see cref="CabbageValueControl"/>, so it is linked to its channel here rather than in
    /// <see cref="Bind"/>. The font scales with the widget height.
    /// </summary>
    /// <param name="w">The combobox widget.</param>
    /// <returns>The combo box.</returns>
    Control BuildCombo(CabbageWidget w)
    {
        var choices = w.Choices.Count > 0 ? w.Choices.ToList() : [Loc.S("(vacío)", "(empty)")];
        double h = Math.Max(10, w.Bounds.Height);
        var combo = new ComboBox
        {
            ItemsSource = choices,
            FontSize = Math.Clamp(h * 0.5, 7, 16),
            MinHeight = 0,
            MinWidth = 0,
            Padding = new Thickness(6, 0, 2, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        if (w.Colour("colour") is { } bg) combo.Background = CabbageColours.Brush(bg, AppTheme.ColorOf("Surface2"));
        if (w.Colour("fontcolour", "textcolour") is { } fg) combo.Foreground = CabbageColours.Brush(fg, AppTheme.ColorOf("Text"));
        if (w.Channels.Count == 0) { combo.SelectedIndex = Math.Clamp((int)(w.Value ?? 1) - 1, 0, choices.Count - 1); return combo; }

        // Cabbage comboboxes send the 1-based index of the chosen item.
        // 'updating' suppresses SelectionChanged while the view model drives the selection, so a
        // channel change is not written straight back to the channel.
        bool updating = false;
        void Show(double v)
        {
            updating = true;
            combo.SelectedIndex = Math.Clamp((int)Math.Round(v) - 1, 0, choices.Count - 1);
            updating = false;
        }
        var vm = Link(w.Channels[0], Show);
        Show(vm?.Value ?? CabbageSupport.RangeOf(w).Value);
        combo.SelectionChanged += (_, _) =>
        {
            if (!updating && combo.SelectedIndex >= 0 && vm is not null)
                vm.Value = combo.SelectedIndex + 1;
        };
        ToolTip.SetTip(combo, $"{w.Type} «{w.Channels[0]}»");
        ControlsByChannel.TryAdd(w.Channels[0], combo);
        return combo;
    }

    /// <summary>
    /// A Cabbage <c>keyboard</c> becomes the app's <see cref="PianoKeyboard"/>. Its notes go to the
    /// same MIDI path as the main on-screen keyboard (<see cref="MainViewModel.KeyboardNoteOn"/>),
    /// so they reach Csound's MIDI input. <c>value(n)</c> sets the lowest note (default 48 = C3).
    /// </summary>
    /// <param name="w">The keyboard widget.</param>
    /// <returns>The keyboard control.</returns>
    Control BuildKeyboard(CabbageWidget w)
    {
        var kb = new PianoKeyboard { LowestNote = (int)Math.Clamp(w.Value ?? 48, 0, 108) };
        ToolTip.SetTip(kb, Loc.S("Teclado de Cabbage: toca notas MIDI en el instrumento (con Csound sonando).",
                                 "Cabbage keyboard: plays MIDI notes into the instrument (while Csound plays)."));
        if (main is not null)
        {
            kb.NoteOn += main.KeyboardNoteOn;
            kb.NoteOff += main.KeyboardNoteOff;
        }
        keyboards.Add(kb);
        return kb;
    }

    /// <summary>
    /// A static text. Without <c>fontSize(…)</c> the text fills 80 % of the widget height, as in
    /// Cabbage, where a label's height sets its font size.
    /// </summary>
    /// <param name="w">The label widget.</param>
    /// <returns>A non-interactive border holding the text.</returns>
    Control BuildLabel(CabbageWidget w)
    {
        double h = Math.Max(4, w.Bounds.Height);
        var tb = new TextBlock
        {
            Text = w.Label,
            FontSize = double.IsNaN(w.FontSize) || w.FontSize <= 0 ? Math.Max(5, h * 0.8) : w.FontSize,
            Foreground = CabbageColours.Brush(w.Colour("fontcolour", "textcolour"), AppTheme.ColorOf("Text")),
            TextAlignment = w.Align switch { "left" => TextAlignment.Left, "right" => TextAlignment.Right, _ => TextAlignment.Center },
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
        };
        return new Border
        {
            Background = w.Colour("colour") is { } c ? CabbageColours.Brush(c, Colors.Transparent) : Brushes.Transparent,
            CornerRadius = new CornerRadius(double.IsNaN(w.Corners) ? 0 : w.Corners),
            Child = tb,
            IsHitTestVisible = false,
        };
    }

    /// <summary>
    /// A groupbox: rounded frame with an optional centred caption and a rule under it. Its
    /// children are added separately by <see cref="Add"/> on a canvas laid over it.
    /// </summary>
    /// <param name="w">The groupbox widget.</param>
    /// <returns>A non-interactive border.</returns>
    Control BuildGroup(CabbageWidget w)
    {
        var bg = CabbageColours.Resolve(w.Colour("colour"), AppTheme.ColorOf("Surface"));
        string caption = w.Caption ?? (w.Text.Count > 0 ? w.Text[0] : "");
        var outline = CabbageColours.Resolve(w.Colour("outlinecolour"), AppTheme.ColorOf("Line"));
        double thickness = double.IsNaN(w.OutlineThickness) ? 1 : w.OutlineThickness;
        var stack = new DockPanel { LastChildFill = false };
        if (caption.Length > 0)
        {
            var title = new TextBlock
            {
                Text = caption,
                FontSize = Math.Clamp(w.Bounds.Height * 0.12, 10, 15),
                FontWeight = FontWeight.SemiBold,
                Foreground = CabbageColours.Brush(w.Colour("fontcolour", "textcolour"), AppTheme.ColorOf("Text")),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
            };
            DockPanel.SetDock(title, Dock.Top);
            stack.Children.Add(title);
            var rule = new Border { Height = 1, Background = new SolidColorBrush(outline), Margin = new Thickness(6, 0) };
            DockPanel.SetDock(rule, Dock.Top);
            stack.Children.Add(rule);
        }
        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(outline),
            BorderThickness = new Thickness(thickness),
            CornerRadius = new CornerRadius(double.IsNaN(w.Corners) ? 5 : w.Corners),
            Child = stack,
            IsHitTestVisible = false,
        };
    }

    /// <summary>
    /// An <c>image</c>: a filled rectangle/rounded rectangle/ellipse, with the picture from
    /// <c>file(…)</c> (resolved next to the .csd; PNG, JPEG, BMP or GIF) stretched over it when it
    /// can be loaded. Cabbage uses plain images as coloured shapes and as plant backgrounds.
    /// </summary>
    /// <param name="w">The image widget.</param>
    /// <returns>A non-interactive shape.</returns>
    Control BuildImage(CabbageWidget w)
    {
        var bg = CabbageColours.Resolve(w.Colour("colour"), Colors.Transparent);
        var outline = CabbageColours.Resolve(w.Colour("outlinecolour"), Colors.Transparent);
        double thickness = double.IsNaN(w.OutlineThickness) ? 0 : w.OutlineThickness;
        bool ellipse = w.Shape is "ellipse" or "circle";
        double radius = !double.IsNaN(w.Corners) ? w.Corners : w.Shape == "rounded" ? 5 : 0;
        Control? picture = null;
        if (w.File is { Length: > 0 } file && folder is not null)
        {
            try
            {
                string path = Path.GetFullPath(Path.Combine(folder, file));
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (File.Exists(path) && ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif")
                    picture = new Image { Source = new Bitmap(path), Stretch = Stretch.Fill };
            }
            catch (Exception)
            {
                // A broken image file just leaves the coloured rectangle.
            }
        }
        if (ellipse)
        {
            var e = new Avalonia.Controls.Shapes.Ellipse { Fill = new SolidColorBrush(bg), Stroke = new SolidColorBrush(outline), StrokeThickness = thickness, IsHitTestVisible = false };
            return picture is null ? e : new Panel { Children = { e, picture }, IsHitTestVisible = false };
        }
        return new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(outline),
            BorderThickness = new Thickness(thickness),
            CornerRadius = new CornerRadius(radius),
            ClipToBounds = true,
            Child = picture,
            IsHitTestVisible = false,
        };
    }

    /// <summary>
    /// View model → widgets: when a linked channel's <c>Value</c> changes, runs every setter
    /// registered for that view model. Notifications from other threads are posted to the UI thread.
    /// </summary>
    void OnChannelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChannelControlViewModel.Value) || sender is not ChannelControlViewModel vm) return;
        void Apply()
        {
            foreach (var (lvm, apply) in links)
                if (ReferenceEquals(lvm, vm)) apply(vm.Value);
        }
        if (Dispatcher.UIThread.CheckAccess()) Apply();
        else Dispatcher.UIThread.Post(Apply);
    }

    /// <summary>Lights or releases a key on every Cabbage keyboard for a note played from outside (MIDI port, main keyboard).</summary>
    /// <param name="note">MIDI note number.</param>
    /// <param name="on">True for note-on, false for note-off.</param>
    void OnExternalNote(int note, bool on)
    {
        foreach (var kb in keyboards) kb.SetExternal(note, on);
    }

    /// <inheritdoc/>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (attached) return;
        attached = true;
        foreach (var vm in links.Select(l => l.Vm).Distinct())
        {
            vm.PropertyChanged += OnChannelChanged;
            // Catch up with changes made while detached.
            OnChannelChanged(vm, new PropertyChangedEventArgs(nameof(ChannelControlViewModel.Value)));
        }
        if (main is not null && keyboards.Count > 0) main.ExternalNote += OnExternalNote;
    }

    /// <inheritdoc/>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (!attached) return;
        attached = false;
        foreach (var vm in links.Select(l => l.Vm).Distinct())
            vm.PropertyChanged -= OnChannelChanged;
        if (main is not null && keyboards.Count > 0) main.ExternalNote -= OnExternalNote;
        // Release held keys so no note hangs when the panel is closed or rebuilt mid-press.
        foreach (var kb in keyboards) kb.ReleaseAll();
    }

    /// <summary>A one-line summary of what could not be drawn, or "" when everything is supported.</summary>
    /// <param name="form">Parsed form.</param>
    /// <returns>Localised text such as "Not drawn (dashed boxes): gentable ×2, soundfiler".</returns>
    public static string Summary(CabbageForm form)
    {
        if (form.UnsupportedTypes.Count == 0) return "";
        string list = string.Join(", ", form.UnsupportedTypes.Select(kv => kv.Value > 1 ? $"{kv.Key} ×{kv.Value}" : kv.Key));
        return Loc.S($"Sin dibujar (marcos punteados): {list}", $"Not drawn (dashed boxes): {list}");
    }
}
