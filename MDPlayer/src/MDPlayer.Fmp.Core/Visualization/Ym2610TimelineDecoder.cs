namespace Fmp.Core.Visualization;

/// <summary>
/// YM2610/OPNB FM and integrated SSG decoder. ADPCM voices are represented in
/// the topology and become activity-only until sample-memory semantics are
/// available from the backend.
/// </summary>
internal sealed class Ym2610TimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[,] _registers = new byte[2, 256];
    private readonly MutableNote?[] _fmNotes = new MutableNote?[4];
    private readonly MutableNote?[] _ssgNotes = new MutableNote?[3];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.Ym2610;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));
        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ym2610Voices(device.Id.Instance))
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

        int port = write.Port & 1;
        int address = write.Address & 0xFF;
        _registers[port, address] = (byte)(write.Data & 0xFF);
        if (address == 0x28)
        {
            int channel = port == 0 ? write.Data & 0x03 : 3;
            if ((write.Data & 0xF0) == 0)
                CloseFm(channel, write.SamplePosition);
            else
                StartFm(channel, write.SamplePosition);
            return;
        }

        if (port == 0 && (address is <= 5 or 7 or >= 8 and <= 10))
        {
            for (int channel = 0; channel < 3; channel++)
                ReconcileSsg(channel, write.SamplePosition);
            if ((_registers[0, 7] & 0x38) != 0x38)
            {
                int volume = Enumerable.Range(8, 3)
                    .Select(index => _registers[0, index] & 0x0F)
                    .DefaultIfEmpty()
                    .Max();
                if (volume > 0)
                    _timeline.AddRhythm(new RhythmEvent(
                        "noise",
                        new VoiceId(_device.Id, VoiceKind.Noise, 0).ToString(),
                        write.SamplePosition,
                        volume / 15.0f,
                        0));
            }
            return;
        }

        int channelFromFrequency = port == 0
            ? address switch
            {
                >= 0xA0 and <= 0xA2 => address - 0xA0,
                >= 0xA4 and <= 0xA6 => address - 0xA4,
                _ => -1,
            }
            : address is 0xA0 or 0xA4 ? 3 : -1;
        if (channelFromFrequency >= 0)
            _fmNotes[channelFromFrequency]?.AddPitch(write.SamplePosition, DecodeFmPitch(channelFromFrequency));
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        _completed = true;
        for (int channel = 0; channel < _fmNotes.Length; channel++)
            CloseFm(channel, endSample);
        for (int channel = 0; channel < _ssgNotes.Length; channel++)
            CloseSsg(channel, endSample);
    }

    private void StartFm(int channel, long sample)
    {
        CloseFm(channel, sample);
        Pitch pitch = DecodeFmPitch(channel);
        if (pitch.MidiNote < 0)
            return;
        string id = $"ym2610:{_device.Id.Instance}:fm:{channel}";
        _timeline.AddInstrument(new InstrumentDefinition(
            id, "fm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _fmNotes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Fm, channel),
            sample,
            pitch,
            id);
    }

    private Pitch DecodeFmPitch(int channel)
    {
        int port = channel == 3 ? 1 : 0;
        int local = channel == 3 ? 0 : channel;
        int fNumber = _registers[port, 0xA0 + local]
            | ((_registers[port, 0xA4 + local] & 0x07) << 8);
        int block = (_registers[port, 0xA4 + local] >> 3) & 0x07;
        if (fNumber <= 0)
            return Pitch.Unpitched;
        double frequency = fNumber * _device.ClockHz * Math.Pow(2, block)
            / (1_048_576.0 * 24.0 * 6.0);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private void ReconcileSsg(int channel, long sample)
    {
        bool toneEnabled = (_registers[0, 7] & (1 << channel)) == 0;
        int volume = _registers[0, 8 + channel] & 0x0F;
        if (!toneEnabled || volume == 0)
        {
            CloseSsg(channel, sample);
            return;
        }
        Pitch pitch = DecodeSsgPitch(channel);
        if (_ssgNotes[channel] == null)
        {
            string id = $"ym2610:{_device.Id.Instance}:ssg:{channel}";
            _timeline.AddInstrument(new InstrumentDefinition(
                id, "psg", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
            _ssgNotes[channel] = new MutableNote(
                new VoiceId(_device.Id, VoiceKind.Ssg, channel),
                sample,
                pitch,
                id,
                VisualizationNoteMode.SsgTone);
        }
        else
        {
            _ssgNotes[channel]!.AddPitch(sample, pitch);
        }
    }

    private Pitch DecodeSsgPitch(int channel)
    {
        int period = _registers[0, channel * 2]
            | ((_registers[0, channel * 2 + 1] & 0x0F) << 8);
        if (period <= 0)
            return Pitch.Unpitched;
        double frequency = _device.ClockHz / (16.0 * period);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private void CloseFm(int channel, long sample)
    {
        MutableNote? note = _fmNotes[channel];
        if (note == null)
            return;
        _fmNotes[channel] = null;
        AddNote(note, sample);
    }

    private void CloseSsg(int channel, long sample)
    {
        MutableNote? note = _ssgNotes[channel];
        if (note == null)
            return;
        _ssgNotes[channel] = null;
        AddNote(note, sample);
    }

    private void AddNote(MutableNote note, long endSample)
    {
        if (endSample <= note.StartSample)
            return;
        _timeline.AddNote(
            note.Voice,
            note.StartSample,
            endSample,
            note.InitialMidiNote,
            note.InitialFrequencyHz,
            note.InstrumentId,
            note.Mode,
            false,
            note.Pitch);
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);
    }

    private sealed class MutableNote
    {
        public MutableNote(
            VoiceId voice,
            long startSample,
            Pitch pitch,
            string instrumentId,
            VisualizationNoteMode mode = VisualizationNoteMode.Fm)
        {
            Voice = voice;
            StartSample = startSample;
            InitialFrequencyHz = pitch.FrequencyHz;
            InitialMidiNote = pitch.MidiNote;
            InstrumentId = instrumentId;
            Mode = mode;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public double InitialFrequencyHz { get; }
        public double InitialMidiNote { get; }
        public string InstrumentId { get; }
        public VisualizationNoteMode Mode { get; }
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
