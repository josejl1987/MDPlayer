namespace Fmp.Core.Visualization;

/// <summary>
/// YM2413/OPLL key and rhythm decoder. Its pitch normalization follows the
/// existing MDPlayer keyboard formula: f-number relative to 172, with the
/// octave offset from register 0x20.
/// </summary>
internal sealed class Ym2413TimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[] _registers = new byte[256];
    private readonly MutableNote?[] _notes = new MutableNote?[9];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.Ym2413;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));
        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ym2413Voices(device.Id.Instance))
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

        int address = write.Address & 0xFF;
        _registers[address] = (byte)(write.Data & 0xFF);
        if (address is >= 0x10 and <= 0x18)
        {
            int channel = address - 0x10;
            _notes[channel]?.AddPitch(write.SamplePosition, DecodePitch(channel));
        }
        else if (address is >= 0x20 and <= 0x28)
        {
            ReconcileKey(address - 0x20, write.SamplePosition);
        }
        else if (address == 0x0E)
        {
            EmitRhythm(write.SamplePosition);
        }
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        _completed = true;
        for (int channel = 0; channel < _notes.Length; channel++)
            Close(channel, endSample);
    }

    private void ReconcileKey(int channel, long sample)
    {
        bool on = (_registers[0x20 + channel] & 0x10) != 0;
        if (!on)
        {
            Close(channel, sample);
            return;
        }
        if (_notes[channel] != null)
        {
            _notes[channel]!.AddPitch(sample, DecodePitch(channel));
            return;
        }

        string id = $"ym2413:{_device.Id.Instance}:fm:{channel}";
        _timeline.AddInstrument(new InstrumentDefinition(
            id, "fm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Fm, channel),
            sample,
            DecodePitch(channel),
            id);
    }

    private Pitch DecodePitch(int channel)
    {
        int fNumber = _registers[0x10 + channel]
            | ((_registers[0x20 + channel] & 0x01) << 8);
        int octave = (_registers[0x20 + channel] >> 1) & 0x07;
        if (fNumber <= 0)
            return Pitch.Unpitched;
        double frequency = 523.25 * (fNumber / 172.0) * Math.Pow(2, octave - 4);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private void EmitRhythm(long sample)
    {
        if ((_registers[0x0E] & 0x20) == 0)
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
            if ((_registers[0x0E] & percussion[index].Bit) == 0)
                continue;
            _timeline.AddRhythm(new RhythmEvent(
                percussion[index].Name,
                new VoiceId(_device.Id, VoiceKind.Rhythm, index, Name: percussion[index].Name).ToString(),
                sample,
                1,
                0));
        }
    }

    private void Close(int channel, long endSample)
    {
        MutableNote? note = _notes[channel];
        if (note == null)
            return;
        _notes[channel] = null;
        if (endSample <= note.StartSample)
            return;
        _timeline.AddNote(
            note.Voice,
            note.StartSample,
            endSample,
            note.InitialMidiNote,
            note.InitialFrequencyHz,
            note.InstrumentId,
            VisualizationNoteMode.Fm,
            false,
            note.Pitch);
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
