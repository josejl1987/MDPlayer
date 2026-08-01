namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the YM2151/OPM channel key-code and key-fraction registers. The
/// register interpretation is shared by VGM and future MXDRV event sources.
/// </summary>
internal sealed class Ym2151TimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[] _registers = new byte[256];
    private readonly MutableNote?[] _notes = new MutableNote?[8];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.Ym2151;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ym2151Voices(device.Id.Instance))
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
        int data = write.Data & 0xFF;
        _registers[address] = (byte)data;
        if (address == 0x08)
        {
            int channel = data & 0x07;
            if ((data & 0x78) != 0)
                Start(channel, write.SamplePosition);
            else
                Close(channel, write.SamplePosition);
            return;
        }

        if (address is >= 0x28 and <= 0x2F)
        {
            int channel = address - 0x28;
            if (_notes[channel] != null)
                _notes[channel]!.AddPitch(write.SamplePosition, DecodePitch(channel));
        }
        else if (address is >= 0x30 and <= 0x37)
        {
            int channel = address - 0x30;
            if (_notes[channel] != null)
                _notes[channel]!.AddPitch(write.SamplePosition, DecodePitch(channel));
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

        string instrumentId = $"ym2151:{_device.Id.Instance}:{channel}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrumentId,
            "fm",
            null,
            null,
            null,
            null,
            Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Fm, channel),
            sample,
            pitch,
            instrumentId);
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
        int keyCode = _registers[0x28 + channel] & 0x7F;
        if (keyCode == 0)
            return Pitch.Unpitched;

        int octave = (keyCode & 0x70) >> 4;
        int noteCode = keyCode & 0x0F;
        int note = noteCode < 3
            ? noteCode
            : noteCode < 7
                ? noteCode - 1
                : noteCode < 11
                    ? noteCode - 2
                    : noteCode - 3;
        double midi = octave * 12 + note + 12
            + (_registers[0x30 + channel] & 0xFC) / 256.0;
        double frequency = 440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0);
        return new Pitch(frequency, midi);
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
            if (Math.Abs(previous - pitch.MidiNote) < 0.0001)
                return;
            Pitch.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}
