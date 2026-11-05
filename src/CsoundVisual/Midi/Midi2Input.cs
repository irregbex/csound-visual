using System.Diagnostics;
using CsoundVisual.Localization;
using Windows.Devices.Midi2;
using Windows.Devices.Midi2.Enumeration;

namespace CsoundVisual.Midi;

/// <summary>
/// MIDI input through <b>Windows MIDI Services</b> (the Windows 11 MIDI 2.0 stack), using the
/// <c>Windows.Devices.Midi2</c> WinRT API (Preview 11, API surface locked).
/// </summary>
/// <remarks>
/// <para>
/// Every endpoint speaks Universal MIDI Packets (UMP), including classic MIDI 1.0 devices, which
/// the service wraps. Two packet types matter for playing:
/// <list type="bullet">
/// <item><b>type 0x2</b> — MIDI 1.0 channel voice in one 32-bit word (7-bit values; how MIDI 1.0
/// devices arrive);</item>
/// <item><b>type 0x4</b> — MIDI 2.0 channel voice in two words: 16-bit velocity, 32-bit
/// controllers, pressure and pitch bend. Decoded at full resolution by <see cref="MidiMessage.FromUmp"/>.</item>
/// </list>
/// Other types (system, SysEx, stream, utility) are ignored here.
/// </para>
/// <para>
/// One <see cref="MidiSession"/> is shared by all connections and closed by <see cref="Shutdown"/>.
/// <see cref="MidiMessageReceivedEventArgs.FillWords"/> is used instead of
/// <c>GetMessagePacket()</c> so no packet object is allocated per message. The event is raised on
/// a service thread; the handler only decodes and forwards.
/// </para>
/// <para>
/// <b>Known issues of the preview SDK</b> (see docs/en/midi.md): WinRT activation failures surface
/// as exceptions (caught by <see cref="IsAvailable"/>); the API requires Windows 11 25H2 or later;
/// when Windows ships its own copy in System32 that one is used instead of the app-local DLL;
/// tearing down virtual devices could hang before the November 2026 release.
/// </para>
/// </remarks>
public sealed class Midi2Input : IDisposable
{
    /// <summary>Backend name shown in the UI.</summary>
    public const string BackendName = "MIDI 2.0";

    /// <summary>Guards the shared static state below (session creation, availability probe).</summary>
    static readonly object Gate = new();
    /// <summary>The one session all connections live in; created lazily by <see cref="Open"/>.</summary>
    static MidiSession? session;
    /// <summary>Cached result of <see cref="IsAvailable"/>; <c>null</c> until first probed.</summary>
    static bool? available;
    /// <summary>
    /// Why the API is unavailable, kept in both languages so <see cref="IsAvailable"/> can return
    /// it in whichever UI language is active when it is asked.
    /// </summary>
    static (string Es, string En)? unavailableReason;

    /// <summary>The open endpoint connection; <c>null</c> after <see cref="Dispose"/>.</summary>
    MidiEndpointConnection? connection;

    /// <summary>The port this input listens to.</summary>
    public MidiPort Port { get; }

    /// <summary>Raised (on a service thread) for every channel-voice message.</summary>
    public event Action<MidiMessage>? Received;

    Midi2Input(MidiPort port) => Port = port;

    /// <summary>
    /// True when Windows MIDI Services can be used: the API activates and the service is running
    /// (it is demand-started if needed). The result is cached.
    /// </summary>
    /// <param name="reason">Localised explanation when unavailable; <c>null</c> when available.</param>
    /// <remarks>
    /// The first call may take a moment (WinRT activation, service start) and is made from the UI
    /// thread when the port list is built; later calls only read the cache.
    /// </remarks>
    public static bool IsAvailable(out string? reason)
    {
        lock (Gate)
        {
            if (available is null)
            {
                try
                {
                    available = MidiApi.EnsureServiceAvailable();
                    unavailableReason = null;
                    if (available != true)
                        unavailableReason = ("el servicio Windows MIDI Services no arrancó", "the Windows MIDI Services service did not start");
                }
                catch (Exception ex)
                {
                    available = false;
                    string detail = " (" + ex.GetType().Name + ": " + ex.Message + ")";
                    unavailableReason = ("la API de MIDI 2.0 no está disponible" + detail, "the MIDI 2.0 API is not available" + detail);
                }
            }
            reason = unavailableReason is { } r ? Loc.S(r.Es, r.En) : null;
            return available == true;
        }
    }

