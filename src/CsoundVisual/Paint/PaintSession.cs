// PaintSession.cs — Paint Rooms foundation.
//
// Role: the state SHARED by all rooms of the Paint Rooms window, kept for the whole app session
// (it lives in MainViewModel, so closing and reopening the window loses nothing):
//   * the current SpectralImage and its undo/redo history;
//   * the synthesis settings and the live player that plays images through the app's Csound;
//   * the selection — a SelectionMask: a rectangle, ellipse, lasso or spline shape with soft
//     (0…1) coverage, which limits the image operations — a pixel clipboard, and a SOUND
//     clipboard for rooms that exchange audio (Spectrum Room analysis/resynthesis, Image Filter);
//   * the persisted preferences (PaintRoomSettings).
// Rooms never keep their own copy of the image: they read Session.Image and listen to
// ImageReplaced / PixelsChanged / Edited.
//
// UI thread only.
//
// Event flow of an edit (what rooms and the player rely on):
//   * a brush dab / operation changes pixels → SpectralImage.Changed → PixelsChanged(x0, x1)
//     (the canvas repaints those columns, the live player marks them dirty);
//   * the edit is complete (mouse up, operation done, undo/redo) → Edited (rooms save the history
//     state, refresh status, re-render the Spectrum Room preview…);
//   * the image OBJECT is swapped (resize, retune, import, preset, undo of those) → ImageReplaced,
//     followed by Edited.
//
// Pixel layout reminder (see SpectralImage): column-major, index = x · Height + y; x = column
// (time, left → right), y = row (pitch, 0 = lowest). Levels are floats 0…1 per layer (Left/Right).
// CellRect is half-open: [X0, X1) × [Y0, Y1).
//
// Shaped selections: every edit that uses the selection works on its bounding rectangle (Region)
// and is then BLENDED by the mask's coverage c of each cell: new = old + (result − old) · c. So
// cells outside the shape never change, cells inside change fully, and the anti-aliased / feathered
// rim changes partly (no hard edge in the sound). Copy keeps the shape (the clip carries the
// coverage), paste and move re-apply it, and the undo snapshot always covers the whole region.
//
// Noise layer (SpectralImage.Noise, optional per-cell noisiness): every pixel-moving edit here
// carries it along with the levels — the region snapshots, the mask blend, copy (PixelClip.Noise),
// paste (the pasted cell's noisiness wins where its level wins) and move. An image without a layer
// is treated as «all zeros» (tonal); a layer is created only when noisy cells arrive.

namespace CsoundVisual.Paint;

/// <summary>A copied piece of image (pixel clipboard).</summary>
/// <param name="Width">Columns.</param>
/// <param name="Height">Rows.</param>
/// <param name="Left">Left levels, column-major (Width·Height).</param>
/// <param name="Right">Right levels, column-major.</param>
/// <param name="Mask">
/// Coverage 0…1 of every clip cell (same layout) when the copy came from a shaped selection, or
/// <c>null</c> for a rectangle: a paste writes each cell weighted by it, so the pasted piece keeps
/// the shape (and soft edge) it was copied with.
/// </param>
/// <param name="Noise">Noisiness of every clip cell (same layout), or <c>null</c> when the source had no noise layer.</param>
public sealed record PixelClip(int Width, int Height, float[] Left, float[] Right, float[]? Mask = null, float[]? Noise = null);

/// <summary>Shared state of the Paint Rooms window (see the file header).</summary>
public sealed class PaintSession
{
    /// <summary>Backing field of <see cref="Image"/>; always subscribed to with <see cref="OnPixelsChanged"/>.</summary>
    SpectralImage image;

    /// <summary>Backing field of <see cref="Synth"/>.</summary>
    PaintSynthSettings synth;

    /// <summary>Backing field of <see cref="Selection"/> (never an empty mask: the factories return null instead).</summary>
    SelectionMask? selection;

    /// <summary>Backing field of <see cref="Sound"/>.</summary>
    AudioData? sound;

