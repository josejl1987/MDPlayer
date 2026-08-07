namespace Fmp.Core.Visualization;

/// <summary>
/// YM2612 register-to-note decoder. It intentionally shares the normalized
/// timeline contract with YM2608 while retaining YM2612-specific DAC and FM3
/// special-mode semantics.
///
/// YM2612 DAC playback is analyzed by the shared <see cref="DacPlaybackTracker"/>
/// and <see cref="DacSampleCatalog"/> into per-trigger sample events, rendered on
/// a dedicated sample lane rather than a pitched note.
/// </summary>
internal sealed class Ym2612TimelineDecoder : IChipTimelineDecoder
{
	private const int RegisterBankSize = 0x100;
	private const double FmDivider = 6.0;

    private readonly byte[] _registers = new byte[RegisterBankSize * 2];
    private readonly MutableNote?[] _fmNotes = new MutableNote?[6];
    private readonly MutableNote?[] _fm3Notes = new MutableNote?[4];
    private readonly int[] _fm3KeyMask = new int[1];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _fm3SpecialMode;
    private readonly Ym2612DacNormalizer _dacNormalizer = new();
    private readonly DacPlaybackTracker _dacTracker = new();
    private readonly List<DacOperation> _dacOps = [];
    private bool _dacEnabled;
    private bool _completed;

