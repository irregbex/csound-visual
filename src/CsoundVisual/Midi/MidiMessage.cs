using CsoundVisual.Localization;

namespace CsoundVisual.Midi;

/// <summary>Kind of channel-voice message.</summary>
public enum MidiKind : byte
{
    /// <summary>Note on (velocity &gt; 0).</summary>
    NoteOn,
    /// <summary>Note off (or note on with velocity 0 in MIDI 1.0).</summary>
    NoteOff,
    /// <summary>Control change.</summary>
    ControlChange,
    /// <summary>Program change.</summary>
    ProgramChange,
    /// <summary>Pitch bend (centre = 0.5).</summary>
    PitchBend,
    /// <summary>Channel pressure (aftertouch).</summary>
    ChannelPressure,
    /// <summary>Polyphonic key pressure.</summary>
    PolyPressure,
}

/// <summary>
/// A channel-voice MIDI message in a protocol-neutral, high-resolution form.
/// </summary>
/// <remarks>
/// <para>
/// Values are normalised to 0…1 doubles, so MIDI 1.0 (7/14-bit) and MIDI 2.0 (16/32-bit, from
/// Universal MIDI Packets) messages share one type and nothing is lost when a MIDI 2.0 device
/// sends 32-bit controller values. <see cref="ToMidi1"/> produces the classic 3-byte form that
/// Csound's MIDI opcodes understand.
/// </para>
/// <para>
/// <see cref="Group"/> is the UMP group (0–15) for MIDI 2.0 sources; MIDI 1.0 ports use 0.
/// </para>
/// <para>
/// Producers: <see cref="WinMmMidiInput"/> (MIDI 1.0 bytes via <see cref="FromMidi1"/>),
/// <see cref="Midi2Input"/> (UMP words via <see cref="FromUmp"/>) and the on-screen/PC keyboard.
/// Everything is funnelled through <see cref="MidiHub"/>, which forwards the MIDI 1.0 form to
/// Csound and the full-resolution value to the monitor and MIDI learn. Being a small
/// <c>readonly record struct</c>, it is copied by value and can be queued without allocation.
/// </para>
/// </remarks>
/// <param name="Kind">Message kind.</param>
/// <param name="Channel">Channel 0–15.</param>
/// <param name="Index">Note number for notes/poly pressure, controller number for CC, program for program change.</param>
/// <param name="Value">Normalised value 0…1 (velocity, controller value, pressure, pitch bend with 0.5 = centre).</param>
/// <param name="Group">UMP group (MIDI 2.0), 0 for MIDI 1.0.</param>
/// <param name="Timestamp">Receive time in seconds (monotonic clock).</param>
public readonly record struct MidiMessage(MidiKind Kind, byte Channel, byte Index, double Value, byte Group = 0, double Timestamp = 0)
{
    /// <summary>Converts to MIDI 1.0 bytes (status, data1, data2), rounding high-resolution values.</summary>
    /// <returns>
    /// The status byte (kind in the high nibble, channel in the low nibble) and two data bytes.
    /// For 1-data-byte messages (program change, channel pressure) <c>Data2</c> is 0 and must not
    /// be sent (see <see cref="DataLength"/>).
    /// </returns>
    public (byte Status, byte Data1, byte Data2) ToMidi1()
    {
        // 0…1 → 0…127. Math.Round uses banker's rounding; the difference is at most one step.
        byte v7 = (byte)Math.Clamp((int)Math.Round(Value * 127), 0, 127);
        return Kind switch
        {
            // A very soft MIDI 2.0 velocity can round to 0, which MIDI 1.0 reads as note-off:
            // clamp to 1 so the note still sounds.
            MidiKind.NoteOn => ((byte)(0x90 | Channel), Index, Math.Max((byte)1, v7)),
            MidiKind.NoteOff => ((byte)(0x80 | Channel), Index, v7),
            MidiKind.ControlChange => ((byte)(0xB0 | Channel), Index, v7),
            MidiKind.ProgramChange => ((byte)(0xC0 | Channel), Index, 0),
            MidiKind.ChannelPressure => ((byte)(0xD0 | Channel), v7, 0),
            MidiKind.PolyPressure => ((byte)(0xA0 | Channel), Index, v7),
            _ => PitchBend14(),
        };
    }

    /// <summary>
    /// Encodes <see cref="Value"/> as a 14-bit MIDI 1.0 pitch bend (0…16383, centre 8192):
    /// status <c>0xEn</c>, then the low 7 bits (LSB) and the high 7 bits (MSB).
    /// </summary>
    (byte, byte, byte) PitchBend14()
    {
        int v = Math.Clamp((int)Math.Round(Value * 16383), 0, 16383);
        return ((byte)(0xE0 | Channel), (byte)(v & 0x7F), (byte)(v >> 7));
    }

    /// <summary>Number of data bytes after the status byte for a MIDI 1.0 status.</summary>
    /// <remarks>Program change (0xC0) and channel pressure (0xD0) carry one data byte; every other channel message carries two.</remarks>
    public static int DataLength(byte status) => (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;

    /// <summary>Parses a MIDI 1.0 channel message; returns <c>null</c> for system/real-time messages.</summary>
    /// <param name="status">Status byte (0x80–0xEF for channel messages; running status must already be resolved).</param>
    /// <param name="d1">First data byte (0–127).</param>
    /// <param name="d2">Second data byte (0–127; ignored for 1-byte messages).</param>
    /// <param name="timestamp">Receive time in seconds.</param>
    public static MidiMessage? FromMidi1(byte status, byte d1, byte d2, double timestamp)
    {
        byte ch = (byte)(status & 0x0F);
        return (status & 0xF0) switch
        {
            // MIDI 1.0 convention: note-on with velocity 0 is a note-off.
            // Pitch bend: 14-bit value, LSB first; 8192/16383 ≈ 0.50003 is the centre.
            0x90 when d2 > 0 => new MidiMessage(MidiKind.NoteOn, ch, d1, d2 / 127.0, 0, timestamp),
            0x90 or 0x80 => new MidiMessage(MidiKind.NoteOff, ch, d1, d2 / 127.0, 0, timestamp),
            0xB0 => new MidiMessage(MidiKind.ControlChange, ch, d1, d2 / 127.0, 0, timestamp),
            0xC0 => new MidiMessage(MidiKind.ProgramChange, ch, d1, 0, 0, timestamp),
            0xD0 => new MidiMessage(MidiKind.ChannelPressure, ch, 0, d1 / 127.0, 0, timestamp),
            0xA0 => new MidiMessage(MidiKind.PolyPressure, ch, d1, d2 / 127.0, 0, timestamp),
            0xE0 => new MidiMessage(MidiKind.PitchBend, ch, 0, ((d2 << 7) | d1) / 16383.0, 0, timestamp),
            _ => null,
        };
    }

    /// <summary>
    /// Parses a MIDI 2.0 channel-voice Universal MIDI Packet (message type 0x4, two 32-bit
    /// words) keeping full resolution: 16-bit velocity, 32-bit controllers/pressure/pitch bend.
    /// Returns <c>null</c> for other message types.
    /// </summary>
    /// <remarks>
    /// Word 0 layout (bits 31→0): message type (4) · group (4) · opcode (4) · channel (4) ·
    /// index (8: note or controller; the top bit is reserved) · attribute/flags (8).
    /// Word 1 carries the data: for notes, velocity in the top 16 bits and the attribute data in
    /// the low 16; for CC, pressure and pitch bend, a full 32-bit value (pitch-bend centre
    /// 0x80000000); for program change, the program number in bits 30–24.
    /// Per-note controllers, registered/assignable controllers and per-note pitch bend
    /// (opcodes 0x0–0x6, 0xF) are not mapped and return <c>null</c>.
    /// </remarks>
    /// <param name="word0">First 32-bit word of the packet.</param>
    /// <param name="word1">Second 32-bit word of the packet.</param>
    /// <param name="timestamp">Receive time in seconds.</param>
    public static MidiMessage? FromUmp(uint word0, uint word1, double timestamp)
    {
        if ((word0 >> 28) != 0x4)
            return null;
        byte group = (byte)((word0 >> 24) & 0xF);
        byte opcode = (byte)((word0 >> 20) & 0xF);
        byte ch = (byte)((word0 >> 16) & 0xF);
        byte index = (byte)((word0 >> 8) & 0x7F);
        double v32 = word1 / (double)uint.MaxValue;
        double v16 = (word1 >> 16) / 65535.0;
        return opcode switch
        {
            0x9 when (word1 >> 16) > 0 => new MidiMessage(MidiKind.NoteOn, ch, index, v16, group, timestamp),
            0x9 or 0x8 => new MidiMessage(MidiKind.NoteOff, ch, index, v16, group, timestamp),
            0xB => new MidiMessage(MidiKind.ControlChange, ch, index, v32, group, timestamp),
            0xC => new MidiMessage(MidiKind.ProgramChange, ch, (byte)((word1 >> 24) & 0x7F), 0, group, timestamp),
            0xD => new MidiMessage(MidiKind.ChannelPressure, ch, 0, v32, group, timestamp),
            0xA => new MidiMessage(MidiKind.PolyPressure, ch, index, v32, group, timestamp),
            0xE => new MidiMessage(MidiKind.PitchBend, ch, 0, v32, group, timestamp),
            _ => null,
        };
    }

    /// <summary>Short human-readable form for the MIDI monitor ("Nota on  C4 (60)  vel 0.79  ch 1").</summary>
    public override string ToString()
    {
        static string NoteName(int n) => new[] { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" }[n % 12] + (n / 12 - 1);
        string ch = $"ch {Channel + 1}" + (Group > 0 ? $" g{Group + 1}" : "");
        return Kind switch
        {
            MidiKind.NoteOn => Loc.S($"Nota on   {NoteName(Index)} ({Index})  vel {Value:0.###}  {ch}", $"Note on   {NoteName(Index)} ({Index})  vel {Value:0.###}  {ch}"),
            MidiKind.NoteOff => Loc.S($"Nota off  {NoteName(Index)} ({Index})  {ch}", $"Note off  {NoteName(Index)} ({Index})  {ch}"),
            MidiKind.ControlChange => $"CC {Index}  = {Value:0.####}  {ch}",
            MidiKind.ProgramChange => Loc.S($"Programa {Index}  {ch}", $"Program {Index}  {ch}"),
            MidiKind.PitchBend => $"Pitch bend {(Value - 0.5) * 2:+0.###;-0.###;0}  {ch}",
            MidiKind.ChannelPressure => $"Aftertouch {Value:0.###}  {ch}",
            _ => Loc.S($"Presión {Index} {Value:0.###}  {ch}", $"Poly pressure {Index} {Value:0.###}  {ch}"),
        };
    }
}
