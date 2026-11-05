using System.Diagnostics;

namespace CsoundVisual.Audio;

/// <summary>
/// How the channels of the live input device are routed to Csound's input channels
/// (<c>in</c>, <c>ins</c>, <c>inch</c>). Chosen in Settings › Audio › «Input channels» and
/// applied live by <see cref="AdaptiveInputReader.Read"/>.
/// </summary>
/// <remarks>
/// An audio interface such as the UR22mkII exposes its two inputs as ONE stereo capture device:
/// input 1 = left, input 2 = right. <see cref="Stereo"/> keeps them as two independent mono
/// channels (<c>inch 1</c> = input 1, <c>inch 2</c> = input 2), which is what an orchestra that
/// processes two sources separately wants. The mono modes copy one signal to every Csound input
/// channel, so a stereo effect (<c>ain1, ain2 in</c>) hears a single microphone on both sides.
/// The numeric values are stored in the settings file: do not reorder.
/// </remarks>
public enum InputRouting
{
    /// <summary>Device input 1 → Csound channel 1, input 2 → channel 2 (two independent channels).</summary>
    Stereo = 0,

    /// <summary>Device input 1 only, copied to every Csound input channel.</summary>
    Input1 = 1,

    /// <summary>Device input 2 only, copied to every Csound input channel.</summary>
    Input2 = 2,

    /// <summary>Inputs 1 + 2 summed (not scaled) and copied to every Csound input channel.</summary>
    Sum = 3,
}

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of interleaved <c>float</c> frames,
/// used to hand live audio from the capture thread (<see cref="WasapiInput"/>) to the Csound
/// performance thread (<see cref="AdaptiveInputReader"/>).
/// </summary>
/// <remarks>
/// <para>
/// The buffer is allocated once; neither side allocates, locks or waits. Positions are 64-bit
/// frame counters that only grow (they never wrap in practice), so the fill level is simply
/// <c>write − read</c>. The capacity is a power of two so a position maps to a slot with a mask.
/// </para>
/// <para>
/// <b>Ownership rules.</b> Only the producer calls <see cref="Write"/>; only the consumer calls
/// <see cref="TryReadFrame"/> and <see cref="Skip"/>. The producer publishes data with a
/// release write of <c>writePos</c> after the samples are stored; the consumer publishes freed
/// space with a release write of <c>readPos</c>. When the ring is full the producer drops the
/// incoming frames (it may not touch <c>readPos</c>); dropping the <i>oldest</i> frames is the
/// consumer's job (<see cref="Skip"/>), see <see cref="AdaptiveInputReader"/>.
/// </para>
/// <para>
/// Each write also records when it happened (<see cref="LastWriteTicks"/>), so the consumer can
/// estimate a <i>continuous</i> fill level between packets (see <see cref="AdaptiveInputReader"/>).
/// </para>
/// </remarks>
public sealed class InputRingBuffer
{
    /// <summary>Sample storage: <see cref="CapacityFrames"/> × <see cref="Channels"/> interleaved floats.</summary>
    readonly float[] data;
    /// <summary><c>CapacityFrames − 1</c>: maps a frame position to its slot.</summary>
    readonly int mask;
    /// <summary>Total frames ever written (producer-owned; published with a release write).</summary>
    long writePos;
    /// <summary>Total frames ever consumed (consumer-owned; published with a release write).</summary>
    long readPos;
    /// <summary>Frames rejected because the ring was full.</summary>
    long droppedFrames;
    /// <summary>Timestamp of the last write (see <see cref="LastWriteTicks"/>).</summary>
    long lastWriteTicks;

    /// <summary>Creates a ring holding at least <paramref name="minFrames"/> frames of <paramref name="channels"/> channels.</summary>
    /// <remarks>The capacity is rounded up to the next power of two (e.g. 48 000 → 65 536 frames).</remarks>
    public InputRingBuffer(int channels, int minFrames)
    {
        Channels = Math.Max(1, channels);
        int cap = 1;
        while (cap < minFrames) cap <<= 1;
        CapacityFrames = cap;
        mask = cap - 1;
        data = new float[cap * Channels];
    }

    /// <summary>Interleaved channels per frame.</summary>
    public int Channels { get; }

    /// <summary>Capacity in frames (a power of two).</summary>
    public int CapacityFrames { get; }

