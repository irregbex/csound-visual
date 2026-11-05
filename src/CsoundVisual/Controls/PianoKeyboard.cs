using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// On-screen piano keyboard. It plays notes with the mouse (velocity depends on how low on the
/// key you press; dragging plays a glissando) and, while focused, with the computer keyboard:
/// <c>A W S E D F T G Y H U J K O L P</c> are the notes from C, <c>Z</c>/<c>X</c> change the octave and
/// <c>C</c>/<c>V</c> lower/raise the velocity. Notes received from MIDI devices are highlighted too.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where it is used.</b> In the MIDI card of the main window (<c>x:Name="Piano"</c>) and, one per
/// <c>keyboard</c> widget, inside Cabbage panels (<c>Cabbage.CabbagePanel</c>). In both cases
/// <see cref="NoteOn"/>/<see cref="NoteOff"/> are wired to <c>MainViewModel.KeyboardNoteOn</c>/<c>KeyboardNoteOff</c>,
/// which inject the note into <c>MidiHub</c> as if it came from a device (so it reaches Csound,
/// the MIDI monitor and everything else), and <c>MainViewModel.ExternalNote</c> drives
/// <see cref="SetExternal"/>. The control itself knows nothing about MIDI or Csound.
/// </para>
/// <para>
/// <b>Layout.</b> The number of octaves is not a property: it is derived from the width
/// (one octave per 140 px, i.e. white keys at least 20 px wide, 1…8 octaves). White keys share the
/// width equally; black keys are 64 % of a white key wide and 60 % of the height tall, centred on
/// the boundary between two white keys. All geometry is recomputed on every render/hit test from
/// <see cref="LowestNote"/> and the current bounds, so resizing needs no extra handling.
/// </para>
/// <para>
/// <b>Colours.</b> Notes played by this control are drawn in the accent colour, notes from MIDI
/// devices in green ("Good"); the former wins when both apply.
/// </para>
/// <para>
/// <b>Threading.</b> Everything runs on the UI thread: input handlers here, and
/// <see cref="SetExternal"/> is called from the view model's UI-thread MIDI drain.
/// </para>
/// </remarks>
public sealed class PianoKeyboard : Control
{
    /// <summary>Lowest MIDI note shown (always a C).</summary>
    public static readonly StyledProperty<int> LowestNoteProperty =
        AvaloniaProperty.Register<PianoKeyboard, int>(nameof(LowestNote), 48);

    /// <summary>For each pitch class (0 = C … 11 = B): whether it is a black key.</summary>
    static readonly bool[] IsBlack = [false, true, false, true, false, false, true, false, true, false, true, false];
    /// <summary>
    /// For each pitch class: index of its white key within the octave (0 = C … 6 = B). For a black
    /// key it is the white key to its <i>left</i>, so the black key is drawn on that key's right edge.
    /// </summary>
    static readonly int[] WhiteIndex = [0, 0, 1, 1, 2, 3, 3, 4, 4, 5, 5, 6];
    /// <summary>
    /// Computer-keyboard layout, one character per semitone starting at C (the usual "tracker"
    /// layout: the home row is the white keys, the row above the black keys). Covers C to D#
    /// of the next octave (16 semitones).
    /// </summary>
    const string PcKeys = "awsedftgyhujkolp";
    /// <summary>
    /// Highest valid MIDI note number. Keys above it can be on screen (LowestNote 108 + 8 octaves)
    /// but never produce a <see cref="NoteOn"/>.
    /// </summary>
    const int MaxNote = 127;

    /// <summary>Unpressed white key.</summary>
    static readonly IBrush White = AppTheme.Brush("PianoWhite");
    /// <summary>Unpressed black key.</summary>
    static readonly IBrush Black = AppTheme.Brush("BgDeep");
    /// <summary>Key held by this control (mouse or computer keyboard); also the focus border.</summary>
    static readonly IBrush Pressed = AppTheme.Brush("Accent");
    /// <summary>Key sounding from an external MIDI device.</summary>
    static readonly IBrush External = AppTheme.Brush("Good");
    /// <summary>Colour of the octave labels ("C3", "C4", …) on the C keys.</summary>
    static readonly IBrush Label = AppTheme.Brush("TextFaint");
    /// <summary>Outline of the white keys.</summary>
    static readonly IPen Edge = new Pen(AppTheme.Brush("Line"), 1);

