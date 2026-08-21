namespace Fmp.Core.Visualization;

/// <summary>Captures the command stream exposed by the FMP PPZ8 shim.</summary>
internal sealed class Ppz8TimelineDecoder : IChipTimelineDecoder
{
    private readonly State[] _states = new State[8];
    private readonly List<Ppz8Event> _events = [];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private int? _currentBank;
    private readonly Dictionary<(int Bank, int Slot), string> _sampleIds = [];
    private bool _completed;

    public ChipType ChipType => ChipType.Ppz8;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ppz8Voices(device.Id.Instance))
            timeline.AddVoice(voice);
    }

    public void ObserveBank(string assetId)
    {
        if (string.IsNullOrWhiteSpace(assetId))
            return;
        string[] parts = assetId.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[1], out int bank)
            && bank >= 0)
            _currentBank = bank;
    }

    public void ObserveBank(string assetId, ReadOnlyMemory<byte>[] samples)
    {
        ObserveBank(assetId);
        if (_currentBank is not int bank || samples == null)
            return;
        for (int slot = 0; slot < samples.Length; slot++)
        {
            ReadOnlySpan<byte> bytes = samples[slot].Span;
            if (bytes.Length == 0)
                continue;
            var normalized = new float[bytes.Length];
            for (int index = 0; index < bytes.Length; index++)
                normalized[index] = unchecked((sbyte)bytes[index]) / 128f;
            SampleDefinition sample = VisualizationAssetBuilder.CreateSample(
                "pcm", normalized, 16_000, displayName: $"SMP {slot:X2}");
            _sampleIds[(bank, slot)] = sample.Id;
            _timeline.AddSample(sample);
        }
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id)
            return;

        if (write.Port == 0)
        {
            for (int index = 0; index < _states.Length; index++)
                Close(index, write.SamplePosition);
            return;
        }
        if (write.Address is < 0 or >= 8)
            return;

        int channel = write.Address;
        State state = _states[channel] ??= new State();
        switch (write.Port)
        {
            case 0: // initialization/reset
                Close(channel, write.SamplePosition);
                break;
            case 1: // key on, arg1=channel, arg2=sample number
                bool retrigger = state.Current != null;
                Close(channel, write.SamplePosition);
                state.SampleNumber = ClampNullable(write.Data);
                state.Current = new MutableEvent(
                    channel,
                    write.SamplePosition,
                    state.SampleNumber,
                    _currentBank,
                    state.PlaybackFnum,
                    state.PlaybackRate,
                    state.SourceSampleRate,
                    state.MidiNote,
                    state.Volume,
                    state.Pan,
                    retrigger);
                break;
            case 2: // key off
                Close(channel, write.SamplePosition);
                break;
            case 7: // channel volume, 0..15
                state.Volume = Math.Clamp(write.Data, 0, 15) / 15f;
                state.Current?.Update(
                    state.PlaybackFnum,
                    state.PlaybackRate,
                    state.SourceSampleRate,
                    state.MidiNote,
                    state.Volume,
                    state.Pan);
                break;
            case 11: // playback FNUM (fixed-point ratio, 0x8000 = source rate)
                state.PlaybackFnum = write.Data;
                state.PlaybackRate = DecodePlaybackRate(state.PlaybackFnum);
                state.MidiNote = null; // sample root pitch is not known here.
                state.Current?.Update(
                    state.PlaybackFnum,
                    state.PlaybackRate,
                    state.SourceSampleRate,
                    state.MidiNote,
                    state.Volume,
                    state.Pan);
                break;
            case 19: // pan, represented by the driver's signed control value
                state.Pan = DecodePan(write.Data);
                state.Current?.Update(
                    state.PlaybackFnum,
                    state.PlaybackRate,
                    state.SourceSampleRate,
                    state.MidiNote,
                    state.Volume,
                    state.Pan);
                break;
            case 21: // source sample rate, kept separate from playback FNUM
                if (write.Data > 0)
                {
                    state.SourceSampleRate = write.Data;
                    state.Current?.Update(
                        state.PlaybackFnum,
                        state.PlaybackRate,
                        state.SourceSampleRate,
                        state.MidiNote,
                        state.Volume,
                        state.Pan);
                }
                break;
            case 22: // source/bank selector used by some FMP builds
                if (write.Data >= 0)
                    _currentBank = write.Data;
                break;
        }
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int channel = 0; channel < _states.Length; channel++)
            Close(channel, endSample);
        foreach (Ppz8Event value in _events)
        {
            string sampleId = value.Bank is int bank && value.SampleNumber is int slot
                && _sampleIds.TryGetValue((bank, slot), out string known)
                ? known
                : null;
            string voiceId = new VoiceId(
                _device.Id,
                VoiceKind.Pcm,
                value.Channel).ToString();
            _timeline.AddPpz8(value, sampleId, voiceId);
        }
    }

    private void Close(int channel, long endSample)
    {
        State state = _states[channel];
        MutableEvent current = state?.Current;
        if (current == null)
            return;
        if (endSample > current.StartSample)
        {
            _events.Add(new Ppz8Event(
                current.Channel,
                current.StartSample,
                endSample,
                current.Bank,
                current.SampleNumber,
                current.PlaybackFnum,
                current.PlaybackRate,
                current.SourceSampleRate,
                current.MidiNote,
                current.Volume,
                current.Pan,
                current.IsRetrigger));
        }
        state.Current = null;
    }

    private static int? ClampNullable(int value)
        => value is >= 0 and <= byte.MaxValue ? value : null;

    private static double DecodePlaybackRate(int? fnum)
        => fnum is > 0 ? fnum.Value / 32768.0 : 1.0;

    private static float DecodePan(int value)
    {
        if (value is >= -128 and <= 128)
            return Math.Clamp(value / 128f, -1, 1);
        if (value is >= 0 and <= 15)
            return Math.Clamp((value - 7.5f) / 7.5f, -1, 1);
        return 0;
    }

    private sealed class State
    {
        public int? SampleNumber { get; set; }
        public int? PlaybackFnum { get; set; }
        public double PlaybackRate { get; set; } = 1.0;
        public int? SourceSampleRate { get; set; } = 16_000;
        public double? MidiNote { get; set; }
        public float Volume { get; set; } = 1;
        public float Pan { get; set; }
        public MutableEvent Current { get; set; }
    }

    private sealed class MutableEvent
    {
        public MutableEvent(
            int channel,
            long startSample,
            int? sampleNumber,
            int? bank,
            int? playbackFnum,
            double playbackRate,
            int? sourceSampleRate,
            double? midiNote,
            float volume,
            float pan,
            bool isRetrigger)
        {
            Channel = channel;
            StartSample = startSample;
            SampleNumber = sampleNumber;
            Bank = bank;
            PlaybackFnum = playbackFnum;
            PlaybackRate = playbackRate;
            SourceSampleRate = sourceSampleRate;
            MidiNote = midiNote;
            Volume = volume;
            Pan = pan;
            IsRetrigger = isRetrigger;
        }

        public int Channel { get; }
        public long StartSample { get; }
        public int? SampleNumber { get; }
        public int? Bank { get; }
        public int? PlaybackFnum { get; private set; }
        public double PlaybackRate { get; private set; }
        public int? SourceSampleRate { get; private set; }
        public double? MidiNote { get; private set; }
        public float Volume { get; private set; }
        public float Pan { get; private set; }
        public bool IsRetrigger { get; }

        public void Update(
            int? playbackFnum,
            double playbackRate,
            int? sourceSampleRate,
            double? midiNote,
            float volume,
            float pan)
        {
            PlaybackFnum = playbackFnum;
            PlaybackRate = playbackRate;
            SourceSampleRate = sourceSampleRate;
            MidiNote = midiNote;
            Volume = volume;
            Pan = pan;
        }
    }
}
