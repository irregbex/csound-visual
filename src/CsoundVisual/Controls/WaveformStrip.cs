// WaveformStrip.cs — Paint Rooms: a waveform overview drawn TIME-ALIGNED with a PaintCanvas.
//
// Role: the Spectrum Room shows the analysed recording's waveform in a strip right above its canvas,
// so a transient in the waveform sits exactly above its column in the picture whatever the zoom and
// scroll. The strip reads the canvas geometry (RulerWidth, ScrollX, ZoomX, the image's seconds per
// column) on every repaint and repaints when the canvas reports ViewChanged / CursorMoved.
//
// Drawing (fast for hour-long files): the audio is summarised once into min/max PEAKS per block of
// 256 samples per channel; each screen pixel then needs at most a few peak blocks (or the raw
// samples when zoomed in further than one block per pixel). Stereo: left channel in the upper half,
// right channel in the lower half. It also draws the play-from cursor (dashed) and the play line.
//
// Mouse: clicking or dragging in the strip moves the canvas's play-from cursor.
// UI thread only.
//
// Coordinate mapping (shared with PaintCanvas, so both stay aligned pixel for pixel):
//   control x  →  image column  = ScrollX + (x − RulerWidth) / ZoomX      (ZoomX = pixels per column)
//   image column → seconds      = column · SecondsPerColumn
//   seconds    →  sample index  = seconds · SampleRate
// The left RulerWidth pixels are a gutter that mirrors the canvas's pitch ruler; it holds the
// channel labels instead of waveform. The strip has no zoom or scroll of its own.
//
// Cost: BuildPeaks is O(samples) once per Audio assignment (UI thread; ≈ tens of ms for an hour of
// stereo 48 kHz). Render is O(width × blocks-per-pixel) and never touches more raw samples than
// Block per pixel, so it stays cheap at any zoom.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using CsoundVisual.Paint;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>Waveform overview aligned with a <see cref="PaintCanvas"/> (see the file header).</summary>
public sealed class WaveformStrip : Control
{
    /// <summary>Samples summarised by one peak entry.</summary>
    const int Block = 256;

    /// <summary>Font of the hint text and channel labels (font fallback list).</summary>
    static readonly Typeface Face = new("Segoe UI, Inter, Arial");

    /// <summary>The canvas whose time axis (scroll, zoom, seconds per column, play-from cursor) the strip follows.</summary>
    readonly PaintCanvas canvas;

    /// <summary>Backing field of <see cref="Audio"/>.</summary>
    AudioData? audio;

    /// <summary>
    /// Peak summary: <c>mins[c][b]</c> / <c>maxs[c][b]</c> are the smallest / largest sample of block
    /// b (samples b·<see cref="Block"/> … (b+1)·Block − 1) of channel c. Empty arrays when there is no audio.
    /// </summary>
    float[][] mins = [], maxs = [];   // per channel, per block

    /// <summary>Backing field of <see cref="PlaySeconds"/>.</summary>
    double? playSeconds;

    /// <summary>Creates a strip that follows <paramref name="canvas"/>.</summary>
    /// <param name="canvas">The Spectrum Room canvas to align with.</param>
    /// <remarks>
    /// Subscribes to the canvas's view and cursor events for repaints. The strip and the canvas are
    /// created and discarded together by the room, so the subscriptions are never removed.
    /// </remarks>
    public WaveformStrip(PaintCanvas canvas)
    {
        this.canvas = canvas;
        ClipToBounds = true;
        canvas.ViewChanged += InvalidateVisual;
        canvas.CursorMoved += InvalidateVisual;
    }

    /// <summary>The sound shown (first two channels), or <c>null</c> for an empty strip. Setting it rebuilds the peaks.</summary>
    public AudioData? Audio
    {
        get => audio;
        set
        {
            audio = value;
            BuildPeaks();
            InvalidateVisual();
        }
    }

    /// <summary>Time being heard (seconds), or <c>null</c> when nothing plays (draws the play line).</summary>
    public double? PlaySeconds
    {
        get => playSeconds;
        set
        {
            if (playSeconds == value) return;
            playSeconds = value;
            InvalidateVisual();
        }
    }