    /// <summary>Frames currently stored (safe to call from either side; may be slightly stale).</summary>
    public int Count => (int)(Volatile.Read(ref writePos) - Volatile.Read(ref readPos));

    /// <summary>Frames the producer had to throw away because the ring was full.</summary>
    public long DroppedFrames => Volatile.Read(ref droppedFrames);

    /// <summary><see cref="Stopwatch"/> timestamp of the most recent <see cref="Write"/> (0 before the first).</summary>
    public long LastWriteTicks => Volatile.Read(ref lastWriteTicks);

    /// <summary>
    /// Producer: appends up to <paramref name="frames"/> interleaved frames. Frames that do not fit
    /// are dropped (counted in <see cref="DroppedFrames"/>).
    /// </summary>
    /// <param name="interleaved">Samples, <see cref="Channels"/> per frame.</param>
    /// <param name="frames">Frames to append.</param>
    /// <param name="timestampTicks"><see cref="Stopwatch"/> time of arrival of this packet (simulations pass their own clock).</param>
    /// <returns>The number of frames stored.</returns>
    public int Write(ReadOnlySpan<float> interleaved, int frames, long timestampTicks)
    {
        long w = writePos; // only this thread writes writePos
        int free = CapacityFrames - (int)(w - Volatile.Read(ref readPos));
        int n = Math.Min(frames, Math.Max(0, free));
        int ch = Channels;
        for (int f = 0; f < n; f++)
        {
            int slot = (int)((w + f) & mask) * ch;
            for (int c = 0; c < ch; c++)
                data[slot + c] = interleaved[f * ch + c];
        }
        if (n < frames)
            Interlocked.Add(ref droppedFrames, frames - n);
        Volatile.Write(ref lastWriteTicks, timestampTicks);
        Volatile.Write(ref writePos, w + n); // publish after the samples are stored
        return n;
    }

    /// <summary>Consumer: removes the oldest frame into <paramref name="frame"/> (length ≥ <see cref="Channels"/>).</summary>
    /// <returns><c>false</c> when the ring is empty.</returns>
    public bool TryReadFrame(Span<float> frame)
    {
        long r = readPos; // only this thread writes readPos
        if (Volatile.Read(ref writePos) - r <= 0)
            return false;
        int slot = (int)(r & mask) * Channels;
        for (int c = 0; c < Channels; c++)
            frame[c] = data[slot + c];
        Volatile.Write(ref readPos, r + 1);
        return true;
    }

    /// <summary>Consumer: discards up to <paramref name="frames"/> of the oldest frames.</summary>
    public void Skip(int frames)
    {
        long r = readPos;
        long available = Volatile.Read(ref writePos) - r;
        Volatile.Write(ref readPos, r + Math.Clamp(frames, 0, available));
    }
}

