using CsoundVisual.Localization;

namespace CsoundVisual.Engine;

/// <summary>
/// An insert chain the engine runs on Csound's output (implemented by
/// <see cref="Integration.VstChain"/>: the user's VST3 effects and instruments).
/// </summary>
/// <remarks>
/// <see cref="Prepare"/> is called on the performance thread once per run, before the first
/// block; <see cref="Process"/> is called on the same thread for every control block and must
/// never block.
/// </remarks>
public unsafe interface IAudioInsertChain
{
    /// <summary>True when there is nothing to run (no slots).</summary>
    bool IsEmpty { get; }

    /// <summary>
    /// Readies every slot for <paramref name="sampleRate"/> and blocks of up to
    /// <paramref name="maxBlock"/> frames. Returns a short description of the chain for the
    /// console (or <c>null</c> when empty).
    /// </summary>
    string? Prepare(double sampleRate, int maxBlock);

    /// <summary>Processes one stereo block in place (±1.0 full scale).</summary>
    /// <param name="left">Left channel, <paramref name="frames"/> floats owned by the engine (valid only during the call).</param>
    /// <param name="right">Right channel, same length.</param>
    /// <param name="frames">Frames in the block (Csound's ksmps).</param>
    void Process(float* left, float* right, int frames);
}

/// <summary>Plug-in insert chain support of the engine (see <see cref="IAudioInsertChain"/>).</summary>
public sealed unsafe partial class CsoundEngine
{
    /// <summary>
    /// Insert chain applied to channels 1–2 of Csound's output, after <c>0dbfs</c> scaling and
    /// before the meters, the scope, the WAV recorder and the device. Only the app's own
    /// «Windows · WASAPI» engine runs it; with Csound's own audio modules (PortAudio, ASIO…) and
    /// offline renders the output never passes through the app, so the chain is skipped (a
    /// console line says so). Set from the UI thread; read once at the start of each run.
    /// </summary>
    public IAudioInsertChain? Inserts { get; set; }

    // Per-run state, touched only by the performance thread.
    /// <summary>The chain snapshot used by this run (null when none, or when preparing it failed).</summary>
    IAudioInsertChain? runInserts;
    /// <summary>Planar float scratch buffers of ksmps frames, allocated once per run in <see cref="PrepareInserts"/>.</summary>
    float[] insertLeft = [], insertRight = [];

    /// <summary>
    /// App WASAPI path: prepares the chain for this run's sr/ksmps (performance thread, before
    /// the first block). Called from <see cref="PerformHost"/>.
    /// </summary>
    void PrepareInserts()
    {
        runInserts = null;
        if (Inserts is not { } chain)
            return;
        try
        {
            // The chain is prepared for a conventional maximum block (at least 1024 frames), a
            // safe upper bound: the engine itself always sends exactly ksmps frames per call.
            string? description = chain.Prepare(SampleRate, Math.Max(Ksmps, 1024));
            if (description is not null)
                Post("🎛 " + description, MessageKind.Info);
            insertLeft = new float[Math.Max(Ksmps, 1)];
            insertRight = new float[Math.Max(Ksmps, 1)];
            runInserts = chain;
        }
        catch (Exception ex)
        {
            Post(Loc.S("⚠ Cadena de plugins desactivada: ", "⚠ Plug-in chain disabled: ") + ex.Message, MessageKind.Warning);
        }
    }

    /// <summary>
    /// Csound drives the device itself (or renders offline): the chain cannot run. Tells the
    /// user once per run when the chain has plug-ins. Called from <see cref="Perform"/>.
    /// </summary>
    void SkipInserts()
    {
        runInserts = null;
        if (Inserts is { IsEmpty: false })
            Post(Loc.S("ℹ La cadena de plugins VST3 solo suena con el motor «Windows · WASAPI» de la app (no con PortAudio/ASIO de Csound ni al renderizar).",
                       "ℹ The VST3 plug-in chain only runs with the app's «Windows · WASAPI» engine (not with Csound's PortAudio/ASIO, nor when rendering)."),
                 MessageKind.Info);
    }

    /// <summary>
    /// Runs the chain on one control block of <c>spout</c>, in place (performance thread, right
    /// after <c>csoundPerformKsmps</c>). Mono orchestras are processed as dual mono and folded
    /// back; channels beyond 2 are left untouched.
    /// </summary>
    /// <remarks>
    /// Units: <c>spout</c> holds interleaved doubles in Csound's 0dbfs scale; plug-ins want planar
    /// floats at ±1.0. So samples are multiplied by <c>scale</c> (1/0dbfs) on the way in and by
    /// <c>zeroDbfs</c> on the way out, leaving <c>spout</c> in Csound units for the rest of
    /// <c>Step</c> (meters, scope, recorder and device all apply <c>scale</c> again).
    /// </remarks>
    void ApplyInserts(double* spout)
    {
        if (runInserts is not { } chain || chain.IsEmpty)
            return;
        int n = Ksmps, nch = OutputChannels;
        if (n <= 0 || nch <= 0 || insertLeft.Length < n)
            return;
        float s = scale;
        // fixed: pins the managed scratch arrays so native plug-in code can write through the pointers.
        fixed (float* l = insertLeft, r = insertRight)
        {
            for (int f = 0; f < n; f++)
            {
                double* frame = spout + f * nch;
                l[f] = (float)(frame[0] * s);
                r[f] = nch > 1 ? (float)(frame[1] * s) : l[f];
            }
            chain.Process(l, r, n);
            double back = zeroDbfs;
            for (int f = 0; f < n; f++)
            {
                double* frame = spout + f * nch;
                if (nch > 1)
                {
                    frame[0] = l[f] * back;
                    frame[1] = r[f] * back;
                }
                else
                    frame[0] = 0.5 * (l[f] + r[f]) * back;
            }
        }
    }
}
