namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the native NES APU register set. Pulse and triangle channels are
/// pitched; noise and DPCM are represented as activity/percussion events.
/// </summary>
internal sealed class NesApuTimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[] _registers = new byte[0x20];
    private readonly MutableNote?[] _notes = new MutableNote?[3];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _noiseActive;
    private bool _completed;

    public ChipType ChipType => ChipType.NesApu;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.NesApuVoices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address is < 0 or >= 0x20)
            return;

        _registers[write.Address] = (byte)write.Data;
        UpdatePulse(0, 0x00, 0x02, 0x03, write.SamplePosition);
        UpdatePulse(1, 0x04, 0x06, 0x07, write.SamplePosition);
        UpdateTriangle(write.SamplePosition);

        bool noiseActive = (_registers[0x15] & 0x08) != 0
            && (_registers[0x0C] & 0x0F) != 0;
        if (noiseActive && !_noiseActive)
        {
            _timeline.AddRhythm(new RhythmEvent(
                "noise",
                new VoiceId(_device.Id, VoiceKind.Noise, 0).ToString(),
                write.SamplePosition,
                (_registers[0x0C] & 0x0F) / 15.0f,
                0.5f));
        }
        _noiseActive = noiseActive;

        // $4015 bit 4 enables DPCM. A write to $4011 changes its DAC level;
        // retain that as activity without inventing a conventional pitch.
        if (write.Address is 0x11 or 0x15
            && (_registers[0x15] & 0x10) != 0
            && _registers[0x11] != 0)
        {
            _timeline.AddRhythm(new RhythmEvent(
                "dpcm",
                new VoiceId(_device.Id, VoiceKind.Dpcm, 0).ToString(),
                write.SamplePosition,
                _registers[0x11] / 127.0f,
                0.5f));
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
            Close(ref _notes[channel], endSample);
    }

    private void UpdatePulse(int channel, int control, int timerLow, int timerHigh, long sample)
    {
        int period = _registers[timerLow] | ((_registers[timerHigh] & 0x07) << 8);
        int volume = _registers[control] & 0x0F;
        bool enabled = (_registers[0x15] & (1 << channel)) != 0;
        Pitch pitch = period <= 0
            ? Pitch.Unpitched
            : Pitch.FromFrequency(_device.ClockHz / (16.0 * (period + 1)));
        UpdateNote(
            ref _notes[channel],
            new VoiceId(_device.Id, VoiceKind.Pulse, channel),
            pitch,
            enabled && volume > 0,
            sample,
            $"nes-apu:pulse:{channel + 1}");
    }

    private void UpdateTriangle(long sample)
    {
        int period = _registers[0x0A] | ((_registers[0x0B] & 0x07) << 8);
        Pitch pitch = period <= 0
            ? Pitch.Unpitched
            : Pitch.FromFrequency(_device.ClockHz / (32.0 * (period + 1)));
        UpdateNote(
            ref _notes[2],
            new VoiceId(_device.Id, VoiceKind.Triangle, 0),
            pitch,
            (_registers[0x15] & 0x04) != 0 && (_registers[0x08] & 0x80) != 0,
            sample,
            "nes-apu:triangle");
    }

    private void UpdateNote(
        ref MutableNote? note,
        VoiceId voice,
        Pitch pitch,
        bool active,
        long sample,
        string instrument)
    {
        if (!active || pitch.MidiNote < 0)
        {
            Close(ref note, sample);
            return;
        }
        if (note != null && Math.Abs(note.Pitch.MidiNote - pitch.MidiNote) < 0.0001)
            return;
        bool retrigger = note != null;
        Close(ref note, sample);
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "nes-apu", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        note = new MutableNote(voice, sample, pitch, instrument, retrigger);
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
                note.Pitch.MidiNote,
                note.Pitch.FrequencyHz,
                note.InstrumentId,
                VisualizationNoteMode.SsgTone,
                note.IsRetrigger,
                note.PitchChanges.ToArray());
        }
        note = null;
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);

        public static Pitch FromFrequency(double frequency) =>
            !double.IsFinite(frequency) || frequency <= 0
                ? Unpitched
                : new(frequency, 69 + 12 * Math.Log2(frequency / 440.0));
    }

    private sealed class MutableNote
    {
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId, bool retrigger)
        {
            Voice = voice;
            StartSample = startSample;
            Pitch = pitch;
            InstrumentId = instrumentId;
            IsRetrigger = retrigger;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public Pitch Pitch { get; }
        public string InstrumentId { get; }
        public bool IsRetrigger { get; }
        public List<PitchChange> PitchChanges { get; } = [];
    }
}