/// <summary>
/// Consumer side of the live input: once per Csound control cycle it pulls frames from an
/// <see cref="InputRingBuffer"/>, converts the capture device's sample rate to Csound's and keeps
/// the two device clocks in step, then writes the result into Csound's <c>spin</c> buffer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it is needed.</b> The input and output may be different devices (e.g. the UR22mkII
/// input and Bluetooth headphones), each with its own crystal. Even at the same nominal rate they
/// differ by tens of ppm, so a plain FIFO would slowly empty (clicks) or fill up (ever-growing
/// latency). They may also run at different nominal rates (input 44.1 kHz, output 48 kHz).
/// </para>
/// <para>
/// <b>Adaptive resampling.</b> Output frames are read from the input stream at a fractional step
/// <c>step = inRate / outRate × (1 + c)</c> with 4-point cubic Hermite (Catmull-Rom)
/// interpolation. <c>c</c> is a small proportional correction computed from the ring's
/// <i>smoothed</i> fill level: <c>c = (fill − target) / (inRate × 4 s)</c>, clamped to ±0.5 %.
/// If the input clock is faster the ring fills, <c>c</c> grows and frames are consumed slightly
/// faster, and vice versa; deviations decay with a ≈4 s time constant. A steady drift of
/// <i>d</i> ppm leaves a constant fill offset of only <c>d × 10⁻⁶ × inRate × 4</c> frames
/// (≈19 frames for 100 ppm at 48 kHz). The pitch change is inaudible (100 ppm ≈ 0.17 cent).
/// </para>
/// <para>
/// <b>Continuous fill estimate.</b> The ring only grows in whole capture packets (3–10 ms), and
/// the consumer reads in output-period bursts. Sampling the raw count would see a value that
/// depends on the slowly drifting <i>phase</i> between the two devices and jumps by a whole
/// packet when the phases cross, which makes the controller wander. Instead the fill is
/// estimated as <c>count + (now − time of the last packet) × inRate</c>: the samples the input
/// device has already recorded but not yet delivered. That estimate grows smoothly with time,
/// and a ≈0.5 s one-pole filter removes the remaining per-burst sawtooth.
/// </para>
/// <para>
/// <b>Glitch fallbacks.</b> If the ring runs dry (input device stalled or unplugged) the reader
/// outputs silence and waits until the ring is back at the target fill, then fades in over one
/// block (<see cref="Underruns"/>). If the ring grows far beyond the target (e.g. the output
/// stalled), the oldest frames are dropped to return to the target (<see cref="Overruns"/>).
/// </para>
/// <para>
/// <b>Quality.</b> Cubic interpolation is transparent for the small corrections used for drift.
/// For a real rate conversion it is decent: upsampling (44.1 → 48 kHz) is clean for music and
/// voice (some loss of the top octave near 20 kHz); downsampling (96 → 48 kHz) has no anti-alias
/// filter, so content above the new Nyquist frequency could fold back (microphones and
/// instruments rarely carry much there).
/// </para>
/// <para>All buffers are preallocated; <see cref="Read"/> never allocates, locks or blocks.</para>
/// </remarks>
public sealed unsafe class AdaptiveInputReader
{
    /// <summary>Time constant of the drift correction, in seconds.</summary>
    const double CorrectionSeconds = 4.0;

    /// <summary>Largest relative speed correction (±0.5 %, ≈8.6 cents, reached only after big transients).</summary>
    const double MaxCorrection = 0.005;

    /// <summary>Time constant of the fill-level smoothing filter, in seconds.</summary>
    const double FillSmoothingSeconds = 0.5;

    /// <summary>Longest gap since the last packet that still counts as "recorded, not yet delivered".</summary>
    const double MaxPendingSeconds = 0.05;

    /// <summary>The ring this reader consumes (this reader is its only consumer).</summary>
    readonly InputRingBuffer ring;
    /// <summary>Input channel count (= <see cref="InputRingBuffer.Channels"/>).</summary>
    readonly int ch;
    /// <summary>Capture device rate in Hz.</summary>
    readonly double inRate;
    /// <summary>Csound's sr in Hz.</summary>
    readonly double outRate;
    /// <summary>Nominal read step in input frames per output frame (<c>inRate / outRate</c>).</summary>
    readonly double baseStep;
    /// <summary>Interpolation history: 4 frames x[-1], x[0], x[1], x[2], interleaved; output lies between x[0] and x[1].</summary>
    readonly float[] window; // 4 frames: x[-1], x[0], x[1], x[2], interleaved
    /// <summary>Scratch for one frame popped from the ring.</summary>
    readonly float[] incoming;
    /// <summary>Half a burst, in input frames: extra fill kept on restart so the controller starts at equilibrium.</summary>
    readonly double startOffset;
    /// <summary>Fractional read position between x[0] (0) and x[1] (1).</summary>
    double frac;
    /// <summary>One-pole filtered fill estimate (input frames); written here, read by the UI via <see cref="SmoothedFill"/>.</summary>
    double smoothedFill;
    /// <summary>True until the ring holds <see cref="TargetFrames"/> (at start and after every underrun): output silence meanwhile.</summary>
    bool priming = true;
    /// <summary>Frames of linear fade-in still to apply after priming ends.</summary>
    int fadeIn;
    /// <summary>Current relative speed correction <c>c</c> (published for <see cref="CorrectionPpm"/>).</summary>
    double correction;
    /// <summary>Counters published to the UI thread via <see cref="Underruns"/> / <see cref="Overruns"/>.</summary>
    int underruns, overruns;

