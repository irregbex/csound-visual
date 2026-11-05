using System.Runtime.InteropServices;
using CsoundVisual.Localization;
using static CsoundVisual.Audio.WasapiInterop;

namespace CsoundVisual.Audio;

/// <summary>How the application's own WASAPI output shares the device.</summary>
public enum WasapiMode
{
    /// <summary>
    /// Shared mode: the standard Windows 11 output. Other apps keep playing; Windows mixes.
    /// Uses <c>IAudioClient3</c> low-latency engine periods when the driver supports them.
    /// </summary>
    Shared,

    /// <summary>Exclusive mode: the app owns the device. Lowest latency, other apps are muted meanwhile.</summary>
    Exclusive,
}

/// <summary>Latency/robustness trade-off chosen by the user.</summary>
public enum LatencyPreset
{
    /// <summary>Smallest period the device allows. Best for playing live; needs a fast machine.</summary>
    Minimum,
    /// <summary>About twice the minimum. A good default for live use.</summary>
    Low,
    /// <summary>The device's default period (≈10 ms in shared mode). Most robust.</summary>
    Normal,
}

/// <summary>An audio output endpoint as seen by Windows.</summary>
/// <param name="Id">MMDevice endpoint id, or <c>null</c> for "follow the Windows default device".</param>
/// <param name="Name">Friendly name shown in the Sound settings.</param>
public sealed record WasapiEndpoint(string? Id, string Name)
{
    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>What one output device can do, as reported by <see cref="WasapiOutput.Probe"/>.</summary>
/// <param name="Name">Friendly device name.</param>
/// <param name="MixFormat">Shared-mode format ("48000 Hz · 2 ch · float32").</param>
/// <param name="Shared">Shared-mode period summary (low-latency support).</param>
/// <param name="Exclusive">Exclusive-mode summary (accepted format and minimum period), or why it is unavailable.</param>
public sealed record AudioCapabilities(string Name, string MixFormat, string Shared, string Exclusive)
{
    /// <summary>Multi-line text for the diagnostics window and the command line.</summary>
    public override string ToString() => Loc.S(
        $"{Name}\n  formato: {MixFormat}\n  compartido: {Shared}\n  exclusivo: {Exclusive}",
        $"{Name}\n  format: {MixFormat}\n  shared: {Shared}\n  exclusive: {Exclusive}");
}

/// <summary>Error opening or running the WASAPI output, with a message meant for the user.</summary>
public sealed class WasapiException(string message, int hresult = 0) : Exception(message)
{
    /// <summary>The failing HRESULT, if any.</summary>
    public int Code { get; } = hresult;
}

/// <summary>
/// Event-driven WASAPI render stream used by the host audio engine.
/// </summary>
/// <remarks>
/// <para>
/// <b>Shared mode</b> uses the device mix format (normally 32-bit float at the rate set in the
/// Windows Sound settings). If the driver supports it, <c>IAudioClient3.InitializeSharedAudioStream</c>
/// is used with a short engine period (on many Windows 11 machines 2.67 ms at 48 kHz instead
/// of the default 10 ms); otherwise it falls back to a classic event-driven shared stream.
/// </para>
/// <para>
/// <b>Exclusive mode</b> negotiates a format the hardware accepts (float32, int32, int24-in-32,
/// packed int24, int16 — in that order, at the mix rate, then 48 and 44.1 kHz) and uses the
/// device's minimum period, re-aligning the buffer when the driver asks for it.
/// </para>
/// <para>
/// The instance must be created, used and disposed on the same (MTA) audio thread.
/// </para>
/// <para>
/// <b>Who uses it.</b> <c>CsoundEngine.PerformHost</c> (the Csound performance thread, which
/// doubles as the WASAPI event thread) opens the stream, pre-fills it, calls <see cref="Start"/>,
/// then loops: <see cref="Wait"/> for the period event → <see cref="FramesWritable"/> → run as many
/// Csound control cycles as needed → <see cref="Write"/>. The static helpers
/// (<see cref="ListEndpoints()"/>, <see cref="GetMixRate"/>, <see cref="Probe"/>) are also called from
/// the UI thread for pickers and diagnostics; they create and release their own COM objects.
/// </para>
/// <para>
/// <b>Real-time rules.</b> <see cref="FramesWritable"/>, <see cref="Write"/> and <see cref="Wait"/> do
/// not allocate and only throw on device loss (<see cref="AUDCLNT_E_DEVICE_INVALIDATED"/>), which
/// ends the performance anyway. All sample data crossing this class is interleaved 32-bit float
/// at ±1.0 full scale; the conversion to the device's integer format happens in <see cref="Write"/>.
/// </para>
/// </remarks>
public sealed unsafe class WasapiOutput : IDisposable
{
    /// <summary>The opened endpoint (owned RCW, released in <see cref="Dispose"/>).</summary>
    IMMDevice? device;
    /// <summary>The audio client of the stream (activated as IAudioClient3).</summary>
    IAudioClient3? client;
    /// <summary>Render service obtained from <see cref="client"/>; lends the device buffer.</summary>
    IAudioRenderClient? render;
    /// <summary>The stream's actual sample format (mix format in shared mode, negotiated in exclusive).</summary>
    WaveFormatExtensible format;
    /// <summary>Auto-reset Win32 event Windows signals once per device period.</summary>
    IntPtr eventHandle;
    /// <summary>Set after <c>IAudioClient.Start</c>; an empty buffer only counts as an underrun once started.</summary>
    bool started;

