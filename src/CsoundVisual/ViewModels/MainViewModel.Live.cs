using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Editor;
using CsoundVisual.Engine;
using CsoundVisual.Localization;

namespace CsoundVisual.ViewModels;

/// <summary>One variable pinned in the "Observar" / "Watch" panel.</summary>
/// <remarks>
/// <para>
/// Created by <see cref="MainViewModel.Watch(TokenInsight)"/> when the user picks "Watch" in the
/// editor's context menu on a variable. The item only remembers <i>which</i> variable it shows
/// (name + owning instrument block); the values are pushed in by
/// <see cref="MainViewModel"/>'s frame loop through <see cref="Update"/>, always on the UI thread.
/// </para>
/// <para>
/// The <c>[ObservableProperty]</c> fields below are expanded by the CommunityToolkit.Mvvm source
/// generator into public properties (<c>ValueText</c>, <c>DetailText</c>, <c>History</c>) that
/// raise <c>PropertyChanged</c>; the watch card in <c>MainWindow.axaml</c> binds to those.
/// </para>
/// </remarks>
public sealed partial class WatchItemViewModel : ObservableObject
{
    /// <summary>Creates a watch.</summary>
    /// <param name="name">Variable name.</param>
    /// <param name="block">Instrument the variable belongs to, or <c>null</c> for a global.</param>
    public WatchItemViewModel(string name, CodeBlock? block)
    {
        Name = name;
        Block = block;
    }

    /// <summary>Variable name.</summary>
    public string Name { get; }

    /// <summary>Owning instrument, or <c>null</c> for globals.</summary>
    public CodeBlock? Block { get; }

    /// <summary>"instr Voice" or "global".</summary>
    public string Scope => Block?.Label ?? "global";

    /// <summary>Current value text, formatted by <see cref="MainViewModel.FormatLive"/> ("—" until the first sample).</summary>
    [ObservableProperty] string valueText = "—";

    /// <summary>
    /// Secondary line: minimum/maximum seen so far, the number of active instances (polyphony,
    /// shown only above one) and an "audio peak" tag for a-rate variables.
    /// </summary>
    [ObservableProperty] string detailText = Loc.S("sin datos todavía (toca para verlo en vivo)", "no data yet (press Play to see it live)");

    /// <summary>Recent values for the sparkline (the monitor's history ring, oldest first); <c>null</c> before the first sample.</summary>
    [ObservableProperty] float[]? history;

    /// <summary>Updates the texts and sparkline from a live sample.</summary>
    /// <param name="value">
    /// The latest sample from <see cref="LiveValueMonitor"/>, or <c>null</c> when there is none
    /// (not playing, instrument not active). A <c>null</c> sample leaves the last values on screen
    /// on purpose, so the card keeps showing what the variable last did after the note ends.
    /// </param>
    public void Update(LiveValue? value)
    {
        if (value is null)
            return;
        ValueText = MainViewModel.FormatLive(value.Value);
        DetailText = Loc.S($"mín {MainViewModel.FormatLive(value.Minimum)} · máx {MainViewModel.FormatLive(value.Maximum)}",
                           $"min {MainViewModel.FormatLive(value.Minimum)} · max {MainViewModel.FormatLive(value.Maximum)}") +
                     (value.Instances > 1 ? Loc.S($" · {value.Instances} notas", $" · {value.Instances} notes") : "") +
                     (value.IsAudio ? Loc.S(" · pico de audio", " · audio peak") : "");
        History = value.History;
    }
}