    /// <summary>Creates the reader.</summary>
    /// <param name="ring">Ring filled by the capture thread.</param>
    /// <param name="inputRate">Capture stream sample rate.</param>
    /// <param name="outputRate">Csound's sample rate (sr).</param>
    /// <param name="targetFrames">Fill level (in input frames) the drift control aims for; this is the added latency.</param>
    /// <param name="burstFrames">
    /// Input frames consumed per output wake-up (output period × inRate / outRate). The blocks of
    /// one burst sample the fill as it drops by this amount, so on (re)start the fill is set half a
    /// burst above the target and the controller starts at equilibrium instead of correcting a
    /// transient of up to ≈0.1 % for several seconds.
    /// </param>
    public AdaptiveInputReader(InputRingBuffer ring, double inputRate, double outputRate, int targetFrames, int burstFrames = 0)
    {
        startOffset = Math.Max(0, burstFrames) / 2.0;
        this.ring = ring;
        ch = ring.Channels;
        inRate = inputRate;
        outRate = outputRate;
        baseStep = inputRate / outputRate;
        TargetFrames = Math.Clamp(targetFrames, 16, ring.CapacityFrames / 4);
        window = new float[4 * ch];
        incoming = new float[ch];
        smoothedFill = TargetFrames;
    }

    /// <summary>Fill level the drift control aims for, in input frames (≈ the latency the input adds).</summary>
    public int TargetFrames { get; }

    /// <summary>True when the input and Csound run at different nominal rates (real conversion).</summary>
    public bool Resampling => Math.Abs(baseStep - 1.0) > 1e-9;

    /// <summary>Times the ring ran dry (silence was output until it refilled).</summary>
    public int Underruns => Volatile.Read(ref underruns);

    /// <summary>Times the ring overflowed far beyond the target and old frames were dropped.</summary>
    public int Overruns => Volatile.Read(ref overruns);

    /// <summary>Current speed correction in ppm (positive = input clock faster than output).</summary>
    public double CorrectionPpm => Volatile.Read(ref correction) * 1e6;

    /// <summary>Smoothed (continuous) fill estimate of the ring, in input frames.</summary>
    public double SmoothedFill => Volatile.Read(ref smoothedFill);

