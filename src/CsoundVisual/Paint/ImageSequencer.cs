// ImageSequencer.cs — Paint Rooms: the model behind the «Image Sequencer» room.
//
// Role: melodic phrases and rhythmic loops made of PICTURES. The user paints small PATTERNS
// (a few steps wide, one column per step, the room's pitch axis tall) and arranges them on a
// timeline of LANES and BARS as CLIPS: each clip places one pattern at a step, repeats it, and may
// transpose it (rows = semitones on the default 12-tone axis), reverse it in time, invert it
// upside down and change its gain. Tempo and «steps per beat» quantise everything to a grid.
// (The idea of sequencing pictures comes from MetaSynth's Image Sequencer; names, layout and
// behaviour here are our own.)
//
// HOW IT SOUNDS — composition, not scheduling. The arrangement is COMPOSED into one long
// SpectralImage (SequencerComposer.Compose): every clip's pattern is stamped, transposed / reversed
// / inverted / scaled, into a canvas of TotalSteps × Sub columns, and that canvas is played or
// rendered by the existing Paint Synth engine (PaintLivePlayer / PaintRenderer, instr 9301).
// Why: (1) it reuses the proven synthesis (sines, noise layer, stereo layers, true-peak render,
// «Show Csound code») with zero new Csound code; (2) timing is sample-accurate by construction —
// a step is exactly Sub columns of a canvas whose duration is TotalSteps × StepSeconds, so the
// instrument's column clock IS the sequencer clock (no event scheduling, no jitter, nothing to
// drift); (3) edits while playing are just pixel edits of that canvas, which the live player
// already uploads column by column (only the columns that changed). The cost: changing the tempo
// or the length restarts the note (the column timing is a p-field), and polyphony per row is one
// voice (two clips on the same row at the same time merge by max — as two pictures overlaid).
//
// Sub-columns and the GATE: one step is Sub (1…8) composed columns. A lit pattern cell sounds for
// round(Gate·Sub) of them and is silent for the rest, so repeated notes on the same row RE-ATTACK
// (the synthesis has no note-on: a row only rises again after it fell). A LEGATO pattern instead
// joins a lit cell with the next step's lit cell on the same row into one held note.
//
// Gain: levels are not amplitudes (PaintSynthSettings.LevelToAmplitude applies a floor and a
// power curve), so a clip gain of g (linear amplitude) maps a level ℓ above the floor f to
//     ℓ' = f + (ℓ − f) · g^(1/curve)
// which multiplies the synthesised amplitude by exactly g (until ℓ' reaches 1).
//
// Threading: UI thread only (the composed image is handed to the player / renderer like any
// canvas; the renderer clones it).

namespace CsoundVisual.Paint;

/// <summary>One placement of a pattern on the timeline (immutable: edits replace the record).</summary>
/// <param name="Id">Unique id inside the document (selection, undo).</param>
/// <param name="PatternId">The <see cref="SeqPattern.Id"/> it plays.</param>
/// <param name="Lane">Lane (row of the timeline), 0 = top. Lanes only organise the view: all lanes sound together.</param>
/// <param name="StartStep">First step (0 = start of bar 1).</param>
/// <param name="Repeats">How many times the pattern plays back to back (1…64).</param>
/// <param name="Transpose">Rows to shift up (+) or down (−); on a 12-tone axis one row = one semitone.</param>
/// <param name="Reverse">Play the pattern's steps backwards.</param>
/// <param name="Invert">Mirror the pattern upside down inside the rows it uses (melodic inversion).</param>
/// <param name="GainDb">Gain in dB (amplitude), −48…+12.</param>
/// <param name="Length">
/// Length of the clip in steps, chosen by drawing it or dragging its right edge; 0 (the default,
/// and every clip saved before this existed) = the pattern's steps × <paramref name="Repeats"/>.
/// </param>
/// <param name="Stretch">
/// False: the pattern plays at its own speed and loops (or is cut) to fill the clip. True: ONE
/// pass of the pattern is squeezed or stretched in time to fill the clip exactly.
/// </param>
public sealed record SeqClip(int Id, int PatternId, int Lane, int StartStep, int Repeats = 1, int Transpose = 0,
    bool Reverse = false, bool Invert = false, double GainDb = 0, int Length = 0, bool Stretch = false);

/// <summary>A painted pattern: its own small canvas (with its own undo history) plus a name.</summary>
public sealed class SeqPattern
{
    /// <summary>Creates a pattern around an existing image.</summary>
    /// <param name="id">Unique id (kept in clips and in the settings).</param>
    /// <param name="name">Display name.</param>
    /// <param name="image">The pattern's picture (one column per step).</param>
    /// <param name="canvasPrefs">Settings object the pattern's session uses (shared by all patterns).</param>
    public SeqPattern(int id, string name, SpectralImage image, PaintRoomSettings canvasPrefs)
    {
        Id = id;
        Name = name;
        Session = new PaintSession(canvasPrefs) { ImageName = name };
        // Not undoable: the history starts empty with this picture.
        Session.ReplaceImage(image, "pattern", undoable: false);
    }

