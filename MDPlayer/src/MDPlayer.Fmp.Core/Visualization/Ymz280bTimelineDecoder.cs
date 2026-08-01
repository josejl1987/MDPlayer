namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes YMZ280B's eight PCM voices. The chip's keyboard display maps its
/// ten-bit pitch register to a note table; the same table is used here so the
/// offline timeline remains consistent with MDPlayer's monitor.
/// </summary>
internal sealed class Ymz280bTimelineDecoder : IChipTimelineDecoder
{
    private static readonly int[] NoteTable =
    [
        0x002, 0x002, 0x002, 0x002, 0x002, 0x003, 0x003, 0x003, 0x004, 0x004, 0x004, 0x004,
        0x005, 0x005, 0x006, 0x006, 0x006, 0x007, 0x007, 0x008, 0x008, 0x009, 0x009, 0x00A,
        0x00B, 0x00B, 0x00C, 0x00D, 0x00E, 0x00F, 0x00F, 0x010, 0x011, 0x013, 0x014, 0x015,
        0x016, 0x018, 0x019, 0x01B, 0x01C, 0x01E, 0x020, 0x022, 0x024, 0x026, 0x028, 0x02B,
        0x02D, 0x030, 0x033, 0x036, 0x03A, 0x03D, 0x041, 0x045, 0x049, 0x04D, 0x052, 0x057,
        0x05C, 0x062, 0x067, 0x06E, 0x074, 0x07B, 0x082, 0x08A, 0x093, 0x09B, 0x0A5, 0x0AF,
        0x0B9, 0x0C4, 0x0D0, 0x0DC, 0x0E9, 0x0F7, 0x106, 0x116, 0x126, 0x138, 0x14A, 0x15E,
        0x173, 0x189, 0x1A0, 0x1B9, 0x1D4, 0x1EF, 0x1FF, 0x1FF, 0x1FF, 0x1FF, 0x1FF, 0x1FF,
    ];

    private readonly byte[] _registers = new byte[0x100];
    private readonly MutableNote?[] _notes = new MutableNote?[8];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.Ymz280b;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ymz280bVoices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address is < 0 or >= 0x100)
            return;

        _registers[write.Address] = (byte)write.Data;
        if (write.Address >= 0x20)
            return;
        UpdateChannel(write.Address / 4, write.SamplePosition);
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
        int offset = channel * 4;
        int frequencyRegister = _registers[offset] | ((_registers[offset + 1] & 1) << 8);
        int volume = Math.Min(19, _registers[offset + 2] / 12);
        bool active = (_registers[offset + 1] & 0x80) != 0
            && volume > 0;
        Pitch pitch = DecodePitch(frequencyRegister);
        if (!active || pitch.MidiNote < 0)
        {
            Close(ref _notes[channel], sample);
            return;
        }

        string sampleKey = $"start:{_registers[offset]:X2}{_registers[offset + 1]:X2}{_registers[offset + 2]:X2}";
        string sampleId = $"sample:ymz280b:{sampleKey.ToLowerInvariant()}";
        if (_notes[channel] != null
            && _notes[channel]!.SampleId == sampleId
            && Math.Abs(_notes[channel]!.Pitch.MidiNote - pitch.MidiNote) < 0.0001)
            return;

        bool retrigger = _notes[channel] != null;
        Close(ref _notes[channel], sample);
        string instrument = $"ymz280b:pcm:{channel + 1}";
        _timeline.AddSample(VisualizationAssetBuilder.CreateSyntheticSample(
            sampleId, "pcm", 0, displayName: $"YMZ280B {sampleKey}"));
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "pcm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Pcm, channel),
            sample,
            pitch,
            instrument,
            sampleId,
            retrigger);
    }

    private static Pitch DecodePitch(int frequencyRegister)
    {
        if (frequencyRegister <= 0)
            return Pitch.Unpitched;

        int noteIndex = Array.BinarySearch(NoteTable, frequencyRegister);
        if (noteIndex < 0)
            noteIndex = Math.Clamp(~noteIndex - 1, 0, NoteTable.Length - 1);
        double midiNote = noteIndex + 12;
        return new Pitch(440.0 * Math.Pow(2.0, (midiNote - 69.0) / 12.0), midiNote);
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
                note.PitchChanges.ToArray(),
                note.SampleId);
        }
        note = null;
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
            string sampleId,
            bool retrigger)
        {
            Voice = voice;
            StartSample = startSample;
            Pitch = pitch;
            InstrumentId = instrumentId;
            SampleId = sampleId;
            IsRetrigger = retrigger;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public Pitch Pitch { get; }
        public string InstrumentId { get; }
        public string SampleId { get; }
        public bool IsRetrigger { get; }
        public List<PitchChange> PitchChanges { get; } = [];
    }
}