    /// <summary>Summarises the first two channels into min/max per <see cref="Block"/> samples.</summary>
    void BuildPeaks()
    {
        if (audio is null) { mins = maxs = []; return; }
        int chans = Math.Min(2, audio.Channels.Length);
        mins = new float[chans][];
        maxs = new float[chans][];
        for (int c = 0; c < chans; c++)
        {
            var x = audio.Channels[c];
            int blocks = (x.Length + Block - 1) / Block;
            var mn = new float[blocks];
            var mx = new float[blocks];
            // The last block may be shorter than Block (end is clamped to the channel length).
            for (int b = 0; b < blocks; b++)
            {
                float lo = float.MaxValue, hi = float.MinValue;
                int end = Math.Min(x.Length, (b + 1) * Block);
                for (int i = b * Block; i < end; i++)
                {
                    float v = x[i];
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
                mn[b] = lo;
                mx[b] = hi;
            }
            mins[c] = mn;
            maxs[c] = mx;
        }
    }

    /// <summary>Seconds at control x (through the canvas's column mapping).</summary>
    /// <param name="x">Horizontal position in this control (pixels).</param>
    /// <param name="img">The canvas's image (gives <see cref="SpectralImage.SecondsPerColumn"/>).</param>
    /// <returns>Time in seconds (may be negative or beyond the end; callers clamp).</returns>
    double SecondsAt(double x, SpectralImage img) => (canvas.ScrollX + (x - PaintCanvas.RulerWidth) / canvas.ZoomX) * img.SecondsPerColumn;

    /// <summary>Control x of a time in seconds (inverse of <see cref="SecondsAt"/>).</summary>
    /// <param name="seconds">Time in seconds.</param>
    /// <param name="img">The canvas's image.</param>
    /// <returns>Horizontal position in this control (pixels).</returns>
    double XOf(double seconds, SpectralImage img) => PaintCanvas.RulerWidth + (seconds / img.SecondsPerColumn - canvas.ScrollX) * canvas.ZoomX;

    /// <inheritdoc/>
    /// <remarks>
    /// Background, gutter, then (clipped to the waveform area) one vertical min→max line per pixel
    /// column for every channel lane, the play-from cursor and the play line; finally the channel
    /// labels in the gutter. With no audio or no image it only shows a «(no sound)» hint.
    /// </remarks>
    public override void Render(DrawingContext ctx)
    {
        var bounds = new Rect(Bounds.Size);
        ctx.FillRectangle(AppTheme.Brush("BgDeep"), bounds);
        ctx.FillRectangle(AppTheme.Brush("Surface"), new Rect(0, 0, PaintCanvas.RulerWidth, bounds.Height));
        var img = canvas.Session?.Image;
        if (audio is null || img is null || mins.Length == 0)
        {
            var hint = new FormattedText(Localization.Loc.S("(sin sonido)", "(no sound)"), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Face, 11, AppTheme.Brush("TextDim"));
            ctx.DrawText(hint, new Point(PaintCanvas.RulerWidth + 8, (bounds.Height - hint.Height) / 2));
            return;
        }
        // One horizontal lane per channel (mono: the whole height; stereo: left on top, right below).
        int chans = mins.Length;
        double laneH = bounds.Height / chans;
        var wave = new Pen(AppTheme.Brush("AccentHi"), 1);
        var axisPen = new Pen(AppTheme.Brush("Grid"), 1);
        double sr = audio.SampleRate;
        double right = bounds.Width;
        using (ctx.PushClip(new Rect(PaintCanvas.RulerWidth, 0, Math.Max(0, right - PaintCanvas.RulerWidth), bounds.Height)))
        {
            for (int c = 0; c < chans; c++)
            {
                // mid = the lane's zero line; half = pixels for full scale (±1), with a 2 px margin.
                double mid = laneH * (c + 0.5), half = laneH / 2 - 2;
                ctx.DrawLine(axisPen, new Point(PaintCanvas.RulerWidth, mid), new Point(right, mid));
                var x = audio.Channels[c];
                for (double px = PaintCanvas.RulerWidth; px < right; px += 1)
                {
                    // Sample range [s0, s1) covered by this pixel column; skip pixels before the start or
                    // after the end of the sound, and force at least one sample when zoomed in very far.
                    long s0 = (long)Math.Floor(SecondsAt(px, img) * sr), s1 = (long)Math.Floor(SecondsAt(px + 1, img) * sr);
                    if (s1 <= 0 || s0 >= x.Length) continue;
                    s0 = Math.Max(0, s0);
                    s1 = Math.Clamp(s1, s0 + 1, x.Length);
                    float lo, hi;
                    if (s1 - s0 >= Block)
                    {
                        // Many samples per pixel: use the peak blocks.
                        lo = float.MaxValue; hi = float.MinValue;
                        for (long b = s0 / Block; b <= (s1 - 1) / Block; b++)
                        {
                            lo = Math.Min(lo, mins[c][b]);
                            hi = Math.Max(hi, maxs[c][b]);
                        }
                    }
                    else
                    {
                        // Fewer samples than a block: scan the raw samples (exact, and at most Block reads).
                        lo = float.MaxValue; hi = float.MinValue;
                        for (long i = s0; i < s1; i++)
                        {
                            lo = Math.Min(lo, x[i]);
                            hi = Math.Max(hi, x[i]);
                        }
                    }
                    double y0 = mid - Math.Clamp(hi, -1, 1) * half, y1 = mid - Math.Clamp(lo, -1, 1) * half;
                    // y grows downwards, so the maximum gives the top; silence still draws a 1 px tick.
                    if (y1 - y0 < 1) y1 = y0 + 1;
                    ctx.DrawLine(wave, new Point(px + 0.5, y0), new Point(px + 0.5, y1));
                }
            }
            // Play-from cursor (dashed, as on the canvas) and play line.
            double cx = XOf(canvas.CursorColumn * img.SecondsPerColumn, img);
            ctx.DrawLine(new Pen(AppTheme.Brush("Warn"), 1, new DashStyle([3, 3], 0)), new Point(cx, 0), new Point(cx, bounds.Height));
            if (playSeconds is { } ps)
            {
                double x = XOf(ps, img);
                ctx.DrawLine(new Pen(AppTheme.Brush("Good"), 2), new Point(x, 0), new Point(x, bounds.Height));
            }
        }
        // Channel labels in the gutter (aligned with the canvas's pitch ruler).
        for (int c = 0; c < chans; c++)
        {
            string label = chans == 1 ? "Mono" : c == 0 ? Localization.Loc.S("Izq.", "L") : Localization.Loc.S("Der.", "R");
            var ft = new FormattedText(label, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Face, 10.5, AppTheme.Brush("TextDim"));
            ctx.DrawText(ft, new Point(PaintCanvas.RulerWidth - 6 - ft.Width, laneH * (c + 0.5) - ft.Height / 2));
        }
        ctx.DrawLine(new Pen(AppTheme.Brush("Line"), 1), new Point(PaintCanvas.RulerWidth - 0.5, 0), new Point(PaintCanvas.RulerWidth - 0.5, bounds.Height));
    }

    /// <inheritdoc/>
    /// <remarks>Moves the cursor and captures the pointer so a drag keeps working outside the strip.</remarks>
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        MoveCursor(e.GetPosition(this));
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    /// <inheritdoc/>
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) MoveCursor(e.GetPosition(this));
    }

    /// <inheritdoc/>
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
    }

    /// <summary>Moves the canvas's play-from cursor to the time under <paramref name="p"/>.</summary>
    /// <param name="p">Pointer position in this control; ignored inside the gutter.</param>
    /// <remarks>
    /// <see cref="PaintCanvas.CursorColumn"/> is a FRACTIONAL column (0 … image width); setting it
    /// raises the canvas's CursorMoved, which repaints both the canvas and this strip.
    /// </remarks>
    void MoveCursor(Point p)
    {
        if (canvas.Session?.Image is not { } img || p.X < PaintCanvas.RulerWidth) return;
        canvas.CursorColumn = Math.Clamp(canvas.ScrollX + (p.X - PaintCanvas.RulerWidth) / canvas.ZoomX, 0, img.Width);
    }
}