    /// <summary>Unique id inside the document.</summary>
    public int Id { get; }

    /// <summary>Display name («Bass», «Arpeggio»…).</summary>
    public string Name { get; set; }

    /// <summary>Join a lit step with the next lit step on the same row into one held note (no re-attack).</summary>
    public bool Legato { get; set; }

    /// <summary>Colour slot for the timeline (index into the room's palette of theme roles).</summary>
    public int ColorIndex { get; set; }

    /// <summary>The pattern's own paint session: image, undo history, selection, clipboard.</summary>
    public PaintSession Session { get; }

    /// <summary>The pattern picture (columns = steps, rows = the document's pitch axis).</summary>
    public SpectralImage Image => Session.Image;

    /// <summary>Length in steps (= image width).</summary>
    public int Steps => Image.Width;
}

/// <summary>Persisted state of the Image Sequencer room (inside <see cref="PaintRoomSettings.Sequencer"/>).</summary>
/// <remarks>Plain properties for System.Text.Json. Pattern pixels are stored compactly as base64 16-bit levels.</remarks>
public sealed class SequencerPrefs
{
    /// <summary>Tempo in beats per minute (20…400).</summary>
    public double Tempo { get; set; } = 120;
    /// <summary>Steps per beat (1…8; 4 = sixteenth notes in 4/4).</summary>
    public int StepsPerBeat { get; set; } = 4;
    /// <summary>Beats per bar (1…16).</summary>
    public int BeatsPerBar { get; set; } = 4;
    /// <summary>Length of the arrangement in bars (1…64); the loop length.</summary>
    public int Bars { get; set; } = 8;
    /// <summary>Number of lanes shown (1…8).</summary>
    public int Lanes { get; set; } = 4;
    /// <summary>Note length as a fraction of a step (0.125…1), see the file header.</summary>
    public double Gate { get; set; } = 0.5;
    /// <summary>Pitch axis preset (see <see cref="SequencerDocument.AxisForPreset"/>).</summary>
    public int AxisPreset { get; set; }
    /// <summary>Loop the arrangement while playing.</summary>
    public bool Loop { get; set; } = true;
    /// <summary>Synthesis settings of the room (Glide is always forced off when playing).</summary>
    public PaintSynthSettings Synth { get; set; } = SequencerDocument.DefaultSynth;
    /// <summary>Canvas / brush preferences of the pattern editor (tool, brush size, noise…).</summary>
    public PaintRoomSettings Canvas { get; set; } = SequencerDocument.DefaultCanvasPrefs();
    /// <summary>The patterns (empty = start with the built-in example).</summary>
    public List<SeqPatternData> Patterns { get; set; } = [];
    /// <summary>The clips of the arrangement.</summary>
    public List<SeqClip> Clips { get; set; } = [];
    /// <summary>Id of the pattern shown in the editor.</summary>
    public int SelectedPattern { get; set; }
    /// <summary>
    /// True once the user SAVED this document (File ▾ › Save for next session, or «Save» on close).
    /// False in settings written by older versions, which saved automatically: such a document is
    /// treated as unsaved work, so the user is asked about it and «Don't save» forgets it.
    /// </summary>
    public bool Saved { get; set; }
}

/// <summary>A pattern as stored in the settings.</summary>
public sealed class SeqPatternData
{
    /// <summary>Pattern id.</summary>
    public int Id { get; set; }
    /// <summary>Display name.</summary>
    public string Name { get; set; } = "";
    /// <summary>Steps (image width).</summary>
    public int Steps { get; set; }
    /// <summary>Rows when saved (must match the axis preset; otherwise the pattern is retuned on load).</summary>
    public int Rows { get; set; }
    /// <summary>Legato flag.</summary>
    public bool Legato { get; set; }
    /// <summary>Colour slot.</summary>
    public int Color { get; set; }
    /// <summary>Left levels, base64 of little-endian ushorts (level × 65535), column-major.</summary>
    public string Left { get; set; } = "";
    /// <summary>Right levels, same encoding.</summary>
    public string Right { get; set; } = "";
    /// <summary>Noise layer, same encoding, or <c>null</c> when the pattern has none.</summary>
    public string? Noise { get; set; }
}

/// <summary>The sequencer's document: timing, pitch axis, patterns and clips (see the file header).</summary>
public sealed class SequencerDocument
{
    /// <summary>Largest arrangement in steps (64 bars of 4/4 at 16ths = 1024; the composed canvas stays ≤ 8192 columns).</summary>
    public const int MaxTotalSteps = 4096;

    /// <summary>Longest pattern in steps.</summary>
    public const int MaxPatternSteps = 256;

    /// <summary>Next free id for patterns and clips.</summary>
    int nextId = 1;