    /// <summary>
    /// Writes <paramref name="frames"/> frames of <paramref name="spinChannels"/> channels into
    /// Csound's interleaved <paramref name="spin"/> buffer, scaled by <paramref name="gain"/>
    /// (Csound's 0dbfs). Channel mapping: input channel <i>c</i> feeds Csound channel <i>c</i>; a
    /// mono input feeds every Csound channel; Csound channels beyond a multichannel input get silence.
    /// </summary>
    /// <param name="spin">Csound's input buffer (<c>csoundGetSpin</c>).</param>
    /// <param name="frames">Frames to write (Csound's ksmps).</param>
    /// <param name="spinChannels">Csound's input channel count (nchnls_i).</param>
    /// <param name="gain">Scale applied to the ±1.0 samples (0dbfs).</param>
    /// <param name="nowTicks"><see cref="Stopwatch"/> time of this call (simulations pass their own clock).</param>
    /// <remarks>Called on the Csound performance thread just before each <c>csoundPerformKsmps</c>.</remarks>
    /// <param name="routing">
    /// How the device's channels reach Csound's input channels (see <see cref="InputRouting"/>):
    /// read once per call, so it can be changed live from the UI thread.
    /// </param>
    public void Read(double* spin, int frames, int spinChannels, double gain, long nowTicks, InputRouting routing = InputRouting.Stereo)
    {
        // --- Drift control, once per block ---
        int count = ring.Count;
        // Seconds since the last packet × inRate = frames the device has recorded but not yet
        // delivered. Capped at 50 ms so a stalled input does not look like a full ring.
        double pending = Math.Clamp((nowTicks - ring.LastWriteTicks) / (double)Stopwatch.Frequency, 0, MaxPendingSeconds) * inRate;
        double estimate = count + pending;
        // One-pole low-pass coefficient for a block of `frames` output samples with a 0.5 s time
        // constant: alpha = 1 − e^(−Δt/τ), Δt = frames / outRate.
        double alpha = 1.0 - Math.Exp(-frames / (outRate * FillSmoothingSeconds));
        double avg = smoothedFill + alpha * (estimate - smoothedFill);

        if (priming)
        {
            if (count < TargetFrames)
            {
                Silence(spin, frames * spinChannels);
                return;
            }
            // Enough data again: drop the excess so the fill *estimate* starts at the target (the
            // controller then has little to correct), reset the window and fade in.
            int skip = Math.Clamp((int)(estimate - TargetFrames - startOffset), 0, count - TargetFrames / 2);
            ring.Skip(skip);
            Array.Clear(window);
            for (int i = 1; i < 4; i++)
                if (ring.TryReadFrame(incoming))
                    incoming.AsSpan().CopyTo(window.AsSpan(i * ch, ch));
            frac = 0;
            avg = TargetFrames;
            fadeIn = Math.Max(1, frames);
            priming = false;
        }
        else if (count > 3 * TargetFrames + (int)(inRate * 0.02))
        {
            // Threshold: three times the target plus 20 ms, well beyond anything drift produces.
            // Far too much buffered (the consumer stalled): drop the oldest frames.
            ring.Skip(count - TargetFrames);
            avg = TargetFrames;
            Interlocked.Increment(ref overruns);
        }
        Volatile.Write(ref smoothedFill, avg);
        double c = Math.Clamp((avg - TargetFrames) / (inRate * CorrectionSeconds), -MaxCorrection, MaxCorrection);
        Volatile.Write(ref correction, c);
        double step = baseStep * (1.0 + c);

        // --- Interpolation ---
        int fadeLength = fadeIn;
        for (int f = 0; f < frames; f++)
        {
            double t = frac;
            double g = gain;
            if (fadeIn > 0)
            {
                g *= 1.0 - (double)fadeIn / (fadeLength + 1);
                fadeIn--;
            }
            double* dst = spin + f * spinChannels;
            if (routing == InputRouting.Stereo || ch == 1)
            {
                for (int sc = 0; sc < spinChannels; sc++)
                {
                    // Same channel (device input 1 → Csound channel 1, input 2 → 2…); a mono
                    // device feeds every channel; missing channels → silence.
                    int src = sc < ch ? sc : ch == 1 ? 0 : -1;
                    dst[sc] = src < 0 ? 0 : Interpolate(src, t) * g;
                }
            }
            else
            {
                // Mono modes for a stereo (or wider) device: one value copied to every Csound
                // input channel, so effects written for a stereo pair hear the source on both
                // sides even when the microphone is plugged into only one input.
                double v = routing switch
                {
                    InputRouting.Input1 => Interpolate(0, t),
                    InputRouting.Input2 => Interpolate(1, t),
                    _ => Interpolate(0, t) + Interpolate(1, t), // Sum: unscaled, so a single used input keeps its level
                } * g;
                for (int sc = 0; sc < spinChannels; sc++)
                    dst[sc] = v;
            }

            // Advance the read position; each whole input frame crossed shifts the window by one.
            frac += step;
            while (frac >= 1.0)
            {
                frac -= 1.0;
                if (!ring.TryReadFrame(incoming))
                {
                    // Ran dry: silence for the rest of the block, then wait for the target fill.
                    Interlocked.Increment(ref underruns);
                    priming = true;
                    Silence(spin + (f + 1) * spinChannels, (frames - f - 1) * spinChannels);
                    return;
                }
                Array.Copy(window, ch, window, 0, 3 * ch);
                incoming.AsSpan().CopyTo(window.AsSpan(3 * ch, ch));
            }
        }
    }

    /// <summary>
    /// Catmull-Rom interpolation of device channel <paramref name="src"/> at fraction
    /// <paramref name="t"/> (0…1) between the 2nd and 3rd frames of the 4-frame history window.
    /// </summary>
    double Interpolate(int src, double t)
    {
        double xm1 = window[src], x0 = window[ch + src], x1 = window[2 * ch + src], x2 = window[3 * ch + src];
        double c1 = 0.5 * (x1 - xm1);
        double c2 = xm1 - 2.5 * x0 + 2.0 * x1 - 0.5 * x2;
        double c3 = 0.5 * (x2 - xm1) + 1.5 * (x0 - x1);
        return ((c3 * t + c2) * t + c1) * t + x0;
    }

    /// <summary>Zeroes <paramref name="count"/> doubles of Csound's spin buffer.</summary>
    static void Silence(double* p, int count)
    {
        for (int i = 0; i < count; i++) p[i] = 0;
    }
}
