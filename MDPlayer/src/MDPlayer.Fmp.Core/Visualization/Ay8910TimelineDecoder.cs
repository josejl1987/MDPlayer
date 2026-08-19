namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the three tone generators and the shared noise activity of an
/// AY-3-8910-compatible PSG. Noise activity becomes percussion triggers,
/// emitted EDGE-driven (one trigger per inactive → active transition — INV1),
/// never per register write. Only tone-enabled voices with a representable
/// musical pitch create conventional pitched notes (INV5: tone-disabled or
/// ultrasonic periods are silent, never notes).
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
        if (address is <= 5 or 7 or >= 8 and <= 10)
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
            _notes[channel] = new MutableNote(
                new VoiceId(_device.Id, VoiceKind.Psg, channel),
                sample,
                pitch,
                id);
        }
        else
        {
            _notes[channel]!.AddPitch(sample, pitch);
        }
    }

    /// <summary>
    /// Emits the shared noise channel as a percussion trigger, EDGE-driven (INV1):
    /// a trigger is emitted only on an inactive → active transition of the audible
    /// noise state, never on every register write while the state stays active.
    /// PSG drivers rewrite the mixer/volume registers every frame; per-write
    /// emission turned that update stream into a machine-gun of identical kicks
    /// (≈ 60 Hz), while the actual rhythm part is the set of volume onsets. The
    /// AY-3-8910 exposes no separate envelope-restart signal for the noise channel
    /// in this decode path (reg 13 envelope writes are not tracked), and the
    /// register trace of the Gradius II fixture shows the volume genuinely returns
    /// to 0 between hits, so the volume edge is the complete trigger model.
    private void ReconcileNoise(long sample)
    {
        bool noiseEnabled = (_registers[7] & 0x38) != 0x38;
        bool audible = noiseEnabled && _registers[8..11]
            .Any(value => (value & 0x10) != 0 || (value & 0x0F) > 0);
        if (!audible)
        {
            _noiseActive = false;
            return;
        }
        if (_noiseActive)
            return;
        _noiseActive = true;

        int volume = _registers[8..11]
            .Select(value => value & 0x0F)
            .DefaultIfEmpty()
            .Max();
        _timeline.AddRhythm(new RhythmEvent(
            "noise",
            new VoiceId(_device.Id, VoiceKind.Noise, 0).ToString(),
            sample,
            volume / 15.0f,
            0));
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
            VisualizationNoteMode.SsgTone,
            false,
            note.Pitch);
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);
    }

    private sealed class MutableNote
    {
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId)
        {
            Voice = voice;
            StartSample = startSample;
            InitialFrequencyHz = pitch.FrequencyHz;
            InitialMidiNote = pitch.MidiNote;
            InstrumentId = instrumentId;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public double InitialFrequencyHz { get; }
        public double InitialMidiNote { get; }
        public string InstrumentId { get; }
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