    /// <summary>Creates the session with a blank canvas built from <paramref name="settings"/>.</summary>
    /// <param name="settings">The persisted preferences (kept by reference and updated as the session changes).</param>
    public PaintSession(PaintRoomSettings settings)
    {
        Settings = settings;
        synth = settings.Synth;
        // The pitch axis comes from the saved preferences (log/linear/ET/Scala); the error text
        // (e.g. a Scala file that no longer exists → logarithmic fallback) is not shown at start-up.
        var axis = settings.BuildAxis(out _);
        image = new SpectralImage(settings.Width, axis, settings.DurationSeconds);
        image.Changed += OnPixelsChanged;
        // The brush engine keeps a reference to the session and reads Image/Settings at each stroke.
        Brushes = new BrushEngine(this);
        // Any complete edit (stroke, operation, import, undo…) makes the picture «not saved».
        Edited += () => HasUnsavedPicture = true;
    }

    /// <summary>
    /// True when the picture changed since it was last saved (exported as PNG), loaded from an
    /// example or created empty. The app asks «Save?» on exit when this is true and the picture is
    /// not blank (<see cref="PictureIsBlank"/>), because the Paint Synth picture is not kept
    /// between sessions.
    /// </summary>
    public bool HasUnsavedPicture { get; private set; }

    /// <summary>Marks the current picture as saved (after an export, an example or a new empty canvas).</summary>
    public void MarkPictureSaved() => HasUnsavedPicture = false;

    /// <summary>True when every cell of the picture is silent (nothing to lose).</summary>
    public bool PictureIsBlank => !image.Left.Any(v => v > 0) && !image.Right.Any(v => v > 0);

    /// <summary>Persisted preferences (axis, scale file, brush, synthesis…); saved by the app on close.</summary>
    public PaintRoomSettings Settings { get; }

    /// <summary>Undo/redo of image edits.</summary>
    public ImageHistory History { get; } = new();

    /// <summary>The painting tools bound to this session.</summary>
    public BrushEngine Brushes { get; }

    /// <summary>Plays images live through the app's Csound (set by the app; <c>null</c> in tests without an engine).</summary>
    public PaintLivePlayer? Player { get; set; }

    /// <summary>Display name of the current image (preset or file name, «untitled»…).</summary>
    public string ImageName { get; set; } = "untitled";

    /// <summary>The canvas. Replace it with <see cref="ReplaceImage"/> (undoable).</summary>
    public SpectralImage Image => image;

    /// <summary>Synthesis settings; setting them raises <see cref="SynthChanged"/> and stores them in <see cref="Settings"/>.</summary>
    public PaintSynthSettings Synth
    {
        get => synth;
        set
        {
            if (value == synth) return; // record value equality: an identical copy is not a change
            var old = synth;
            synth = value;
            // Every change of the synthesis settings is undoable (coalesced per burst); the swap
            // goes back through this setter, so SynthChanged keeps the room and the player in step.
            PushState("synth", old, s => { var cur = synth; Synth = (PaintSynthSettings)s!; return cur; });
            Settings.Synth = value;
            SynthChanged?.Invoke();
        }
    }

