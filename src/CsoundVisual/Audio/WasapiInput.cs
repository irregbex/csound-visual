using System.Runtime.InteropServices;
using CsoundVisual.Localization;
using static CsoundVisual.Audio.WasapiInterop;

namespace CsoundVisual.Audio;

/// <summary>
/// Event-driven WASAPI <b>capture</b> stream (microphone, line or audio-interface input) in
/// shared mode, feeding an <see cref="InputRingBuffer"/> from its own real-time thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared mode only</b>: the input coexists with every other application (Cubase, browsers,
/// voice chat) exactly like the default output does. If the driver supports it,
/// <c>IAudioClient3.InitializeSharedAudioStream</c> gives a short engine period (≈3 ms); otherwise
/// a classic event-driven stream with the default ≈10 ms period is used.
/// </para>
/// <para>
/// <b>Formats.</b> The device mix format is converted to interleaved <c>float</c> (±1.0 full
/// scale): float32, int16, packed int24 and int32 (including 24-in-32) are handled, with any
/// channel count. Packets flagged silent are stored as zeros.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Open"/> runs on the Csound performance thread (MTA). The capture
/// loop runs on a dedicated MTA thread registered with MMCSS ("Pro Audio"); it only converts and
/// copies samples into the preallocated ring, so it never allocates or locks. Its only shared
/// state with the consumer is the ring and a few counters.
/// </para>
/// <para>
/// <b>Failures.</b> When the device is unplugged, disabled or stops delivering packets, the loop
/// ends and <see cref="Error"/> is set with a user-facing message. Nothing is thrown at the engine:
/// the consumer just finds the ring empty and feeds silence, so the output keeps playing.
/// </para>
/// </remarks>
public sealed unsafe class WasapiInput : IDisposable
{
    /// <summary>HRESULT S_OK-class code meaning "no packet available".</summary>
    const int AUDCLNT_S_BUFFER_EMPTY = 0x08890001;

    /// <summary>Most channels metered by <see cref="TakePeaks"/>.</summary>
    public const int MaxMeterChannels = 8;

    /// <summary>The capture (or, for loopback, render) endpoint; released in <see cref="Dispose"/>.</summary>
    IMMDevice? device;
    /// <summary>Audio client of the stream, activated as IAudioClient3.</summary>
    IAudioClient3? client;
    /// <summary>Capture service: hands out the recorded packets.</summary>
    IAudioCaptureClient? capture;
    /// <summary>Mix format of the endpoint (what the packets contain).</summary>
    WaveFormatExtensible format;
    /// <summary>Auto-reset event Windows signals when a packet is ready.</summary>
    IntPtr eventHandle;
    /// <summary>The "WASAPI capture" thread running <see cref="CaptureLoop"/>.</summary>
    Thread? thread;
    /// <summary>Set by <see cref="Dispose"/> (any thread); polled by the capture loop at least every 200 ms.</summary>
    volatile bool stopping;
    /// <summary>True after <c>IAudioClient.Start</c> succeeded, so <see cref="Dispose"/> knows to call Stop.</summary>
    bool started;
    /// <summary>
    /// Preallocated conversion buffer (interleaved float). Only the capture thread touches it,
    /// so packets are converted without allocating.
    /// </summary>
    float[] scratch = [];
    /// <summary>
    /// Per-channel peak magnitudes since the last <see cref="TakePeaks"/>: written by the capture
    /// thread, read and zeroed by the UI thread without synchronisation (a lost peak only
    /// affects a meter frame).
    /// </summary>
    readonly float[] peaks = new float[MaxMeterChannels];
    /// <summary>User-facing failure text published by the capture thread (see <see cref="Error"/>).</summary>
    volatile string? error;
    /// <summary>Count of packets flagged <see cref="AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY"/> (interlocked).</summary>
    int glitches;
    /// <summary>Count of packets truncated by the scratch-buffer cap in <see cref="DrainPackets"/> (interlocked).</summary>
    int truncatedPackets;

    /// <summary>Private: use <see cref="Open"/> or <see cref="OpenLoopback"/>.</summary>
    WasapiInput() { }

    /// <summary>Sample rate of the capture stream (the device's mix rate).</summary>
    public int SampleRate { get; private set; }

    /// <summary>Channel count of the capture stream.</summary>
    public int Channels { get; private set; }

    /// <summary>Engine/device period in frames (≈ size of each captured packet).</summary>
    public int PeriodFrames { get; private set; }

    /// <summary>Total endpoint buffer size in frames.</summary>
    public int BufferFrames { get; private set; }

