namespace CsoundVisual.Music;

/// <summary>One note of the piano roll. Times are in beats (quarter notes) from the start of the roll.</summary>
public sealed class RollNote
{
    /// <summary>Start, beats.</summary>
    public double Start { get; set; }

    /// <summary>Length, beats (&gt; 0).</summary>
    public double Length { get; set; }

    /// <summary>MIDI note number, 0…127 (60 = C4, middle C).</summary>
    public int Pitch { get; set; }

    /// <summary>Velocity, 1…127 (converted to p5 by the chosen <see cref="VelocityMapping"/>).</summary>
    public int Velocity { get; set; } = 100;

    /// <summary>End, beats.</summary>
    public double End => Start + Length;

    /// <summary>A copy (undo snapshots, clipboard, scheduler snapshots).</summary>
    public RollNote Clone() => new() { Start = Start, Length = Length, Pitch = Pitch, Velocity = Velocity };

    /// <summary>Value equality (used by tests and by «did anything change?» checks).</summary>
    public bool SameAs(RollNote o) =>
        Math.Abs(Start - o.Start) < 1e-9 && Math.Abs(Length - o.Length) < 1e-9 && Pitch == o.Pitch && Velocity == o.Velocity;

    /// <inheritdoc/>
    public override string ToString() => $"{PFields.NoteName(Pitch)}({Pitch}) @{PFields.Num(Start)} len {PFields.Num(Length)} vel {Velocity}";
}

/// <summary>
/// Everything the piano roll edits: notes, selection, undo/redo, clipboard and the playback /
/// export settings (tempo, snap, loop, target instrument, p-field mapping). UI-thread only; the
/// scheduler gets an immutable <see cref="RollPattern"/> through <see cref="Snapshot"/>.
/// </summary>
/// <remarks>
/// <para>
/// Lives in the main view model (not in the window), so closing the piano roll window keeps the
/// notes for the session. The view (<c>Controls/PianoRollView</c>) mutates notes directly during a
/// drag and calls <see cref="Checkpoint"/> once at the start of each gesture.
/// </para>
/// <para>
/// <b>Undo model:</b> whole-list snapshots (deep copies of every note), at most 200. Simple and
/// robust for the few hundred notes a roll typically holds; <see cref="Undo"/>/<see cref="Redo"/>
/// swap whole lists, which is why the selection is cleared (its references would point into the
/// previous list).
/// </para>
/// </remarks>
public sealed class PianoRollModel
{
    /// <summary>Resolution all positions are rounded to when imported (960 ticks per beat, like a DAW's PPQ).</summary>
    public const double Tick = 1.0 / 960;

    /// <summary>Undo stack of note-list snapshots (oldest first, capped at 200).</summary>
    readonly List<List<RollNote>> undo = [];
    /// <summary>Redo stack; cleared by every new <see cref="Checkpoint"/>.</summary>
    readonly List<List<RollNote>> redo = [];
    /// <summary>Copied notes (deep copies, absolute positions); see <see cref="Paste"/>.</summary>
    List<RollNote> clipboard = [];

    /// <summary>The notes (unordered while editing).</summary>
    public List<RollNote> Notes { get; private set; } = [];

    /// <summary>Selected notes (references into <see cref="Notes"/>).</summary>
    public HashSet<RollNote> Selected { get; } = [];

    /// <summary>Tempo, BPM.</summary>
    public double Bpm { get; set; } = 120;

    /// <summary>Grid in beats (1 = quarter, 0.25 = sixteenth, 1/3 = eighth triplet…); 0 = no snap.</summary>
    public double Snap { get; set; } = 0.25;

    /// <summary>Length given to new notes (the last length drawn or resized), beats.</summary>
    public double LastLength { get; set; } = 1;

    /// <summary>Loop region, beats.</summary>
    public double LoopStart { get; set; }

    /// <summary>Loop region end, beats.</summary>
    public double LoopEnd { get; set; } = 16;

    /// <summary>Whether playback loops.</summary>
    public bool LoopOn { get; set; } = true;

    /// <summary>Target instrument id (number or name, unquoted).</summary>
    public string Instrument { get; set; } = "1";

    /// <summary>How the pitch goes into p4.</summary>
    public PitchMapping PitchMapping { get; set; } = PitchMapping.MidiNote;

    /// <summary>How the velocity goes into p5.</summary>
    public VelocityMapping VelocityMapping { get; set; } = VelocityMapping.Unit;

    /// <summary>True once the user picked the instrument (or an import set it): it is then never replaced by a default.</summary>
    public bool InstrumentChosen { get; set; }

    /// <summary>Counter bumped by the window on every edit (for the «not inserted yet» indicator).</summary>
    public int Revision { get; set; }

    /// <summary><see cref="Revision"/> at the last «Insert into score» / import.</summary>
    public int SavedRevision { get; set; }

    /// <summary>Raised after any change of notes or selection (the view repaints, the scheduler snapshot is refreshed).</summary>
    public event Action? Changed;