    public ChipType ChipType => ChipType.Ym2612;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device with
        {
            Capabilities = device.Capabilities | DeviceCapabilities.SampleIdentity,
        });
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ym2612Voices(device.Id.Instance))
            _timeline.AddVoice(voice);
        _timeline.AddVoice(new VoiceDescriptor(
            DacVoice,
            "DAC",
            VoicePresentationKind.Pcm,
            10,
            false,
            false,
            false));
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Port is < 0 or > 1 || write.Address is < 0 or > 0xFF)
            return;

        _registers[write.Port * RegisterBankSize + write.Address] = (byte)write.Data;
        if (write.Port == 0 && write.Address == 0x2B)
        {
            ProcessDacRegisters(write.SamplePosition, write.Port, write.Address, write.Data);
            Fm6DacGate(write.SamplePosition);
            return;
        }
        if (write.Port == 0 && write.Address == 0x2A)
        {
            ProcessDacRegisters(write.SamplePosition, write.Port, write.Address, write.Data);
            return;
        }
        if (write.Port == 0 && write.Address == 0x27)
        {
            ApplyFm3Mode(write.SamplePosition, write.Data);
            return;
        }
        if (write.Port == 0 && write.Address == 0x28)
        {
            ApplyKey(write.SamplePosition, write.Data);
            return;
        }
        if (IsPitchRegister(write.Port, write.Address))
            ApplyPitchChange(write.SamplePosition, write.Port, write.Address);
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));

        _completed = true;
        for (int channel = 0; channel < _fmNotes.Length; channel++)
            Close(ref _fmNotes[channel], endSample);
        for (int op = 0; op < _fm3Notes.Length; op++)
            Close(ref _fm3Notes[op], endSample);

        EmitDacSamples(endSample);
    }

    private VoiceId DacVoice => new(_device.Id, VoiceKind.Pcm, 0, Name: "dac");

    /// <summary>
    /// Feeds 0x2A/0x2B DAC writes through the normalizer state machine and
    /// playback tracker. FM channel 6 gating on DAC enable is applied
    /// separately by <see cref="Fm6DacGate"/>.
    /// </summary>
    private void ProcessDacRegisters(long sample, int port, int address, int value)
    {
        _dacNormalizer.Process(sample, port, address, value, _dacOps);
        while (_dacOps.Count > 0)
        {
            _dacTracker.Add(_dacOps[0]);
            _dacOps.RemoveAt(0);
        }
    }

    private void Fm6DacGate(long sample)
    {
        bool enabled = (_registers[0x2B] & 0x80) != 0;
        if (enabled == _dacEnabled)
            return;
        _dacEnabled = enabled;
        if (enabled)
            Close(ref _fmNotes[5], sample);
    }

    /// <summary>
    /// Closes the DAC tracker at end-of-stream, builds the sample catalog, and
    /// emits one <see cref="SampleDefinition"/> per deduplicated asset plus one
    /// <see cref="SamplePlaybackEvent"/> per trigger onto the DAC sample voice.
    /// </summary>
    private void EmitDacSamples(long endSample)
    {
        _dacNormalizer.Complete(endSample, _dacOps);
        foreach (DacOperation op in _dacOps)
            _dacTracker.Add(op);
        _dacOps.Clear();

        _dacTracker.Complete(endSample);
        var catalog = new DacSampleCatalog().Build(_dacTracker.Candidates);
        _dacTracker.ApplyAssetIds(catalog);

        foreach (DacSampleAsset asset in catalog.Assets)
        {
            _timeline.AddSample(new SampleDefinition(
                asset.TimelineSampleId,
                "pcm",
                asset.Payload.Length,
                NativeSampleRate: null,
                LoopStart: null,
                LoopEnd: null,
                SampleLoopMode.None,
                Array.Empty<WaveformEnvelopePoint>(),
                asset.StableName)
            {
                IdentityKind = AssetIdentityKind.ContentHash,
            });
        }

        string lastEmittedSampleId = null;
        foreach (DacPlaybackEvent evt in _dacTracker.PlaybackEvents)
        {
            if (evt.SampleId is null)
                continue;
            double playbackRate = evt.InitialRateHz is double rate && rate > 0
                ? rate
                : 1.0;
            bool retrigger = string.Equals(evt.SampleId, lastEmittedSampleId, StringComparison.Ordinal)
                || evt.StopReason is DacStopReason.Retriggered or DacStopReason.SourceDiscontinuity;
            _timeline.AddSamplePlayback(new SamplePlaybackEvent(
                DacVoice.ToString(),
                evt.StartSample,
                Math.Max(evt.StartSample + 1, evt.EndSample),
                evt.SampleId,
                MidiPitch: null,
                playbackRate,
                Gain: evt.Gain is double gain ? (float)gain : 1f,
                Pan: evt.Pan is double pan ? (float)pan : 0f,
                Retrigger: retrigger,
                Looping: false));
            lastEmittedSampleId = evt.SampleId;
        }
    }

    private void ApplyKey(long sample, int value)
    {
        int channelCode = value & 0x07;
        int channel = channelCode <= 2 ? channelCode : channelCode - 1;
        if (channel is < 0 or > 5)
            return;

        int keyMask = value & 0xF0;
        if (channel == 2 && _fm3SpecialMode)
        {
            int previous = _fm3KeyMask[0];
            _fm3KeyMask[0] = keyMask;
            for (int op = 0; op < 4; op++)
            {
                int bit = 0x10 << op;
                bool wasOn = (previous & bit) != 0;
                bool isOn = (keyMask & bit) != 0;
                if (!isOn)
                {
                    Close(ref _fm3Notes[op], sample);
                    continue;
                }

                bool retrigger = wasOn || _fm3Notes[op] != null;
                Close(ref _fm3Notes[op], sample);
                _fm3Notes[op] = OpenFm3(op, sample, retrigger);
            }
            return;
        }

        if (channel == 2)
            _fm3KeyMask[0] = keyMask;
        if (channel == 5 && _dacEnabled)
            return;

        if (keyMask == 0)
        {
            Close(ref _fmNotes[channel], sample);
            return;
        }

        bool retriggered = _fmNotes[channel] != null;
        Close(ref _fmNotes[channel], sample);
        _fmNotes[channel] = OpenFm(channel, sample, retriggered);
    }

    private void ApplyFm3Mode(long sample, int value)
    {
        bool enabled = (value & 0x40) != 0;
        if (enabled == _fm3SpecialMode)
            return;

        if (enabled)
        {
            Close(ref _fmNotes[2], sample);
            _fm3SpecialMode = true;
            for (int op = 0; op < 4; op++)
            {
                if ((_fm3KeyMask[0] & (0x10 << op)) != 0)
                    _fm3Notes[op] = OpenFm3(op, sample, false);
            }
        }
        else
        {
            for (int op = 0; op < 4; op++)
                Close(ref _fm3Notes[op], sample);
            _fm3SpecialMode = false;
            if (_fm3KeyMask[0] != 0)
                _fmNotes[2] = OpenFm(2, sample, false);
        }
    }

    private void ApplyPitchChange(long sample, int port, int address)
    {
        int channel = GetOrdinaryChannel(port, address);
        if (channel >= 0)
        {
            if (channel == 2 && _fm3SpecialMode)
                AddPitch(_fm3Notes[0], sample, DecodeFm3Pitch(0));
            else
                AddPitch(_fmNotes[channel], sample, DecodeFmPitch(channel));
        }

        if (port == 0 && _fm3SpecialMode)
        {
            int op = address switch
            {
                0xA8 or 0xAC => 1,
                0xA9 or 0xAD => 2,
                0xAA or 0xAE => 3,
                _ => -1,
            };
            if (op >= 0)
                AddPitch(_fm3Notes[op], sample, DecodeFm3Pitch(op));
        }
    }

    private MutableNote OpenFm(int channel, long sample, bool retrigger)
    {
        Pitch pitch = DecodeFmPitch(channel);
        string instrumentId = $"ym2612:fm:{channel + 1}";
        AddInstrument(instrumentId);
        var note = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Fm, channel),
            sample,
            pitch,
            instrumentId,
            VisualizationNoteMode.Fm,
            retrigger);
        return note;
    }

    private MutableNote OpenFm3(int op, long sample, bool retrigger)
    {
        Pitch pitch = DecodeFm3Pitch(op);
        const string instrumentId = "ym2612:fm3";
        AddInstrument(instrumentId);
        return new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Fm3Operator, op),
            sample,
            pitch,
            instrumentId,
            VisualizationNoteMode.Fm3Operator,
            retrigger);
    }

    private void AddInstrument(string id)
    {
        _timeline.AddInstrument(new InstrumentDefinition(
            id,
            "fm",
            null,
            null,
            null,
            null,
            Array.Empty<FmOperatorDefinition>()));
    }

    private void AddPitch(MutableNote? note, long sample, Pitch pitch)
    {
        if (note == null || pitch.MidiNote < 0)
            return;
        note.AddPitch(sample, pitch);
    }

    private Pitch DecodeFmPitch(int channel)
    {
        int port = channel >= 3 ? 1 : 0;
        int local = channel % 3;
        int fNumber = _registers[port * RegisterBankSize + 0xA0 + local]
            | ((_registers[port * RegisterBankSize + 0xA4 + local] & 0x07) << 8);
        int block = (_registers[port * RegisterBankSize + 0xA4 + local] >> 3) & 0x07;
        return DecodePitch(fNumber, block);
    }

    private Pitch DecodeFm3Pitch(int op)
    {
        int low = op == 0 ? 0xA2 : 0xA8 + op - 1;
        int high = op == 0 ? 0xA6 : 0xAC + op - 1;
        int fNumber = _registers[low] | ((_registers[high] & 0x07) << 8);
        int block = (_registers[high] >> 3) & 0x07;
        return DecodePitch(fNumber, block);
    }

    private Pitch DecodePitch(int fNumber, int block)
    {
        if (fNumber <= 0)
            return Pitch.Unpitched;
        double frequency = fNumber * _device.ClockHz * Math.Pow(2, block)
            / (1_048_576.0 * 24.0 * FmDivider);
        if (!double.IsFinite(frequency) || frequency <= 0)
            return Pitch.Unpitched;
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private static int GetOrdinaryChannel(int port, int address)
    {
        int local = address switch
        {
            >= 0xA0 and <= 0xA2 => address - 0xA0,
            >= 0xA4 and <= 0xA6 => address - 0xA4,
            _ => -1,
        };
        return local < 0 ? -1 : port * 3 + local;
    }

    private static bool IsPitchRegister(int port, int address) =>
        GetOrdinaryChannel(port, address) >= 0
        || (port == 0 && address is >= 0xA8 and <= 0xAE);

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
                note.IsRetrigger,
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
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId, VisualizationNoteMode mode, bool retrigger)
        {
            Voice = voice;
            StartSample = startSample;
            InitialFrequencyHz = pitch.FrequencyHz;
            InitialMidiNote = pitch.MidiNote;
            InstrumentId = instrumentId;
            Mode = mode;
            IsRetrigger = retrigger;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public double InitialFrequencyHz { get; }
        public double InitialMidiNote { get; }
        public string InstrumentId { get; }
        public VisualizationNoteMode Mode { get; }
        public bool IsRetrigger { get; }
        public List<PitchChange> Pitch { get; } = [];

        public void AddPitch(long sample, Pitch pitch)
        {
            if (pitch.MidiNote < 0)
                return;
            double previous = Pitch.Count == 0 ? InitialMidiNote : Pitch[^1].MidiNote;
            if (Math.Abs(previous - pitch.MidiNote) < 0.0001)
                return;
            if (Pitch.Count > 0 && Pitch[^1].SamplePosition == sample)
                Pitch[^1] = new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote);
            else
                Pitch.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}
