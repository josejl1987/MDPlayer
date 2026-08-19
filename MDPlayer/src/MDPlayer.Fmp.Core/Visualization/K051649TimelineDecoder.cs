namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the five-channel SCC/K051649 wavetable register bus. The 12-bit
/// frequency registers are written as two byte-port writes; the low byte is held
/// PENDING until the paired high byte (or any later write) settles it, so the
/// intermediate register value between the two bytes can never become a note
/// boundary (INV4). Channels whose tone period is disabled or ultrasonic produce
/// no notes (INV5).
/// </summary>
internal sealed class K051649TimelineDecoder : IChipTimelineDecoder
{
    private readonly int[] _frequency = new int[5];
    private readonly int[] _volume = new int[5];
    private readonly byte[][] _waveRam = new byte[5][];
    private readonly string[] _waveformIds = new string[5];
    private readonly MutableNote?[] _notes = new MutableNote?[5];
    private readonly PendingFrequency?[] _pendingFrequency = new PendingFrequency?[5];
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private long _plateauSamples;
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
        _plateauSamples = Math.Max(1, (long)Math.Round(timeline.SampleRate * 0.010));
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
            // Waveform loading is unrelated to frequency updates; settle any held
            // low byte so it cannot dangle across the driver's next section.
            for (int pending = 0; pending < 5; pending++)
                FlushPendingFrequency(pending);
            UpdateWaveform(channel, write.SamplePosition);
            return;
        }
        switch (write.Address >> 1)
        {
            case 1:
                channel = Math.Clamp(_selectedRegister >> 1, 0, 4);
                if ((_selectedRegister & 1) == 0)
                {
                    // Frequency LOW byte: the 12-bit register value is written as
                    // two byte-port writes, and the intermediate value between them
                    // is a chip-glitch state, never a voice state (INV4). Hold the
                    // low byte as PENDING instead of reconciling at the intermediate
                    // value; the paired high-byte write (or any later write, which
                    // proves the low byte was standalone) settles it. A previous
                    // unpaired low byte is settled first.
                    if (_pendingFrequency[channel].HasValue)
                        FlushPendingFrequency(channel);
                    _frequency[channel] = (_frequency[channel] & 0xF00) | (write.Data & 0xFF);
                    _pendingFrequency[channel] = new PendingFrequency(write.SamplePosition);
                }
                else
                {
                    _frequency[channel] = (_frequency[channel] & 0x0FF) | ((write.Data & 0x0F) << 8);
                    if (_pendingFrequency[channel] is PendingFrequency pending)
                    {
                        // The high byte completes the two-byte value: reconcile ONCE
                        // with the final pitch at the pair's first write.
                        _pendingFrequency[channel] = null;
                        UpdateChannel(channel, pending.SamplePosition);
                    }
                    else
                    {
                        UpdateChannel(channel, write.SamplePosition);
                    }
                }
                return;
            case 2:
                _volume[channel] = write.Data & 0x0F;
                break;
            case 3:
                _keyMask = write.Data & 0x1F;
                break;
            default:
                return;
        }

        // A volume / key-mask write proves the driver moved on: any held low byte is
        // a STANDALONE frequency update (no paired high byte follows), so settle it
        // at its own sample before reconciling the current write.
        for (int pending = 0; pending < 5; pending++)
            FlushPendingFrequency(pending);
        for (channel = 0; channel < 5; channel++)
            UpdateChannel(channel, write.SamplePosition);
    }

    /// <summary>
    /// Settles a held frequency low byte at its own sample position. The held value
    /// is already in <see cref="_frequency"/> (the low byte was merged at write
    /// time); only the note reconciliation was deferred so the intermediate state
    /// between the low and high byte of one 12-bit value can never become a note
    /// boundary (INV4).
    /// </summary>
    private void FlushPendingFrequency(int channel)
    {
        if (_pendingFrequency[channel] is not PendingFrequency pending)
            return;
        _pendingFrequency[channel] = null;
        UpdateChannel(channel, pending.SamplePosition);
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int channel = 0; channel < _notes.Length; channel++)
        {
            FlushPendingFrequency(channel);
            Close(ref _notes[channel], endSample);
        }
    }

    private void UpdateChannel(int channel, long sample)
    {
        Pitch pitch = DecodePitch(channel);
        bool active = (_keyMask & (1 << channel)) != 0
            && _volume[channel] > 0
            // INV5 source classifier: a tone-disabled / ultrasonic / initialization
            // period (<= 0, or decoding above the audible ceiling) is not a
            // representable musical pitch and never becomes a note (aligned with
            // the AY8910 tone gate).
            && ChipPitchDomain.IsRepresentable(pitch.FrequencyHz, pitch.MidiNote);
        if (!active)
        {
            Close(ref _notes[channel], sample);
            return;
        }

        if (_notes[channel] != null)
        {
            _notes[channel]!.AddPitch(sample, pitch);
            return;
        }

        StartNote(channel, sample, pitch);
    }

    private void StartNote(int channel, long sample, Pitch pitch)
    {
        string instrument = $"k051649:wave:{channel + 1}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "wavetable", null, null, null, null, Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Wavetable, channel),
            sample,
            pitch,
            instrument);
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
        if (period <= 8)
            return Pitch.Unpitched;
        return Pitch.FromFrequency(_device.ClockHz / (32.0 * (period + 1)));
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
                note.InitialPitch.MidiNote,
                note.InitialPitch.FrequencyHz,
                note.InstrumentId,
                VisualizationNoteMode.SsgTone,
                false,
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

    /// <summary>
    /// A frequency low byte awaiting its paired high byte (or proof of standalone
    /// status). The sample is the low-byte write where the two-byte value begins.
    /// </summary>
    private readonly record struct PendingFrequency(long SamplePosition);

    private readonly record struct PendingPlateau(
        long SamplePosition,
        int RoundedMidiNote,
        Pitch Pitch);

    private sealed class MutableNote
    {
        private PendingPlateau? _candidate;

        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId)
        {
            Voice = voice;
            StartSample = startSample;
            InitialPitch = pitch;
            Pitch = pitch;
            StableMidiNote = (int)Math.Round(pitch.MidiNote);
            InstrumentId = instrumentId;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public Pitch InitialPitch { get; }
        public Pitch Pitch { get; private set; }
        public int StableMidiNote { get; private set; }
        public string InstrumentId { get; }

        public bool TryCommitPending(
            long sample, long minimumSamples, out PendingPlateau plateau)
        {
            plateau = default;
            if (_candidate is not PendingPlateau candidate
                || sample - candidate.SamplePosition < minimumSamples)
                return false;
            StableMidiNote = candidate.RoundedMidiNote;
            plateau = candidate;
            _candidate = null;
            return true;
        }

        public bool TryPromotePlateau(
            long sample, Pitch pitch, long minimumSamples, out PendingPlateau plateau)
        {
            plateau = default;
            if (!ChipPitchDomain.IsRepresentable(pitch.FrequencyHz, pitch.MidiNote))
                return false;

            int rounded = (int)Math.Round(pitch.MidiNote);
            if (rounded == StableMidiNote)
            {
                _candidate = null;
                AddPitch(sample, pitch);
                return false;
            }

            if (_candidate is not PendingPlateau candidate || candidate.RoundedMidiNote != rounded)
            {
                _candidate = new PendingPlateau(sample, rounded, pitch);
                AddPitch(sample, pitch);
                return false;
            }

            AddPitch(sample, pitch);
            if (sample - candidate.SamplePosition < minimumSamples)
                return false;

            StableMidiNote = rounded;
            plateau = candidate;
            _candidate = null;
            return true;
        }

        public void RemovePitchChangesFrom(long sample) =>
            PitchChanges.RemoveAll(change => change.SamplePosition >= sample);

        private void AddPitch(long sample, Pitch pitch)
        {
            if (Math.Abs(Pitch.MidiNote - pitch.MidiNote) >= 0.0001)
                PitchChanges.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
            Pitch = pitch;
        }

        public List<PitchChange> PitchChanges { get; } = [];
    }
}