    /// <summary>Creates an empty document on <paramref name="axisPreset"/>.</summary>
    /// <param name="canvasPrefs">Settings shared by the pattern sessions (brush, tool…).</param>
    /// <param name="axisPreset">Pitch axis preset.</param>
    public SequencerDocument(PaintRoomSettings canvasPrefs, int axisPreset = 0)
    {
        CanvasPrefs = canvasPrefs;
        AxisPreset = Math.Clamp(axisPreset, 0, AxisPresetCount - 1);
        Axis = AxisForPreset(AxisPreset);
    }

    /// <summary>Default synthesis for sequences: sines, quick 3 ms attack, 60 ms release, no glide, loop.</summary>
    public static PaintSynthSettings DefaultSynth => new() { AttackMs = 3, ReleaseMs = 60, Glide = false, Loop = true };

    /// <summary>Canvas preferences for a new sequencer: 12-tone axis, 16 columns, pencil with a 2-pixel size —
    /// the brush radius is never smaller than half a cell, so a click paints exactly ONE note on ONE step.</summary>
    /// <returns>A new settings object (the pattern sessions only use it for their initial blank image and the brush).</returns>
    public static PaintRoomSettings DefaultCanvasPrefs() => new()
    {
        AxisKind = PitchAxisKind.EqualTemperament, LowHz = 32.70, HighHz = 8372.02, StepsPerOctave = 12,
        Width = 16, DurationSeconds = 2, BrushSize = 2, Opacity = 0.85, Hardness = 1, Tool = PaintTool.Pencil,
    };

    /// <summary>Number of pitch axis presets.</summary>
    public const int AxisPresetCount = 3;

    /// <summary>
    /// The pitch axis presets: 0 = notes C1…C9 (12-tone, 97 rows: bass to hi-hats), 1 = notes C2…C7
    /// (61 rows, a compact keyboard), 2 = logarithmic 40 Hz…16 kHz, 192 rows (free pitch).
    /// </summary>
    /// <param name="preset">0…2 (clamped).</param>
    /// <returns>The axis.</returns>
    public static PitchAxis AxisForPreset(int preset) => Math.Clamp(preset, 0, AxisPresetCount - 1) switch
    {
        1 => PitchAxis.EqualTemperament(65.40, 2093.01, 12),
        2 => PitchAxis.Logarithmic(40, 16000, 192),
        _ => PitchAxis.EqualTemperament(32.70, 8372.02, 12),
    };

    /// <summary>Shared canvas preferences of the pattern sessions.</summary>
    public PaintRoomSettings CanvasPrefs { get; }

    /// <summary>Current axis preset.</summary>
    public int AxisPreset { get; private set; }

    /// <summary>The pitch axis of every pattern and of the composed canvas.</summary>
    public PitchAxis Axis { get; private set; }

    /// <summary>Tempo (BPM).</summary>
    public double Tempo { get; set; } = 120;

    /// <summary>Steps per beat.</summary>
    public int StepsPerBeat { get; set; } = 4;

    /// <summary>Beats per bar.</summary>
    public int BeatsPerBar { get; set; } = 4;

    /// <summary>Arrangement length in bars.</summary>
    public int Bars { get; set; } = 8;

    /// <summary>Lanes shown.</summary>
    public int Lanes { get; set; } = 4;

    /// <summary>Note length as a fraction of a step.</summary>
    public double Gate { get; set; } = 0.5;

    /// <summary>Synthesis settings (Glide is forced off when composing for playback).</summary>
    public PaintSynthSettings Synth { get; set; } = DefaultSynth;

    /// <summary>The patterns, in library order.</summary>
    public List<SeqPattern> Patterns { get; } = [];

    /// <summary>The clips (order does not matter for the sound).</summary>
    public List<SeqClip> Clips { get; } = [];

    /// <summary>Seconds per step: 60 / (tempo · steps per beat).</summary>
    public double StepSeconds => 60.0 / (Math.Clamp(Tempo, 20, 400) * Math.Max(1, StepsPerBeat));

    /// <summary>Steps per bar.</summary>
    public int StepsPerBar => Math.Max(1, StepsPerBeat) * Math.Max(1, BeatsPerBar);

    /// <summary>Arrangement length in steps (bars × steps per bar, capped at <see cref="MaxTotalSteps"/>).</summary>
    public int TotalSteps => Math.Clamp(Bars * StepsPerBar, 1, MaxTotalSteps);

    /// <summary>Finds a pattern by id.</summary>
    /// <param name="id">Pattern id.</param>
    /// <returns>The pattern or <c>null</c>.</returns>
    public SeqPattern? Find(int id) => Patterns.FirstOrDefault(p => p.Id == id);

    /// <summary>A new unique id (patterns and clips share the counter).</summary>
    /// <returns>The id.</returns>
    public int NewId() => nextId++;

