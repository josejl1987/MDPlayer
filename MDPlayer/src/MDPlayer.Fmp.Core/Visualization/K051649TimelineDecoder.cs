namespace Fmp.Core.Visualization;

/// <summary>Decodes the five-channel SCC/K051649 wavetable register bus.</summary>
internal sealed class K051649TimelineDecoder : IChipTimelineDecoder
{
    private readonly int[] _frequency = new int[5];
    private readonly int[] _volume = new int[5];
    private readonly byte[][] _waveRam = new byte[5][];
    private readonly string[] _waveformIds = new string[5];
    private readonly MutableNote?[] _notes = new MutableNote?[5];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private int _selectedRegister;
    private int _keyMask;
    private bool _completed;

    public ChipType ChipType => ChipType.K051649;

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
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.K051649Voices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address < 0)
            return;

        if ((write.Address & 1) == 0)
        {
            _selectedRegister = write.Data & 0xFF;
            return;
        }

        int channel = Math.Clamp(_selectedRegister & 0x07, 0, 4);
        if ((write.Address >> 1) == 0)
        {
            channel = Math.Clamp(_selectedRegister / 0x20, 0, 4);
            int index = _selectedRegister & 0x1F;
            _waveRam[channel][index] = (byte)write.Data;
            UpdateWaveform(channel, write.SamplePosition);
            return;
        }
        switch (write.Address >> 1)
        {
            case 1:
                channel = Math.Clamp(_selectedRegister >> 1, 0, 4);
                if ((_selectedRegister & 1) == 0)
                    _frequency[channel] = (_frequency[channel] & 0xF00) | (write.Data & 0xFF);
                else
                    _frequency[channel] = (_frequency[channel] & 0x0FF) | ((write.Data & 0x0F) << 8);
                break;
            case 2:
                _volume[channel] = write.Data & 0x0F;
                break;
            case 3:
                _keyMask = write.Data & 0x1F;
                break;
            default:
                return;
        }

        for (channel = 0; channel < 5; channel++)
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
        Pitch pitch = DecodePitch(channel);
        bool active = (_keyMask & (1 << channel)) != 0
            && _volume[channel] > 0
            && pitch.MidiNote >= 0;
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
        string instrument = $"k051649:wave:{channel + 1}";
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
            source[index] = (sbyte)_waveRam[channel][index];
        WaveformDefinition waveform = VisualizationAssetBuilder.CreateIntegerWaveform(
            "wavetable", source, -128, 127, $"WAVE {channel + 1}");
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
        int period = _frequency[channel];
        if (period <= 0)
            return Pitch.Unpitched;
        return Pitch.FromFrequency(_device.ClockHz / (8.0 * period));
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