    /// <summary>
    /// Describes which copy of the API is active: the one in Windows (System32) or the app-local
    /// preview DLL shipped next to the executable.
    /// </summary>
    public static string ApiOrigin()
    {
        try
        {
            return MidiApi.IsSystemProvided()
                ? Loc.S("API de Windows (System32)", "Windows API (System32)")
                : Loc.S("API preview incluida con la app (0.99.92-preview.11)", "Preview API bundled with the app (0.99.92-preview.11)");
        }
        catch (Exception)
        {
            return Loc.S("desconocido", "unknown");
        }
    }

    /// <summary>Lists the standard MIDI endpoints (diagnostic loopbacks excluded).</summary>
    public static List<MidiPort> ListPorts()
    {
        var ports = new List<MidiPort>();
        if (!IsAvailable(out _))
            return ports;
        try
        {
            foreach (var endpoint in MidiEndpointDeviceInformation.FindAll(
                         MidiEndpointDeviceInformationSortOrder.Name, MidiEndpointDeviceInformationFilters.AllStandardEndpoints))
                ports.Add(new MidiPort(endpoint.EndpointDeviceId, endpoint.Name, BackendName));
        }
        catch (Exception)
        {
            // Enumeration failures leave the WinMM ports available.
        }
        return ports;
    }

    /// <summary>Opens an endpoint and starts receiving.</summary>
    /// <param name="port">A port from <see cref="ListPorts"/>; its id is the endpoint device id.</param>
    /// <exception cref="InvalidOperationException">With a readable reason.</exception>
    /// <remarks>The handler is attached before <c>Open()</c> so no packet that arrives right after opening is missed.</remarks>
    public static Midi2Input Open(MidiPort port)
    {
        if (!IsAvailable(out string? reason))
            throw new InvalidOperationException("MIDI 2.0: " + reason);
        var input = new Midi2Input(port);
        lock (Gate)
        {
            session ??= MidiSession.Create("Csound Visual");
            input.connection = session.CreateEndpointConnection(port.Id);
        }
        if (input.connection is null)
            throw new InvalidOperationException(Loc.S($"MIDI 2.0: no se pudo conectar con «{port.Name}».", $"MIDI 2.0: could not connect to «{port.Name}»."));
        input.connection.MessageReceived += input.OnMessage;
        if (!input.connection.Open())
        {
            input.Dispose();
            throw new InvalidOperationException(Loc.S($"MIDI 2.0: no se pudo abrir «{port.Name}».", $"MIDI 2.0: could not open «{port.Name}»."));
        }
        return input;
    }

    /// <summary>
    /// Handles one incoming UMP on the service's event thread: decodes channel-voice packets and
    /// raises <see cref="Received"/>. Allocation-free apart from what the subscriber does.
    /// </summary>
    /// <param name="sender">The connection that received the packet.</param>
    /// <param name="args">Event data; the packet's up to four 32-bit words are read with <c>FillWords</c>.</param>
    void OnMessage(IMidiMessageReceivedEventSource sender, MidiMessageReceivedEventArgs args)
    {
        // Only the first two words are needed (types 0x2 and 0x4 are at most 64 bits).
        args.FillWords(out uint w0, out uint w1, out _, out _);
        // Same Stopwatch time base as WinMmMidiInput; the service's own timestamp is not used.
        double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        MidiMessage? message = (w0 >> 28) switch
        {
            // Type 0x2 (MIDI 1.0 channel voice): bits 31–28 type, 27–24 group, 23–16 status,
            // 15–8 data1, 7–0 data2. Data bytes are masked to 7 bits as in MIDI 1.0.
            0x2 => MidiMessage.FromMidi1((byte)(w0 >> 16), (byte)((w0 >> 8) & 0x7F), (byte)(w0 & 0x7F), now) is { } m1
                ? m1 with { Group = (byte)((w0 >> 24) & 0xF) }
                : null,
            0x4 => MidiMessage.FromUmp(w0, w1, now),
            _ => null,
        };
        if (message is { } m)
            Received?.Invoke(m);
    }

    /// <summary>Unsubscribes and disconnects this endpoint; the shared session stays open for other ports.</summary>
    public void Dispose()
    {
        if (connection is null)
            return;
        connection.MessageReceived -= OnMessage;
        lock (Gate)
        {
            try { session?.DisconnectEndpointConnection(connection.ConnectionId); }
            catch (Exception) { /* the service may already be gone */ }
        }
        connection = null;
    }

    /// <summary>Closes the shared session (on application exit).</summary>
    public static void Shutdown()
    {
        lock (Gate)
        {
            try { session?.Dispose(); }
            catch (Exception) { /* ignore */ }
            session = null;
        }
    }
}
