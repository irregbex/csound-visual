using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CsoundVisual.Localization;
using CsoundVisual.Theming;
using CsoundVisual.ViewModels;
using CsoundVisual.Views;

namespace CsoundVisual.Cabbage;

/// <summary>
/// Follows <see cref="MainViewModel.SelectedDocument"/> and raises <see cref="Changed"/> whenever
/// the Cabbage panel of the shown document must be rebuilt: another document is shown, its
/// <c>&lt;Cabbage&gt;</c> section changed, or its channel list changed.
/// </summary>
/// <remarks>
/// Shared by the side-panel <see cref="CabbageCard"/> and the pop-out <see cref="CabbageWindow"/>
/// so both rebuild under the same rules. A rebuild is needed when the channel list changes because
/// <see cref="CabbagePanel"/> links each widget to a specific <see cref="ChannelControlViewModel"/>
/// instance at construction; when the document re-detects its channels, those instances are
/// replaced. A file path change matters because relative image paths resolve against the folder.
/// </remarks>
public sealed class CabbageTracker : IDisposable
{
    /// <summary>Main view model whose <see cref="MainViewModel.SelectedDocument"/> is followed.</summary>
    readonly MainViewModel vm;
    /// <summary>Document currently watched (its property and channel-collection events are subscribed).</summary>
    DocumentViewModel? doc;
    /// <summary>True while a <see cref="Changed"/> notification is already queued on the dispatcher.</summary>
    bool pending;

    /// <summary>Starts tracking <paramref name="vm"/>.</summary>
    public CabbageTracker(MainViewModel vm)
    {
        this.vm = vm;
        vm.PropertyChanged += OnMainChanged;
        Watch(vm.SelectedDocument);
    }

    /// <summary>The shown document.</summary>
    public DocumentViewModel? Document => doc;

    /// <summary>Its Cabbage form, or null.</summary>
    public CabbageForm? Form => doc?.CabbageForm;

    /// <summary>Raised (coalesced, on the UI thread) when the panel must be rebuilt.</summary>
    public event Action? Changed;

    /// <summary>Switches to the newly selected document tab.</summary>
    void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedDocument))
            Watch(vm.SelectedDocument);
    }

    /// <summary>Moves the subscriptions from the current document to <paramref name="next"/> (null = none) and requests a rebuild.</summary>
    void Watch(DocumentViewModel? next)
    {
        if (doc is not null)
        {
            doc.PropertyChanged -= OnDocChanged;
            doc.Channels.CollectionChanged -= OnChannelsChanged;
        }
        doc = next;
        if (doc is not null)
        {
            doc.PropertyChanged += OnDocChanged;
            doc.Channels.CollectionChanged += OnChannelsChanged;
        }
        Raise();
    }

    /// <summary>Rebuilds when the document's form or its path changes (other properties, e.g. text or dirty flag, are ignored).</summary>
    void OnDocChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DocumentViewModel.CabbageForm) || e.PropertyName == nameof(DocumentViewModel.FilePath))
            Raise();
    }

    /// <summary>Any change of the document's channel list (new view model instances) needs a rebuild.</summary>
    void OnChannelsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Raise();

    /// <summary>Coalesces bursts (Channels.Clear + Add…) into one rebuild.</summary>
    /// <remarks>
    /// The first call posts one callback at <see cref="DispatcherPriority.Background"/> (after the
    /// current batch of bindings and layout); later calls before it runs are absorbed by
    /// <see cref="pending"/>. Re-detecting channels clears and refills the collection, i.e. one event
    /// per channel, so without this the panel would be rebuilt dozens of times in a row. The post
    /// also moves the notification to the UI thread whatever thread raised the change.
    /// </remarks>
    void Raise()
    {
        if (pending) return;
        pending = true;
        Dispatcher.UIThread.Post(() =>
        {
            pending = false;
            Changed?.Invoke();
        }, DispatcherPriority.Background);
    }

    /// <summary>Removes every subscription. A rebuild already queued may still run, but with no listeners.</summary>
    public void Dispose()
    {
        vm.PropertyChanged -= OnMainChanged;
        Watch(null);
        // Clear listeners after Watch(null), whose queued Raise must not reach a disposed owner.
        Changed = null;
    }
}