/// <summary>
/// Live variable values: the hover card and the watch panel read the values sampled by the
/// engine's <see cref="LiveValueMonitor"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Data flow.</b> While Csound plays, <see cref="LiveValueMonitor"/> (registered as the Csound
/// debugger callback on the performance thread) publishes an immutable dictionary keyed by
/// (instrument number, variable name) about 30 times per second. This partial only <i>reads</i>
/// that snapshot from the UI thread: the editor's hover card asks through
/// <see cref="GetLiveValue"/> (part of <see cref="IScrubHost"/>), and the watch panel is refreshed
/// by <see cref="UpdateWatches"/>, called from <see cref="Tick"/> on every animation frame.
/// Nothing here touches Csound directly.
/// </para>
/// <para>
/// <b>Instrument numbers.</b> The monitor keys values by Csound's instrument <i>number</i>; global
/// variables live under number 0. Named instruments (<c>instr Voice</c>) are resolved through the
/// <c>InstrumentNumbers</c> map the engine fills at start-up from <see cref="NamedInstruments"/>.
/// </para>
/// <para>
/// The <see cref="IScrubHost"/> members marked <c>inheritdoc</c> below (<see cref="GetLiveValue"/>,
/// <see cref="Watch"/>, <see cref="GoToDefinition"/>, <see cref="OpenOpcodeHelp"/>) are the
/// editor's hooks into the view model for the hover card and its context menu.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Pinned variables shown in the "Observar" panel.</summary>
    public ObservableCollection<WatchItemViewModel> Watches { get; } = [];

    /// <summary>True when at least one variable is pinned.</summary>
    public bool HasWatches => Watches.Count > 0;

    /// <summary>
    /// Whether variable values are sampled while playing (Csound debugger; small CPU cost).
    /// Persisted as <see cref="AppSettings.LiveValues"/> (the constructor loads it into the backing
    /// field directly, so loading does not trigger a save). It is copied into the engine's job
    /// when a live run starts (never for render-to-file), so toggling it takes effect on the next Play.
    /// </summary>
    [ObservableProperty] bool liveValuesEnabled = true;

    /// <summary>Frame counter used by <see cref="UpdateWatches"/> to refresh only every third frame.</summary>
    int watchFrame;

    /// <summary>
    /// CommunityToolkit hook generated for <see cref="LiveValuesEnabled"/>: the source generator
    /// calls this partial method after the property changes. It saves the choice to the settings
    /// file immediately (settings are written on every change, not on exit).
    /// </summary>
    /// <param name="value">The new value.</param>
    partial void OnLiveValuesEnabledChanged(bool value)
    {
        settings.LiveValues = value;
        settings.Save();
    }

    /// <summary>
    /// Numeric value formatting shared by the live displays: fewer decimals as the magnitude grows
    /// (≥ 1000 → integer, ≥ 10 → 1 decimal, ≥ 1 → 2, otherwise 3), always with the invariant
    /// culture so the decimal separator is a dot like in Csound code.
    /// </summary>
    /// <param name="v">Value to format.</param>
    /// <returns>The formatted text.</returns>
    public static string FormatLive(double v) =>
        v.ToString(Math.Abs(v) >= 1000 ? "0" : Math.Abs(v) >= 10 ? "0.0" : Math.Abs(v) >= 1 ? "0.00" : "0.000", CultureInfo.InvariantCulture);

    /// <summary>Instrument number of a block: its numeric id, or the number Csound gave a named instrument.</summary>
    /// <param name="block">An <c>instr</c> block from the document analyser.</param>
    /// <returns>
    /// The number, or <c>null</c> when the block is named and the running engine has not
    /// resolved that name (not playing, or the name is not in the running orchestra). Only the
    /// block's first id is used, so <c>instr 1, 2</c> is looked up as instrument 1.
    /// </returns>
    int? InstrumentNumber(CodeBlock block)
    {
        string id = block.Ids.FirstOrDefault() ?? "";
        if (int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n;
        return engine.Live?.InstrumentNumbers.TryGetValue(id, out int named) == true ? named : null;
    }

    /// <summary>Live value of a variable in a block (or a global when <paramref name="block"/> is null).</summary>
    /// <param name="name">Variable name as written in the code (e.g. <c>kcut</c>, <c>gaRev</c>).</param>
    /// <param name="block">Owning instrument, or <c>null</c> for a global.</param>
    /// <returns>The latest sample, or <c>null</c> when nothing is running, live values are off or the variable has not been seen.</returns>
    LiveValue? Lookup(string name, CodeBlock? block)
    {
        if (engine.Live is not { } live || !IsRunning)
            return null;
        if (block is null)
            return live.Get(0, name); // globals are stored under instrument number 0
        return InstrumentNumber(block) is { } n ? live.Get(n, name) : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Only answers for the document that is actually playing: the same variable name in another
    /// tab would otherwise show values from a different orchestra.
    /// </remarks>
    public LiveValue? GetLiveValue(TokenInsight token) =>
        token.Kind == InsightKind.Variable && SelectedDocument?.IsPlaying == true
            ? Lookup(token.Text, token.IsGlobal ? null : token.Block)
            : null;

    /// <inheritdoc/>
    /// <remarks>
    /// Duplicates are ignored. Blocks are compared by <c>Label</c> rather than by reference
    /// because the analyser creates new <see cref="CodeBlock"/> objects when it re-analyses the text.
    /// <see cref="HasWatches"/> is computed, so its change is raised by hand.
    /// </remarks>
    public void Watch(TokenInsight token)
    {
        if (token.Kind != InsightKind.Variable)
            return;
        var block = token.IsGlobal ? null : token.Block;
        if (Watches.Any(w => w.Name == token.Text && w.Block?.Label == block?.Label))
            return;
        Watches.Add(new WatchItemViewModel(token.Text, block));
        OnPropertyChanged(nameof(HasWatches));
    }

    /// <summary>
    /// Removes a pinned variable (closing its enlarged view first if it is the focused one).
    /// <c>[RelayCommand]</c> generates <c>RemoveWatchCommand</c>, bound to the ✕ of each watch card
    /// with the card's item as the command parameter.
    /// </summary>
    /// <param name="item">The watch to remove.</param>
    [RelayCommand]
    void RemoveWatch(WatchItemViewModel item)
    {
        if (FocusedWatch == item)
            FocusedWatch = null;
        RemoveWatchCore(item);
    }

    /// <summary>
    /// Watched variable shown enlarged in the centre of the window (click a watch card); the rest
    /// of the window is dimmed. <c>null</c> = no enlarged view. Clicking outside or Esc closes it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWatchFocused))]
    WatchItemViewModel? focusedWatch;

    /// <summary>True while the enlarged watch view is open.</summary>
    public bool IsWatchFocused => FocusedWatch is not null;

    /// <summary>Closes the enlarged watch view (generated <c>CloseFocusedWatchCommand</c>: click outside or Esc).</summary>
    [RelayCommand]
    void CloseFocusedWatch() => FocusedWatch = null;

    /// <summary>Removes a watch from the collection and re-raises the computed <see cref="HasWatches"/>.</summary>
    /// <param name="item">The watch to remove.</param>
    void RemoveWatchCore(WatchItemViewModel item)
    {
        Watches.Remove(item);
        OnPropertyChanged(nameof(HasWatches));
    }

    /// <inheritdoc/>
    /// <remarks>The window listens to <c>GoToLineRequested</c> and moves the caret; the view model never touches the editor control.</remarks>
    public void GoToDefinition(int line)
    {
        if (SelectedDocument is { } doc)
            GoToLineRequested?.Invoke(doc, line);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Opens the online Csound manual page of the opcode in the default browser
    /// (<c>UseShellExecute</c> lets Windows pick the handler for the URL). A failure is reported
    /// in the console instead of throwing.
    /// </remarks>
    public void OpenOpcodeHelp(string opcode)
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://csound.com/manual/opcodes/{Uri.EscapeDataString(opcode)}/") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ConsoleLines.Add(new ConsoleLine(Loc.S($"No se pudo abrir el manual: {ex.Message}", $"Could not open the manual: {ex.Message}"), MessageKind.Error));
        }
    }

    /// <summary>
    /// Refreshes the watch panel. Called from <see cref="Tick"/> on every animation frame while
    /// playing, but only does the work every third frame (≈ 20 Hz on a 60 Hz display): the monitor
    /// publishes at about 30 Hz, so refreshing faster would only re-read the same snapshot.
    /// </summary>
    void UpdateWatches()
    {
        if (Watches.Count == 0 || ++watchFrame % 3 != 0)
            return;
        foreach (var w in Watches)
            w.Update(Lookup(w.Name, w.Block));
    }

    /// <summary>Named instruments of a document, passed to the engine to resolve their numbers.</summary>
    /// <param name="doc">The document about to be played.</param>
    /// <returns>
    /// Every non-numeric id of every <c>instr</c> block, without surrounding quotes
    /// (<c>instr "Voice"</c> and <c>instr Voice</c> both give <c>Voice</c>).
    /// </returns>
    static IReadOnlyList<string> NamedInstruments(DocumentViewModel doc) =>
        [.. doc.Analyzer.Blocks.Where(b => b.Keyword == "instr").SelectMany(b => b.Ids)
            .Where(id => !int.TryParse(id, out _)).Select(id => id.Trim('"'))];
}