    /// <summary>The selection (a shaped, soft-edged mask of cells) or <c>null</c>. Image operations apply to it when set.</summary>
    /// <remarks>
    /// A <see cref="CellRect"/> can be assigned directly (implicit conversion to a rectangular
    /// mask; an empty rectangle becomes <c>null</c>). Masks are immutable, so «changed» means
    /// «another object» and <see cref="SelectionChanged"/> is raised for every new mask.
    /// </remarks>
    public SelectionMask? Selection
    {
        get => selection;
        set
        {
            if (ReferenceEquals(value, selection)) return;
            selection = value;
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Copied pixels (Ctrl+C), or <c>null</c>.</summary>
    public PixelClip? Clipboard { get; set; }

    /// <summary>
    /// A sound shared between rooms (e.g. the Spectrum Room puts the analysed file here, the Image
    /// Filter room takes its input from here). Setting it raises <see cref="SoundChanged"/>.
    /// </summary>
    /// <remarks>Unlike <see cref="Synth"/> and <see cref="Selection"/>, there is no equality check:
    /// setting the same object again still raises the event.</remarks>
    public AudioData? Sound
    {
        get => sound;
        set
        {
            sound = value;
            SoundChanged?.Invoke();
        }
    }

    /// <summary>Raised after the image OBJECT changed (new size/axis, import, preset, undo of those).</summary>
    public event Action? ImageReplaced;

    /// <summary>Raised whenever pixels of the current image change, with the dirty columns [x0, x1).</summary>
    public event Action<int, int>? PixelsChanged;

    /// <summary>Raised after a complete, undoable edit (end of a stroke, an operation, undo/redo).</summary>
    public event Action? Edited;

    /// <summary>Raised when <see cref="Synth"/> changes.</summary>
    public event Action? SynthChanged;

    /// <summary>Raised when <see cref="Selection"/> changes.</summary>
    public event Action? SelectionChanged;

    /// <summary>Raised when <see cref="Sound"/> changes.</summary>
    public event Action? SoundChanged;

    /// <summary>The region operations apply to: the selection's bounding rectangle, or the whole image.</summary>
    /// <remarks>For a shaped selection the result inside this rectangle is blended by the mask (see <see cref="Apply"/>).</remarks>
    public CellRect Region => selection?.Bounds ?? CellRect.All(image);

    /// <summary>
    /// Makes <paramref name="newImage"/> the current image. With <paramref name="undoable"/> the
    /// previous image object is kept in the history (Ctrl+Z brings it back).
    /// </summary>
    /// <param name="newImage">The new canvas (the session takes ownership; do not keep editing it elsewhere).</param>
    /// <param name="label">Undo-history label (operation id such as «retune», «import»).</param>
    /// <param name="undoable">False for replacements that should not create an undo step.</param>
    /// <remarks>Raises <see cref="ImageReplaced"/> then <see cref="Edited"/>. Replacing with the same object does nothing.</remarks>
    public void ReplaceImage(SpectralImage newImage, string label, bool undoable = true)
    {
        if (ReferenceEquals(newImage, image)) return;
        // Replacements made BY an undo/redo (a settings step restoring an older picture) are not new steps.
        if (undoable && !IsApplyingHistory) History.PushReplaced(image, label);
        Swap(newImage);
        Edited?.Invoke();
    }

    /// <summary>Called by tools after a complete edit (raises <see cref="Edited"/>).</summary>
    public void NotifyEdited() => Edited?.Invoke();

    /// <summary>Undoes the last edit.</summary>
    /// <remarks>
    /// <see cref="ImageHistory.Undo"/> either restores pixels of the CURRENT image in place (and
    /// reports the restored column range) or returns a different image object (undo of a
    /// replacement); <see cref="Step"/> handles both.
    /// </remarks>
    public void Undo()
    {
        var sel = selection;
        IsApplyingHistory = true;
        try
        {
            var before = image;
            var next = History.Undo(image, ref sel, out var dirty);
            // A settings step may itself have put back another picture (ReplaceImage during the
            // swap): then that picture is the result, not the object History handed back.
            if (!ReferenceEquals(image, before)) next = image;
            Step(next, dirty, sel);
        }
        finally { IsApplyingHistory = false; }
    }

    /// <summary>
    /// True while an undo or redo is being applied. Changes made by it (a settings swap setting
    /// <see cref="Synth"/>, a room restoring its controls…) must not record new steps: every
    /// undoable setter and <see cref="PushState"/> check it.
    /// </summary>
    public bool IsApplyingHistory { get; private set; }

    /// <summary>
    /// Records a room-specific settings change as an undo step (Ctrl+Z / ↶ restores it). Ignored
    /// while an undo/redo is being applied. See <see cref="ImageHistory.PushState"/> (coalescing).
    /// </summary>
    /// <param name="label">Step label and coalescing key.</param>
    /// <param name="previous">The state before the change.</param>
    /// <param name="swap">Applies a state and returns the one it replaced.</param>
    public void PushState(string label, object? previous, Func<object?, object?> swap)
    {
        if (IsApplyingHistory) return;
        History.PushState(label, previous, swap);
        Edited?.Invoke();
    }

    /// <summary>
    /// Changes the canvas duration (seconds; the pixels stay, only the time per column changes) as
    /// an undoable step, and raises <see cref="DurationChanged"/> so the room refreshes its box and
    /// the live player restarts with the new timing.
    /// </summary>
    /// <param name="seconds">New duration.</param>
    public void SetDuration(double seconds)
    {
        if (Math.Abs(seconds - image.DurationSeconds) < 1e-9) return;
        double before = image.DurationSeconds;
        ApplyDuration(seconds);
        PushState("duration", before, s => { double cur = image.DurationSeconds; ApplyDuration((double)s!); return cur; });
    }

    /// <summary>Sets the duration without recording a step (shared by <see cref="SetDuration"/> and its undo).</summary>
    /// <param name="seconds">New duration.</param>
    void ApplyDuration(double seconds)
    {
        image.DurationSeconds = seconds;
        Settings.DurationSeconds = seconds;
        DurationChanged?.Invoke();
    }

    /// <summary>Raised when the duration changed through <see cref="SetDuration"/> or its undo/redo.</summary>
    public event Action? DurationChanged;

    /// <summary>Redoes the last undone edit.</summary>
    /// <remarks>Same two outcomes as <see cref="Undo"/>.</remarks>
    public void Redo()
    {
        var sel = selection;
        IsApplyingHistory = true;
        try
        {
            var before = image;
            var next = History.Redo(image, ref sel, out var dirty);
            // A settings step may itself have put back another picture (ReplaceImage during the
            // swap): then that picture is the result, not the object History handed back.
            if (!ReferenceEquals(image, before)) next = image;
            Step(next, dirty, sel);
        }
        finally { IsApplyingHistory = false; }
    }

    /// <summary>
    /// Changes the selection as an UNDOABLE step (Ctrl+Z brings the previous selection back). Use
    /// it for changes the user makes on purpose: a new shape, select all, deselect, invert.
    /// </summary>
    /// <param name="next">The new selection (<c>null</c> = none).</param>
    /// <param name="label">History label (shown in the Undo button's tooltip).</param>
    /// <remarks>
    /// Setting <see cref="Selection"/> directly stays possible for changes that must not create a
    /// step of their own (the live feather re-shaping the selection just drawn, a selection moved
    /// together with its pixels, internal resets).
    /// </remarks>
    public void ChangeSelection(SelectionMask? next, string label = "select")
    {
        if (ReferenceEquals(next, selection)) return;
        History.PushSelection(selection, label);
        Selection = next;
    }

    /// <summary>
    /// Runs an image operation on <see cref="Region"/> with <paramref name="parameter"/>, as one
    /// undo step. Returns false when nothing happened.
    /// </summary>
    /// <param name="op">The operation (see <see cref="ImageOps"/>).</param>
    /// <param name="parameter">Its numeric parameter (threshold, exponent, rows to shift…; ignored by some).</param>
    /// <returns>False only when a replacing operation produced no image.</returns>
    public bool Apply(ImageOperation op, double parameter)
    {
        var r = Region;
        // Two kinds: «Replace» operations build a NEW image (e.g. the time stretch, which changes the width)
        // and are undone by swapping objects; in-place ones modify the region's columns, which are
        // snapshotted first (History.Begin) so the undo can restore exactly those columns.
        if (op.Kind == OperationKind.Replace)
        {
            var result = op.Apply(image, r, parameter);
            if (result is null) return false;
            ReplaceImage(result, op.Id);
            return true;
        }
        History.Begin(image, r.X0, r.X1, op.Id);
        // A shaped selection: keep the region's cells as they were, run the operation on the whole
        // bounding rectangle, then blend the result back by the coverage (outside the shape = old).
        var mask = selection is { IsRectangle: false } m ? m : null;
        var before = mask is null ? default : SnapshotRegion(r);
        op.Apply(image, r, parameter);
        if (mask is not null) BlendByMask(r, mask, before.Left, before.Right, before.Noise);
        History.Commit();
        image.NotifyChanged(r.X0, r.X1);
        Edited?.Invoke();
        return true;
    }

    /// <summary>Copies the cells of <paramref name="r"/> out of the layers (column-major, height r.Height).</summary>
    /// <param name="r">The region.</param>
    /// <returns>(left, right, noise) copies, each r.Width · r.Height values; noise is <c>null</c> when the image has no noise layer.</returns>
    (float[] Left, float[] Right, float[]? Noise) SnapshotRegion(CellRect r)
    {
        int h = image.Height;
        var l = new float[r.Width * r.Height];
        var rr = new float[l.Length];
        var nz = image.Noise is null ? null : new float[l.Length];
        for (int x = 0; x < r.Width; x++)
        {
            Array.Copy(image.Left, (r.X0 + x) * h + r.Y0, l, x * r.Height, r.Height);
            Array.Copy(image.Right, (r.X0 + x) * h + r.Y0, rr, x * r.Height, r.Height);
            if (nz is not null) Array.Copy(image.Noise!, (r.X0 + x) * h + r.Y0, nz, x * r.Height, r.Height);
        }
        return (l, rr, nz);
    }

    /// <summary>
    /// Restricts an edit of region <paramref name="r"/> to <paramref name="mask"/>: every cell becomes
    /// old + (new − old) · coverage, where «old» comes from <paramref name="beforeL"/>/<paramref name="beforeR"/>.
    /// </summary>
    /// <param name="r">The edited region (the mask's bounds).</param>
    /// <param name="mask">The selection shape.</param>
    /// <param name="beforeL">Left layer of the region before the edit (from <see cref="SnapshotRegion"/>).</param>
    /// <param name="beforeR">Right layer of the region before the edit.</param>
    /// <param name="beforeN">Noise layer of the region before the edit, or <c>null</c> when the image had
    /// none (then «before» counts as 0 = tonal, in case the operation just created the layer).</param>
    void BlendByMask(CellRect r, SelectionMask mask, float[] beforeL, float[] beforeR, float[]? beforeN = null)
    {
        int h = image.Height;
        var nz = image.Noise;
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                float c = mask[x, y];
                if (c >= 1) continue;                    // fully inside: keep the operation's result
                int i = x * h + y, j = (x - r.X0) * r.Height + (y - r.Y0);
                image.Left[i] = beforeL[j] + (image.Left[i] - beforeL[j]) * c;
                image.Right[i] = beforeR[j] + (image.Right[i] - beforeR[j]) * c;
                if (nz is not null)
                {
                    float bn = beforeN?[j] ?? 0f;
                    nz[i] = bn + (nz[i] - bn) * c;
                }
            }
    }

    /// <summary>Copies the selection (or the whole image) to <see cref="Clipboard"/>.</summary>
    /// <remarks>
    /// The clip is stored column-major with its OWN height (r.Height), so each column of the
    /// region is one contiguous <see cref="Array.Copy(Array, int, Array, int, int)"/>. The pixels
    /// of the whole bounding rectangle are copied unchanged; a shaped selection adds its coverage as
    /// <see cref="PixelClip.Mask"/>, which the paste applies.
    /// </remarks>
    public void Copy()
    {
        var r = Region;
        var (l, rr, nz) = SnapshotRegion(r);
        Clipboard = new PixelClip(r.Width, r.Height, l, rr, selection is { IsRectangle: false } m ? m.ToArray() : null, nz);
    }

    /// <summary>
    /// Pastes <see cref="Clipboard"/> with its lower-left cell at (x, y) — lightening (max) or
    /// replacing — and selects the pasted area. One undo step.
    /// </summary>
    /// <param name="x">Target column of the clip's first column (may be partly off-canvas).</param>
    /// <param name="y">Target row of the clip's lowest row.</param>
    /// <param name="replace">True: overwrite; false (default): keep the brighter of old and pasted,
    /// consistent with «painting only ever brightens».</param>
    /// <remarks>
    /// A clip copied from a shaped selection keeps its shape: each cell is blended by the clip's
    /// coverage m (old + (pasted − old) · m), and the new selection is that shape at the target.
    /// </remarks>
    public void Paste(int x, int y, bool replace = false)
    {
        if (Clipboard is not { } c) return;
        int h = image.Height;
        // The visible part of the pasted rectangle (clipped to the canvas): undo snapshot, change
        // notification and new selection all use it; cells outside the canvas are skipped below.
        var target = new CellRect(Math.Clamp(x, 0, image.Width), Math.Clamp(y, 0, h),
            Math.Clamp(x + c.Width, 0, image.Width), Math.Clamp(y + c.Height, 0, h));
        if (target.IsEmpty) return;
        History.Begin(image, target.X0, target.X1, "paste");
        // A clip with noisiness needs a noise layer here (created as «all tonal»); a tonal clip
        // pasted into a picture WITH a layer writes 0 (tonal) where it wins.
        var nz = c.Noise is not null ? image.EnsureNoise() : image.Noise;
        for (int cx = 0; cx < c.Width; cx++)
        {
            int tx = x + cx;
            if (tx < 0 || tx >= image.Width) continue;
            for (int cy = 0; cy < c.Height; cy++)
            {
                int ty = y + cy;
                if (ty < 0 || ty >= h) continue;
                int i = tx * h + ty, j = cx * c.Height + cy;
                float m = c.Mask?[j] ?? 1f;
                if (m <= 0) continue;
                float nl = replace ? c.Left[j] : Math.Max(image.Left[i], c.Left[j]);
                float nr = replace ? c.Right[j] : Math.Max(image.Right[i], c.Right[j]);
                if (nz is not null)
                {
                    // The pasted noisiness wins where the pasted cell is (at least as) loud — always when replacing.
                    bool wins = replace || Math.Max(c.Left[j], c.Right[j]) >= Math.Max(image.Left[i], image.Right[i]);
                    if (wins) nz[i] += ((c.Noise?[j] ?? 0f) - nz[i]) * m;
                }
                image.Left[i] += (nl - image.Left[i]) * m;
                image.Right[i] += (nr - image.Right[i]) * m;
            }
        }
        History.Commit();
        image.NotifyChanged(target.X0, target.X1);
        Selection = c.Mask is null ? target : SelectionMask.FromCoverage(new CellRect(x, y, x + c.Width, y + c.Height), c.Mask, image.Width, h);
        Edited?.Invoke();
    }

    /// <summary>
    /// Moves the selected cells by (dx, dy): cut + paste (replace) at the new place, one undo step.
    /// </summary>
    /// <param name="dx">Columns to the right (negative = left).</param>
    /// <param name="dy">Rows up (negative = down).</param>
    /// <remarks>
    /// Side effect: the moved cells also become the <see cref="Clipboard"/> content. Cells moved off
    /// the canvas are lost. The undo snapshot covers the union of the old and new column ranges;
    /// when the selection is moved partly off-canvas that union extends beyond 0…Width, which
    /// <see cref="ImageHistory.Begin"/> and <see cref="SpectralImage.NotifyChanged(int, int)"/> clamp.
    /// A shaped selection moves only what it covers: each source cell keeps (1 − coverage) of its
    /// level, and the moved cells are blended in by the same coverage at the target.
    /// </remarks>
    public void MoveSelection(int dx, int dy)
    {
        if (selection is not { } sel || (dx == 0 && dy == 0)) return;
        var r = sel.Bounds;
        int h = image.Height;
        Copy();
        var c = Clipboard!;
        // Columns touched: where the cells were (cleared) and where they land.
        int x0 = Math.Min(r.X0, r.X0 + dx), x1 = Math.Max(r.X1, r.X1 + dx);
        History.Begin(image, x0, x1, "move");
        var nz = image.Noise;   // the clip came from this image, so it has noise exactly when the image has a layer
        // 1) Lift the cells out of the source (× (1 − coverage): a full cell becomes silent),
        // 2) write the clip at the offset (replace, not max), weighted by the same coverage.
        for (int x = r.X0; x < r.X1; x++)
            for (int y = r.Y0; y < r.Y1; y++)
            {
                float keep = 1 - sel[x, y];
                image.Left[x * h + y] *= keep;
                image.Right[x * h + y] *= keep;
            }
        for (int cx = 0; cx < c.Width; cx++)
            for (int cy = 0; cy < c.Height; cy++)
            {
                int tx = r.X0 + dx + cx, ty = r.Y0 + dy + cy;
                if (!image.Contains(tx, ty)) continue;
                int i = tx * h + ty, j = cx * c.Height + cy;
                float m = c.Mask?[j] ?? 1f;
                image.Left[i] += (c.Left[j] - image.Left[i]) * m;
                image.Right[i] += (c.Right[j] - image.Right[i]) * m;
                if (nz is not null) nz[i] += ((c.Noise?[j] ?? 0f) - nz[i]) * m;
            }
        History.Commit();
        image.NotifyChanged(x0, x1);
        // The selection follows the cells (clipped to the canvas).
        Selection = sel.Translated(dx, dy, image.Width, h);
        Edited?.Invoke();
    }

    /// <summary>Clears the selected cells (Delete key), one undo step.</summary>
    /// <remarks>Only with a selection (never clears the whole canvas by accident); uses the «clear» image operation.</remarks>
    public void DeleteSelection()
    {
        if (selection is null) return;
        Apply(ImageOps.Find("clear")!, 0);
    }

    /// <summary>
    /// Replaces the image by a copy on <paramref name="axis"/> (and/or with <paramref name="width"/>
    /// columns), keeping painted pitches where they were in Hz. Undoable.
    /// </summary>
    /// <param name="axis">The new pitch axis (may be the current one when only the width changes).</param>
    /// <param name="width">New column count, or <c>null</c> to keep it (columns are resampled).</param>
    public void Retune(PitchAxis axis, int? width = null)
    {
        // Retuned keeps the duration too; set explicitly so a resample never changes the length in seconds.
        var next = image.Retuned(width ?? image.Width, axis);
        next.DurationSeconds = image.DurationSeconds;
        ReplaceImage(next, "retune");
    }

    // ------------------------------------------------------------------------------------------

    /// <summary>Applies the result of an undo/redo: swaps the image object, or notifies the restored columns.</summary>
    /// <param name="next">The image after the undo/redo (the current one, or another object).</param>
    /// <param name="dirty">Columns [X0, X1) restored in place (empty when nothing changed or the object was swapped).</param>
    /// <param name="sel">The selection after the undo/redo (changed only by selection steps).</param>
    void Step(SpectralImage next, (int X0, int X1) dirty, SelectionMask? sel)
    {
        if (!ReferenceEquals(next, image))
            Swap(next);
        else if (dirty.X1 > dirty.X0)
            image.NotifyChanged(dirty.X0, dirty.X1);
        // A restored selection must still fit the (possibly different) current image.
        if (sel is not null && (sel.Bounds.X1 > image.Width || sel.Bounds.Y1 > image.Height)) sel = null;
        Selection = sel;
        Edited?.Invoke();
    }

    /// <summary>Makes <paramref name="next"/> current: moves the pixel subscription, drops a selection that no longer fits, remembers its size in the settings and raises <see cref="ImageReplaced"/>.</summary>
    /// <param name="next">The new current image.</param>
    void Swap(SpectralImage next)
    {
        // Move the subscription so the old object (kept in the undo history) no longer reports edits.
        image.Changed -= OnPixelsChanged;
        image = next;
        image.Changed += OnPixelsChanged;
        // Keep the selection only if it still fits.
        if (selection is { } s && (s.Bounds.X1 > image.Width || s.Bounds.Y1 > image.Height)) Selection = null;
        // New canvases (and the next app start) reuse the size and duration of the latest image.
        Settings.Width = image.Width;
        Settings.DurationSeconds = image.DurationSeconds;
        ImageReplaced?.Invoke();
    }

    /// <summary>Relays the current image's <see cref="SpectralImage.Changed"/> as <see cref="PixelsChanged"/> (so rooms never re-subscribe when the image object changes).</summary>
    /// <param name="x0">First changed column.</param>
    /// <param name="x1">One past the last changed column.</param>
    void OnPixelsChanged(int x0, int x1) => PixelsChanged?.Invoke(x0, x1);
}
