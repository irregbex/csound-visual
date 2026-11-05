namespace CsoundVisual.Music;

/// <summary>One row of the step sequencer.</summary>
/// <remarks>
/// Mutable and edited only on the UI thread; the scheduler sees copies made by
/// <see cref="StepPattern.Clone"/>. Each active step becomes <c>i &lt;Instrument&gt; t dur &lt;Note&gt; &lt;velocity&gt;</c>
/// with p4/p5 written according to the pattern's <see cref="PitchMapping"/>/<see cref="VelocityMapping"/>.
/// </remarks>
public sealed class StepTrack
{
    /// <summary>Maximum number of steps a pattern can have.</summary>
    public const int MaxSteps = 32;

    /// <summary>Display name (e.g. «Kick»).</summary>
    public string Name { get; set; } = "";

    /// <summary>Target instrument id (number or name, unquoted).</summary>
    public string Instrument { get; set; } = "1";

    /// <summary>MIDI note sent as p4 (drums use it to tune; melodic instruments as pitch).</summary>
    public int Note { get; set; } = 60;

    /// <summary>Velocity given to a step when it is switched on, 1…127.</summary>
    public int Velocity { get; set; } = 100;

    /// <summary>Muted tracks do not play.</summary>
    public bool Mute { get; set; }

    /// <summary>When any track is soloed, only soloed tracks play.</summary>
    public bool Solo { get; set; }

    /// <summary>Chance (0…100 %) that an active step really plays on each pass.</summary>
    public int Probability { get; set; } = 100;

    /// <summary>Per-step velocity: 0 = step off, 1…127 = on with that velocity.</summary>
    public int[] Steps { get; set; } = new int[MaxSteps];

    /// <summary>A deep copy.</summary>
    public StepTrack Clone() => new()
    {
        Name = Name, Instrument = Instrument, Note = Note, Velocity = Velocity, Mute = Mute, Solo = Solo,
        Probability = Probability, Steps = (int[])Steps.Clone(),
    };
}

/// <summary>
/// The step sequencer's pattern: tracks × steps (sixteenth notes), tempo, swing and the p-field
/// mapping. Also the Euclidean and random fills and the default drum-kit groove.
/// </summary>
/// <remarks>
/// <para><b>Timing.</b> A step is a sixteenth note (0.25 beat). Swing delays every odd step
/// (the «and» sixteenths: 2nd, 4th…) by <c>swing × step length</c>: 0 = straight, 0.33 ≈ triplet
/// feel, 0.5 = dotted feel, 0.75 = maximum. The pattern loops every <c>StepCount × 0.25</c> beats.</para>
/// <para>The UI edits a <see cref="StepPattern"/> on the UI thread and hands the scheduler a
/// <see cref="Clone"/> after every change, so the scheduler thread never sees a half-made edit.</para>
/// </remarks>
public sealed class StepPattern : IPatternSource
{
    /// <summary>Length of one step, beats (a sixteenth note).</summary>
    public const double StepBeats = 0.25;

    /// <summary>Rows.</summary>
    public List<StepTrack> Tracks { get; set; } = [];

    /// <summary>Steps per pattern: 8, 16 or 32.</summary>
    public int StepCount { get; set; } = 16;

    /// <summary>Tempo, BPM.</summary>
    public double Bpm { get; set; } = 110;

    /// <summary>Swing, 0…0.75 (fraction of a step the odd steps are delayed by).</summary>
    public double Swing { get; set; }

    /// <summary>How many times the pattern is written when exported to the score.</summary>
    public int ExportLoops { get; set; } = 4;

    /// <summary>How the note goes into p4.</summary>
    public PitchMapping PitchMapping { get; set; } = PitchMapping.MidiNote;

    /// <summary>How the velocity goes into p5.</summary>
    public VelocityMapping VelocityMapping { get; set; } = VelocityMapping.Unit;

    /// <summary>Random source for the per-track probability (the scheduler thread only).</summary>
    readonly Random random = new();

    /// <summary>Pattern length, beats.</summary>
    public double LengthBeats => StepCount * StepBeats;

    /// <summary>Start of step <paramref name="step"/> in beats, with swing applied.</summary>
    /// <param name="step">Step index, 0-based (odd indices are the swung «and» sixteenths).</param>
    public double StepStart(int step) => step * StepBeats + (step % 2 == 1 ? Math.Clamp(Swing, 0, 0.75) * StepBeats : 0);

    /// <summary>True when the track plays, considering mute and solo.</summary>
    public bool IsAudible(StepTrack t) => !t.Mute && (!Tracks.Any(x => x.Solo) || t.Solo);

    /// <inheritdoc/>
    public (double Start, double End) LoopRange => (0, LengthBeats);

    /// <inheritdoc/>
    public bool Loop => true;

    /// <inheritdoc/>
    public double End => LengthBeats;

    /// <inheritdoc/>
    public void Collect(double fromBeat, double toBeat, List<PatternEvent> into) => Collect(fromBeat, toBeat, into, random);