/// <summary>
/// Side-panel card «Cabbage UI / Interfaz Cabbage»: shows the Cabbage widgets of the shown
/// document (hidden for ordinary documents) with a button that pops them out to a resizable
/// <see cref="CabbageWindow"/>. Its data context is the <see cref="MainViewModel"/>.
/// </summary>
/// <remarks>
/// <para>
/// Built in code (it is placed in <c>MainWindow.axaml</c> as a single element) because its content
/// is a <see cref="CabbagePanel"/> rebuilt from scratch whenever <see cref="CabbageTracker"/>
/// says so; style classes (<c>card</c>, <c>section</c>, <c>dim</c>, <c>tool</c>) come from the app theme.
/// </para>
/// <para>
/// Layout: a header row (title · caption · "⧉ Window" button), the panel host (height capped at
/// 460 so a tall form does not push the rest of the side panel away; the panel's Viewbox scales
/// it down), and a summary line listing widget types that are only drawn as placeholders (its
/// tooltip lists the first parse warnings). The title switches to "KNOBS (;@)" for forms built
/// from annotations. Texts are bilingual through <see cref="Loc.S"/> and refreshed on
/// <see cref="Loc.Changed"/>; a theme change rebuilds the panel because widget brushes are
/// resolved once.
/// </para>
/// </remarks>
public sealed class CabbageCard : Border
{
    /// <summary>Form caption (or "from ;@knob comments"), trimmed with an ellipsis; full text in its tooltip.</summary>
    readonly TextBlock caption = new() { Classes = { "dim" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    /// <summary>Card title ("CABBAGE UI" or "KNOBS (;@)").</summary>
    readonly TextBlock title = new() { Classes = { "section" }, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>"Not drawn: …" line; hidden when every widget is supported.</summary>
    readonly TextBlock summary = new() { Classes = { "dim" }, FontSize = 10.5, TextWrapping = TextWrapping.Wrap };
    /// <summary>Opens the pop-out <see cref="CabbageWindow"/>.</summary>
    readonly Button popOut = new() { Classes = { "tool" }, Padding = new Thickness(6, 2), FontSize = 11 };
    /// <summary>Holds the current <see cref="CabbagePanel"/>; replaced on each rebuild.</summary>
    readonly Border host = new() { CornerRadius = new CornerRadius(6), ClipToBounds = true, MaxHeight = 460 };
    /// <summary>Follows the selected document; null until a <see cref="MainViewModel"/> data context arrives.</summary>
    CabbageTracker? tracker;
    /// <summary>The data context, as a main view model.</summary>
    MainViewModel? vm;
    /// <summary>The pop-out window, if one was opened (reused while visible).</summary>
    CabbageWindow? window;

    /// <summary>Creates the card (hidden until a Cabbage document is shown).</summary>
    public CabbageCard()
    {
        Classes.Add("card");
        IsVisible = false;
        popOut.Click += (_, _) => PopOut();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(title);
        Grid.SetColumn(caption, 1);
        header.Children.Add(caption);
        Grid.SetColumn(popOut, 2);
        header.Children.Add(popOut);
        Child = new StackPanel { Spacing = 8, Children = { header, host, summary } };
        Texts();
        // Static events, never unsubscribed: the card lives as long as the main window.
        Loc.Changed += OnLanguageChanged;
        AppTheme.Changed += Rebuild;
    }

    /// <summary>Language switched: refresh the fixed texts and rebuild (placeholder captions are localised too).</summary>
    void OnLanguageChanged()
    {
        Texts();
        Rebuild();
    }

    /// <summary>Sets the localised fixed texts and tooltips (title is overridden by <see cref="Rebuild"/> for annotation forms).</summary>
    void Texts()
    {
        title.Text = Loc.S("INTERFAZ CABBAGE", "CABBAGE UI");
        popOut.Content = Loc.S("⧉ Ventana", "⧉ Window");
        ToolTip.SetTip(popOut, Loc.S("Abre la interfaz Cabbage en una ventana propia que puedes agrandar.",
                                     "Opens the Cabbage UI in its own window, which you can resize."));
        ToolTip.SetTip(title, Loc.S("Controles declarados en la sección <Cabbage> del archivo. Mueven los mismos canales que los sliders de abajo (MIDI learn incluido).",
                                    "Widgets declared in the file's <Cabbage> section. They move the same channels as the sliders below (MIDI learn included)."));
    }

    /// <inheritdoc/>
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // Data context inherited from the main window; replace the tracker only on a real change.
        if (ReferenceEquals(DataContext, vm)) return;
        tracker?.Dispose();
        tracker = null;
        vm = DataContext as MainViewModel;
        if (vm is not null)
        {
            tracker = new CabbageTracker(vm);
            tracker.Changed += Rebuild;
        }
        Rebuild();
    }

    /// <summary>
    /// Replaces the panel with a fresh <see cref="CabbagePanel"/> for the shown document, or hides
    /// the card when the document has no widgets. The old panel leaves the visual tree and so
    /// unsubscribes itself from the channel view models.
    /// </summary>
    void Rebuild()
    {
        var form = tracker?.Form;
        var doc = tracker?.Document;
        IsVisible = form is not null;
        if (form is null || doc is null)
        {
            host.Child = null;
            return;
        }
        title.Text = form.FromAnnotations ? Loc.S("PERILLAS (;@)", "KNOBS (;@)") : Loc.S("INTERFAZ CABBAGE", "CABBAGE UI");
        caption.Text = form.FromAnnotations ? Loc.S("de los comentarios ;@knob", "from ;@knob comments") : form.Caption;
        ToolTip.SetTip(caption, caption.Text);
        host.Child = new CabbagePanel(form, doc, vm);
        summary.Text = CabbagePanel.Summary(form);
        summary.IsVisible = summary.Text.Length > 0;
        // Tooltip: the first parse warnings of this form. Bug fix: it is always (re)assigned, and
        // cleared (null) when this form has no warnings; before, a form without warnings kept
        // the previous document's tooltip, describing problems that were no longer there.
        ToolTip.SetTip(summary, form.Warnings.Count > 0
            ? string.Join("\n", form.Warnings.Take(12).Select(w => Loc.S("línea ", "line ") + w.Line + ": " + w.Message))
            : null);
    }

    /// <summary>Opens (or brings to front) the pop-out window.</summary>
    /// <remarks>
    /// The window is owned by the main window when there is one (stays above it, closes with it,
    /// shares its icon). It follows the selected document on its own, independently of this card.
    /// </remarks>
    public void PopOut()
    {
        if (vm is null) return;
        if (window is { IsVisible: true })
        {
            window.Activate();
            return;
        }
        window = new CabbageWindow(vm);
        if (TopLevel.GetTopLevel(this) is Window owner)
        {
            window.Icon = owner.Icon;
            window.Show(owner);
        }
        else
            window.Show();
    }
}