    /// <summary>Length of a clip in steps (pattern steps × repeats; 0 when its pattern is gone).</summary>
    /// <param name="c">The clip.</param>
    public int ClipSteps(SeqClip c) => Find(c.PatternId) is not { } p ? 0 : c.Length > 0 ? c.Length : p.Steps * Math.Max(1, c.Repeats);

    /// <summary>
    /// A copy of <paramref name="src"/> squeezed or stretched in time to <paramref name="steps"/>
    /// columns (pattern «Stretch»): the content keeps its shape, only its length changes.
    /// </summary>
    /// <param name="src">The pattern image.</param>
    /// <param name="steps">New length in steps (clamped 2…<see cref="MaxPatternSteps"/>).</param>
    /// <param name="stepSeconds">Seconds per step (the duration becomes steps × this).</param>
    /// <returns>A new image on the same axis; <paramref name="src"/> is not modified.</returns>
    /// <remarks>
    /// Step-wise rules instead of <see cref="SpectralImage.Retuned"/>'s smooth resampling, because a
    /// pattern is a grid of on/off steps: STRETCHING repeats each source step (nearest column, so a
    /// lit step becomes two lit steps at ×2, never a fade between steps); SQUEEZING keeps the
    /// LOUDEST of the source steps that fall into a destination step (a short note is not halved in
    /// level by being averaged with silence). The noise layer follows the cell that won.
    /// </remarks>
    public static SpectralImage TimeStretched(SpectralImage src, int steps, double stepSeconds)
    {
        steps = Math.Clamp(steps, 2, MaxPatternSteps);
        var dst = new SpectralImage(steps, src.Axis, steps * stepSeconds);
        int h = src.Height;
        var sn = src.Noise;
        var dn = sn is null ? null : dst.EnsureNoise();
        double scale = (double)src.Width / steps;   // source columns per destination column
        for (int x = 0; x < steps; x++)
        {
            // Source columns [a, b) covered by destination column x (at least one: the nearest).
            int a = Math.Min(src.Width - 1, (int)Math.Floor(x * scale));
            int b = Math.Max(a + 1, Math.Min(src.Width, (int)Math.Ceiling((x + 1) * scale - 1e-9)));
            if (scale <= 1) { a = Math.Min(src.Width - 1, (int)Math.Floor((x + 0.5) * scale)); b = a + 1; }
            int o = x * h;
            for (int sx = a; sx < b; sx++)
            {
                int so = sx * h;
                for (int y = 0; y < h; y++)
                {
                    float l = src.Left[so + y], r = src.Right[so + y];
                    float loudNow = MathF.Max(dst.Left[o + y], dst.Right[o + y]);
                    if (MathF.Max(l, r) > loudNow && dn is not null) dn[o + y] = sn![so + y];
                    if (l > dst.Left[o + y]) dst.Left[o + y] = l;
                    if (r > dst.Right[o + y]) dst.Right[o + y] = r;
                }
            }
        }
        return dst;
    }

    /// <summary>Adds an empty pattern on the document's axis.</summary>
    /// <param name="name">Display name.</param>
    /// <param name="steps">Length in steps (clamped 2…<see cref="MaxPatternSteps"/>).</param>
    /// <returns>The new pattern (appended to the library).</returns>
    public SeqPattern AddPattern(string name, int steps)
    {
        var img = new SpectralImage(Math.Clamp(steps, 2, MaxPatternSteps), Axis, Math.Clamp(steps, 2, MaxPatternSteps) * StepSeconds);
        return AddPattern(name, img);
    }

    /// <summary>Adds a pattern around an existing picture (it must be on <see cref="Axis"/>; otherwise it is retuned).</summary>
    /// <param name="name">Display name.</param>
    /// <param name="image">The picture (one column per step).</param>
    /// <returns>The new pattern.</returns>
    public SeqPattern AddPattern(string name, SpectralImage image)
    {
        if (!image.Axis.Equals(Axis)) image = image.Retuned(image.Width, Axis);
        image.DurationSeconds = image.Width * StepSeconds;
        var p = new SeqPattern(NewId(), name, image, CanvasPrefs) { ColorIndex = Patterns.Count };
        Patterns.Add(p);
        return p;
    }

    /// <summary>Copies a pattern (pixels, legato) under a new name; clips are not duplicated.</summary>
    /// <param name="p">The pattern to copy.</param>
    /// <param name="name">Name of the copy.</param>
    /// <returns>The copy, appended to the library.</returns>
    public SeqPattern Duplicate(SeqPattern p, string name)
    {
        var copy = AddPattern(name, p.Image.Clone());
        copy.Legato = p.Legato;
        return copy;
    }

    /// <summary>Removes a pattern and every clip that plays it.</summary>
    /// <param name="p">The pattern.</param>
    public void RemovePattern(SeqPattern p)
    {
        Patterns.Remove(p);
        Clips.RemoveAll(c => c.PatternId == p.Id);
    }

