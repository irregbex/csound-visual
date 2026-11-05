using System.Collections.ObjectModel;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CsoundVisual.Editor;
using CsoundVisual.Engine;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>
/// One open file (one editor tab): its text, path, dirty state, the channel sliders detected in
/// it and the lines Csound reported errors for.
/// </summary>
/// <remarks>
/// <para>
/// <b>Role.</b> <see cref="MainViewModel"/> owns a collection of these (one per tab) and a
/// <c>SelectedDocument</c>; the editor in <c>MainWindow</c> shows the selected one's
/// <see cref="Document"/>. A document is plain state: it never starts Csound itself. When the
/// main view model plays it, it sets <see cref="IsPlaying"/>, and only then do slider moves
/// reach the engine (see <see cref="Send"/>), so tweaking a slider in a tab that is not
/// playing cannot disturb the performance of another tab.
/// </para>
/// <para>
/// <b>Partial files.</b> <c>DocumentViewModel.Cabbage.cs</c> adds the parsed Cabbage /
/// <c>;@knob</c> widget form; <c>DocumentViewModel.Breakpoints.cs</c> adds the per-document
/// breakpoint store.
/// </para>
/// <para>
/// <b>Change tracking.</b> Every text change bumps a private <see cref="revision"/> counter and
/// marks the document dirty. The counter lets <see cref="Analyzer"/> be rebuilt lazily (only
/// when asked for after a change) instead of on every keystroke. Channel re-detection is not
/// automatic: <c>MainWindow</c> calls <see cref="RefreshChannels"/> 450 ms after typing stops
/// (debounce timer) and <c>MainViewModel</c> calls it when a tab is selected.
/// </para>
/// <para>
/// Fields marked <c>[ObservableProperty]</c> are turned into public notifying properties by the
/// CommunityToolkit.Mvvm source generator (<c>filePath</c> → <see cref="FilePath"/>);
/// <c>[NotifyPropertyChangedFor]</c> also raises change notifications for the listed computed
/// properties. All members are UI-thread only.
/// </para>
/// </remarks>
public sealed partial class DocumentViewModel : ObservableObject
{
    /// <summary>
    /// Forwards a channel value to the engine (supplied by <see cref="MainViewModel"/>); called by
    /// <see cref="Send"/> only while this document is playing.
    /// </summary>
    readonly Action<string, double> sendToEngine;

    /// <summary>Incremented on every text change (and by <see cref="InvalidateAnalysis"/>).</summary>
    int revision;

    /// <summary>
    /// The <see cref="revision"/> the cached <see cref="analyzer"/> was built for; -1 so the first
    /// access always builds one.
    /// </summary>
    int analyzerRevision = -1;

    /// <summary>Cached analysis of the text, valid while <see cref="analyzerRevision"/> equals <see cref="revision"/>.</summary>
    CsoundAnalyzer? analyzer;

    /// <summary>Creates a document.</summary>
    /// <param name="text">Initial text.</param>
    /// <param name="path">File path, or <c>null</c> for an unsaved document.</param>
    /// <param name="sendToEngine">Sends a channel value to the engine; only used while this document is playing.</param>
    public DocumentViewModel(string text, string? path, Action<string, double> sendToEngine)
    {
        this.sendToEngine = sendToEngine;
        Document = new TextDocument(text);
        // The initial text must not be undoable (Ctrl+Z would empty the freshly opened file).
        Document.UndoStack.ClearAll();
        // Assign the backing field, not FilePath: no notification is needed during construction.
        filePath = path;
        // Subscribed after the initial text is in place, so a freshly loaded document is clean.
        Document.TextChanged += (_, _) =>
        {
            revision++;
            IsDirty = true;
        };
        RefreshChannels();
    }

    /// <summary>The AvaloniaEdit document (text + undo stack) shown in the editor.</summary>
    public TextDocument Document { get; }

    /// <summary>Sliders for the channels found in the text.</summary>
    /// <remarks>
    /// Rebuilt by <see cref="RefreshChannels"/>. Instances are reused for channels that still
    /// exist, so a binding to one slider survives re-detection.
    /// </remarks>
    public ObservableCollection<ChannelControlViewModel> Channels { get; } = [];

    /// <summary>1-based lines Csound reported errors for during the last run of this document.</summary>
    /// <remarks>
    /// Filled by <c>MainViewModel</c> while parsing Csound's console messages and cleared when a
    /// new run starts; drawn as red lines by the editor's <c>ErrorLineRenderer</c>. A plain set
    /// (no notifications): the main view model raises <c>ErrorLinesChanged</c> instead.
    /// </remarks>
    public HashSet<int> ErrorLines { get; } = [];

    /// <summary>Path on disk, or <c>null</c> if never saved.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Title))]
    string? filePath;

    /// <summary>True when the text differs from what is on disk.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    bool isDirty;

    /// <summary>
    /// True while this document is the one being performed. Set by <c>MainViewModel</c> when a
    /// run starts and cleared when it finishes; gates <see cref="Send"/>.
    /// </summary>
    [ObservableProperty] bool isPlaying;

