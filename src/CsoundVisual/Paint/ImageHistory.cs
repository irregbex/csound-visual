// ImageHistory.cs — Paint Rooms foundation.
//
// Role: undo/redo (Ctrl+Z / Ctrl+Y) for the Paint Rooms canvas with BOUNDED MEMORY. A 512×1024
// stereo image is 4 MB, so storing a full copy per brush stroke would exhaust memory quickly.
// Instead most edits store only the COLUMNS they touched (a brush stroke usually covers a few
// dozen columns): a «region» entry. Operations that change the image size or pitch axis store
// the whole previous image object instead: an «image» entry.
//
// Undo swaps the stored pixels with the current ones (so the same entry, now holding the undone
// state, moves to the redo stack) — no extra copy is needed for redo.
//
// The oldest entries are dropped when the total exceeds MaxBytes (default 384 MB) or MaxEntries.
// UI thread only (it mutates the session's image).
//
// Protocol used by PaintSession / PaintBrushes:
//   * brush-like tools: Begin(x0, x1) at mouse-down → Extend(...) before every stamp that reaches
//     new columns → Commit() at mouse-up (Cancel() drops an unfinished capture);
//   * whole-canvas or selection operations: Begin over the affected columns → modify → Commit;
//   * replacing the image object (resize, new axis, import, preset): PushReplaced(old image);
//   * selection changes made by the user (a new shape, select all, deselect, invert):
//     PushSelection(previous selection), so Ctrl+Z steps back through selections too, like an
//     image editor, instead of jumping over them to the last pixel edit (which made the first undo
//     after a series of selections look like "back to the initial state");
//   * SETTINGS of a room (duration, synthesis settings, filter settings, analysis settings, tempo…):
//     PushState(label, previous state, swap). The entry keeps an opaque snapshot and a swap function
//     supplied by the room: undo calls swap(snapshot), which applies it and returns the state it
//     replaced (kept for redo). Repeated changes of the same setting within a moment (spinner clicks,
//     typing) are coalesced into ONE step that remembers the oldest value.
// The column layout matches SpectralImage: column-major, so one column is Height consecutive floats
// and a column range [x0, x1) is ONE contiguous block — capturing it is a single Array.Copy.
//
// Noise layer: a region entry also captures the optional noisiness layer (SpectralImage.Noise).
// N == null in an entry means «the image had no noise layer then», i.e. every cell was tonal (0).
// Because the layer can be CREATED during an edit (the first stroke of the noise brush), restoring
// handles every combination: entry without N + image with a layer → those columns go back to 0;
// entry with N + image without a layer → the layer is created and filled. A layer is never removed
// by an undo (an all-zero layer sounds exactly like none, see SpectralImage.HasNoise).

namespace CsoundVisual.Paint;

/// <summary>
/// Bounded undo/redo stacks of image edits, used through <see cref="PaintSession"/>.
/// </summary>
public sealed class ImageHistory
{
    /// <summary>One undoable step.</summary>
    /// <param name="Label">Short description (for menus: «Undo pencil»).</param>
    abstract record Entry(string Label)
    {
        /// <summary>Approximate memory held by the entry, in bytes.</summary>
        public abstract long Bytes { get; }
    }

    /// <summary>
    /// Columns [<paramref name="X0"/>, <paramref name="X1"/>) of both layers, as they were before
    /// (on the undo stack) or after (on the redo stack) the edit. Only valid for the image object
    /// <paramref name="Target"/> (same size and axis).
    /// </summary>
    /// <param name="Label">Short description of the step.</param>
    /// <param name="Target">The image object the columns belong to (compared by reference).</param>
    /// <param name="X0">First captured column (inclusive).</param>
    /// <param name="X1">End of the captured columns (exclusive).</param>
    /// <param name="L">Left layer of the columns, column-major ((X1 − X0) × Height floats).</param>
    /// <param name="R">Right layer of the columns, same layout.</param>
    /// <param name="N">Noisiness of the columns (same layout), or <c>null</c> = the image had no noise layer (all tonal).</param>
    sealed record RegionEntry(string Label, SpectralImage Target, int X0, int X1, float[] L, float[] R, float[]? N = null) : Entry(Label)
    {
        /// <inheritdoc/>
        /// <remarks>The pixel arrays (noise included when captured) plus a rough 64-byte allowance for the record itself.</remarks>
        public override long Bytes => (L.LongLength + R.LongLength + (N?.LongLength ?? 0)) * sizeof(float) + 64;
    }

