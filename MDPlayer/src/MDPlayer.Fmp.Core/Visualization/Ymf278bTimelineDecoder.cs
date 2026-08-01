namespace Fmp.Core.Visualization;

/// <summary>
/// Combines the OPL3-compatible FM bank with the OPL4 PCM bank. YMF278B
/// exposes both through the same VGM device, so keeping the two register
/// interpretations behind one registry entry preserves device identity.
/// </summary>
internal sealed class Ymf278bTimelineDecoder : IChipTimelineDecoder
{
    private readonly OplTimelineDecoder _fm = new(ChipType.Ymf278b, 18);
    private readonly Ymf278bPcmTimelineDecoder _pcm = new();

    public ChipType ChipType => ChipType.Ymf278b;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        _fm.Initialize(device, timeline);
        _pcm.Initialize(device, timeline);
    }

    public void Process(in TimedChipWrite write)
    {
        _fm.Process(write);
        _pcm.Process(write);
    }

    public void Complete(long endSample)
    {
        _fm.Complete(endSample);
        _pcm.Complete(endSample);
    }
}

internal sealed class Ymf278bPcmTimelineDecoder : IChipTimelineDecoder
{
    private static readonly int[] PitchTable = [0, 61, 125, 194, 266, 343, 424, 510, 602, 698, 801, 909];
    private readonly byte[,] _registers = new byte[3, 0x100];
    private readonly MutableNote?[] _notes = new MutableNote?[24];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.Ymf278b;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in Ymf278bPcmVoices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Port is < 0 or > 2 || write.Address is < 0 or >= 0x100)
            return;

        _registers[write.Port, write.Address] = (byte)write.Data;
        if (write.Port != 2 || write.Address < 0x08 || write.Address > 0x7F)
            return;

        int channel = write.Address switch
        {
            >= 0x08 and <= 0x1F => write.Address - 0x08,
            >= 0x20 and <= 0x37 => write.Address - 0x20,
            >= 0x38 and <= 0x4F => write.Address - 0x38,
            >= 0x50 and <= 0x67 => write.Address - 0x50,
            >= 0x68 and <= 0x7F => write.Address - 0x68,
            _ => -1,
        };
        if (channel >= 0)
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
        int fnum = (_registers[2, 0x20 + channel] >> 1)
            | ((_registers[2, 0x38 + channel] & 0x07) << 7);
        int octave = (_registers[2, 0x38 + channel] >> 4) & 0x0F;
        bool active = (_registers[2, 0x68 + channel] & 0x80) != 0
            && (_registers[2, 0x50 + channel] >> 1) < 127;
        Pitch pitch = DecodePitch(fnum, octave);
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
        string instrument = $"ymf278b:pcm:{channel + 1}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "pcm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Pcm, channel),
            sample,
            pitch,
            instrument,
            retrigger);
    }

    private static Pitch DecodePitch(int fnum, int octave)
    {
        if (fnum <= 0)
            return Pitch.Unpitched;
        int semitone = 0;
        int distance = int.MaxValue;
        for (int index = 0; index < PitchTable.Length; index++)
        {
            int candidate = Math.Abs(fnum - PitchTable[index]);
            if (candidate < distance)
            {
                distance = candidate;
                semitone = index;
            }
        }

        double midiNote = ((octave + 7) & 0x0F) * 12 + semitone - 5 + 12;
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
                note.PitchChanges.ToArray());
        }
        note = null;
    }

    private static IReadOnlyList<VoiceDescriptor> Ymf278bPcmVoices(int instance)
    {
        DeviceId device = new(ChipType.Ymf278b, instance);
        var voices = new List<VoiceDescriptor>(24);
        for (int index = 0; index < 24; index++)
        {
            voices.Add(new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Pcm, index),
                $"PCM {index + 1}",
                VoicePresentationKind.Pcm,
                23 + index,
                false,
                false,
                true));
        }
        return voices;
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