    /// <summary>Switches the pitch axis preset and retunes every pattern (each keeps its pitches in Hz; undoable per pattern).</summary>
    /// <param name="preset">New preset.</param>
    public void SetAxis(int preset)
    {
        preset = Math.Clamp(preset, 0, AxisPresetCount - 1);
        if (preset == AxisPreset) return;
        AxisPreset = preset;
        Axis = AxisForPreset(preset);
        foreach (var p in Patterns) p.Session.Retune(Axis);
    }

    /// <summary>Sets every pattern's duration from the tempo (so the pattern editor's time ruler shows real seconds).</summary>
    public void UpdatePatternDurations()
    {
        foreach (var p in Patterns) p.Image.DurationSeconds = p.Steps * StepSeconds;
    }

    /// <summary>Row of a MIDI note on the current axis (nearest row; −1 when outside).</summary>
    /// <param name="midi">MIDI note number (60 = C4 = 261.63 Hz).</param>
    public int RowOfMidi(int midi) => Axis.NearestRow(440 * Math.Pow(2, (midi - 69) / 12.0));

    // ------------------------------------------------------------------------------------------
    //  Persistence
    // ------------------------------------------------------------------------------------------

    /// <summary>Writes the document into <paramref name="prefs"/> (patterns encoded as base64 16-bit levels).</summary>
    /// <param name="prefs">Target settings object (its Canvas reference is kept as is).</param>
    /// <param name="selectedPattern">Id of the pattern shown in the editor.</param>
    /// <param name="loop">Loop flag of the room.</param>
    public void SaveTo(SequencerPrefs prefs, int selectedPattern, bool loop)
    {
        prefs.Tempo = Tempo; prefs.StepsPerBeat = StepsPerBeat; prefs.BeatsPerBar = BeatsPerBar; prefs.Bars = Bars;
        prefs.Lanes = Lanes; prefs.Gate = Gate; prefs.AxisPreset = AxisPreset; prefs.Synth = Synth; prefs.Loop = loop;
        prefs.SelectedPattern = selectedPattern;
        prefs.Clips = [.. Clips];
        prefs.Patterns = Patterns.Select(p => new SeqPatternData
        {
            Id = p.Id, Name = p.Name, Steps = p.Steps, Rows = p.Image.Height, Legato = p.Legato, Color = p.ColorIndex,
            Left = Encode(p.Image.Left), Right = Encode(p.Image.Right), Noise = p.Image.Noise is { } n ? Encode(n) : null,
        }).ToList();
    }

    /// <summary>Rebuilds a document from <paramref name="prefs"/>; an empty or unreadable one gives the starter example.</summary>
    /// <param name="prefs">The saved settings.</param>
    /// <returns>The document.</returns>
    public static SequencerDocument Load(SequencerPrefs prefs)
    {
        if (prefs.Patterns.Count == 0) return Starter(prefs.Canvas);
        try
        {
            var doc = new SequencerDocument(prefs.Canvas, prefs.AxisPreset)
            {
                Tempo = Math.Clamp(prefs.Tempo, 20, 400), StepsPerBeat = Math.Clamp(prefs.StepsPerBeat, 1, 8),
                BeatsPerBar = Math.Clamp(prefs.BeatsPerBar, 1, 16), Bars = Math.Clamp(prefs.Bars, 1, 64),
                Lanes = Math.Clamp(prefs.Lanes, 1, 8), Gate = Math.Clamp(prefs.Gate, 0.125, 1), Synth = prefs.Synth ?? DefaultSynth,
            };
            foreach (var d in prefs.Patterns)
            {
                // Decode at the SAVED size, then retune if the axis preset's row count changed meanwhile.
                int rows = d.Rows > 0 ? d.Rows : doc.Axis.RowCount;
                var axis = rows == doc.Axis.RowCount ? doc.Axis : PitchAxis.Logarithmic(40, 16000, Math.Clamp(rows, PitchAxis.MinRows, PitchAxis.MaxRows));
                var img = new SpectralImage(Math.Clamp(d.Steps, 2, MaxPatternSteps), axis, 1);
                Decode(d.Left, img.Left);
                Decode(d.Right, img.Right);
                if (d.Noise is { } n) Decode(n, img.EnsureNoise());
                if (!img.Axis.Equals(doc.Axis)) img = img.Retuned(img.Width, doc.Axis);
                img.DurationSeconds = img.Width * doc.StepSeconds;
                // The SAVED id is kept, so the saved clips still find their pattern.
                doc.Patterns.Add(new SeqPattern(d.Id, d.Name, img, prefs.Canvas) { Legato = d.Legato, ColorIndex = d.Color });
                doc.nextId = Math.Max(doc.nextId, d.Id + 1);
            }
            foreach (var c in prefs.Clips)
                if (doc.Find(c.PatternId) is not null)
                {
                    doc.Clips.Add(c with { Lane = Math.Clamp(c.Lane, 0, 7), Repeats = Math.Clamp(c.Repeats, 1, 64), Length = Math.Clamp(c.Length, 0, MaxTotalSteps) });
                    doc.nextId = Math.Max(doc.nextId, c.Id + 1);
                }
            return doc;
        }
        catch
        {
            // Corrupt or incompatible data must never prevent the room from opening.
            return Starter(prefs.Canvas);
        }
    }

