namespace Fmp.Core.Visualization;

/// <summary>Decodes the four native Game Boy sound lanes from APU writes.</summary>
internal sealed class DmgTimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[] _registers = new byte[0x40];
    private readonly byte[] _waveRam = new byte[16];
    private string _waveformId;
    private readonly MutableNote?[] _notes = new MutableNote?[3];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _noiseActive;
    private bool _completed;

    public ChipType ChipType => ChipType.Dmg;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.DmgVoices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address is < 0 or >= 0x40)
            return;

        _registers[write.Address] = (byte)write.Data;
        if (write.Address is >= 0x20 and <= 0x2F)
            UpdateWaveform(write.SamplePosition);
        for (int channel = 0; channel < 3; channel++)
        {
            Pitch pitch = DecodePitch(channel);
            if (!IsActive(channel, pitch))
            {
                Close(ref _notes[channel], write.SamplePosition);
                continue;
            }

            if (_notes[channel] != null
                && Math.Abs(_notes[channel].Pitch.MidiNote - pitch.MidiNote) < 0.0001)
                continue;

            bool retrigger = _notes[channel] != null;
            Close(ref _notes[channel], write.SamplePosition);
            string instrument = $"dmg:{channel + 1}";
            _timeline.AddInstrument(new InstrumentDefinition(
                instrument, "psg", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
            _notes[channel] = new MutableNote(
                new VoiceId(_device.Id, channel == 2 ? VoiceKind.Wavetable : VoiceKind.Pulse, channel == 2 ? 0 : channel),
                write.SamplePosition,
                pitch,
                instrument,
                channel == 2 ? VisualizationNoteMode.SsgTone : VisualizationNoteMode.SsgTone,
                retrigger);
        }

        bool noiseActive = (_registers[0x11] >> 4) != 0
            && (_registers[0x15] & 0x88) != 0
            && (_registers[0x16] & 0x80) != 0;
        if (noiseActive && !_noiseActive)
        {
            _timeline.AddRhythm(new RhythmEvent(
                "noise",
                new VoiceId(_device.Id, VoiceKind.Noise, 0).ToString(),
                write.SamplePosition,
                (_registers[0x11] >> 4) / 15.0f,
                0.5f));
        }
        _noiseActive = noiseActive;
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

    private bool IsActive(int channel, Pitch pitch)
    {
        if (pitch.MidiNote < 0)
            return false;
        int envelope = channel switch
        {
            0 => _registers[0x02] >> 4,
            1 => _registers[0x07] >> 4,
            _ => (_registers[0x0C] & 0x60) >> 5,
        };
        int panMask = channel switch
        {
            0 => 0x11,
            1 => 0x22,
            _ => 0x44,
        };
        return envelope > 0 && (_registers[0x15] & panMask) != 0;
    }

    private void UpdateWaveform(long sample)
    {
        for (int index = 0; index < _waveRam.Length; index++)
            _waveRam[index] = _registers[0x20 + index];

        var source = new int[32];
        for (int index = 0; index < source.Length; index++)
        {
            byte packed = _waveRam[index / 2];
            source[index] = (index & 1) == 0 ? packed >> 4 : packed & 0x0F;
        }

        WaveformDefinition waveform = VisualizationAssetBuilder.CreateIntegerWaveform(
            "wavetable", source, 0, 15, "WAVE");
        _timeline.AddWaveform(waveform);
        if (_waveformId == waveform.Id)
            return;
        _waveformId = waveform.Id;
        _timeline.AddWaveformChange(new WaveformChangeEvent(
            new VoiceId(_device.Id, VoiceKind.Wavetable, 0).ToString(), sample, waveform.Id));
    }

    private Pitch DecodePitch(int channel)
    {
        int low = channel switch { 0 => 0x03, 1 => 0x08, _ => 0x0D };
        int high = channel switch { 0 => 0x04, 1 => 0x09, _ => 0x0E };
        int period = _registers[low] | ((_registers[high] & 0x07) << 8);
        if (period <= 0 || period >= 2048)
            return Pitch.Unpitched;
        double frequency = _device.ClockHz / (8.0 * (2048 - period));
        return Pitch.FromFrequency(frequency);
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
                note.Mode,
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
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId, VisualizationNoteMode mode, bool retrigger)
        {
            Voice = voice;
            StartSample = startSample;
            Pitch = pitch;
            InstrumentId = instrumentId;
            Mode = mode;
            IsRetrigger = retrigger;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public Pitch Pitch { get; }
        public string InstrumentId { get; }
        public VisualizationNoteMode Mode { get; }
        public bool IsRetrigger { get; }
        public List<PitchChange> PitchChanges { get; } = [];
    }
}