    /// <summary>Private: instances come from <see cref="Open"/> (or <see cref="Probe"/> internally).</summary>
    WasapiOutput() { }

    /// <summary>Sample rate of the stream in Hz; the engine passes it to Csound as <c>--sample-rate</c>.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Channel count of the stream (the device's mix-format channels; Csound's output is mapped onto it).</summary>
    public int Channels { get; private set; }

    /// <summary>
    /// Sample rate of the device's shared-mode mix format, i.e. the rate set in the Windows Sound
    /// panel (or the driver's control panel). Equals <see cref="SampleRate"/> in shared mode; in
    /// exclusive mode the stream may run at another rate the hardware accepts.
    /// </summary>
    public int MixRate { get; private set; }

    /// <summary>Total endpoint buffer size in frames.</summary>
    public int BufferFrames { get; private set; }

    /// <summary>Engine/device period in frames (how many frames are requested per wake-up).</summary>
    public int PeriodFrames { get; private set; }

    /// <summary>True in exclusive mode.</summary>
    public bool Exclusive { get; private set; }

    /// <summary>True when the IAudioClient3 low-latency shared path is active.</summary>
    public bool LowLatencyShared { get; private set; }

    /// <summary>Advice for the user when the requested latency could not be reached, or <c>null</c>.</summary>
    public string? Hint { get; private set; }

    /// <summary>Name of the opened device.</summary>
    public string DeviceName { get; private set; } = "";

    /// <summary>Handle signalled by Windows whenever the device wants more audio.</summary>
    public IntPtr EventHandle => eventHandle;

    /// <summary>Approximate output latency in milliseconds (one period plus the stream latency reported by Windows).</summary>
    public double LatencyMs { get; private set; }

    /// <summary>One-line description for the console and the status bar.</summary>
    public string Description => Loc.S(
        $"WASAPI {(Exclusive ? "exclusivo" : LowLatencyShared ? "compartido (baja latencia)" : "compartido")} · {DeviceName} · " +
        $"{SampleRate} Hz{(MixRate != SampleRate && MixRate > 0 ? $" (Windows: {MixRate} Hz)" : "")} · {Channels} ch · {format.Describe()} · periodo {PeriodFrames} frames ({1000.0 * PeriodFrames / SampleRate:0.##} ms) · " +
        $"latencia ≈ {LatencyMs:0.#} ms",
        $"WASAPI {(Exclusive ? "exclusive" : LowLatencyShared ? "shared (low latency)" : "shared")} · {DeviceName} · " +
        $"{SampleRate} Hz{(MixRate != SampleRate && MixRate > 0 ? $" (Windows: {MixRate} Hz)" : "")} · {Channels} ch · {format.Describe()} · period {PeriodFrames} frames ({1000.0 * PeriodFrames / SampleRate:0.##} ms) · " +
        $"latency ≈ {LatencyMs:0.#} ms");

    /// <summary>Lists the active render endpoints. The first entry means "Windows default device".</summary>
    /// <returns>Never empty: on any COM failure only the default entry is returned.</returns>
    public static List<WasapiEndpoint> ListEndpoints() => ListEndpoints(eRender);