    /// <summary>
    /// Events whose (swung) start is in [<paramref name="fromBeat"/>, <paramref name="toBeat"/>).
    /// Each step lasts one step (drum instruments set their own decay length).
    /// </summary>
    /// <param name="fromBeat">Inclusive window start, beats within the pattern (0…<see cref="LengthBeats"/>).</param>
    /// <param name="toBeat">Exclusive window end, beats.</param>
    /// <param name="into">Receives one event per audible active step; <see cref="PatternEvent.Tag"/> = track index.</param>
    /// <param name="rng">Random source for <see cref="StepTrack.Probability"/> (tests pass a seeded one).</param>
    public void Collect(double fromBeat, double toBeat, List<PatternEvent> into, Random rng)
    {
        bool anySolo = Tracks.Any(x => x.Solo);
        // Candidate steps: one step earlier than the window's grid position, because swing moves an
        // odd step's start later, possibly into this window; the exact test is done below.
        int first = Math.Max(0, (int)Math.Floor(fromBeat / StepBeats) - 1);
        int last = Math.Min(StepCount - 1, (int)Math.Ceiling(toBeat / StepBeats));
        for (int s = first; s <= last; s++)
        {
            double beat = StepStart(s);
            if (beat < fromBeat || beat >= toBeat) continue;
            for (int ti = 0; ti < Tracks.Count; ti++)
            {
                var t = Tracks[ti];
                int vel = s < t.Steps.Length ? t.Steps[s] : 0;
                if (vel <= 0 || t.Mute || (anySolo && !t.Solo)) continue;
                if (t.Probability < 100 && rng.Next(100) >= t.Probability) continue;
                into.Add(new PatternEvent(beat, StepBeats, PFields.InstrumentToken(t.Instrument),
                    PFields.Pitch(t.Note, PitchMapping) + " " + PFields.Velocity(vel, VelocityMapping), ti));
            }
        }
    }

    /// <summary>A deep copy (handed to the scheduler).</summary>
    public StepPattern Clone() => new()
    {
        Tracks = [.. Tracks.Select(t => t.Clone())], StepCount = StepCount, Bpm = Bpm, Swing = Swing,
        ExportLoops = ExportLoops, PitchMapping = PitchMapping, VelocityMapping = VelocityMapping,
    };

    /// <summary>
    /// Euclidean rhythm (Bjorklund / Toussaint): <paramref name="hits"/> onsets spread as evenly as
    /// possible over <paramref name="steps"/> steps, rotated right by <paramref name="rotation"/>.
    /// E(3,8) = x..x..x. (tresillo), E(5,8) = x.x.xx.x (a rotation of the cinquillo), E(4,16) = four on the floor.
    /// </summary>
    /// <param name="hits">Number of onsets (clamped to 0…<paramref name="steps"/>).</param>
    /// <param name="steps">Pattern length in steps.</param>
    /// <param name="rotation">Steps to rotate right (negative = left).</param>
    /// <returns>One flag per step, <c>true</c> = onset.</returns>
    public static bool[] Euclid(int hits, int steps, int rotation = 0)
    {
        var r = new bool[Math.Max(0, steps)];
        if (steps <= 0) return r;
        hits = Math.Clamp(hits, 0, steps);
        // Bresenham formulation: step i is a hit when floor(i·k/n) changes; gives the same set
        // of onsets as Bjorklund's algorithm, starting with a hit on step 0.
        for (int i = 0; i < steps; i++)
        {
            bool on = hits > 0 && (i * hits) % steps < hits;
            r[((i + rotation) % steps + steps) % steps] = on;
        }
        return r;
    }

    /// <summary>The track names, GM drum notes and kit instrument numbers of <c>Assets/DrumKit.csd</c>.</summary>
    public static readonly (string Name, string NameEs, int Instr, int Note)[] KitTracks =
    [
        ("Kick", "Bombo", 1, 36), ("Snare", "Caja", 2, 38), ("Closed hat", "Hi-hat cerrado", 3, 42), ("Open hat", "Hi-hat abierto", 4, 46),
        ("Clap", "Palmas", 5, 39), ("Tom", "Tom", 6, 45), ("Rim", "Aro", 7, 37), ("Cowbell", "Cencerro", 8, 56),
    ];

    /// <summary>
    /// The default pattern: the eight kit tracks with a classic funk/pop groove, so pressing Play
    /// with the drum kit open gives a beat right away.
    /// </summary>
    /// <param name="english">Track names in English (else Spanish).</param>
    public static StepPattern DefaultGroove(bool english)
    {
        var p = new StepPattern { StepCount = 16, Bpm = 108, Swing = 0.12 };
        foreach (var (name, nameEs, instr, note) in KitTracks)
            p.Tracks.Add(new StepTrack { Name = english ? name : nameEs, Instrument = instr.ToString(System.Globalization.CultureInfo.InvariantCulture), Note = note, Velocity = 100 });
        void Set(int track, string steps)
        {
            // 'X' = accent (120), 'x' = normal (95), 'o' = ghost (55), '.' = off.
            for (int i = 0; i < steps.Length && i < StepTrack.MaxSteps; i++)
                p.Tracks[track].Steps[i] = steps[i] switch { 'X' => 120, 'x' => 95, 'o' => 55, _ => 0 };
        }
        Set(0, "X.....x.X.x.....");   // kick
        Set(1, "....X..o....X...");   // snare with a ghost note
        Set(2, "x.x.X.x.x.x.X...");   // closed hat, accents on the snare beats
        Set(3, "..............x.");   // open hat before the bar line
        Set(4, "............x...");   // clap doubles the second snare
        Set(5, "................");   // tom (free)
        Set(6, "...o.......o....");   // rim ghosts
        Set(7, "................");   // cowbell (free)
        return p;
    }
}