    /// <summary>MIDI notes currently held by this control (mouse or computer keys); each got one <see cref="NoteOn"/>.</summary>
    readonly HashSet<int> ownNotes = [];
    /// <summary>MIDI notes reported as sounding by <see cref="SetExternal"/>.</summary>
    readonly HashSet<int> externalNotes = [];
    /// <summary>
    /// Computer keys currently down. Used to ignore key auto-repeat (a repeated KeyDown would
    /// otherwise retrigger the note) and to match each KeyUp to its own KeyDown.
    /// </summary>
    readonly HashSet<Key> heldKeys = [];
    /// <summary>Note held by the mouse button, or <c>null</c> when no button is down on a key.</summary>
    int? pointerNote;

    /// <summary>Makes <see cref="LowestNoteProperty"/> repaint on change and makes the control focusable by default (needed for the computer-keyboard input).</summary>
    static PianoKeyboard()
    {
        AffectsRender<PianoKeyboard>(LowestNoteProperty);
        FocusableProperty.OverrideDefaultValue<PianoKeyboard>(true);
    }

    /// <summary>Lowest note shown (a C); changed by Z/X.</summary>
    /// <remarks>
    /// The CLR setter snaps the value down to a C (<c>value / 12 * 12</c>) and clamps it to 0…108;
    /// a value set through the styled property directly (styles, bindings) bypasses that.
    /// </remarks>
    public int LowestNote
    {
        get => GetValue(LowestNoteProperty);
        set => SetValue(LowestNoteProperty, Math.Clamp(value / 12 * 12, 0, 108));
    }

    /// <summary>Velocity used by the computer keyboard (0…1); changed by C/V.</summary>
    public double KeyboardVelocity { get; private set; } = 0.75;

    /// <summary>Raised when the user starts a note (note, velocity 0…1).</summary>
    /// <remarks>Raised at most once per note until the matching <see cref="NoteOff"/>; raised on the UI thread.</remarks>
    public event Action<int, double>? NoteOn;

    /// <summary>Raised when the user releases a note.</summary>
    public event Action<int>? NoteOff;

    /// <summary>Marks a note as sounding from an external MIDI device (drawn in green).</summary>
    /// <param name="note">MIDI note number.</param>
    /// <param name="on"><c>true</c> for note-on, <c>false</c> for note-off.</param>
    public void SetExternal(int note, bool on)
    {
        // Repaint only when the set actually changed (duplicate note-ons/offs are common).
        if (on ? externalNotes.Add(note) : externalNotes.Remove(note))
            InvalidateVisual();
    }

    /// <summary>Releases every note the keyboard itself is holding (e.g. when focus is lost).</summary>
    public void ReleaseAll()
    {
        // ToList: Release removes from ownNotes, which must not change while being enumerated.
        foreach (int n in ownNotes.ToList()) Release(n);
        heldKeys.Clear();
        pointerNote = null;
    }

    /// <summary>Octaves that fit in the current width: one per 7 white keys of at least 20 px, clamped to 1…8.</summary>
    int Octaves => Math.Clamp((int)(Bounds.Width / (7 * 20)), 1, 8);
    /// <summary>Width of one white key in pixels (the whole width divided among all white keys).</summary>
    double WhiteWidth => Bounds.Width / (Octaves * 7);

    /// <summary>Stretches horizontally (desired width 0) with a default height of 86 px.</summary>
    /// <param name="availableSize">Space offered by the parent (unused).</param>
    /// <returns>Desired size 0 × 86.</returns>
    protected override Size MeasureOverride(Size availableSize) => new(0, 86);