    /// <summary>
    /// Lists the active endpoints of one data flow (<see cref="WasapiInterop.eRender"/> or
    /// <see cref="WasapiInterop.eCapture"/>). The first entry (id <c>null</c>) means "Windows default device".
    /// </summary>
    internal static List<WasapiEndpoint> ListEndpoints(int dataFlow)
    {
        var list = new List<WasapiEndpoint> { new(null, Loc.S("Predeterminado de Windows", "Windows default")) };
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = CreateEnumerator();
            if (enumerator.EnumAudioEndpoints(dataFlow, DEVICE_STATE_ACTIVE, out var collection) != 0)
                return list;
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var dev) != 0) continue;
                dev.GetId(out string id);
                list.Add(new WasapiEndpoint(id, ReadStringProperty(dev, PKEY_Device_FriendlyName) ?? id));
                Marshal.ReleaseComObject(dev);
            }
            Marshal.ReleaseComObject(collection);
        }
        catch (Exception)
        {
            // No audio devices / COM failure: only the default entry is offered.
        }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
        return list;
    }

    /// <summary>
    /// Shared-mode mix rate (the rate set in the Windows Sound panel) of a render endpoint, or of
    /// the default output when <paramref name="deviceId"/> is <c>null</c>; 0 when it cannot be read.
    /// Nothing is opened or played.
    /// </summary>
    public static int GetMixRate(string? deviceId)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? dev = null;
        object? obj = null;
        try
        {
            enumerator = CreateEnumerator();
            int hr = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out dev)
                : enumerator.GetDevice(deviceId, out dev);
            if (hr != 0 || dev is null) return 0;
            // Activating a client does not open a stream: GetMixFormat works without Initialize.
            var iid = IID_IAudioClient3;
            if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out obj) != 0) return 0;
            if (((IAudioClient3)obj).GetMixFormat(out IntPtr mixPtr) != 0) return 0;
            int rate = (int)ReadFormat(mixPtr).SamplesPerSec;
            CoTaskMemFree(mixPtr);
            return rate;
        }
        catch (Exception)
        {
            return 0;
        }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
            if (dev is not null) Marshal.ReleaseComObject(dev);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>
    /// Best-effort lookup of the mix rate of the render endpoint a Csound/PortAudio device name
    /// refers to (PortAudio names are the WASAPI friendly name, sometimes truncated to 31
    /// characters by MME). <c>null</c> or an unmatched name gives the default output's rate.
    /// </summary>
    public static int FindMixRateByName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            // The picker shows PortAudio devices as "WASAPI · Speakers (X)": compare the bare name.
            int sep = name.LastIndexOf(" · ", StringComparison.Ordinal);
            string bare = (sep >= 0 ? name[(sep + 3)..] : name).Trim();
            // Either the PortAudio name contains the full friendly name, or it is a (≥12-char)
            // MME-truncated prefix of it. The first match wins.
            foreach (var e in ListEndpoints().Skip(1))
                if (bare.Contains(e.Name, StringComparison.OrdinalIgnoreCase) ||
                    (bare.Length >= 12 && e.Name.StartsWith(bare, StringComparison.OrdinalIgnoreCase)))
                    return GetMixRate(e.Id);
        }
        return GetMixRate(null);
    }

    /// <summary>
    /// Asks every active output what it can do, without playing anything: mix format, shared-mode
    /// engine periods (IAudioClient3) and exclusive-mode support. Used by the audio diagnostics.
    /// </summary>
    /// <returns>One entry per active render endpoint; a device that fails reports the error text instead.</returns>
    public static List<AudioCapabilities> Probe()
    {
        var result = new List<AudioCapabilities>();
        foreach (var endpoint in ListEndpoints().Skip(1))
        {
            // A throw-away instance is used only so Dispose releases the device and client RCWs.
            // Bug fix (leaks on exceptions): the enumerator, the client and the COM-allocated mix
            // format used to be released by straight-line code, so any throw in between (a device
            // unplugged mid-probe, a failing COM call) leaked them. Now the enumerator and the
            // format are freed in finally blocks and the client is stored in the instance, so
            // o.Dispose() in the outer finally always releases it.
            var o = new WasapiOutput();
            try
            {
                var enumerator = CreateEnumerator();
                try
                {
                    enumerator.GetDevice(endpoint.Id!, out o.device);
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }
                var client = o.client = o.Activate();
                client.GetMixFormat(out IntPtr mixPtr);
                WaveFormatExtensible mix;
                string shared = Loc.S("periodo fijo de 10 ms", "fixed 10 ms period");
                try
                {
                    mix = ReadFormat(mixPtr);
                    double rate = mix.SamplesPerSec;
                    double ms(double frames) => 1000.0 * frames / rate;

                    if (client.GetSharedModeEnginePeriod(mixPtr, out uint def, out _, out uint min, out _) == 0 && min > 0)
                        shared = min < def
                            ? Loc.S($"periodo mínimo {min} frames ({ms(min):0.##} ms), por defecto {def} ({ms(def):0.##} ms)",
                                    $"minimum period {min} frames ({ms(min):0.##} ms), default {def} ({ms(def):0.##} ms)")
                            : Loc.S($"periodo {def} frames ({ms(def):0.##} ms), sin modo de baja latencia",
                                    $"period {def} frames ({ms(def):0.##} ms), no low-latency mode");
                }
                finally
                {
                    CoTaskMemFree(mixPtr);
                }

                // Exclusive support: try the same format ladder as InitExclusive, but only at the
                // mix rate. Device periods are REFERENCE_TIME (100 ns), hence / 10 000 for ms.
                string exclusive = Loc.S("no disponible", "not available");
                client.GetDevicePeriod(out long defPeriod, out long minPeriod);
                IntPtr fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
                try
                {
                    foreach (var (bits, valid, isFloat) in new[] { (32, 32, true), (32, 32, false), (32, 24, false), (24, 24, false), (16, 16, false) })
                    {
                        var f = WaveFormatExtensible.Create(mix.SamplesPerSec, mix.Channels, bits, valid, isFloat);
                        Marshal.StructureToPtr(f, fmtPtr, false);
                        int hr = client.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, fmtPtr, out IntPtr closest);
                        if (closest != IntPtr.Zero) CoTaskMemFree(closest);
                        if (hr == 0)
                        {
                            exclusive = Loc.S($"{f.Describe()}, periodo mínimo {minPeriod / 10_000.0:0.##} ms (defecto {defPeriod / 10_000.0:0.##} ms)",
                                              $"{f.Describe()}, minimum period {minPeriod / 10_000.0:0.##} ms (default {defPeriod / 10_000.0:0.##} ms)");
                            break;
                        }
                        if (hr == AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED) { exclusive = Loc.S("bloqueado en las propiedades del dispositivo", "blocked in the device properties"); break; }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(fmtPtr);
                }
                // The client is released by o.Dispose() (it is stored in o.client above).
                result.Add(new AudioCapabilities(endpoint.Name, $"{mix.SamplesPerSec} Hz · {mix.Channels} ch · {mix.Describe()}", shared, exclusive));
            }
            catch (Exception ex)
            {
                result.Add(new AudioCapabilities(endpoint.Name, "?", "error: " + ex.Message, "?"));
            }
            finally
            {
                o.Dispose();
            }
        }
        return result;
    }

    /// <summary>Opens and initialises (but does not start) an output stream.</summary>
    /// <param name="deviceId">Endpoint id from <see cref="ListEndpoints()"/>, or <c>null</c> for the default multimedia output.</param>
    /// <param name="mode">Shared or exclusive.</param>
    /// <param name="preset">Requested period size.</param>
    /// <returns>An initialised stream owned by the caller; dispose it on the same thread.</returns>
    /// <exception cref="WasapiException">With a user-facing explanation.</exception>
    public static WasapiOutput Open(string? deviceId, WasapiMode mode, LatencyPreset preset)
    {
        var o = new WasapiOutput();
        try
        {
            o.OpenCore(deviceId, mode, preset);
            return o;
        }
        catch
        {
            o.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Body of <see cref="Open"/>: resolves the endpoint, initialises the client in the requested
    /// mode, attaches the period event, obtains the render client and estimates the latency.
    /// Partially created objects are released by the caller's <see cref="Dispose"/> on failure.
    /// </summary>
    void OpenCore(string? deviceId, WasapiMode mode, LatencyPreset preset)
    {
        var enumerator = CreateEnumerator();
        try
        {
            int hr = deviceId is null
                ? enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out device)
                : enumerator.GetDevice(deviceId, out device);
            if (hr != 0 || device is null)
                throw new WasapiException(Loc.S("No se encontró el dispositivo de salida. ¿Está conectado y activo en Configuración de sonido?", "Output device not found. Is it connected and enabled in Sound settings?"), hr);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
        DeviceName = ReadStringProperty(device, PKEY_Device_FriendlyName) ?? Loc.S("dispositivo", "device");

        // Auto-reset, initially unsignalled: each period wakes exactly one Wait().
        eventHandle = CreateEventW(IntPtr.Zero, false, false, IntPtr.Zero);
        if (mode == WasapiMode.Exclusive)
            InitExclusive(preset);
        else
            InitShared(preset);

        Check(client!.SetEventHandle(eventHandle), Loc.S("No se pudo configurar el evento de audio", "Could not set the audio event"));
        Check(client.GetBufferSize(out uint frames), Loc.S("No se pudo leer el tamaño del buffer", "Could not read the buffer size"));
        BufferFrames = (int)frames;
        // Classic shared stream without IAudioClient3 info: treat the whole buffer as one period.
        if (PeriodFrames == 0) PeriodFrames = BufferFrames;

        var iid = IID_IAudioRenderClient;
        Check(client.GetService(ref iid, out object rc), Loc.S("No se pudo obtener el render client", "Could not get the render client"));
        render = (IAudioRenderClient)rc;

        // Estimate = one period of buffering in the app + the engine/driver latency Windows reports
        // (REFERENCE_TIME, 100 ns → ms). Hardware/Bluetooth latency is not included.
        client.GetStreamLatency(out long streamLatency);
        LatencyMs = 1000.0 * PeriodFrames / SampleRate + streamLatency / 10_000.0;
    }

    /// <summary>Activates a fresh <c>IAudioClient3</c> on <see cref="device"/>.</summary>
    /// <exception cref="WasapiException">When activation fails (e.g. before Windows 10).</exception>
    IAudioClient3 Activate()
    {
        var iid = IID_IAudioClient3;
        int hr = device!.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object obj);
        if (hr != 0)
            throw new WasapiException(Loc.S("No se pudo activar el cliente de audio (se requiere Windows 10 o 11).", "Could not activate the audio client (Windows 10 or 11 required)."), hr);
        return (IAudioClient3)obj;
    }

    /// <summary>
    /// Initialises a shared-mode stream in the mix format. Tries the IAudioClient3 low-latency
    /// path first (only when the chosen period is shorter than the default; a period equal to
    /// the default gains nothing), then falls back to <c>IAudioClient.Initialize</c>.
    /// </summary>
    /// <remarks>
    /// Shared mode always runs at the rate set in Windows, so no format negotiation is needed:
    /// the mix format is float32 on every Windows 10/11 machine in practice.
    /// </remarks>
    void InitShared(LatencyPreset preset)
    {
        client = Activate();
        Check(client.GetMixFormat(out IntPtr mixPtr), Loc.S("No se pudo leer el formato del dispositivo", "Could not read the device format"));
        try
        {
            format = ReadFormat(mixPtr);
            SampleRate = (int)format.SamplesPerSec;
            MixRate = SampleRate;
            Channels = format.Channels;

            // Engine periods in frames: def = default (≈10 ms), fund = granularity every valid
            // period is a multiple of, min = shortest supported. Drivers without low-latency
            // support report min == def.
            if (client.GetSharedModeEnginePeriod(mixPtr, out uint def, out uint fund, out uint min, out _) == 0 && min > 0)
            {
                uint period = preset switch
                {
                    LatencyPreset.Minimum => min,
                    LatencyPreset.Low => Math.Min(def, RoundUp(min * 2, Math.Max(fund, 1))),
                    _ => def,
                };
                if (period < def &&
                    client.InitializeSharedAudioStream(AUDCLNT_STREAMFLAGS_EVENTCALLBACK, period, mixPtr, IntPtr.Zero) == 0)
                {
                    LowLatencyShared = true;
                    PeriodFrames = (int)period;
                    return;
                }
                PeriodFrames = (int)def;
            }

            // Classic event-driven shared stream (≈10 ms period). Duration 0 and periodicity 0 let
            // Windows choose; in shared event mode periodicity must be 0.
            Check(client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, 0, 0, mixPtr, IntPtr.Zero),
                Loc.S("No se pudo abrir la salida compartida de Windows", "Could not open the Windows shared-mode output"));
        }
        finally
        {
            CoTaskMemFree(mixPtr);
        }
    }

    /// <summary>
    /// Initialises an exclusive-mode stream: negotiates rate and sample format, picks a period
    /// that is a whole number of ms and frames (<see cref="AlignedPeriod"/>), and handles the
    /// <see cref="AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED"/> retry dance.
    /// </summary>
    /// <remarks>
    /// In exclusive event-driven mode <c>bufferDuration</c> must equal <c>periodicity</c>; the
    /// endpoint buffer then holds exactly one period and the app refills all of it on every event.
    /// </remarks>
    void InitExclusive(LatencyPreset preset)
    {
        client = Activate();
        Check(client.GetMixFormat(out IntPtr mixPtr), Loc.S("No se pudo leer el formato del dispositivo", "Could not read the device format"));
        var mix = ReadFormat(mixPtr);
        CoTaskMemFree(mixPtr);
        MixRate = (int)mix.SamplesPerSec;

        // Rates whose 1 ms is a whole number of frames come first (48/96 kHz). At 44.1 kHz a 3 or
        // 6 ms period is 132.3 / 264.6 frames: USB drivers, which work in 1 ms packets, then
        // drift by a fraction of a sample every period and the output crackles. Only 10 ms
        // multiples are exact at 44.1 kHz.
        var rates = new[] { 48000u, 96000u, mix.SamplesPerSec, 44100u, 88200u }.Distinct();
        // Format ladder per rate: float32 (no conversion), int32, int24-in-32 (common on USB
        // interfaces), packed int24, int16. The first one the driver accepts wins.
        var candidates = new List<WaveFormatExtensible>();
        foreach (uint rate in rates)
        {
            int ch = mix.Channels;
            candidates.Add(WaveFormatExtensible.Create(rate, ch, 32, 32, true));
            candidates.Add(WaveFormatExtensible.Create(rate, ch, 32, 32, false));
            candidates.Add(WaveFormatExtensible.Create(rate, ch, 32, 24, false));
            candidates.Add(WaveFormatExtensible.Create(rate, ch, 24, 24, false));
            candidates.Add(WaveFormatExtensible.Create(rate, ch, 16, 16, false));
        }

        IntPtr fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatExtensible>());
        try
        {
            bool found = false;
            foreach (var candidate in candidates)
            {
                Marshal.StructureToPtr(candidate, fmtPtr, false);
                if (client.IsFormatSupported(AUDCLNT_SHAREMODE_EXCLUSIVE, fmtPtr, out IntPtr closest) == 0)
                {
                    format = candidate;
                    found = true;
                    if (closest != IntPtr.Zero) CoTaskMemFree(closest);
                    break;
                }
                if (closest != IntPtr.Zero) CoTaskMemFree(closest);
            }
            if (!found)
                throw new WasapiException(Loc.S("El dispositivo no acepta ningún formato en modo exclusivo. Usa el modo compartido.", "The device accepts no format in exclusive mode. Use shared mode."));

            SampleRate = (int)format.SamplesPerSec;
            Channels = format.Channels;
            Marshal.StructureToPtr(format, fmtPtr, false);

            // Device periods in REFERENCE_TIME (100 ns): e.g. minPeriod 30 000 = 3 ms.
            client.GetDevicePeriod(out long defPeriod, out long minPeriod);
            long wanted = preset switch
            {
                LatencyPreset.Minimum => minPeriod,
                LatencyPreset.Low => Math.Min(defPeriod, minPeriod * 2),
                _ => defPeriod,
            };
            long period = AlignedPeriod(Math.Max(wanted, minPeriod), SampleRate);
            // Alignment cost more than 0.5 ms at a rate that is not a multiple of 1 kHz
            // (44.1/88.2 kHz): tell the user how to reach the short period.
            if (period > Math.Max(wanted, minPeriod) + 5_000 && SampleRate % 1000 != 0)
                Hint = Loc.S($"A {SampleRate} Hz el periodo exacto más corto es {period / 10_000} ms. Para bajar a ≈3 ms configura el dispositivo a 48000 Hz " +
                             "(Configuración › Sonido › el dispositivo › Propiedades › Opciones avanzadas, o el panel de control de su driver).",
                             $"At {SampleRate} Hz the shortest exact period is {period / 10_000} ms. To get down to ≈3 ms set the device to 48000 Hz " +
                             "(Settings › Sound › the device › Properties › Advanced, or its driver's control panel).");

            int hr = client.Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, period, period, fmtPtr, IntPtr.Zero);
            if (hr == AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED)
            {
                // The driver wants a different size: use the aligned one it reports. Per the
                // WASAPI docs, after this error GetBufferSize returns the aligned frame count, but
                // the failed client cannot be re-initialised: release it and activate a new one.
                // frames → REFERENCE_TIME: 10^7 × frames / rate, rounded.
                client.GetBufferSize(out uint aligned);
                Marshal.ReleaseComObject(client);
                client = Activate();
                period = (long)(10_000_000.0 * aligned / SampleRate + 0.5);
                hr = client.Initialize(AUDCLNT_SHAREMODE_EXCLUSIVE, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, period, period, fmtPtr, IntPtr.Zero);
            }
            Check(hr, Loc.S("No se pudo abrir el modo exclusivo", "Could not open exclusive mode"));
            Exclusive = true;
            PeriodFrames = (int)Math.Round(period * SampleRate / 10_000_000.0);
        }
        finally
        {
            Marshal.FreeHGlobal(fmtPtr);
        }
    }

    /// <summary>Starts the stream. Call after pre-filling the buffer with <see cref="Write"/>.</summary>
    /// <remarks>Pre-filling avoids an initial glitch: the device starts consuming immediately.</remarks>
    /// <exception cref="WasapiException">When the device refuses to start.</exception>
    public void Start()
    {
        Check(client!.Start(), Loc.S("No se pudo iniciar el audio", "Could not start audio"));
        started = true;
    }

    /// <summary>
    /// Frames that can be written now: free space in shared mode, the whole buffer in exclusive
    /// event mode. Sets <paramref name="underrun"/> when the device ran dry.
    /// </summary>
    /// <param name="underrun">
    /// <c>true</c> when, after <see cref="Start"/>, the shared buffer was completely empty
    /// (padding 0): the device played silence, i.e. a dropout. Never set in exclusive mode.
    /// </param>
    /// <returns>The number of frames the caller should render and pass to <see cref="Write"/> (≥ 0).</returns>
    /// <exception cref="WasapiException">When the device was invalidated (unplugged/format change).</exception>
    public int FramesWritable(out bool underrun)
    {
        underrun = false;
        // Exclusive event mode: the buffer is one period and the event means "all of it is free".
        if (Exclusive)
            return BufferFrames;
        // Shared mode: padding = frames still queued for the audio engine; the rest is free.
        int hr = client!.GetCurrentPadding(out uint padding);
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
            throw new WasapiException(Loc.S("El dispositivo de audio se desconectó o cambió.", "The audio device was disconnected or changed."), hr);
        underrun = started && padding == 0;
        return BufferFrames - (int)padding;
    }

    /// <summary>
    /// Writes <paramref name="frames"/> interleaved float frames (±1.0 full scale, one value per
    /// stream channel) into the device buffer, converting to the device sample format.
    /// </summary>
    /// <param name="interleaved">At least <paramref name="frames"/> × <see cref="Channels"/> samples.</param>
    /// <param name="frames">Frames to write; must not exceed <see cref="FramesWritable"/>.</param>
    /// <remarks>
    /// Integer formats are clamped to ±1.0 and scaled to the positive maximum (2^(n−1) − 1), so
    /// −1.0 maps to −max rather than the most negative code; that asymmetry is inaudible. The
    /// 32-bit integer path also serves "int24 in 32": a full-scale int32 is MSB-aligned, which is
    /// exactly how the driver reads a 24-bit value in a 32-bit container. Packed int24 is written
    /// little-endian byte by byte. Runs on the audio thread: no allocation.
    /// </remarks>
    /// <exception cref="WasapiException">When the device was invalidated.</exception>
    public void Write(ReadOnlySpan<float> interleaved, int frames)
    {
        if (frames <= 0) return;
        int hr = render!.GetBuffer((uint)frames, out IntPtr data);
        if (hr == AUDCLNT_E_DEVICE_INVALIDATED)
            throw new WasapiException(Loc.S("El dispositivo de audio se desconectó o cambió.", "The audio device was disconnected or changed."), hr);
        // Other failures (e.g. AUDCLNT_E_BUFFER_TOO_LARGE) skip this period: a dropout, not a crash.
        if (hr != 0)
            return;

        int samples = frames * Channels;
        byte* dst = (byte*)data;
        if (format.IsFloat)
        {
            interleaved[..samples].CopyTo(new Span<float>(dst, samples));
        }
        else if (format.BitsPerSample == 16)
        {
            var d = (short*)dst;
            for (int i = 0; i < samples; i++)
                d[i] = (short)(Math.Clamp(interleaved[i], -1f, 1f) * 32767f);
        }
        else if (format.BitsPerSample == 24)
        {
            for (int i = 0; i < samples; i++)
            {
                int v = (int)(Math.Clamp(interleaved[i], -1f, 1f) * 8388607f);
                dst[i * 3] = (byte)v;
                dst[i * 3 + 1] = (byte)(v >> 8);
                dst[i * 3 + 2] = (byte)(v >> 16);
            }
        }
        else
        {
            var d = (int*)dst;
            for (int i = 0; i < samples; i++)
                d[i] = (int)(Math.Clamp((double)interleaved[i], -1.0, 1.0) * 2147483647.0);
        }
        render.ReleaseBuffer((uint)frames, 0);
    }

    /// <summary>Waits until the device asks for more audio (or the timeout elapses). Returns true when signalled.</summary>
    /// <param name="timeoutMs">Maximum wait in milliseconds; the engine uses a generous value so a stalled device is noticed.</param>
    public bool Wait(uint timeoutMs) => WaitForSingleObject(eventHandle, timeoutMs) == 0;

    /// <summary>Stops the stream (if started) and releases every COM object and the event handle. Idempotent.</summary>
    public void Dispose()
    {
        try { if (started) client?.Stop(); } catch (Exception) { /* device may be gone already */ }
        if (render is not null) Marshal.ReleaseComObject(render);
        if (client is not null) Marshal.ReleaseComObject(client);
        if (device is not null) Marshal.ReleaseComObject(device);
        render = null; client = null; device = null;
        if (eventHandle != IntPtr.Zero) CloseHandle(eventHandle);
        eventHandle = IntPtr.Zero;
    }

    /// <summary>Reads a WAVEFORMATEX(TENSIBLE) returned by Windows, normalising plain WAVEFORMATEX headers.</summary>
    /// <param name="ptr">COM-allocated format; the caller still owns it and must <see cref="CoTaskMemFree"/> it.</param>
    /// <remarks>
    /// Bug fix: this used to copy the full 40-byte extensible structure unconditionally, which for a
    /// plain 18-byte WAVEFORMATEX read 22 bytes past the end of the COM allocation (an out-of-bounds
    /// read that could fault if the block sat at the end of a page). Now the 18-byte header is read
    /// field by field first, and the extensible tail is only read when the header says it is there
    /// (<c>wFormatTag == WAVE_FORMAT_EXTENSIBLE</c> and <c>cbSize ≥ 22</c>).
    /// </remarks>
    internal static WaveFormatExtensible ReadFormat(IntPtr ptr)
    {
        // WAVEFORMATEX header layout (packed, 18 bytes): tag@0, channels@2, rate@4, avg@8,
        // blockAlign@12, bits@14, cbSize@16. Never read beyond it unless cbSize allows.
        ushort tag = (ushort)Marshal.ReadInt16(ptr, 0);
        ushort cbSize = (ushort)Marshal.ReadInt16(ptr, 16);
        if (tag == WAVE_FORMAT_EXTENSIBLE && cbSize >= 22)
            return Marshal.PtrToStructure<WaveFormatExtensible>(ptr);

        var f = new WaveFormatExtensible
        {
            FormatTag = tag,
            Channels = (ushort)Marshal.ReadInt16(ptr, 2),
            SamplesPerSec = (uint)Marshal.ReadInt32(ptr, 4),
            AvgBytesPerSec = (uint)Marshal.ReadInt32(ptr, 8),
            BlockAlign = (ushort)Marshal.ReadInt16(ptr, 12),
            BitsPerSample = (ushort)Marshal.ReadInt16(ptr, 14),
            Size = cbSize,
            // Plain WAVEFORMATEX: there is no extensible tail, synthesise an equivalent one.
            ValidBitsPerSample = (ushort)Marshal.ReadInt16(ptr, 14),
            ChannelMask = 0,
            SubFormat = tag == WAVE_FORMAT_IEEE_FLOAT ? SubtypeFloat : SubtypePcm,
        };
        return f;
    }

    /// <summary>Rounds <paramref name="value"/> up to the next multiple of <paramref name="multiple"/> (&gt; 0).</summary>
    static uint RoundUp(uint value, uint multiple) => (value + multiple - 1) / multiple * multiple;

    /// <summary>
    /// The smallest period ≥ <paramref name="minimum"/> (in 100-ns units) that is a whole number
    /// of milliseconds <i>and</i> a whole number of frames at <paramref name="rate"/>, so USB
    /// devices (1 ms packets) never accumulate fractional samples. At 48 kHz every millisecond
    /// qualifies; at 44.1 kHz only multiples of 10 ms do.
    /// </summary>
    /// <returns>The aligned period in 100-ns units, or <paramref name="minimum"/> if nothing up to 100 ms qualifies.</returns>
    static long AlignedPeriod(long minimum, int rate)
    {
        // rate × ms / 1000 must be an integer number of frames.
        for (int ms = (int)Math.Ceiling(minimum / 10_000.0); ms <= 100; ms++)
            if (rate * (long)ms % 1000 == 0)
                return ms * 10_000L;
        return minimum;
    }

    /// <summary>Throws a <see cref="WasapiException"/> with a localised reason when <paramref name="hr"/> is a failure.</summary>
    /// <param name="hr">HRESULT to test (0 = S_OK; S_FALSE and other success codes are treated as failures too).</param>
    /// <param name="what">User-facing description of the step that failed.</param>
    static void Check(int hr, string what)
    {
        if (hr == 0) return;
        string reason = hr switch
        {
            AUDCLNT_E_DEVICE_IN_USE => Loc.S("el dispositivo está en uso exclusivo por otra aplicación", "the device is in exclusive use by another application"),
            AUDCLNT_E_EXCLUSIVE_MODE_NOT_ALLOWED => Loc.S("Windows no permite el modo exclusivo en este dispositivo (revisa sus propiedades avanzadas)", "Windows does not allow exclusive mode on this device (check its advanced properties)"),
            AUDCLNT_E_UNSUPPORTED_FORMAT => Loc.S("formato no soportado", "unsupported format"),
            AUDCLNT_E_DEVICE_INVALIDATED => Loc.S("el dispositivo se desconectó", "the device was disconnected"),
            _ => $"HRESULT 0x{hr:X8}",
        };
        throw new WasapiException($"{what}: {reason}.", hr);
    }
}