    /// <summary>Levels → base64 of little-endian ushorts (round(level · 65535)): 2 bytes per cell, lossless to 1/65535.</summary>
    /// <param name="layer">A layer (values clamped to 0…1).</param>
    static string Encode(float[] layer)
    {
        var bytes = new byte[layer.Length * 2];
        for (int i = 0; i < layer.Length; i++)
        {
            ushort v = (ushort)Math.Round(Math.Clamp(layer[i], 0f, 1f) * 65535);
            bytes[2 * i] = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        return Convert.ToBase64String(bytes);
    }

    /// <summary>Inverse of <see cref="Encode"/>; fills as many cells as both sides have.</summary>
    /// <param name="text">The base64 text.</param>
    /// <param name="layer">Target layer.</param>
    static void Decode(string text, float[] layer)
    {
        var bytes = Convert.FromBase64String(text);
        int n = Math.Min(layer.Length, bytes.Length / 2);
        for (int i = 0; i < n; i++) layer[i] = (bytes[2 * i] | (bytes[2 * i + 1] << 8)) / 65535f;
    }

    // ------------------------------------------------------------------------------------------
    //  Starter content
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// The example: three patterns generated in code — «Drums» (a sine kick, a snare of noise over a
    /// tonal body, noisy hi-hats: shows off the noise layer), «Bass» (a C minor line with two
    /// harmonics) and «Arpeggio» (Cm7 up and down, alternating left / right) — and an 8-bar
    /// arrangement that transposes the bass and the arpeggio to F and G, reverses one arpeggio
    /// and inverts another.
    /// </summary>
    /// <param name="canvasPrefs">Canvas preferences for the pattern sessions.</param>
    /// <returns>A new document on the C1…C9 axis at 120 BPM, 16ths, gate ½.</returns>
    public static SequencerDocument Starter(PaintRoomSettings canvasPrefs)
    {
        var doc = new SequencerDocument(canvasPrefs, 0);
        // Paints a note: level on the chosen sides, plus harmonics k·f at level·k^−1.5 (rows from Hz).
        void Note(SpectralImage img, int step, int midi, float level, int harmonics = 1, float pan = 0, float noise = 0)
        {
            double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
            for (int k = 1; k <= harmonics; k++)
            {
                int row = img.Axis.NearestRow(f * k);
                if (row < 0) break;
                float lv = level * (float)Math.Pow(k, -1.5);
                // Linear balance law like the brush: the far side fades, the near side stays full.
                img.Set(step, row, MathF.Max(img.GetLeft(step, row), lv * MathF.Min(1, 1 - pan)), MathF.Max(img.GetRight(step, row), lv * MathF.Min(1, 1 + pan)));
                if (noise > 0) img.EnsureNoise()[img.Index(step, row)] = noise;
            }
        }
        // Noise band: every row between two MIDI notes at one step.
        void Band(SpectralImage img, int step, int lowMidi, int highMidi, float level, float noise)
        {
            for (int m = lowMidi; m <= highMidi; m++) Note(img, step, m, level, 1, 0, noise);
        }

        var drums = doc.AddPattern("Drums", 16);
        var d = drums.Image;
        for (int s = 0; s < 16; s += 4) { Note(d, s, 29, 0.95f); Note(d, s, 31, 0.8f); }      // kick: F1 + G1 thump (44 + 49 Hz)
        foreach (int s in new[] { 4, 12 })
        {
            Note(d, s, 50, 0.7f);                                                                 // snare body: D3 tone
            Band(d, s, 62, 86, 0.42f, 1);                                                         // snare rattle: noise D4…D6
        }
        for (int s = 0; s < 16; s += 2) Band(d, s, 106, 117, s == 14 ? 0.5f : 0.32f, 1);         // hats: noise A#7…A8 (3.7–7 kHz)
        drums.Legato = false;

        var bass = doc.AddPattern("Bass", 16);
        var b = bass.Image;
        (int Step, int Midi)[] line = [(0, 36), (2, 36), (3, 48), (6, 36), (8, 39), (10, 41), (11, 43), (14, 34)];
        foreach (var (s, m) in line) Note(b, s, m, 0.85f, harmonics: 3);

        var arp = doc.AddPattern("Arpeggio", 16);
        var a = arp.Image;
        int[] cm7 = [60, 63, 67, 70, 72, 70, 67, 63];
        for (int s = 0; s < 16; s++) Note(a, s, cm7[s % 8], 0.6f, harmonics: 2, pan: s % 2 == 0 ? -0.6f : 0.6f);

        int bar = doc.StepsPerBar;
        void Clip(SeqPattern p, int lane, int startBar, int repeats, int transpose = 0, bool reverse = false, bool invert = false, double gainDb = 0) =>
            doc.Clips.Add(new SeqClip(doc.NewId(), p.Id, lane, startBar * bar, repeats, transpose, reverse, invert, gainDb));
        Clip(drums, 0, 0, 8);
        Clip(bass, 1, 0, 4);
        Clip(bass, 1, 4, 2, transpose: 5);
        Clip(bass, 1, 6, 2, transpose: 7);
        Clip(arp, 2, 2, 2, gainDb: -2);
        Clip(arp, 2, 4, 2, transpose: 5, gainDb: -2);
        Clip(arp, 2, 6, 1, transpose: 7, reverse: true, gainDb: -2);
        Clip(arp, 2, 7, 1, transpose: 7, invert: true, gainDb: -4);
        doc.UpdatePatternDurations();
        return doc;
    }
}

/// <summary>Turns a <see cref="SequencerDocument"/> into one playable canvas (see the file header).</summary>
public static class SequencerComposer
{
    /// <summary>Largest number of composed columns per step.</summary>
    public const int MaxSub = 8;

