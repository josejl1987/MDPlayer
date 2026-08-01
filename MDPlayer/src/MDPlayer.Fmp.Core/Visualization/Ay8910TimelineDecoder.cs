namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the three tone generators and the shared noise activity of an
/// AY-3-8910-compatible PSG. Noise-only writes become activity markers; only
/// tone-enabled voices create conventional pitched notes.
/// </summary>
internal sealed class Ay8910TimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[] _registers = new byte[16];
    private readonly MutableNote?[] _notes = new MutableNote?[3];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.Ay8910;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));
        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ay8910Voices(device.Id.Instance))
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

        int address = write.Address & 0x0F;
        _registers[address] = (byte)(write.Data & 0xFF);
        if (address is <= 5 or 7 or >= 8 and <= 10)
        {
            for (int channel = 0; channel < 3; channel++)
                Reconcile(channel, write.SamplePosition);
            ReconcileNoise(write.SamplePosition);
        }
        else if (address == 6)
        {
            ReconcileNoise(write.SamplePosition);
        }
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        _completed = true;
        for (int channel = 0; channel < 3; channel++)
            Close(channel, endSample);
    }

    private void Reconcile(int channel, long sample)
    {
        bool toneEnabled = (_registers[7] & (1 << channel)) == 0;
        int volume = _registers[8 + channel] & 0x0F;
        bool audible = toneEnabled && volume > 0;
        if (!audible)
        {
            Close(channel, sample);
            return;
        }

        Pitch pitch = DecodePitch(channel);
        if (_notes[channel] == null)
        {
            string id = $"ay8910:{_device.Id.Instance}:tone:{channel}";
            _timeline.AddInstrument(new InstrumentDefinition(
                id, "psg", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
            _notes[channel] = new MutableNote(
                new VoiceId(_device.Id, VoiceKind.Psg, channel),
                sample,
                pitch,
                id);
        }
        else
        {
            _notes[channel]!.AddPitch(sample, pitch);
        }
    }

    private void ReconcileNoise(long sample)
    {
        bool noiseEnabled = (_registers[7] & 0x38) != 0x38;
        bool audible = noiseEnabled && _registers[8..11].Any(value => (value & 0x0F) > 0);
        if (!audible)
            return;

        int volume = _registers[8..11]
            .Select(value => value & 0x0F)
            .DefaultIfEmpty()
            .Max();
        _timeline.AddRhythm(new RhythmEvent(
            "noise",
            new VoiceId(_device.Id, VoiceKind.Noise, 0).ToString(),
            sample,
            volume / 15.0f,
            0));
    }

    private Pitch DecodePitch(int channel)
    {
        int period = _registers[channel * 2]
            | ((_registers[channel * 2 + 1] & 0x0F) << 8);
        if (period <= 0)
            return Pitch.Unpitched;
        double frequency = _device.ClockHz / (16.0 * period);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
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
            VisualizationNoteMode.SsgTone,
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