    /// <summary>True when the IAudioClient3 low-latency shared path is active.</summary>
    public bool LowLatencyShared { get; private set; }

    /// <summary>Friendly name of the opened device.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>Ring the capture thread writes into (created by <see cref="Open"/>, holds ≈1 s).</summary>
    public InputRingBuffer Ring { get; private set; } = null!;

    /// <summary>
    /// User-facing reason why capturing stopped (device unplugged, invalidated…), or <c>null</c>
    /// while it is healthy. Set by the capture thread; read by the engine to report it once.
    /// </summary>
    public string? Error => error;

    /// <summary>Packets Windows flagged as discontinuous (data lost before them).</summary>
    /// <remarks>Also incremented for packets cut short by the scratch-buffer cap (see <see cref="TruncatedPackets"/>).</remarks>
    public int Glitches => Volatile.Read(ref glitches);

    /// <summary>Packets larger than the scratch buffer whose tail had to be discarded (normally 0).</summary>
    public int TruncatedPackets => Volatile.Read(ref truncatedPackets);

    /// <summary>One-line description for the console ("WASAPI input · UR22mkII · 44100 Hz · 2 ch · float32 · period …").</summary>
    public string Description => Loc.S(
        $"Entrada WASAPI {(LowLatencyShared ? "compartida (baja latencia)" : "compartida")} · {DeviceName} · {SampleRate} Hz · {Channels} ch · " +
        $"{format.Describe()} · periodo {PeriodFrames} frames ({1000.0 * PeriodFrames / Math.Max(1, SampleRate):0.##} ms)",
        $"WASAPI input {(LowLatencyShared ? "shared (low latency)" : "shared")} · {DeviceName} · {SampleRate} Hz · {Channels} ch · " +
        $"{format.Describe()} · period {PeriodFrames} frames ({1000.0 * PeriodFrames / Math.Max(1, SampleRate):0.##} ms)");

    /// <summary>
    /// Lists the active capture endpoints (microphones, line/instrument inputs). The first entry
    /// (id <c>null</c>) means "Windows default recording device".
    /// </summary>
    public static List<WasapiEndpoint> ListEndpoints() => WasapiOutput.ListEndpoints(eCapture);