    /// <summary>Composed columns per step for an arrangement of <paramref name="totalSteps"/> steps: 8, fewer for very long ones (canvas ≤ 8192 columns).</summary>
    /// <param name="totalSteps">Steps of the arrangement.</param>
    public static int SubColumns(int totalSteps) => Math.Clamp(SpectralImage.MaxWidth / Math.Max(1, totalSteps), 1, MaxSub);

    /// <summary>Composes the whole arrangement.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>A new canvas: TotalSteps × Sub columns on the document's axis, duration TotalSteps × StepSeconds.</returns>
    public static SpectralImage Compose(SequencerDocument doc) => Compose(doc, doc.Clips);

    /// <summary>Composes an arbitrary list of clips (e.g. a single pattern for the «solo» preview).</summary>
    /// <param name="doc">The document (timing, axis, patterns, gate, level curve).</param>
    /// <param name="clips">The clips to stamp.</param>
    /// <param name="totalSteps">Length in steps (default: the arrangement's).</param>
    /// <returns>The composed canvas.</returns>
    /// <remarks>
    /// For every clip and every step d of the clip (0 … ClipSteps − 1, destination step StartStep + d)
    /// the SOURCE columns are <see cref="SourceColumns"/>: column d mod Steps normally (the pattern
    /// loops, the last pass may be cut), or the columns that fall into d when the clip is
    /// STRETCHED (one pass fitted to the clip: a stretched step repeats a column, a squeezed one
    /// merges several with max); mirrored (Steps−1−s) when reversed. Every lit source cell (level
    /// &gt; 0 on either side) at row y goes to row y' = (inverted ? lo + hi − y : y) + transpose, where
    /// lo…hi are the lowest and highest lit rows of the pattern (so an inversion stays in the same
    /// register); rows that fall off the axis are dropped. It is written into the first g =
    /// round(Gate·Sub) columns of the step (all Sub columns when the pattern is legato and the next
    /// step in playback order has the same row lit). Overlapping notes keep the louder level per
    /// side (max), and the noise of whichever source cell is louder. Steps past the end are dropped.
    /// </remarks>
    public static SpectralImage Compose(SequencerDocument doc, IEnumerable<SeqClip> clips, int? totalSteps = null)
    {
        int total = Math.Clamp(totalSteps ?? doc.TotalSteps, 1, SequencerDocument.MaxTotalSteps);
        int sub = SubColumns(total);
        var axis = doc.Axis;
        int H = axis.RowCount;
        var img = new SpectralImage(total * sub, axis, total * doc.StepSeconds);
        int gateCols = Math.Clamp((int)Math.Round(sub * doc.Gate), 1, sub);
        var synth = doc.Synth;
        double floor = Math.Clamp(synth.Floor, 0, 0.5), curve = Math.Clamp(synth.Curve, 0.25, 6);
        float[]? outN = null;
        foreach (var clip in clips)
        {
            if (doc.Find(clip.PatternId) is not { } pat || pat.Image.Height != H) continue;
            var src = pat.Image;
            int ps = src.Width;
            var srcN = src.Noise;
            if (srcN is not null) outN ??= img.EnsureNoise();
            // Lit row range of the pattern (for the inversion).
            int lo = int.MaxValue, hi = -1;
            for (int i = 0; i < src.Left.Length; i++)
                if (src.Left[i] > 0 || src.Right[i] > 0) { int y = i % H; lo = Math.Min(lo, y); hi = Math.Max(hi, y); }
            if (hi < 0) continue;
            // Gain → level scale above the floor: amplitude × g ⇔ (ℓ − f) × g^(1/curve).
            float k = (float)Math.Pow(Math.Pow(10, Math.Clamp(clip.GainDb, -48, 12) / 20), 1 / curve);
            float Scale(float level) => level <= floor ? level : (float)Math.Min(1, floor + (level - floor) * k);
            int len = doc.ClipSteps(clip);
            for (int d = 0; d < len; d++)
            {
                int step = clip.StartStep + d;
                if (step < 0) continue;
                if (step >= total) break;
                var (a, b) = SourceColumns(d, len, ps, clip.Stretch);
                // The source column heard at the NEXT step (for legato), or −1 at the clip's end.
                int nextCol = d + 1 >= len ? -1 : SourceColumns(d + 1, len, ps, clip.Stretch).A;
                if (nextCol >= 0 && clip.Reverse) nextCol = ps - 1 - nextCol;
                for (int s = a; s < b; s++)
                {
                    int col = clip.Reverse ? ps - 1 - s : s;
                    for (int y = 0; y < H; y++)
                    {
                        int si = col * H + y;
                        float l = src.Left[si], rr = src.Right[si];
                        if (l <= 0 && rr <= 0) continue;
                        int ty = (clip.Invert ? lo + hi - y : y) + clip.Transpose;
                        if ((uint)ty >= (uint)H) continue;
                        bool join = pat.Legato && nextCol >= 0 &&
                                    (src.Left[nextCol * H + y] > 0.02f || src.Right[nextCol * H + y] > 0.02f);
                        int cols = join ? sub : gateCols;
                        float sl = Scale(l), sr = Scale(rr);
                        float sn = srcN?[si] ?? 0f;
                        for (int c = 0; c < cols; c++)
                        {
                            int di = (step * sub + c) * H + ty;
                            float old = MathF.Max(img.Left[di], img.Right[di]);
                            if (outN is not null && MathF.Max(sl, sr) >= old) outN[di] = sn;   // the louder cell decides the noise
                            if (sl > img.Left[di]) img.Left[di] = sl;
                            if (sr > img.Right[di]) img.Right[di] = sr;
                        }
                    }
                }
            }
        }
        return img;
    }

