namespace Fmp.Core.Visualization;

internal sealed class Sn76489TimelineDecoder : IChipTimelineDecoder
{
    private readonly int[] _periods = new int[3];
    private readonly int[] _volumes = [15, 15, 15, 15];
    private readonly MutableNote?[] _notes = new MutableNote?[4];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private int _latchedChannel;
    private bool _latchedVolume;
    private bool _completed;

    public ChipType ChipType => ChipType.Sn76489;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Sn76489Voices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id)
            return;

        int data = write.Data & 0xFF;
        if ((data & 0x80) != 0)
        {
            _latchedChannel = (data >> 5) & 0x03;
            _latchedVolume = (data & 0x10) != 0;
            if (_latchedVolume)
                _volumes[_latchedChannel] = data & 0x0F;
            else if (_latchedChannel < 3)
                _periods[_latchedChannel] = (_periods[_latchedChannel] & 0x3F00) | (data & 0x3F);
        }
        else if (!_latchedVolume && _latchedChannel < 3)
        {
            _periods[_latchedChannel] = (_periods[_latchedChannel] & 0x003F) | ((data & 0x3F) << 4);
        }

        Reconcile(_latchedChannel, write.SamplePosition);
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int channel = 0; channel < _notes.Length; channel++)
            Close(ref _notes[channel], endSample);
    }

    private void Reconcile(int channel, long sample)
    {
        bool audible = _volumes[channel] < 15 && (channel == 3 || _periods[channel] > 0);
        if (!audible)
        {
            Close(ref _notes[channel], sample);
            return;
        }

        if (_notes[channel] == null)
        {
            string id = channel == 3 ? "sn76489:noise" : "sn76489:tone";
            _timeline.AddInstrument(new InstrumentDefinition(
                id,
                channel == 3 ? "noise" : "psg",
                null,
                null,
                null,
                null,
                Array.Empty<FmOperatorDefinition>()));
            Pitch pitch = channel == 3 ? Pitch.Unpitched : DecodePitch(_periods[channel]);
            _notes[channel] = new MutableNote(
                new VoiceId(_device.Id, channel == 3 ? VoiceKind.Noise : VoiceKind.Psg, channel == 3 ? 0 : channel),
                sample,
                pitch,
                id,
                channel == 3 ? VisualizationNoteMode.SsgNoise : VisualizationNoteMode.SsgTone);
            return;
        }

        if (channel < 3)
            _notes[channel].AddPitch(sample, DecodePitch(_periods[channel]));
    }

    private Pitch DecodePitch(int period)
    {
        if (period <= 0)
            return Pitch.Unpitched;
        double frequency = _device.ClockHz / (32.0 * period);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private void Close(ref MutableNote? note, long endSample)
    {
        if (note == null)
            return;
        if (endSample > note.StartSample)
        {
            _timeline.AddNote(
                note.Voice,
                note.StartSample,
                endSample,
                note.InitialMidiNote,
                note.InitialFrequencyHz,
                note.InstrumentId,
                note.Mode,
                false,
                note.Pitch.ToArray());
        }
        note = null;
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);
    }

    private sealed class MutableNote
    {
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId, VisualizationNoteMode mode)
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
            if (Math.Abs(previous - pitch.MidiNote) < 0.0001)
                return;
            Pitch.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}
