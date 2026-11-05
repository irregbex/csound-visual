using CommunityToolkit.Mvvm.Input;
using CsoundVisual.Localization;
using CsoundVisual.Music;

namespace CsoundVisual.ViewModels;

/// <summary>
/// Services for the music tools (piano roll and step sequencer windows): which document they
/// address, its instruments, starting Csound for them, the «press Play» hint and writing their
/// marked blocks into the score.
/// </summary>
/// <remarks>
/// <para>
/// The tool windows (<c>Views/PianoRollWindow.cs</c>, <c>Views/StepSequencerWindow.cs</c>, with the
/// shared <c>Views/ToolTransport.cs</c>) are built and owned by the view; the view model only hands
/// them their data and a few services. The models (<see cref="PianoRoll"/>, <see cref="Sequencer"/>)
/// live here so closing and reopening a window within a session keeps the work; they are not
/// written to the settings file (persistence across sessions is the marked score block, see
/// <see cref="WriteScoreBlock"/>).
/// </para>
/// <para>
/// The status members (<see cref="ToolDocument"/>, <see cref="IsPlayingLive"/>,
/// <see cref="PlayingDocument"/>, <see cref="ToolEngineStatus"/>, <see cref="ToolLateEvents"/>) are
/// plain computed properties that raise no change notifications: the tool windows poll them from
/// their own UI timer. Everything here is called on the UI thread; the note scheduling itself
/// runs on the <see cref="TransportScheduler"/> thread and talks to the engine directly.
/// </para>
/// </remarks>
public sealed partial class MainViewModel
{
    /// <summary>Opens (or focuses) the piano roll window; provided by the view (the view model never creates windows).</summary>
    public Action? ShowPianoRoll { get; set; }

    /// <summary>Opens (or focuses) the step sequencer window; provided by the view.</summary>
    public Action? ShowStepSequencer { get; set; }

    /// <summary>
    /// The piano roll's notes and settings; kept for the whole session, so closing the window loses
    /// nothing. Starts targeting the demo's <c>Voice</c> instrument.
    /// </summary>
    public PianoRollModel PianoRoll { get; } = new() { Instrument = "Voice" };

    /// <summary>
    /// The step sequencer's pattern (the default groove, with track names in the UI language at
    /// start-up; kept for the session). Settable because importing a document's <c>;@seq</c> block
    /// replaces the whole pattern.
    /// </summary>
    public StepPattern Sequencer { get; set; } = StepPattern.DefaultGroove(Loc.IsEnglish);

    /// <summary>True once the sequencer pattern was edited this session (then it is never replaced automatically by a document's block).</summary>
    public bool SequencerEdited { get; set; }

    /// <summary>Piano roll (Ctrl+P). Generated <c>OpenPianoRollCommand</c> for the toolbar button, the ⋯ menu and the key binding.</summary>
    [RelayCommand]
    void OpenPianoRoll() => ShowPianoRoll?.Invoke();

    /// <summary>Step sequencer (Ctrl+Shift+P). Generated <c>OpenStepSequencerCommand</c>.</summary>
    [RelayCommand]
    void OpenStepSequencer() => ShowStepSequencer?.Invoke();

    /// <summary>
    /// The document the tools address: the one being performed while Csound plays (its
    /// instruments are the ones that can sound), otherwise the one shown in the editor.
    /// </summary>
    /// <remarks>
    /// The <c>Documents.Contains</c> check covers a playing tab that was closed: the engine may
    /// still be running its text, but there is no document to write blocks into any more.
    /// </remarks>
    public DocumentViewModel? ToolDocument =>
        Mode == RunMode.Play && lastRunDocument is { } playing && Documents.Contains(playing) ? playing : SelectedDocument;

    /// <summary>True while Csound performs in real time (the tools can only sound then).</summary>
    public bool IsPlayingLive => Mode == RunMode.Play;

    /// <summary>The instruments declared in <paramref name="doc"/> (numbers and names, with the comment of their line).</summary>
    /// <param name="doc">Document to scan, or <c>null</c>.</param>
    /// <returns>The instrument choices for the tools' instrument picker (empty for <c>null</c>); parsed from the current text each call.</returns>
    public static List<InstrumentChoice> InstrumentsOf(DocumentViewModel? doc) =>
        doc is null ? [] : PFields.FindInstruments(doc.Document.Text);

    /// <summary>
    /// Starts Csound on <paramref name="doc"/> for a tool (its Play button pressed while Csound was
    /// stopped). Returns false when it cannot start now (rendering, or no Csound).
    /// </summary>
    /// <param name="doc">The document to perform; it becomes the selected tab because Play always performs the selected document.</param>
    /// <returns>
    /// <c>true</c> when Csound is (now) in Play mode. The performance thread is only starting at
    /// that point and compiles asynchronously (a compile error ends the run a moment later), so the
    /// tool's transport still waits until the engine reports it is running before scheduling
    /// notes. <c>false</c> when Csound cannot start now (rendering, no Csound, Play disabled).
    /// </returns>
    public bool StartPlaybackFor(DocumentViewModel doc)
    {
        if (Mode == RunMode.Play) return true;
        if (Mode != RunMode.None || !csoundAvailable) return false;
        SelectedDocument = doc;
        if (!PlayCommand.CanExecute(null)) return false;
        PlayCommand.Execute(null);
        return Mode == RunMode.Play; // Launch sets Mode synchronously before handing the job to the engine
    }

