namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the three tone generators and the shared noise activity of an
/// AY-3-8910-compatible PSG. Tone-enabled voices create conventional pitched
/// notes when their gate and pitch are audible; noise is retained separately as
/// NoiseStateEvent metadata and never creates a melodic note or rhythm attack.
/// Envelope-selected volume is an audible gate even when the direct nibble is
/// zero, and control writes do not speculate note retriggers.
/// </summary>
internal sealed class Ay8910TimelineDecoder : IChipTimelineDecoder
{
    private readonly byte[] _registers = new byte[16];
    private readonly MutableNote?[] _notes = new MutableNote?[3];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _noiseActive;
    private bool _completed;

    public ChipType ChipType => ChipType.Ay8910;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));
        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ay8910Voices(device.Id.Instance))
            timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id)
            return;

        int address = write.Address & 0x0F;
        _registers[address] = (byte)(write.Data & 0xFF);
        if (address is <= 5 or 7 or >= 8 and <= 10 or >= 11 and <= 13)
        {
            for (int channel = 0; channel < 3; channel++)
                Reconcile(channel, write.SamplePosition);
            ReconcileNoise(write.SamplePosition);
        }
        else if (address == 6)
        {
            ReconcileNoise(write.SamplePosition);
        }
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        _completed = true;
        for (int channel = 0; channel < 3; channel++)
            Close(channel, endSample);
    }

    private void Reconcile(int channel, long sample)
    {
        bool toneEnabled = (_registers[7] & (1 << channel)) == 0;
        bool noiseEnabled = (_registers[7] & (1 << (channel + 3))) == 0;
        int volumeRegister = _registers[8 + channel];
        int volume = volumeRegister & 0x0F;
        bool envelopeEnabled = (volumeRegister & 0x10) != 0;
        bool audible = toneEnabled && (envelopeEnabled || volume > 0);
        if (!audible)
        {
            Close(channel, sample);
            return;
        }

        Pitch pitch = DecodePitch(channel);
        // INV5 source classifier: a tone-disabled / ultrasonic / initialization
        // period (<= 0, or decoding above the audible ceiling) is not a
        // representable musical pitch — it must never become a MIDI note, so the
        // channel is treated as silent (period <= 0 is exactly the tone-disabled
        // oscillator state, aligned with the K051649 decoder's active gate).
        if (!ChipPitchDomain.IsRepresentable(pitch.FrequencyHz, pitch.MidiNote))
        {
            Close(channel, sample);
            return;
        }

        if (_notes[channel] == null)
        {
            string id = $"ay8910:{_device.Id.Instance}:tone:{channel}";
            _timeline.AddInstrument(new InstrumentDefinition(
                id, "psg", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
            VisualizationNoteMode mode = envelopeEnabled
                ? noiseEnabled ? VisualizationNoteMode.SsgEnvelopeToneNoise : VisualizationNoteMode.SsgEnvelopeTone
                : noiseEnabled ? VisualizationNoteMode.SsgToneNoise : VisualizationNoteMode.SsgTone;
            _notes[channel] = new MutableNote(
                new VoiceId(_device.Id, VoiceKind.Psg, channel),
                sample,
                pitch,
                id,
                mode);
        }
        else
        {
            _notes[channel]!.AddPitch(sample, pitch);
        }
    }

    /// Emits the shared noise channel as unpitched metadata, edge-driven:
    /// a state is emitted only on an inactive → active transition, never on
    /// every register write while the state stays active. PSG drivers rewrite
    /// mixer/volume registers every frame, so per-write emission would turn
    /// that update stream into repeated speculative attacks. Envelope-selected
    /// volume is treated as audible even when its direct nibble is zero.
    private void ReconcileNoise(long sample)
    {
        bool audible = false;
        float level = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            bool noiseEnabled = (_registers[7] & (1 << (channel + 3))) == 0;
            int volumeRegister = _registers[8 + channel];
            bool channelAudible = noiseEnabled
                && ((volumeRegister & 0x10) != 0 || (volumeRegister & 0x0F) > 0);
            if (!channelAudible)
                continue;
            audible = true;
            float channelLevel = (volumeRegister & 0x10) != 0
                ? 1.0f
                : (volumeRegister & 0x0F) / 15.0f;
            level = Math.Max(level, channelLevel);
        }

        if (!audible)
        {
            _noiseActive = false;
            return;
        }
        if (_noiseActive)
            return;
        _noiseActive = true;

        _timeline.AddNoiseState(new NoiseStateEvent(
            new VoiceId(_device.Id, VoiceKind.Noise, 0).ToString(),
            sample,
            checked(sample + 1),
            null,
            null,
            level,
            NoiseMode.HardwareDefined));
    }

    private Pitch DecodePitch(int channel)
    {
        int period = _registers[channel * 2]
            | ((_registers[channel * 2 + 1] & 0x0F) << 8);
        if (period <= 0)
            return Pitch.Unpitched;
        double frequency = _device.ClockHz / (16.0 * period);
        return new Pitch(frequency, 69.0 + 12.0 * Math.Log2(frequency / 440.0));
    }

    private void Close(int channel, long endSample)
    {
        MutableNote? note = _notes[channel];
        if (note == null)
            return;
        _notes[channel] = null;
        if (endSample <= note.StartSample)
            return;
        _timeline.AddNote(
            note.Voice,
            note.StartSample,
            endSample,
            note.InitialMidiNote,
            note.InitialFrequencyHz,
            note.InstrumentId,
            note.Mode,
            false,
            note.Pitch);
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
            VisualizationNoteMode mode)
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
            if (!ChipPitchDomain.IsRepresentable(pitch.FrequencyHz, pitch.MidiNote))
                return;
            double previous = Pitch.Count == 0 ? InitialMidiNote : Pitch[^1].MidiNote;
            if (Math.Abs(previous - pitch.MidiNote) >= 0.0001)
                Pitch.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}
