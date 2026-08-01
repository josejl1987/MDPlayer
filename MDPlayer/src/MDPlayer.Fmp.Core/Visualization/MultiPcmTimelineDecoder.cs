namespace Fmp.Core.Visualization;

/// <summary>Decodes MultiPCM's 28 register-backed PCM voices.</summary>
internal sealed class MultiPcmTimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[,] _registers = new byte[28, 8];
    private readonly MutableNote?[] _notes = new MutableNote?[28];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.MultiPcm;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.MultiPcmVoices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address is < 0 or >= 28 * 8)
            return;

        int channel = write.Address / 8;
        int register = write.Address & 7;
        _registers[channel, register] = (byte)write.Data;
        UpdateChannel(channel, write.SamplePosition);
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

    private void UpdateChannel(int channel, long sample)
    {
        int octave = ((_registers[channel, 3] >> 4) - 1) & 0x0F;
        if ((octave & 0x08) != 0)
            octave -= 16;
        octave += 4;
        int pitchIndex = ((_registers[channel, 3] & 0x0F) << 6)
            | (_registers[channel, 2] >> 2);
        int noteIndex = Math.Clamp(octave * 12 + pitchIndex / 85, 0, 7 * 12);
        int totalLevel = (_registers[channel, 5] >> 1) & 0x7F;
        bool active = (_registers[channel, 4] & 0x80) != 0 && totalLevel < 0x7F;
        Pitch pitch = active
            ? new Pitch(440.0 * Math.Pow(2.0, ((noteIndex + 12) - 69.0) / 12.0), noteIndex + 12)
            : Pitch.Unpitched;

        if (!active || pitch.MidiNote < 0)
        {
            Close(ref _notes[channel], sample);
            return;
        }
        if (_notes[channel] != null
            && Math.Abs(_notes[channel].Pitch.MidiNote - pitch.MidiNote) < 0.0001)
            return;

        bool retrigger = _notes[channel] != null;
        Close(ref _notes[channel], sample);
        string instrument = $"multipcm:pcm:{channel + 1}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "pcm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Pcm, channel),
            sample,
            pitch,
            instrument,
            retrigger);
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
                VisualizationNoteMode.Pcm,
                note.IsRetrigger,
                note.PitchChanges.ToArray());
        }
        note = null;
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);
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