    /// <summary>A whole image object that was replaced (size or axis change, import, preset).</summary>
    /// <param name="Label">Short description of the step.</param>
    /// <param name="Image">The replaced (undo stack) or replacing (redo stack) image object, kept alive whole.</param>
    sealed record ImageEntry(string Label, SpectralImage Image) : Entry(Label)
    {
        /// <inheritdoc/>
        public override long Bytes => (Image.Left.LongLength + Image.Right.LongLength + (Image.Noise?.LongLength ?? 0)) * sizeof(float) + 64;
    }

    /// <summary>A selection change: the selection as it was before (undo stack) or after (redo stack).</summary>
    /// <param name="Label">Short description of the step ("select").</param>
    /// <param name="Mask">The selection to restore (<c>null</c> = nothing selected). Masks are immutable, so keeping the reference is enough.</param>
    sealed record SelectionEntry(string Label, SelectionMask? Mask) : Entry(Label)
    {
        /// <inheritdoc/>
        /// <remarks>A shaped mask holds one float per cell of its bounds; a rectangle almost nothing.</remarks>
        public override long Bytes => (Mask is { IsRectangle: false } m ? (long)m.Bounds.Width * m.Bounds.Height * sizeof(float) : 0) + 64;
    }

    /// <summary>A settings change (see the file header): an opaque snapshot plus the room's swap function.</summary>
    /// <param name="Label">Short description; also the coalescing key.</param>
    /// <param name="State">The state to restore (before the change on the undo stack, after it on the redo stack).</param>
    /// <param name="Swap">Applies a state and returns the state it replaced. Runs on the UI thread during undo/redo.</param>
    /// <param name="When">When the step was last extended (UTC): coalescing window.</param>
    sealed record StateEntry(string Label, object? State, Func<object?, object?> Swap, DateTime When) : Entry(Label)
    {
        /// <inheritdoc/>
        /// <remarks>Settings records are small; a flat allowance (a snapshot may also pin an image object, which other entries account for).</remarks>
        public override long Bytes => 256;
    }