    /// <summary>True when there is something to undo / redo.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <inheritdoc cref="CanUndo"/>
    public bool CanRedo => redo.Count > 0;

    /// <summary>Raises <see cref="Changed"/>.</summary>
    public void NotifyChanged() => Changed?.Invoke();

    /// <summary>Snaps a position to the grid (round to nearest; no-op when snap is off).</summary>
    public double SnapBeat(double beat) => Snap > 0 ? Math.Round(beat / Snap) * Snap : beat;

    /// <summary>Snaps a position down to the grid line at or before it.</summary>
    public double SnapFloor(double beat) => Snap > 0 ? Math.Floor(beat / Snap + 1e-9) * Snap : beat;

    /// <summary>Smallest length a note can have: one grid step, or 1/64 of a beat without snap.</summary>
    public double MinLength => Snap > 0 ? Snap : 1.0 / 64;

    /// <summary>Saves the current notes for undo. Call BEFORE every edit (once per gesture).</summary>
    public void Checkpoint()
    {
        undo.Add(Notes.Select(n => n.Clone()).ToList());
        if (undo.Count > 200) undo.RemoveAt(0);
        redo.Clear();
    }

    /// <summary>Drops the last checkpoint when the gesture turned out to change nothing (a plain click on a note).</summary>
    public void DiscardCheckpointIfUnchanged()
    {
        if (undo.Count == 0) return;
        var last = undo[^1];
        if (last.Count == Notes.Count && last.Zip(Notes).All(p => p.First.SameAs(p.Second)))
            undo.RemoveAt(undo.Count - 1);
    }

    /// <summary>Undo (Ctrl+Z).</summary>
    public void Undo()
    {
        if (undo.Count == 0) return;
        redo.Add(Notes);
        Notes = undo[^1];
        undo.RemoveAt(undo.Count - 1);
        Selected.Clear();
        NotifyChanged();
    }

    /// <summary>Redo (Ctrl+Y / Ctrl+Shift+Z).</summary>
    public void Redo()
    {
        if (redo.Count == 0) return;
        undo.Add(Notes);
        Notes = redo[^1];
        redo.RemoveAt(redo.Count - 1);
        Selected.Clear();
        NotifyChanged();
    }

    /// <summary>Adds a note (with checkpoint) and selects only it.</summary>
    /// <param name="start">Start, beats (clamped to ≥ 0).</param>
    /// <param name="pitch">MIDI note (clamped to 0…127).</param>
    /// <param name="length">Length, beats (at least <see cref="MinLength"/>).</param>
    /// <param name="velocity">Velocity (clamped to 1…127).</param>
    /// <returns>The new note (also the only selected note).</returns>
    public RollNote Add(double start, int pitch, double length, int velocity)
    {
        Checkpoint();
        var note = new RollNote { Start = Math.Max(0, start), Pitch = Math.Clamp(pitch, 0, 127), Length = Math.Max(MinLength, length), Velocity = Math.Clamp(velocity, 1, 127) };
        Notes.Add(note);
        Selected.Clear();
        Selected.Add(note);
        NotifyChanged();
        return note;
    }

    /// <summary>Deletes the given notes (with checkpoint).</summary>
    public void Delete(IEnumerable<RollNote> notes)
    {
        var set = notes.ToHashSet();
        if (set.Count == 0) return;
        Checkpoint();
        Notes.RemoveAll(set.Contains);
        Selected.ExceptWith(set);
        NotifyChanged();
    }

    /// <summary>Replaces all notes (with checkpoint), e.g. after an import.</summary>
    public void ReplaceAll(IEnumerable<RollNote> notes)
    {
        Checkpoint();
        Notes = [.. notes];
        Selected.Clear();
        NotifyChanged();
    }

    /// <summary>Selects every note (Ctrl+A).</summary>
    public void SelectAll()
    {
        Selected.Clear();
        Selected.UnionWith(Notes);
        NotifyChanged();
    }

    /// <summary>Moves the selection by beats / semitones (arrow keys), with checkpoint.</summary>
    /// <param name="beats">Time offset in beats (negative = earlier).</param>
    /// <param name="semitones">Pitch offset in semitones.</param>
    public void Nudge(double beats, int semitones)
    {
        if (Selected.Count == 0) return;
        // All-or-nothing: refuse the move if any note would leave the roll, so chords keep their shape.
        if (Selected.Any(n => n.Start + beats < -1e-9 || n.Pitch + semitones is < 0 or > 127)) return;
        Checkpoint();
        foreach (var n in Selected)
        {
            n.Start = Math.Max(0, n.Start + beats);
            n.Pitch += semitones;
        }
        NotifyChanged();
    }

    /// <summary>Copies the selection (Ctrl+C).</summary>
    /// <returns>Number of notes copied.</returns>
    public int Copy()
    {
        clipboard = Selected.Select(n => n.Clone()).ToList();
        return clipboard.Count;
    }

    /// <summary>True when something was copied.</summary>
    public bool HasClipboard => clipboard.Count > 0;