    /// <summary>Pattern columns heard at step <paramref name="d"/> of a clip (before any reversal).</summary>
    /// <param name="d">Step inside the clip (0 … len − 1).</param>
    /// <param name="len">Clip length in steps.</param>
    /// <param name="ps">Pattern length in steps.</param>
    /// <param name="stretch">True when one pass of the pattern is fitted to the clip.</param>
    /// <returns>The source columns [A, B) (always at least one).</returns>
    /// <remarks>
    /// Not stretched: column d mod ps (the pattern loops). Stretched: the columns whose start falls
    /// in [d, d+1)·ps/len — exactly one when stretching (a column is repeated), possibly several
    /// when squeezing (they are merged, louder wins); integer arithmetic so no column is skipped or
    /// doubled by rounding.
    /// </remarks>
    public static (int A, int B) SourceColumns(int d, int len, int ps, bool stretch)
    {
        if (!stretch || len <= 0) { int c = d % ps; return (c, c + 1); }
        int a = (int)((long)d * ps / len);
        int b = (int)((long)(d + 1) * ps / len);
        return (Math.Min(a, ps - 1), Math.Clamp(b, a + 1, ps));
    }

    /// <summary>
    /// Copies <paramref name="from"/> into <paramref name="into"/> (same size) touching only the
    /// columns that differ, and notifies exactly that range (so the live player uploads only those).
    /// </summary>
    /// <param name="from">The freshly composed canvas.</param>
    /// <param name="into">The canvas being played.</param>
    /// <returns>True when anything changed.</returns>
    /// <exception cref="ArgumentException">The sizes differ.</exception>
    public static bool CopyChanged(SpectralImage from, SpectralImage into)
    {
        if (from.Width != into.Width || from.Height != into.Height) throw new ArgumentException("Sizes differ.");
        int h = from.Height, x0 = -1, x1 = -1;
        var fn = from.Noise;
        var tn = fn is not null ? into.EnsureNoise() : into.Noise;
        for (int x = 0; x < from.Width; x++)
        {
            bool diff = false;
            for (int i = x * h; i < (x + 1) * h && !diff; i++)
                diff = from.Left[i] != into.Left[i] || from.Right[i] != into.Right[i] || (tn is not null && (fn?[i] ?? 0f) != tn[i]);
            if (!diff) continue;
            if (x0 < 0) x0 = x;
            x1 = x + 1;
        }
        if (x0 < 0) return false;
        int o = x0 * h, n = (x1 - x0) * h;
        Array.Copy(from.Left, o, into.Left, o, n);
        Array.Copy(from.Right, o, into.Right, o, n);
        if (tn is not null)
        {
            if (fn is not null) Array.Copy(fn, o, tn, o, n);
            else Array.Clear(tn, o, n);
        }
        into.NotifyChanged(x0, x1);
        return true;
    }
}