    /// <summary>
    /// File name shown in tabs and the title bar: the file's name, else <see cref="UntitledName"/>,
    /// else a localised "untitled.csd".
    /// </summary>
    public string FileName => FilePath is null ? UntitledName ?? Loc.S("sin título.csd", "untitled.csd") : Path.GetFileName(FilePath);

    /// <summary>Name shown (and suggested by «Save») while a document opened from a template has no file yet, e.g. «DrumKit.csd».</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Title))]
    string? untitledName;

    /// <summary>Tab caption: file name plus a dot when there are unsaved changes.</summary>
    public string Title => FileName + (IsDirty ? " •" : "");

    /// <summary>True when at least one channel slider exists.</summary>
    public bool HasChannels => Channels.Count > 0;

    /// <summary>
    /// Analyzer for the current text. Rebuilt lazily only when the text changed since the last
    /// request, so hovering is cheap.
    /// </summary>
    /// <remarks>
    /// Used by hover help, drag-to-scrub and completion. Analysis is a full re-parse of the text
    /// (<see cref="CsoundAnalyzer"/> is line-oriented and cheap enough for one pass, but not for
    /// every mouse move), hence the revision check.
    /// </remarks>
    public CsoundAnalyzer Analyzer
    {
        get
        {
            if (analyzer is null || analyzerRevision != revision)
            {
                analyzer = new CsoundAnalyzer(Document.Text);
                analyzerRevision = revision;
            }
            return analyzer;
        }
    }

    /// <summary>Forces the next <see cref="Analyzer"/> access to re-analyze (e.g. after the opcode list loaded).</summary>
    public void InvalidateAnalysis() => revision++;

    /// <summary>Finds a channel slider by name.</summary>
    /// <param name="name">Channel name (case-sensitive, like Csound channel names).</param>
    /// <returns>The slider, or <c>null</c> when the document has no such channel.</returns>
    public ChannelControlViewModel? FindChannel(string name) => Channels.FirstOrDefault(c => c.Name == name);

    /// <summary>Live state of a channel, in the form the analyzer expects.</summary>
    /// <param name="name">Channel name.</param>
    /// <returns>
    /// Range, current slider value and curve of the channel, or <c>null</c> when there is no slider
    /// for it. The analyzer uses this so hover help and scrubbing show/drive the live value.
    /// </returns>
    public ChannelState? ChannelState(string name) =>
        FindChannel(name) is { } c ? new ChannelState(c.Minimum, c.Maximum, c.Value, c.Exponential) : null;

    /// <summary>
    /// Re-detects the channels in the text. Existing sliders are kept (with the value the user set)
    /// when their channel still exists; their range follows the declaration.
    /// </summary>
    /// <remarks>
    /// Also refreshes the Cabbage widget form (<see cref="UpdateCabbage"/>) as a side effect, so
    /// this is the single "text was edited, re-derive the UI" entry point. The collection is only
    /// replaced when the list of names (or its order) changed; otherwise the existing items are
    /// updated in place, which avoids the side panel rebuilding all its slider rows (and losing
    /// a slider being dragged) after every pause in typing.
    /// </remarks>
    public void RefreshChannels()
    {
        // Cabbage instruments: widget channels (with Cabbage's ranges and defaults) come first.
        var specs = Cabbage.CabbageSupport.Merge(CsdTools.DetectChannels(Document.Text), UpdateCabbage(Document.Text));
        var existing = Channels.ToDictionary(c => c.Name);

        var next = specs.Select(s =>
        {
            if (existing.TryGetValue(s.Name, out var old))
            {
                // Keep the user's value (clamped into the possibly new range); the default in
                // the text only applies to channels that are new.
                old.Minimum = s.Minimum;
                old.Maximum = s.Maximum;
                old.Exponential = s.Exponential;
                old.Value = Math.Clamp(old.Value, s.Minimum, s.Maximum);
                return old;
            }
            return new ChannelControlViewModel(s.Name, s.Default, s.Minimum, s.Maximum, s.Exponential, Send);
        }).ToList();

        // Same channels in the same order: the in-place updates above are enough.
        if (next.Select(c => c.Name).SequenceEqual(Channels.Select(c => c.Name)))
            return;

        Channels.Clear();
        foreach (var c in next)
            Channels.Add(c);
        OnPropertyChanged(nameof(HasChannels));
    }

    /// <summary>Marks the text as saved.</summary>
    /// <param name="path">The path the text was written to (may differ from the old one after «Save as»).</param>
    public void MarkSaved(string path)
    {
        FilePath = path;
        IsDirty = false;
    }

    /// <summary>
    /// The <c>send</c> callback given to every <see cref="ChannelControlViewModel"/> of this
    /// document: forwards a slider value to the engine only while this document is playing.
    /// </summary>
    /// <param name="name">Channel name.</param>
    /// <param name="value">New value.</param>
    void Send(string name, double value)
    {
        if (IsPlaying)
            sendToEngine(name, value);
    }
}