    /// <summary>
    /// Pastes the copied notes (Ctrl+V) so the earliest one starts at <paramref name="at"/>; the
    /// pasted notes become the selection. With <paramref name="at"/> = <c>null</c> they go right
    /// after the end of the copied block (quick repeat, like Cubase's «Duplicate»).
    /// </summary>
    public void Paste(double? at)
    {
        if (clipboard.Count == 0) return;
        double first = clipboard.Min(n => n.Start);
        double span = clipboard.Max(n => n.End) - first;
        // Round the block length up to whole grid steps so repeated pastes stay on the grid
        // (the −1e-9 keeps an exact multiple from being bumped one step by rounding noise).
        if (Snap > 0) span = Math.Ceiling(span / Snap - 1e-9) * Snap;
        double target = at ?? first + span;
        Checkpoint();
        Selected.Clear();
        foreach (var c in clipboard)
        {
            var n = c.Clone();
            n.Start = Math.Max(0, target + (c.Start - first));
            Notes.Add(n);
            Selected.Add(n);
        }
        // The next Ctrl+V without a position continues after this copy.
        clipboard = Selected.Select(n => n.Clone()).ToList();
        NotifyChanged();
    }

    /// <summary>Quantizes the starts of the selection (or every note) to the grid, with checkpoint.</summary>
    public void Quantize()
    {
        if (Snap <= 0) return;
        var target = Selected.Count > 0 ? Selected.ToList() : Notes;
        if (target.Count == 0) return;
        Checkpoint();
        foreach (var n in target)
            n.Start = Math.Max(0, SnapBeat(n.Start));
        NotifyChanged();
    }

    /// <summary>End of the last note, beats (0 when empty).</summary>
    public double ContentEnd => Notes.Count == 0 ? 0 : Notes.Max(n => n.End);

    /// <summary>
    /// Immutable copy of the notes and settings for the scheduler and for export.
    /// </summary>
    /// <remarks>
    /// Notes are deep-copied and sorted (start, then pitch) so <see cref="RollPattern.Collect"/> can
    /// binary-search. A degenerate loop is widened to at least a sixteenth (0.25 beat).
    /// </remarks>
    public RollPattern Snapshot() => new(
        [.. Notes.OrderBy(n => n.Start).ThenBy(n => n.Pitch).Select(n => n.Clone())],
        PFields.InstrumentToken(Instrument), PitchMapping, VelocityMapping,
        LoopOn, (LoopStart, Math.Max(LoopEnd, LoopStart + 0.25)));
}

/// <summary>
/// Read-only snapshot of a piano roll (notes sorted by start), playable by the
/// <see cref="TransportScheduler"/> and exportable to the score.
/// </summary>
/// <remarks>
/// Thread-safe by immutability: created on the UI thread by <see cref="PianoRollModel.Snapshot"/>
/// and read on the scheduler thread. (The <see cref="RollNote"/> objects are mutable classes, but
/// they are private clones that nobody edits after the snapshot is taken.)
/// </remarks>
/// <param name="notes">Cloned notes, sorted by start then pitch.</param>
/// <param name="instrumentToken">p1 token (<c>1</c> or <c>"Voice"</c>).</param>
/// <param name="pitch">How pitch is written into p4.</param>
/// <param name="velocity">How velocity is written into p5.</param>
/// <param name="loop">Whether playback loops.</param>
/// <param name="loopRange">Loop region, beats.</param>
public sealed class RollPattern(RollNote[] notes, string instrumentToken, PitchMapping pitch, VelocityMapping velocity,
    bool loop, (double Start, double End) loopRange) : IPatternSource
{
    /// <summary>The notes, sorted by start then pitch.</summary>
    public IReadOnlyList<RollNote> Notes => notes;

    /// <summary>p1 token.</summary>
    public string InstrumentToken => instrumentToken;

    /// <summary>p4/p5 text of a note.</summary>
    public string FieldsOf(RollNote n) => PFields.Pitch(n.Pitch, pitch) + " " + PFields.Velocity(n.Velocity, velocity);

    /// <inheritdoc/>
    public (double Start, double End) LoopRange => loopRange;

    /// <inheritdoc/>
    public bool Loop => loop;

    /// <inheritdoc/>
    public double End => notes.Length == 0 ? 0 : notes.Max(n => n.End);

    /// <inheritdoc/>
    public void Collect(double fromBeat, double toBeat, List<PatternEvent> into)
    {
        // First note with Start >= fromBeat (binary search; notes are sorted by start).
        int lo = 0, hi = notes.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (notes[mid].Start < fromBeat) lo = mid + 1; else hi = mid;
        }
        for (int i = lo; i < notes.Length && notes[i].Start < toBeat; i++)
        {
            var n = notes[i];
            // Inside a loop, a note longer than the region is cut at the loop end.
            double len = loop && n.Start < loopRange.End ? Math.Min(n.Length, loopRange.End - n.Start) : n.Length;
            // Tag = pitch, so the view can light the matching key when the note is sent.
            into.Add(new PatternEvent(n.Start, len, instrumentToken, FieldsOf(n), n.Pitch));
        }
    }
}
