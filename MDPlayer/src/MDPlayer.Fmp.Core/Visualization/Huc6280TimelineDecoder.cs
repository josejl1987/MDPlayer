namespace Fmp.Core.Visualization;

/// <summary>Decodes HuC6280's six PSG channels from its selected-register bus.</summary>
internal sealed class Huc6280TimelineDecoder : IChipTimelineDecoder
{
    private readonly int[] _frequency = new int[6];
    private readonly int[] _volume = new int[6];
    private readonly int[] _left = new int[6];
    private readonly int[] _right = new int[6];
    private readonly byte[][] _waveRam = new byte[6][];
    private readonly int[] _waveIndex = new int[6];
    private readonly string[] _waveformIds = new string[6];
    private readonly MutableNote?[] _notes = new MutableNote?[6];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private int _selectedChannel;
    private bool _completed;

    public ChipType ChipType => ChipType.Huc6280;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        for (int channel = 0; channel < _waveRam.Length; channel++)
            _waveRam[channel] = new byte[32];
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Huc6280Voices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address is < 0 or > 9)
            return;

        switch (write.Address)
        {
            case 0:
                _selectedChannel = Math.Clamp(write.Data & 0x07, 0, 5);
                break;
            case 2:
                _frequency[_selectedChannel] = (_frequency[_selectedChannel] & 0xF00) | (write.Data & 0xFF);
                break;
            case 3:
                _frequency[_selectedChannel] = (_frequency[_selectedChannel] & 0x0FF) | ((write.Data & 0x0F) << 8);
                break;
            case 4:
                _volume[_selectedChannel] = write.Data & 0x1F;
                break;
            case 5:
                _left[_selectedChannel] = (write.Data >> 4) & 0x0F;
                _right[_selectedChannel] = write.Data & 0x0F;
                break;
            case 6:
                _waveRam[_selectedChannel][_waveIndex[_selectedChannel]] = (byte)(write.Data & 0x1F);
                _waveIndex[_selectedChannel] = (_waveIndex[_selectedChannel] + 1) & 31;
                UpdateWaveform(_selectedChannel, write.SamplePosition);
                break;
        }

        UpdateChannel(_selectedChannel, write.SamplePosition);
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
        Pitch pitch = DecodePitch(channel);
        bool active = pitch.MidiNote >= 0
            && _volume[channel] > 0
            && (_left[channel] > 0 || _right[channel] > 0);
        if (!active)
        {
            Close(ref _notes[channel], sample);
            return;
        }

        if (_notes[channel] != null
            && Math.Abs(_notes[channel].Pitch.MidiNote - pitch.MidiNote) < 0.0001)
            return;

        bool retrigger = _notes[channel] != null;
        Close(ref _notes[channel], sample);
        string instrument = $"huc6280:wave:{channel + 1}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "wavetable", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Wavetable, channel),
            sample,
            pitch,
            instrument,
            retrigger);
    }

    private void UpdateWaveform(int channel, long sample)
    {
        int[] source = new int[_waveRam[channel].Length];
        for (int index = 0; index < source.Length; index++)
            source[index] = _waveRam[channel][index];
        WaveformDefinition waveform = VisualizationAssetBuilder.CreateIntegerWaveform(
            "wavetable", source, 0, 31, $"WAVE {channel + 1}");
        _timeline.AddWaveform(waveform);
        if (_waveformIds[channel] == waveform.Id)
            return;
        _waveformIds[channel] = waveform.Id;
        _timeline.AddWaveformChange(new WaveformChangeEvent(
            new VoiceId(_device.Id, VoiceKind.Wavetable, channel).ToString(),
            sample,
            waveform.Id));
    }

    private Pitch DecodePitch(int channel)
    {
        int period = _frequency[channel] & 0xFFF;
        if (period <= 0)
            return Pitch.Unpitched;
        return Pitch.FromFrequency(_device.ClockHz / 32.0 / period);
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