    /// <summary>
    /// Draws the white keys (with octave labels on each C), then the black keys on top, then a
    /// focus border while the control has keyboard focus.
    /// </summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        double ww = WhiteWidth, h = Bounds.Height;
        int first = LowestNote, count = Octaves * 12;
        // Pass 1, white keys. x = (octave · 7 + white index within the octave) · white width.
        for (int n = first; n < first + count; n++)
        {
            if (IsBlack[n % 12]) continue;
            var r = new Rect(((n - first) / 12 * 7 + WhiteIndex[n % 12]) * ww, 0, ww, h);
            ctx.DrawRectangle(Fill(n, White), Edge, r.Deflate(0.5), 0, 0);
            if (n % 12 == 0)
            {
                // Octave naming where MIDI 60 = C4 (so note 0 = C-1).
                var t = new FormattedText("C" + (n / 12 - 1), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Typeface.Default, 9, Label);
                ctx.DrawText(t, new Point(r.X + (ww - t.Width) / 2, h - t.Height - 3));
            }
        }
        // Pass 2, black keys, drawn after the whites so they overlap them. Centred on the right
        // edge of the white key to their left (WhiteIndex + 1), 0.64 white-key widths wide.
        for (int n = first; n < first + count; n++)
        {
            if (!IsBlack[n % 12]) continue;
            double x = ((n - first) / 12 * 7 + WhiteIndex[n % 12] + 1) * ww - ww * 0.32;
            ctx.DrawRectangle(Fill(n, Black), null, new Rect(x, 0, ww * 0.64, h * 0.6), 0, 2);
        }
        if (IsFocused)
            ctx.DrawRectangle(null, new Pen(Pressed, 1.5), new Rect(Bounds.Size).Deflate(0.75), 4, 4);
    }

    /// <summary>Fill for a key: own notes first, then external notes, else the key's normal colour.</summary>
    /// <param name="note">MIDI note of the key.</param>
    /// <param name="normal">Brush of the key when it is not sounding (white or black).</param>
    /// <returns>The brush to fill the key with.</returns>
    IBrush Fill(int note, IBrush normal) =>
        ownNotes.Contains(note) ? Pressed : externalNotes.Contains(note) ? External : normal;

    /// <summary>Note and velocity under a point (black keys first).</summary>
    /// <param name="p">Point in control coordinates (pixels).</param>
    /// <returns>
    /// The MIDI note and a velocity 0.15…1 proportional to how far down the key the point is
    /// (pressing near the front edge = loud, like striking the key harder), or <c>null</c> outside the control.
    /// </returns>
    (int Note, double Velocity)? NoteAt(Point p)
    {
        double ww = WhiteWidth, h = Bounds.Height;
        if (p.X < 0 || p.Y < 0 || p.X >= Bounds.Width || p.Y >= h) return null;
        int first = LowestNote;
        // Black keys are on top, so they win in their upper 60 % band; their velocity is scaled to
        // their own (shorter) length.
        if (p.Y < h * 0.6)
            for (int n = first; n < first + Octaves * 12; n++)
            {
                if (!IsBlack[n % 12]) continue;
                double x = ((n - first) / 12 * 7 + WhiteIndex[n % 12] + 1) * ww - ww * 0.32;
                if (p.X >= x && p.X < x + ww * 0.64)
                    // Out-of-range black keys (above MIDI 127) are inert, like the white ones below.
                    return n > MaxNote ? null : (n, Math.Clamp(p.Y / (h * 0.6), 0.15, 1));
            }
        // Otherwise a white key: which white key from the left → octave and C-major step → semitone.
        int white = (int)(p.X / ww);
        int octave = white / 7, step = white % 7;
        int[] semis = [0, 2, 4, 5, 7, 9, 11];
        int note = first + octave * 12 + semis[step];
        // Bug fix: with a high LowestNote (up to 108) and up to 8 octaves on screen, keys above
        // MIDI 127 are drawn. They are not valid MIDI notes (MidiHub would wrap or reject them), so
        // a press there plays nothing instead of sending an out-of-range note.
        return note > MaxNote ? null : (note, Math.Clamp(p.Y / h, 0.15, 1));
    }

    /// <summary>Starts a note: records it, raises <see cref="NoteOn"/> and repaints, unless it is already held.</summary>
    /// <param name="note">MIDI note.</param>
    /// <param name="velocity">Velocity 0…1.</param>
    void Press(int note, double velocity)
    {
        if (ownNotes.Add(note))
        {
            NoteOn?.Invoke(note, velocity);
            InvalidateVisual();
        }
    }

    /// <summary>Ends a note held by this control: raises <see cref="NoteOff"/> and repaints; no-op if it was not held.</summary>
    /// <param name="note">MIDI note.</param>
    void Release(int note)
    {
        if (ownNotes.Remove(note))
        {
            NoteOff?.Invoke(note);
            InvalidateVisual();
        }
    }

    /// <summary>
    /// Mouse down (any button): takes keyboard focus (so the computer keys work afterwards), plays
    /// the key under the pointer and captures the pointer so the release arrives here even if the
    /// mouse leaves the control.
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (NoteAt(e.GetPosition(this)) is { } hit)
        {
            pointerNote = hit.Note;
            Press(hit.Note, hit.Velocity);
            e.Pointer.Capture(this);
        }
    }

    /// <summary>
    /// Glissando: while a mouse button holds a note, moving onto another key releases the old note
    /// and plays the new one (with the velocity of the new position). Moving within the same key,
    /// or outside the control, does nothing (the last note keeps sounding).
    /// </summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (pointerNote is not { } current || NoteAt(e.GetPosition(this)) is not { } hit || hit.Note == current)
            return;
        Release(current);
        pointerNote = hit.Note;
        Press(hit.Note, hit.Velocity);
    }

    /// <summary>Mouse up: releases the note held by the pointer and the pointer capture.</summary>
    /// <param name="e">Pointer event.</param>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pointerNote is { } n) Release(n);
        pointerNote = null;
        e.Pointer.Capture(null);
    }

    /// <summary>
    /// Bug fix: the pointer capture can be taken away without a pointer-released event (another
    /// control or a popup grabs the pointer, the window loses capture, the control is removed…).
    /// Without this handler the note held by the mouse would hang until focus was lost; now it is
    /// released as soon as the capture goes away.
    /// </summary>
    /// <param name="e">Capture-lost event.</param>
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (pointerNote is { } n) Release(n);
        pointerNote = null;
    }

    /// <summary>
    /// Computer keyboard while focused: note keys from <see cref="PcKeys"/> play one octave above
    /// <see cref="LowestNote"/>; Z/X shift the shown range by an octave (releasing held notes
    /// first so none get stuck at the old pitch); C/V change <see cref="KeyboardVelocity"/> in
    /// steps of 0.1 (0.1…1). Handled keys are marked so they do not trigger shortcuts elsewhere.
    /// </summary>
    /// <param name="e">Key event.</param>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Letter keys stringify as a single character ("A" → "a"); every other key name is longer
        // ("D1", "Space"…), which is how non-letters are filtered out.
        string key = e.Key.ToString().ToLowerInvariant();
        if (key.Length == 1 && PcKeys.IndexOf(key[0]) is var semi and >= 0)
        {
            // heldKeys.Add fails on auto-repeat, so holding a key does not retrigger the note.
            // Bug fix: LowestNote + 12 + semi can reach 135 at the top octave; notes above MIDI 127
            // are ignored (the key is still recorded so its key-up stays balanced).
            if (heldKeys.Add(e.Key) && LowestNote + 12 + semi <= MaxNote)
                Press(LowestNote + 12 + semi, KeyboardVelocity);
            e.Handled = true;
        }
        else if (e.Key == Key.Z) { ReleaseAll(); LowestNote -= 12; e.Handled = true; }
        else if (e.Key == Key.X) { ReleaseAll(); LowestNote += 12; e.Handled = true; }
        else if (e.Key == Key.C) { KeyboardVelocity = Math.Max(0.1, KeyboardVelocity - 0.1); e.Handled = true; }
        else if (e.Key == Key.V) { KeyboardVelocity = Math.Min(1, KeyboardVelocity + 0.1); e.Handled = true; }
    }

    /// <summary>
    /// Releases the note of a computer key. Only keys recorded in <see cref="heldKeys"/> count, so
    /// a key-up that follows an octave change (which already released everything) does nothing.
    /// </summary>
    /// <param name="e">Key event.</param>
    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        string key = e.Key.ToString().ToLowerInvariant();
        if (key.Length == 1 && PcKeys.IndexOf(key[0]) is var semi and >= 0 && heldKeys.Remove(e.Key))
        {
            Release(LowestNote + 12 + semi);
            e.Handled = true;
        }
    }

    /// <summary>Repaints to show the focus border (the cue that the computer keys now play notes).</summary>
    /// <param name="e">Focus event.</param>
    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    /// <summary>
    /// Releases every held note (key-ups would no longer arrive here, so notes would hang) and
    /// hides the focus border.
    /// </summary>
    /// <param name="e">Focus event.</param>
    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseAll();
        InvalidateVisual();
    }
}