    /// <summary>Opens and initialises (but does not start) a shared-mode capture stream.</summary>
    /// <param name="deviceId">Capture endpoint id, or <c>null</c> for the Windows default recording device.</param>
    /// <param name="preset">Latency preset; Minimum/Low try the IAudioClient3 short period.</param>
    /// <exception cref="WasapiException">With a user-facing explanation.</exception>
    public static WasapiInput Open(string? deviceId, LatencyPreset preset)
    {
        var i = new WasapiInput();
        try
        {
            i.OpenCore(deviceId, preset, loopback: false);
            return i;
        }
        catch
        {
            i.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Diagnostics only (<c>--input-probe loopback</c>): captures what Windows is <i>playing</i> on a
    /// render endpoint (WASAPI loopback). Loopback is not covered by the microphone privacy switch,
    /// so it exercises the whole capture path (packets, format conversion, ring) even when
    /// microphone access is blocked. Windows delivers no packets while nothing is playing.
    /// </summary>
    /// <param name="renderDeviceId">Render endpoint id, or <c>null</c> for the default output.</param>
    internal static WasapiInput OpenLoopback(string? renderDeviceId)
    {
        var i = new WasapiInput();
        try
        {
            i.OpenCore(renderDeviceId, LatencyPreset.Normal, loopback: true);
            return i;
        }
        catch
        {
            i.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Shared body of <see cref="Open"/> and <see cref="OpenLoopback"/>: resolves the endpoint,
    /// initialises a shared event-driven stream in the mix format, obtains the capture client and
    /// preallocates the conversion buffer and the ring.
    /// </summary>
    /// <param name="deviceId">Endpoint id or <c>null</c> for the default (capture/console, or render/multimedia for loopback).</param>
    /// <param name="preset">Minimum/Low try IAudioClient3 short periods; Normal uses the default period.</param>
    /// <param name="loopback"><c>true</c> to record a render endpoint's output.</param>
    /// <exception cref="WasapiException">Any failure, with a user-facing reason (including blocked microphone privacy).</exception>
    void OpenCore(string? deviceId, LatencyPreset preset, bool loopback)
    {
        var enumerator = CreateEnumerator();
        try
        {
            // Default capture device uses the eConsole role (Windows "Default Device" for recording);
            // loopback records the default multimedia output, same as WasapiOutput.
            int hr = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(loopback ? eRender : eCapture, loopback ? eMultimedia : eConsole, out device)
                : enumerator.GetDevice(deviceId, out device);
            if (hr != 0 || device is null)
                throw new WasapiException(Loc.S("No se encontró el dispositivo de entrada. ¿Está conectado y activo en Configuración de sonido › Grabación?",
                    "Input device not found. Is it connected and enabled in Sound settings › Recording?"), hr);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        DeviceName = ReadStringProperty(device, PKEY_Device_FriendlyName) ?? Loc.S("entrada", "input");

        var iid = IID_IAudioClient3;
        int ahr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object obj);
        if (ahr != 0)
            throw new WasapiException(Loc.S("No se pudo activar el cliente de audio de la entrada.", "Could not activate the input's audio client."), ahr);
        client = (IAudioClient3)obj;
        eventHandle = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);

        Check(client.GetMixFormat(out IntPtr mixPtr), Loc.S("No se pudo leer el formato de la entrada", "Could not read the input format"));
        try
        {
            format = WasapiOutput.ReadFormat(mixPtr);
            SampleRate = (int)format.SamplesPerSec;
            Channels = format.Channels;
            if (!format.IsFloat && format.BitsPerSample is not (16 or 24 or 32))
                throw new WasapiException(Loc.S($"Formato de entrada no soportado ({format.Describe()}).", $"Unsupported input format ({format.Describe()})."));

            // Low-latency path (never for loopback: InitializeSharedAudioStream has no loopback
            // flag). Low = twice the minimum rounded up to the fundamental period, capped at default.
            bool initialised = false;
            if (!loopback && preset != LatencyPreset.Normal &&
                client.GetSharedModeEnginePeriod(mixPtr, out uint def, out uint fund, out uint min, out _) == 0 && min > 0)
            {
                uint period = preset == LatencyPreset.Minimum ? min : Math.Min(def, (min * 2 + fund - 1) / Math.Max(fund, 1) * Math.Max(fund, 1));
                if (period < def && client.InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK, period, mixPtr, IntPtr.Zero) == 0)
                {
                    LowLatencyShared = true;
                    PeriodFrames = (int)period;
                    initialised = true;
                }
            }
            if (!initialised)
            {
                uint flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK | (loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
                Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, mixPtr, IntPtr.Zero),
                    Loc.S("No se pudo abrir la entrada compartida de Windows", "Could not open the Windows shared-mode input"));
                // Default device period (100-ns units) → frames at the mix rate.
                client.GetDevicePeriod(out long defPeriod, out _);
                PeriodFrames = (int)Math.Round(defPeriod * SampleRate / 10_000_000.0);
            }
        }
        finally
        {
            CoTaskMemFree(mixPtr);
        }

        Check(client.SetEventHandle(eventHandle), Loc.S("No se pudo configurar el evento de la entrada", "Could not set the input event"));
        Check(client.GetBufferSize(out uint frames), Loc.S("No se pudo leer el tamaño del buffer de entrada", "Could not read the input buffer size"));
        BufferFrames = (int)frames;
        if (PeriodFrames <= 0) PeriodFrames = BufferFrames;

        var cid = IID_IAudioCaptureClient;
        Check(client.GetService(ref cid, out object cc), Loc.S("No se pudo obtener el capture client", "Could not get the capture client"));
        capture = (IAudioCaptureClient)cc;

        // Preallocate everything the capture thread needs: one packet never exceeds the endpoint
        // buffer (×2 is extra headroom); the ring holds about one second (at least 4 buffers),
        // far more than the AdaptiveInputReader's target fill of a few tens of ms.
        scratch = new float[Math.Max(BufferFrames, PeriodFrames) * 2 * Channels];
        Ring = new InputRingBuffer(Channels, Math.Max(SampleRate, BufferFrames * 4));
    }

    /// <summary>Starts capturing on a dedicated real-time thread.</summary>
    /// <remarks>
    /// A new thread is an MTA thread by default, so it may use the COM objects created by
    /// <see cref="Open"/> on the (also MTA) performance thread without marshalling.
    /// </remarks>
    /// <exception cref="WasapiException">When the device refuses to start.</exception>
    public void Start()
    {
        Check(client!.Start(), Loc.S("No se pudo iniciar la entrada", "Could not start the input"));
        started = true;
        thread = new Thread(CaptureLoop) { IsBackground = true, Name = "WASAPI capture", Priority = ThreadPriority.Highest };
        thread.Start();
    }

    /// <summary>
    /// Copies the peak of each channel (linear, 1.0 = 0 dBFS) since the previous call into
    /// <paramref name="dest"/> and resets them. Torn reads are harmless (display only).
    /// </summary>
    /// <returns>The number of channels written.</returns>
    public int TakePeaks(Span<float> dest)
    {
        int n = Math.Min(Math.Min(Channels, MaxMeterChannels), dest.Length);
        for (int i = 0; i < n; i++)
        {
            dest[i] = peaks[i];
            peaks[i] = 0;
        }
        return n;
    }

    /// <summary>Body of the capture thread: wait for the device event, drain every packet into the ring.</summary>
    /// <remarks>
    /// Waits are bounded to 200 ms so <see cref="stopping"/> is noticed promptly and a dead
    /// endpoint is detected: ten consecutive timeouts with no packet (≈2 s) end the loop with an
    /// <see cref="Error"/>. Loopback streams legitimately stay silent while nothing plays, and
    /// would also hit this limit; that is acceptable for the diagnostic they serve.
    /// </remarks>
    void CaptureLoop()
    {
        uint taskIndex = 0;
        IntPtr mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        int quietWaits = 0;
        try
        {
            while (!stopping)
            {
                bool signalled = WaitForSingleObject(eventHandle, 200) == 0;
                // Drain even on timeout: a packet may be waiting without the event (e.g. one
                // signal covered two packets).
                if (stopping) break;
                int packets = DrainPackets();
                if (packets < 0) break;
                if (signalled || packets > 0)
                {
                    quietWaits = 0;
                }
                else if (++quietWaits >= 10)
                {
                    // Two seconds without a single packet: the endpoint stopped streaming.
                    error = Loc.S("La entrada de audio dejó de enviar muestras (¿se desconectó o la desactivaron?). La salida sigue con la entrada en silencio.",
                                  "The audio input stopped delivering samples (was it unplugged or disabled?). Output continues with the input silent.");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            error = Loc.S("La entrada de audio falló: ", "The audio input failed: ") + ex.Message;
        }
        finally
        {
            if (mmcss != IntPtr.Zero)
                AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    /// <summary>Reads every waiting packet. Returns the packet count, or -1 when the device failed (sets <see cref="Error"/>).</summary>
    /// <remarks>
    /// WASAPI capture protocol: <c>GetNextPacketSize</c> → <c>GetBuffer</c> (lends a pointer to
    /// one packet) → copy → <c>ReleaseBuffer(frames)</c> with the full packet size, before asking
    /// for the next. Each packet is time-stamped with <see cref="System.Diagnostics.Stopwatch"/>
    /// ticks so the reader can estimate the fill continuously.
    /// </remarks>
    int DrainPackets()
    {
        int count = 0;
        while (true)
        {
            int hr = capture!.GetNextPacketSize(out uint next);
            if (hr != 0)
                return Fail(hr);
            if (next == 0)
                return count;
            hr = capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _);
            if (hr == AUDCLNT_S_BUFFER_EMPTY)
                return count;
            if (hr < 0)
                return Fail(hr);
            if ((flags & AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY) != 0)
                Interlocked.Increment(ref glitches);

            // Defensive cap: a packet larger than scratch would be truncated (the tail is lost but
            // the whole packet is still released). Bug fix: that loss used to be silent; it is now
            // counted in TruncatedPackets and also as a glitch, so diagnostics report lost input.
            int n = (int)Math.Min(frames, (uint)(scratch.Length / Channels));
            if (n < frames)
            {
                Interlocked.Increment(ref truncatedPackets);
                Interlocked.Increment(ref glitches);
            }
            Convert(data, n, (flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0);
            Ring.Write(scratch, n, System.Diagnostics.Stopwatch.GetTimestamp());
            capture.ReleaseBuffer(frames);
            count++;
        }
    }

    /// <summary>Publishes a user-facing <see cref="Error"/> for a failed capture call.</summary>
    /// <returns>Always -1, so callers can <c>return Fail(hr)</c>.</returns>
    int Fail(int hr)
    {
        error = hr == AUDCLNT_E_DEVICE_INVALIDATED
            ? Loc.S("La entrada de audio se desconectó o cambió. La salida sigue con la entrada en silencio; vuelve a tocar para reabrirla.",
                    "The audio input was disconnected or changed. Output continues with the input silent; play again to reopen it.")
            : Loc.S($"La entrada de audio falló (HRESULT 0x{hr:X8}). La salida sigue con la entrada en silencio.",
                    $"The audio input failed (HRESULT 0x{hr:X8}). Output continues with the input silent.");
        return -1;
    }

    /// <summary>Converts <paramref name="frames"/> frames of the device format into <see cref="scratch"/> and updates the peaks.</summary>
    /// <param name="data">Packet pointer lent by <c>GetBuffer</c> (may be null for silent packets).</param>
    /// <param name="frames">Frames in the packet (already capped to the scratch size).</param>
    /// <param name="silent">The packet carries <see cref="AUDCLNT_BUFFERFLAGS_SILENT"/>: write zeros, ignore the data.</param>
    /// <remarks>
    /// Integer → float scaling divides by 2^(n−1) so the most negative code is exactly −1.0.
    /// Packed int24 is sign-extended through the <c>(sbyte)</c> cast of the top byte.
    /// </remarks>
    void Convert(IntPtr data, int frames, bool silent)
    {
        int samples = frames * Channels;
        var dst = scratch.AsSpan(0, samples);
        if (silent || data == IntPtr.Zero)
        {
            dst.Clear();
            return;
        }
        byte* src = (byte*)data;
        if (format.IsFloat)
        {
            new ReadOnlySpan<float>(src, samples).CopyTo(dst);
        }
        else if (format.BitsPerSample == 16)
        {
            var s = (short*)src;
            for (int i = 0; i < samples; i++) dst[i] = s[i] * (1f / 32768f);
        }
        else if (format.BitsPerSample == 24)
        {
            for (int i = 0; i < samples; i++)
            {
                int v = src[i * 3] | (src[i * 3 + 1] << 8) | ((sbyte)src[i * 3 + 2] << 16);
                dst[i] = v * (1f / 8388608f);
            }
        }
        else
        {
            // int32, also 24-in-32 (the valid bits are the most significant ones).
            var s = (int*)src;
            for (int i = 0; i < samples; i++) dst[i] = (float)(s[i] * (1.0 / 2147483648.0));
        }

        int meter = Math.Min(Channels, MaxMeterChannels);
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < meter; c++)
            {
                float a = MathF.Abs(dst[f * Channels + c]);
                if (a > peaks[c]) peaks[c] = a;
            }
    }

    /// <summary>Stops the capture thread and releases the device.</summary>
    /// <remarks>
    /// The join is bounded (1 s, while a wait lasts at most 200 ms) so a wedged driver cannot hang
    /// the performance thread that disposes the input.
    /// </remarks>
    public void Dispose()
    {
        stopping = true;
        if (eventHandle != IntPtr.Zero && thread is not null)
        {
            thread.Join(1000);
        }
        try { if (started) client?.Stop(); } catch (Exception) { /* device may be gone already */ }
        if (capture is not null) Marshal.ReleaseComObject(capture);
        if (client is not null) Marshal.ReleaseComObject(client);
        if (device is not null) Marshal.ReleaseComObject(device);
        capture = null; client = null; device = null;
        if (eventHandle != IntPtr.Zero) CloseHandle(eventHandle);
        eventHandle = IntPtr.Zero;
    }

    /// <summary>Throws a <see cref="WasapiException"/> with a localised reason when <paramref name="hr"/> is not S_OK.</summary>
    /// <remarks>0x80070005 (E_ACCESSDENIED) is what WASAPI returns when Windows microphone privacy blocks desktop apps.</remarks>
    static void Check(int hr, string what)
    {
        if (hr == 0) return;
        string reason = hr switch
        {
            AUDCLNT_E_DEVICE_IN_USE => Loc.S("el dispositivo está en uso exclusivo por otra aplicación", "the device is in exclusive use by another application"),
            AUDCLNT_E_UNSUPPORTED_FORMAT => Loc.S("formato no soportado", "unsupported format"),
            AUDCLNT_E_DEVICE_INVALIDATED => Loc.S("el dispositivo se desconectó", "the device was disconnected"),
            unchecked((int)0x80070005) => Loc.S("Windows bloqueó el acceso al micrófono. Actívalo en Configuración › Privacidad y seguridad › Micrófono: «Acceso al micrófono» y «Permitir que las aplicaciones de escritorio accedan al micrófono» (afecta también a las entradas de línea de la interfaz)",
                                                "Windows blocked microphone access. Turn it on in Settings › Privacy & security › Microphone: «Microphone access» and «Let desktop apps access your microphone» (it also covers the interface's line inputs)"),
            _ => $"HRESULT 0x{hr:X8}",
        };
        throw new WasapiException($"{what}: {reason}.", hr);
    }
}
