using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using CsoundVisual.Theming;

namespace CsoundVisual.Controls;

/// <summary>
/// Horizontal peak meters, one bar per output channel, on a −60…0 dBFS scale. Bars attack
/// instantly and fall back smoothly; a thin marker holds the recent peak for about a second.
/// </summary>
/// <remarks>
/// <para>
/// Fed by <c>MainWindow</c> once per animation frame with the peaks the engine collected since the
/// previous frame (or, while a DAW plug-in is linked, with the plug-in's peaks). The same class is
/// used for the output meter and for the smaller input ("IN") meter.
/// </para>
/// <para>
/// The control is drawn entirely in <see cref="Render"/> (no template, no AXAML). All state lives in
/// fixed-size arrays allocated once, so a frame never allocates except for the channel-number labels.
/// The ballistics are frame-based, not time-based: they assume the ~60 fps animation timer that
/// drives <see cref="Push"/>.
/// </para>
/// <para>
/// Brushes come from <see cref="AppTheme"/>; they are stable objects recoloured in place when the
/// theme changes, which is why caching them in static fields is safe.
/// </para>
/// </remarks>
public sealed class LevelMeter : Control
{
    /// <summary>Bottom of the scale in dBFS; anything quieter draws as an empty bar.</summary>
    const float MinDb = -60f;
    /// <summary>Maximum number of bars; extra channels in a <see cref="Push"/> are ignored.</summary>
    const int MaxChannels = 8;

    /// <summary>Displayed bar length per channel, normalised 0…1 along the dB scale (after release smoothing).</summary>
    readonly float[] level = new float[MaxChannels];
    /// <summary>Peak-hold marker position per channel, normalised 0…1 like <see cref="level"/>.</summary>
    readonly float[] hold = new float[MaxChannels];
    /// <summary>Frames left before each hold marker starts to fall (counts down from 50 in <see cref="Push"/>).</summary>
    readonly int[] holdFrames = new int[MaxChannels];
    /// <summary>Number of bars currently drawn: the length of the last <see cref="Push"/>, clamped to 1…<see cref="MaxChannels"/>.</summary>
    int channels = 2;

    /// <summary>Background of each bar (the unlit part).</summary>
    static readonly IBrush Track = AppTheme.Brush("BgDeep");
    /// <summary>Colour of the thin peak-hold marker.</summary>
    static readonly IBrush HoldBrush = AppTheme.Brush("Text");
    /// <summary>Colour of the channel numbers on the left.</summary>
    static readonly IBrush LabelBrush = AppTheme.Brush("TextDim");
    /// <summary>
    /// Horizontal gradient of the lit part, relative to the whole track (not to the lit length), so a
    /// colour always means the same level: accent up to ~−21 dB, "hot" near −7 dB, clip colour at 0 dB.
    /// </summary>
    static readonly LinearGradientBrush Fill = AppTheme.Gradient(
        new RelativePoint(0, 0, RelativeUnit.Relative), new RelativePoint(1, 0, RelativeUnit.Relative),
        ("AccentDeep", 0.0), ("Accent", 0.65), ("Hot", 0.88), ("Clip", 1.0));

    /// <summary>Feeds one animation frame of linear peaks (1.0 = 0 dBFS), one value per channel.</summary>
    /// <param name="peaks">Absolute peak per channel since the previous frame; its length sets the number of bars.</param>
    /// <remarks>Must be called on the UI thread (it invalidates the visual).</remarks>
    public void Push(ReadOnlySpan<float> peaks)
    {
        channels = Math.Clamp(peaks.Length, 1, MaxChannels);
        for (int i = 0; i < channels; i++)
        {
            float target = ToNorm(peaks[i]);
            // Instant attack; release of ~20 dB/s at 60 fps. The hold marker waits 50 frames, then falls.
            level[i] = target > level[i] ? target : Math.Max(target, level[i] - 0.0055f);
            if (level[i] >= hold[i]) { hold[i] = level[i]; holdFrames[i] = 50; }
            else if (--holdFrames[i] < 0) hold[i] = Math.Max(0, hold[i] - 0.01f);
        }
        InvalidateVisual();
    }

    /// <summary>Clears all bars and hold markers (used when a performance stops).</summary>
    /// <remarks><see cref="holdFrames"/> is not cleared: with <see cref="hold"/> at 0 the next peak resets it anyway.</remarks>
    public void Reset()
    {
        Array.Clear(level);
        Array.Clear(hold);
        InvalidateVisual();
    }

    /// <summary>Maps a linear amplitude to 0…1 on the meter's dB scale.</summary>
    /// <param name="lin">Linear amplitude (1.0 = 0 dBFS).</param>
    /// <returns>0 at or below <see cref="MinDb"/>, 1 at 0 dBFS and above, linear in dB in between.</returns>
    static float ToNorm(float lin)
    {
        // −120 dB guard: avoids Log10(0) = −∞ for digital silence.
        if (lin <= 1e-6f) return 0;
        float db = 20f * MathF.Log10(lin);
        return Math.Clamp((db - MinDb) / -MinDb, 0, 1);
    }

    /// <summary>
    /// Draws one row per channel: the channel number, the track, the lit part (clipped to the
    /// rounded track so the gradient keeps its rounded ends) and the hold marker.
    /// </summary>
    /// <param name="ctx">Avalonia drawing context.</param>
    public override void Render(DrawingContext ctx)
    {
        var b = Bounds;
        // labelW = column for the channel number (px); gap = vertical space between bars (px).
        // Bars share the height equally but never get thinner than 4 px.
        const double labelW = 22, gap = 4;
        double barH = Math.Max(4, (b.Height - gap * (channels - 1)) / channels);
        double w = b.Width - labelW;
        for (int i = 0; i < channels; i++)
        {
            double y = i * (barH + gap);
            var label = new FormattedText((i + 1).ToString(), System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, Typeface.Default, 10, LabelBrush);
            ctx.DrawText(label, new Point(4, y + (barH - label.Height) / 2));

            var track = new Rect(labelW, y, w, barH);
            ctx.DrawRectangle(Track, null, track, 3, 3);
            if (level[i] > 0)
            {
                using (ctx.PushClip(new RoundedRect(track, 3)))
                    ctx.DrawRectangle(Fill, null, new Rect(labelW, y, w * level[i], barH));
            }
            // 2 px marker ending exactly at the held position; hidden when it has fallen to the bottom.
            if (hold[i] > 0.01f)
                ctx.DrawRectangle(HoldBrush, null, new Rect(labelW + w * hold[i] - 2, y, 2, barH));
        }
    }
}
