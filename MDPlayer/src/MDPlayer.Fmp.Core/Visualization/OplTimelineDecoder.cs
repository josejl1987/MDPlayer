namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the common OPL2/OPL3 channel registers. The frequency calculation
/// follows the keyboard windows for YM3526/YM3812 and YMF262; operator and
/// rhythm metadata remain available without making the renderer chip-specific.
/// </summary>
internal sealed class OplTimelineDecoder : IChipTimelineDecoder
{
    private readonly ChipType _chipType;
    private readonly int _channelCount;
    private readonly byte[,] _registers = new byte[2, 256];
    private readonly MutableNote?[] _notes;
    private readonly DeterministicIdentityTable _identityTable = new();
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public OplTimelineDecoder(ChipType chipType, int channelCount)
    {
        if (chipType is not (ChipType.Ym3526 or ChipType.Ym3812 or ChipType.Y8950 or ChipType.Ymf262 or ChipType.Ymf278b))
            throw new ArgumentOutOfRangeException(nameof(chipType));
        if (channelCount is not (9 or 18))
            throw new ArgumentOutOfRangeException(nameof(channelCount));
        _chipType = chipType;
        _channelCount = channelCount;
        _notes = new MutableNote?[channelCount];
    }

    public ChipType ChipType => _chipType;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != _chipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.OplVoices(_chipType, device.Id.Instance))
            timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id)
            return;

        int port = _chipType is ChipType.Ymf262 or ChipType.Ymf278b ? write.Port & 1 : 0;
        int address = write.Address & 0xFF;
        byte previous = _registers[port, address];
        _registers[port, address] = (byte)(write.Data & 0xFF);

        if (address is >= 0xA0 and <= 0xA8)
        {
            int channel = port * 9 + address - 0xA0;
            if (_notes[channel] != null)
                _notes[channel]!.AddPitch(write.SamplePosition, DecodePitch(channel));
            return;
        }

        if (address is >= 0xB0 and <= 0xB8)
        {
            int channel = port * 9 + address - 0xB0;
            bool wasOn = (previous & 0x20) != 0;
            bool isOn = (_registers[port, address] & 0x20) != 0;
            if (isOn && !wasOn)
                Start(channel, write.SamplePosition);
            else if (!isOn && wasOn)
                Close(channel, write.SamplePosition);
            else if (isOn && _notes[channel] != null)
                _notes[channel]!.AddPitch(write.SamplePosition, DecodePitch(channel));
            return;
        }

        if (port == 0 && address == 0xBD)
        {
            EmitRhythm(previous, _registers[port, address], write.SamplePosition);
        }
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int channel = 0; channel < _notes.Length; channel++)
            Close(channel, endSample);
    }

    private void Start(int channel, long sample)
    {
        Close(channel, sample);
        Pitch pitch = DecodePitch(channel);
        if (pitch.MidiNote < 0)
            return;

        // Real 2-operator identity (OPL algorithms 0..3). Unlike OPN four-op FM,
        // the algorithm-7 Op1-feedback exception does not exist here (OPL has no
        // feedback loop equivalent), so only the CARRIER operator TL offset is
        // normalized away; the modulator TL and every other operator parameter
        // remain significant. Deduped by the global deterministic identity table.
        int port = channel / 9;
        int local = channel % 9;
        int c0 = _registers[port, 0xC0 + local];
        int algorithm = (c0 >> 1) & 7;
        int feedback = c0 & 1;

        byte[] identityBytes = WriteOplIdentity(port, local, algorithm, feedback);
        string identityHash = DeterministicIdentityTable.HashBytes(identityBytes);
        InstrumentIdentity identity = _identityTable.GetOrAdd(IdentityFamily.Fm, identityHash, "fm");
        string instrumentId = identity.Canonical;
        _timeline.AddInstrument(new InstrumentDefinition(
            instrumentId,
            "opl",
            algorithm,
            feedback,
            null,
            null,
            BuildOplOperators(port, local)));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Fm, channel),
            sample,
            pitch,
            instrumentId);
    }

    /// <summary>
    /// Serializes a normalized 2-operator identity for OPL2/OPL3. Operator slot 0
    /// is the modulator, slot 1 the carrier. The 2-op operator bank sits at base
    /// 0x20 (op1) / 0x40 (op2); within each bank: +0x00 KSL/TL, +0x20 AR,
    /// +0x40 DR, +0x60 SL+RR, and +0x80 for the OPL3 operator-eight slot. Only the
    /// carrier (slot 1) TL is rewritten to 0; the modulator TL is significant.
    /// </summary>
    private byte[] WriteOplIdentity(int port, int local, int algorithm, int feedback)
    {
        var outBytes = new List<byte>(2 + 2 * 8);
        outBytes.Add((byte)algorithm);
        outBytes.Add((byte)feedback);
        for (int slot = 0; slot < 2; slot++)
        {
            int bank = slot == 0 ? 0x20 : 0x40;
            int tlksl = _registers[port, bank + local];
            int cmd = _registers[port, bank + 0x20 + local]; // AM/VIB/EG/KS/MULT
            int slrr = _registers[port, bank + 0x60 + local]; // SL (high) + RR (low)
            outBytes.Add((byte)((tlksl >> 6) & 0x03)); // ksl
            outBytes.Add(slot == 0 ? (byte)(tlksl & 0x3F) : (byte)0); // carrier TL normalized to 0
            outBytes.Add((byte)(cmd & 0x9F)); // AM + VIB + EG + KS + MULT
            outBytes.Add((byte)(cmd & 0x1F)); // ar
            outBytes.Add((byte)(_registers[port, bank + 0x40 + local] & 0x1F)); // dr
            outBytes.Add((byte)slrr); // sl (msb) + rr (lsb)
        }
        return outBytes.ToArray();
    }

    /// <summary>Builds the two-operator FmOperatorDefinition list for a channel.</summary>
    private FmOperatorDefinition[] BuildOplOperators(int port, int local)
    {
        var ops = new FmOperatorDefinition[2];
        for (int slot = 0; slot < 2; slot++)
        {
            int bank = slot == 0 ? 0x20 : 0x40;
            int tl = _registers[port, bank + local] & 0x3F;
            int ksl = (_registers[port, bank + local] >> 6) & 0x03;
            int cmd = _registers[port, bank + 0x20 + local]; // AM/VIB/EG/KS/MULT
            int ar = cmd & 0x1F;
            bool am = (cmd & 0x80) != 0;
            bool vib = (cmd & 0x40) != 0;
            int mult = cmd & 0x0F;
            int dr = _registers[port, bank + 0x40 + local] & 0x1F;
            int slrr = _registers[port, bank + 0x60 + local];
            int sl = slrr & 0x0F;
            int rr = (slrr >> 4) & 0x0F;
            ops[slot] = new FmOperatorDefinition(
                ar, dr, 0, rr, sl, tl, ksl, mult, 0, am || vib, 0);
        }
        return ops;
    }

    private void Close(int channel, long sample)
    {
        MutableNote? note = _notes[channel];
        if (note == null)
            return;
        _notes[channel] = null;
        if (sample <= note.StartSample)
            return;
        _timeline.AddNote(
            note.Voice,
            note.StartSample,
            sample,
            note.InitialMidiNote,
            note.InitialFrequencyHz,
            note.InstrumentId,
            VisualizationNoteMode.Fm,
            false,
            note.Pitch);
    }

    private Pitch DecodePitch(int channel)
    {
        int port = channel / 9;
        int local = channel % 9;
        int low = _registers[port, 0xA0 + local];
        int high = _registers[port, 0xB0 + local];
        int fNumber = low | ((high & 0x03) << 8);
        int block = (high >> 2) & 0x07;
        if (fNumber <= 0)
            return Pitch.Unpitched;

        double frequency = fNumber / (double)(1 << 19)
            * (_device.ClockHz / 72.0)
            * (1 << block);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private void EmitRhythm(byte previous, byte current, long sample)
    {
        if ((current & 0x20) == 0)
            return;
        (int Bit, string Name)[] percussion =
        [
            (0x10, "bd"),
            (0x08, "sd"),
            (0x04, "tom"),
            (0x02, "cymbal"),
            (0x01, "hi-hat"),
        ];
        for (int index = 0; index < percussion.Length; index++)
        {
            if ((current & percussion[index].Bit) == 0
                || (previous & percussion[index].Bit) != 0)
                continue;
            _timeline.AddRhythm(new RhythmEvent(
                percussion[index].Name,
                new VoiceId(
                    _device.Id,
                    VoiceKind.Rhythm,
                    index,
                    Name: percussion[index].Name).ToString(),
                sample,
                1,
                0));
        }
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);
    }

    private sealed class MutableNote
    {
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId)
        {
            Voice = voice;
            StartSample = startSample;
            InitialFrequencyHz = pitch.FrequencyHz;
            InitialMidiNote = pitch.MidiNote;
            InstrumentId = instrumentId;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public double InitialFrequencyHz { get; }
        public double InitialMidiNote { get; }
        public string InstrumentId { get; }
        public List<PitchChange> Pitch { get; } = [];

        public void AddPitch(long sample, Pitch pitch)
        {
            if (pitch.MidiNote < 0)
                return;
            double previous = Pitch.Count == 0 ? InitialMidiNote : Pitch[^1].MidiNote;
            if (Math.Abs(previous - pitch.MidiNote) >= 0.0001)
                Pitch.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}