    /// <summary>Shows the «Csound must be in Play mode» bubble under the main window's Play button.</summary>
    public void HintPressPlay() => ShowPlayHint();

    /// <summary>Writes a message line to the console (used by the tool windows).</summary>
    /// <param name="text">Already localised text.</param>
    public void ToolInfo(string text) => AddInfo(text);

    /// <summary>
    /// Writes (or replaces) a tool's marked block in <paramref name="doc"/>'s score through the
    /// editor document, so the change can be undone with Ctrl+Z in the editor. Returns false
    /// when the text is not a CSD.
    /// </summary>
    /// <param name="doc">Target document (usually <see cref="ToolDocument"/>).</param>
    /// <param name="kind"><see cref="ScoreBlocks.PianoRoll"/> or <see cref="ScoreBlocks.StepSequencer"/>: selects which marked block is replaced.</param>
    /// <param name="block">The block text built by <see cref="ScoreBlocks.BuildPianoRollBlock"/> / <see cref="ScoreBlocks.BuildSequencerBlock"/> (markers + <c>i</c> lines).</param>
    /// <returns><c>true</c> when the block was written.</returns>
    /// <remarks>
    /// <see cref="ScoreBlocks.MakeInsertEdit"/> computes a single edit (replace the previous block
    /// of that kind, else append at the end of <c>&lt;CsScore&gt;</c>, creating a score if needed);
    /// applying it with one <c>Document.Replace</c> makes it a single undo step in AvaloniaEdit.
    /// </remarks>
    public bool WriteScoreBlock(DocumentViewModel doc, string kind, string block)
    {
        string text = doc.Document.Text;
        if (ScoreBlocks.MakeInsertEdit(text, kind, block) is not { } edit)
            return false;
        doc.Document.Replace(edit.Offset, edit.Length, edit.Text);
        // Show where the block went: that document's tab, scrolled to the block's first line.
        SelectedDocument = doc;
        // The block starts with its ";@…" marker comment; locate it inside the inserted text (the
        // edit may begin with a newline) and clamp to the document end.
        int start = edit.Text.IndexOf(';');
        int line = doc.Document.GetLineByOffset(Math.Min(edit.Offset + Math.Max(0, start), doc.Document.TextLength)).LineNumber;
        GoToLineRequested?.Invoke(doc, line);
        return true;
    }

    /// <summary>Opens the bundled drum kit template in a new tab (and plays it when something else was playing).</summary>
    /// <returns>The new tab (<c>Assets/DrumKit.csd</c>: instr 1–8, p4 = MIDI note, p5 = velocity 0…1), so the sequencer can target it.</returns>
    /// <remarks>
    /// <see cref="OpenTemplate"/> selects the new tab, hence the read of <see cref="SelectedDocument"/>
    /// afterwards. When Csound is already playing another document, <c>FollowWithPlayback</c> stops
    /// it and queues the kit to start once the old performance has finished.
    /// <para>Returns <c>null</c> when the template could not be opened.</para>
    /// <para>Fix: <see cref="OpenTemplate"/> returns nothing and, when the embedded template is missing,
    /// only prints a console line and leaves the selection alone. Reading
    /// <see cref="SelectedDocument"/> afterwards then returned the PREVIOUS tab, which was treated as
    /// the drum kit (started with <c>FollowWithPlayback</c> and handed to the step sequencer). Now the
    /// selection before and after is compared: if no new tab was selected, nothing is played and null
    /// is returned (the caller already handles null).</para>
    /// </remarks>
    public DocumentViewModel? OpenDrumKit()
    {
        var before = SelectedDocument;
        OpenTemplate("DrumKit");
        var doc = SelectedDocument;
        if (doc is null || ReferenceEquals(doc, before))
            return null; // template missing: OpenTemplate already explained it in the console
        FollowWithPlayback(doc);
        return doc;
    }

    /// <summary>
    /// Tool events dropped in the current (or last) performance because they reached the engine
    /// more than 0.25 s late (see <c>CsoundEngine.LateEvents</c>); shown in the tools' status line.
    /// </summary>
    public int ToolLateEvents => engine.LateEvents;

    /// <summary>The document Csound is performing, or <c>null</c> when it is not playing.</summary>
    public DocumentViewModel? PlayingDocument => Mode == RunMode.Play ? lastRunDocument : null;

    /// <summary>Text for tool status lines: «Csound playing file.csd» / «Csound rendering …» / «Csound stopped» (localised with <see cref="Loc.S"/>).</summary>
    public string ToolEngineStatus => Mode switch
    {
        RunMode.Play => Loc.S($"Csound tocando {lastRunDocument?.FileName}", $"Csound playing {lastRunDocument?.FileName}"),
        RunMode.Render => Loc.S("Csound renderizando (espera a que termine)", "Csound rendering (wait until it finishes)"),
        _ => Loc.S("Csound detenido", "Csound stopped"),
    };
}