    /// <summary>Changes of the same setting closer together than this become one undo step.</summary>
    static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1.5);

    /// <summary>Raised after a new step was stored (not on undo/redo). Lets a room order its several histories.</summary>
    public event Action? Pushed;

    /// <summary>Undo steps, oldest first (a linked list so the oldest can be dropped from the front in O(1)).</summary>
    readonly LinkedList<Entry> undo = new();
    /// <summary>Redo steps, most recently undone on top; cleared by every new step.</summary>
    readonly Stack<Entry> redo = new();
    /// <summary>Sum of <see cref="Entry.Bytes"/> over both stacks.</summary>
    long bytes;
    /// <summary>The capture between <see cref="Begin"/> and <see cref="Commit"/>, or <c>null</c>.</summary>
    RegionEntry? pending;

    /// <summary>Memory budget for all stored entries (undo + redo), in bytes.</summary>
    public long MaxBytes { get; set; } = 384L * 1024 * 1024;

    /// <summary>Maximum number of undo steps kept.</summary>
    public int MaxEntries { get; set; } = 200;

    /// <summary>True when there is something to undo.</summary>
    public bool CanUndo => undo.Count > 0;

    /// <summary>True when there is something to redo.</summary>
    public bool CanRedo => redo.Count > 0;

    /// <summary>Label of the next undo step, or <c>null</c>.</summary>
    public string? UndoLabel => undo.Last?.Value.Label;

    /// <summary>Label of the next redo step, or <c>null</c>.</summary>
    public string? RedoLabel => redo.Count > 0 ? redo.Peek().Label : null;

    /// <summary>Memory currently held, in bytes.</summary>
    public long Bytes => bytes;

    /// <summary>
    /// Captures columns [x0, x1) of <paramref name="image"/> BEFORE an edit. Must be followed by
    /// <see cref="Commit"/> (or <see cref="Cancel"/>). Widen the range with <see cref="Extend"/> if
    /// the edit grows (a brush stroke moving sideways).
    /// </summary>
    /// <param name="image">The image about to be edited.</param>
    /// <param name="x0">First column the edit may touch (clamped to the image).</param>
    /// <param name="x1">End column, exclusive (clamped; may be widened later).</param>
    /// <param name="label">Step label (tool or operation id).</param>
    /// <remarks>A previous uncommitted capture is silently replaced.</remarks>
    public void Begin(SpectralImage image, int x0, int x1, string label)
    {
        x0 = Math.Clamp(x0, 0, image.Width);
        x1 = Math.Clamp(x1, x0, image.Width);
        pending = new RegionEntry(label, image, x0, x1, Copy(image.Left, image.Height, x0, x1), Copy(image.Right, image.Height, x0, x1),
            image.Noise is { } n ? Copy(n, image.Height, x0, x1) : null);
    }

    /// <summary>True between <see cref="Begin"/> and <see cref="Commit"/>.</summary>
    public bool IsRecording => pending is not null;

    /// <summary>
    /// Widens the pending region to include [x0, x1). The new columns are captured NOW, so call it
    /// before modifying them (the brush engine does this before every stamp).
    /// </summary>
    /// <param name="x0">First column to include (clamped).</param>
    /// <param name="x1">End column to include, exclusive (clamped).</param>
    /// <remarks>
    /// Re-allocates the capture to the union range each time it grows; a stroke usually grows by a
    /// few columns at a time, so the cost stays proportional to the stroke's width.
    /// </remarks>
    public void Extend(int x0, int x1)
    {
        if (pending is not { } p) return;
        var img = p.Target;
        x0 = Math.Clamp(x0, 0, img.Width);
        x1 = Math.Clamp(x1, 0, img.Width);
        // Already covered: nothing to capture.
        if (x0 >= p.X0 && x1 <= p.X1) return;
        int n0 = Math.Min(x0, p.X0), n1 = Math.Max(x1, p.X1);
        int h = img.Height;
        var l = new float[(n1 - n0) * h];
        var r = new float[(n1 - n0) * h];
        // Noise: captured as soon as the image HAS a layer (it may have been created after Begin,
        // in which case the already captured columns were tonal = zeros, which a new array already is).
        var nn = img.Noise is null ? null : new float[(n1 - n0) * h];
        // Already captured columns keep their ORIGINAL (pre-edit) values; new ones are read now.
        for (int x = n0; x < n1; x++)
        {
            bool inOld = x >= p.X0 && x < p.X1;
            Array.Copy(inOld ? p.L : img.Left, inOld ? (x - p.X0) * h : x * h, l, (x - n0) * h, h);
            Array.Copy(inOld ? p.R : img.Right, inOld ? (x - p.X0) * h : x * h, r, (x - n0) * h, h);
            if (nn is null) continue;
            if (!inOld) Array.Copy(img.Noise!, x * h, nn, (x - n0) * h, h);
            else if (p.N is not null) Array.Copy(p.N, (x - p.X0) * h, nn, (x - n0) * h, h);
        }
        pending = p with { X0 = n0, X1 = n1, L = l, R = r, N = nn };
    }

    /// <summary>Stores the pending region as one undo step (and clears the redo stack).</summary>
    /// <remarks>An empty region (e.g. a stroke entirely outside the canvas) is dropped, not stored.</remarks>
    public void Commit()
    {
        if (pending is not { } p) return;
        pending = null;
        if (p.X1 <= p.X0) return;
        Push(p);
    }

    /// <summary>Drops the pending capture without storing it.</summary>
    public void Cancel() => pending = null;

    /// <summary>Stores <paramref name="previous"/> (an image that is being replaced) as one undo step.</summary>
    /// <param name="previous">The outgoing image object; must not be modified afterwards (it is kept by reference).</param>
    /// <param name="label">Step label.</param>
    public void PushReplaced(SpectralImage previous, string label)
    {
        pending = null;
        Push(new ImageEntry(label, previous));
    }

    /// <summary>Stores the selection that is about to be replaced as one undo step.</summary>
    /// <param name="previous">The selection before the change (<c>null</c> = none).</param>
    /// <param name="label">Step label.</param>
    /// <remarks>Clears the redo stack like any new step. A pending pixel capture is left alone (a
    /// selection never changes in the middle of a stroke).</remarks>
    public void PushSelection(SelectionMask? previous, string label) => Push(new SelectionEntry(label, previous));

    /// <summary>Stores a settings change as an undo step (see the file header).</summary>
    /// <param name="label">Step label and coalescing key (e.g. «synth», «duration»).</param>
    /// <param name="previous">The state before the change (any immutable snapshot the swap understands).</param>
    /// <param name="swap">Applies a snapshot and returns the state it replaced.</param>
    /// <remarks>
    /// When the newest undo step has the same label, nothing was undone since (empty redo stack) and
    /// it is younger than <see cref="CoalesceWindow"/>, it is only refreshed: it keeps the OLDER
    /// state, so one undo goes back to before the whole burst of changes.
    /// </remarks>
    public void PushState(string label, object? previous, Func<object?, object?> swap)
    {
        if (redo.Count == 0 && undo.Last is { Value: StateEntry s } node && s.Label == label && DateTime.UtcNow - s.When < CoalesceWindow)
        {
            node.Value = s with { When = DateTime.UtcNow };
            return;
        }
        Push(new StateEntry(label, previous, swap, DateTime.UtcNow));
    }

    /// <summary>Forgets everything (e.g. after a new empty canvas when memory matters).</summary>
    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        bytes = 0;
        pending = null;
    }

    /// <summary>
    /// Undoes the last step on <paramref name="current"/>. Returns the image that is current
    /// afterwards: the same object for region steps (its pixels were restored in place; the caller
    /// raises the change notification), or the previous image object for replacement steps.
    /// </summary>
    /// <param name="current">The session's current image.</param>
    /// <param name="selection">The session's selection; a selection step replaces it (and the
    /// replaced one goes to the redo stack).</param>
    /// <param name="dirty">Column range that changed (region steps), or the whole new image.</param>
    /// <returns>The image that is current after the undo (unchanged object when there was nothing to undo).</returns>
    public SpectralImage Undo(SpectralImage current, ref SelectionMask? selection, out (int X0, int X1) dirty)
    {
        dirty = (0, 0);
        if (undo.Last is not { } node) return current;
        undo.RemoveLast();
        bytes -= node.Value.Bytes;
        var (next, swapped) = Apply(node.Value, current, ref selection, out dirty);
        redo.Push(swapped);
        bytes += swapped.Bytes;
        return next;
    }

    /// <summary>Redoes the last undone step (see <see cref="Undo"/>).</summary>
    /// <param name="current">The session's current image.</param>
    /// <param name="selection">The session's selection (see <see cref="Undo"/>).</param>
    /// <param name="dirty">Column range that changed.</param>
    /// <returns>The image that is current after the redo.</returns>
    /// <remarks>Unlike <see cref="Push"/>, a redo does not trim the budget: the entry only moves between stacks.</remarks>
    public SpectralImage Redo(SpectralImage current, ref SelectionMask? selection, out (int X0, int X1) dirty)
    {
        dirty = (0, 0);
        if (redo.Count == 0) return current;
        var e = redo.Pop();
        bytes -= e.Bytes;
        var (next, swapped) = Apply(e, current, ref selection, out dirty);
        undo.AddLast(swapped);
        bytes += swapped.Bytes;
        return next;
    }

    /// <summary>
    /// Applies a stored step to <paramref name="current"/> and returns (image now current, entry
    /// holding the state that was replaced) — that entry goes to the opposite stack.
    /// </summary>
    /// <param name="e">The step to apply.</param>
    /// <param name="current">The current image.</param>
    /// <param name="selection">The current selection (replaced by a selection step).</param>
    /// <param name="dirty">Columns that changed ((0, 0) when nothing did).</param>
    /// <returns>The image now current and the entry for the opposite stack.</returns>
    static (SpectralImage Next, Entry Swapped) Apply(Entry e, SpectralImage current, ref SelectionMask? selection, out (int, int) dirty)
    {
        switch (e)
        {
            case StateEntry st:
            {
                // A settings step: the room's swap applies the stored state and hands back the one
                // it replaced, which becomes the opposite step. Pixels are untouched here.
                dirty = (0, 0);
                var replaced = st.Swap(st.State);
                return (current, st with { State = replaced, When = DateTime.MinValue });
            }
            case SelectionEntry s:
            {
                // Swap the stored selection with the current one; pixels are untouched.
                dirty = (0, 0);
                var swapped = new SelectionEntry(s.Label, selection);
                selection = s.Mask;
                return (current, swapped);
            }
            case RegionEntry r when ReferenceEquals(r.Target, current):
            {
                // Swap the stored columns with the current ones (in place, no allocation beyond the temp).
                int h = current.Height, n = (r.X1 - r.X0) * h, o = r.X0 * h;
                var tmpL = new float[n];
                var tmpR = new float[n];
                Array.Copy(current.Left, o, tmpL, 0, n);
                Array.Copy(current.Right, o, tmpR, 0, n);
                Array.Copy(r.L, 0, current.Left, o, n);
                Array.Copy(r.R, 0, current.Right, o, n);
                // Noise layer: save the current columns (null when the image has no layer), then
                // restore the stored ones — zeros when the entry was captured without a layer.
                float[]? tmpN = null;
                if (current.Noise is { } cn)
                {
                    tmpN = new float[n];
                    Array.Copy(cn, o, tmpN, 0, n);
                    if (r.N is null) Array.Clear(cn, o, n);
                }
                if (r.N is not null) Array.Copy(r.N, 0, current.EnsureNoise(), o, n);
                dirty = (r.X0, r.X1);
                return (current, r with { L = tmpL, R = tmpR, N = tmpN });
            }
            case ImageEntry i:
                dirty = (0, i.Image.Width);
                return (i.Image, new ImageEntry(i.Label, current));
            default:
                // A region step recorded on another image object (should not happen because image
                // replacements are steps of their own): ignore it rather than corrupt pixels.
                dirty = (0, 0);
                return (current, e);
        }
    }

    /// <summary>Adds a step, clears the redo stack and trims the oldest steps to the budget.</summary>
    /// <param name="e">The new step.</param>
    void Push(Entry e)
    {
        foreach (var r in redo) bytes -= r.Bytes;
        redo.Clear();
        undo.AddLast(e);
        bytes += e.Bytes;
        Pushed?.Invoke();
        // «Count > 1»: the newest step is always kept, even if it alone exceeds MaxBytes.
        while (undo.Count > 1 && (bytes > MaxBytes || undo.Count > MaxEntries))
        {
            bytes -= undo.First!.Value.Bytes;
            undo.RemoveFirst();
        }
    }

    /// <summary>Copies columns [x0, x1) of a layer.</summary>
    /// <param name="layer">Left or Right array of the image (column-major).</param>
    /// <param name="height">Rows per column.</param>
    /// <param name="x0">First column (inclusive).</param>
    /// <param name="x1">End column (exclusive).</param>
    /// <returns>A new array of (x1 − x0) × height floats (one contiguous block of the layer).</returns>
    static float[] Copy(float[] layer, int height, int x0, int x1)
    {
        var a = new float[(x1 - x0) * height];
        Array.Copy(layer, x0 * height, a, 0, a.Length);
        return a;
    }
}
