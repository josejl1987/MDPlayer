using Fmp.Core.Visualization;

namespace Fmp.Core.Decoding.SnesDsp;

/// <summary>
/// Managed timeline decoder for the SNES S-DSP. The native SPC core (PR 2/3)
/// emits effective semantic transitions; this decoder turns them into
/// piano-roll notes with relative pitch (§13.3) and pitch-change points.
/// Purely managed: it performs no file I/O and has no native dependency.
/// </summary>
internal sealed class SnesDspTimelineDecoder : IChipTimelineDecoder
{
    private const double A4Anchor = 69.0;

    private const int VoiceCount = 8;

    private readonly SpcActiveNote?[] _notes = new SpcActiveNote?[VoiceCount];
    private readonly SpcNoisePeriod?[] _noisePeriods = new SpcNoisePeriod?[VoiceCount];
    private readonly bool[] _voiceActive = new bool[VoiceCount];
    private readonly bool[] _noiseEnabled = new bool[VoiceCount];
    private readonly int[] _latchedSources = new int[VoiceCount];
    private readonly Dictionary<int, double> _rootOffsetBySource = new();
    private readonly Dictionary<int, string> _sampleIdBySource = new();

    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;

    public ChipType ChipType => ChipType.SnesDsp;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType.SnesDsp)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.SnesDspVoices(device.Id.Instance))
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write) =>
        throw new NotSupportedException(
            "The SNES S-DSP decoder consumes semantic events (SpcSemanticEvent), not raw chip writes.");

    public void Process(in SpcSemanticEvent @event)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (@event.SamplePosition < 0)
            throw new ArgumentOutOfRangeException(nameof(@event));
        if (@event.Voice is < 0 or >= VoiceCount)
            return;

        switch (@event.Kind)
        {
            case SpcSemanticEventKind.KeyOn:
                OnKeyOn(@event);
                break;
            case SpcSemanticEventKind.ReleaseStart:
                OnReleaseStart(@event);
                break;
            case SpcSemanticEventKind.VoiceEnd:
                Close(@event.Voice, @event.SamplePosition);
                break;
            case SpcSemanticEventKind.PitchChanged:
                OnPitchChanged(@event);
                break;
            case SpcSemanticEventKind.SourceLatched:
                _latchedSources[@event.Voice] = @event.Value;
                break;
            case SpcSemanticEventKind.NoiseChanged:
                OnNoiseChanged(@event);
                break;
            default:
                break;
        }

        if (@event.Kind is SpcSemanticEventKind.SourceLatched
            or SpcSemanticEventKind.VolumeChanged
            or SpcSemanticEventKind.EnvelopeModeChanged
            or SpcSemanticEventKind.NoiseChanged
            or SpcSemanticEventKind.PitchModChanged
            or SpcSemanticEventKind.EchoSendChanged)
        {
            _timeline.AddSpcVoiceState(new SpcVoiceStateEvent(
                new VoiceId(_device.Id, VoiceKind.PcmVoice, @event.Voice).ToString(),
                @event.SamplePosition,
                @event.Kind.ToString(),
                @event.Value,
                @event.Value2));
        }
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int voice = 0; voice < VoiceCount; voice++)
            Close(voice, endSample);
    }

    /// <summary>
    /// §25.3: installs per-source BRR root estimates so notes land at their
    /// sounding pitch (root + relative S-DSP semitones). Sources without an
    /// estimate keep the A4 anchor (natural rate = 69/440 Hz).
    /// </summary>
    public void SetSourceRoots(IReadOnlyList<SpcSourceRootInfo> sources)
    {
        _rootOffsetBySource.Clear();
        if (sources == null)
            return;
        foreach (SpcSourceRootInfo info in sources)
        {
            if (info.EstimatedRootHz is > 0)
                _rootOffsetBySource[info.SourceNumber] =
                    12.0 * Math.Log2(info.EstimatedRootHz.Value / 440.0);
        }
    }

    /// <summary>Installs bounded generic PCM assets for the BRR sources.</summary>
    public void SetSamples(IReadOnlyList<SpcSampleEntry> samples)
    {
        _sampleIdBySource.Clear();
        if (samples == null)
            return;

        foreach (SpcSampleEntry entry in samples.OrderBy(value => value.Hash, StringComparer.Ordinal))
        {
            string sampleId = "sample:" + entry.ShortHash;
            BrrSample brr = new(
                entry.StartAddress,
                entry.LoopAddress,
                entry.EncodedBytes ?? Array.Empty<byte>(),
                entry.Loops,
                entry.EncodedBytes is { Length: > 0 },
                entry.Hash);
            short[] decoded = BrrDecoder.Decode(brr);
            SampleDefinition definition;
            if (decoded.Length > 0)
            {
                float[] normalized = new float[decoded.Length];
                for (int index = 0; index < decoded.Length; index++)
                    normalized[index] = decoded[index] / 32768f;
                int? loopStart = entry.Loops
                    ? Math.Clamp((entry.LoopAddress - entry.StartAddress) / BrrSampleReader.BlockSize
                        * BrrDecoder.SamplesPerBlock, 0, decoded.Length)
                    : null;
                definition = VisualizationAssetBuilder.CreateSample(
                    "brr",
                    normalized,
                    BrrDecoder.SampleRateHz,
                    loopStart,
                    entry.Loops ? decoded.Length : null,
                    entry.Loops ? SampleLoopMode.Forward : SampleLoopMode.None,
                    $"BRR {entry.ShortHash}") with
                {
                    Id = sampleId,
                };
            }
            else
            {
                definition = VisualizationAssetBuilder.CreateSyntheticSample(
                    sampleId,
                    "brr",
                    0,
                    loopMode: SampleLoopMode.Unknown,
                    displayName: $"BRR {entry.ShortHash}",
                    identityKind: AssetIdentityKind.ContentHash);
            }

            _timeline?.AddSample(definition);
            foreach (int source in entry.SourceNumbers ?? [])
                _sampleIdBySource[source] = sampleId;
        }
    }

    /// <summary>Diagnostic snapshot of one voice's current state.</summary>
    public SpcVoiceState GetVoiceState(int voice)
    {
        if (voice is < 0 or >= VoiceCount)
            throw new ArgumentOutOfRangeException(nameof(voice));
        SpcActiveNote? note = _notes[voice];
        return new SpcVoiceState(
            voice,
            note?.Active ?? false,
            note?.Releasing ?? false,
            note?.ReleaseStartSample,
            note?.SourceNumber ?? _latchedSources[voice],
            note?.InstrumentId ?? "",
            note?.InitialSemitones ?? 0,
            note?.Pitch.Count ?? 0);
    }

    private void OnKeyOn(in SpcSemanticEvent @event)
    {
        int voice = @event.Voice;
        bool retrigger = _notes[voice] != null || _noisePeriods[voice] != null;
        Close(voice, @event.SamplePosition);
        _voiceActive[voice] = true;

        // Native KEY_ON events carry an authoritative source number, including
        // SRCN 0. Synthetic events may explicitly omit it and use the last
        // latched source instead.
        int source = @event.HasSourceNumber ? @event.Value : _latchedSources[voice];
        _latchedSources[voice] = source;
        _timeline.AddSpcVoiceState(new SpcVoiceStateEvent(
            new VoiceId(_device.Id, VoiceKind.PcmVoice, voice).ToString(),
            @event.SamplePosition,
            nameof(SpcSemanticEventKind.SourceLatched),
            source,
            0));
        string instrument = $"spc:src{source}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "pcm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));

        // §24: while the voice uses noise rather than pitched BRR playback,
        // the S-DSP pitch register is not a meaningful musical pitch. A KON
        // in noise mode starts an unpitched noise period within this hardware
        // voice instead of a pitched note.
        if (_noiseEnabled[voice])
        {
            StartNoisePeriod(voice, @event.SamplePosition);
            return;
        }

        _notes[voice] = new SpcActiveNote
        {
            Active = true,
            Releasing = false,
            InitialCarryIn = false,
            StartSample = @event.SamplePosition,
            SourceNumber = source,
            InstrumentId = instrument,
            InitialSemitones = SoundingSemitones(source, @event.EffectivePitch),
            IsRetrigger = retrigger,
        };
    }

    /// <summary>
    /// §24: noise periods are unpitched. When noise turns on mid-voice, any
    /// active pitched note is split at the transition and the noise period is
    /// represented as an unpitched noise block within the same hardware voice.
    /// No fake pitch is derived for noise.
    /// </summary>
    private void OnNoiseChanged(in SpcSemanticEvent @event)
    {
        int voice = @event.Voice;
        bool enabled = @event.Value != 0;
        if (_noiseEnabled[voice] == enabled)
            return;

        if (enabled)
        {
            _noiseEnabled[voice] = true;
            bool sounding = _voiceActive[voice] || _notes[voice] != null;
            if (_notes[voice] != null)
            {
                // Split the pitched note at the noise transition.
                Close(voice, @event.SamplePosition);
            }
            if (sounding)
                StartNoisePeriod(voice, @event.SamplePosition);
        }
        else
        {
            _noiseEnabled[voice] = false;
            CloseNoisePeriod(voice, @event.SamplePosition);
        }
    }

    private void StartNoisePeriod(int voice, long sample)
    {
        if (_noisePeriods[voice] != null)
        {
            // A retrigger within an ongoing noise period closes it first so
            // each noise block stays bounded.
            CloseNoisePeriod(voice, sample);
        }
        _noisePeriods[voice] = new SpcNoisePeriod(sample);
    }

    private void CloseNoisePeriod(int voice, long endSample)
    {
        SpcNoisePeriod? period = _noisePeriods[voice];
        if (period == null)
            return;
        _noisePeriods[voice] = null;
        if (endSample > period.StartSample)
        {
            _timeline.AddNoiseState(new NoiseStateEvent(
                new VoiceId(_device.Id, VoiceKind.PcmVoice, voice).ToString(),
                period.StartSample,
                endSample,
                CentreFrequencyHz: null,
                Period: null,
                Level: 1.0f,
                Mode: NoiseMode.HardwareDefined));
        }
    }

    private void OnReleaseStart(in SpcSemanticEvent @event)
    {
        SpcActiveNote? note = _notes[@event.Voice];
        if (note == null || !note.Active)
            return;
        note.Releasing = true;
        note.ReleaseStartSample ??= @event.SamplePosition;
    }

    private void OnPitchChanged(in SpcSemanticEvent @event)
    {
        SpcActiveNote? note = _notes[@event.Voice];
        if (note == null || !note.Active)
            return;
        // The initial pitch is anchored at the note start; a pitch change must
        // be strictly later than the start and later than every recorded point
        // (out-of-order and equal-position duplicates are dropped).
        if (@event.SamplePosition <= note.StartSample)
            return;
        List<PitchChange> pitch = note.Pitch;
        if (pitch.Count > 0 && @event.SamplePosition <= pitch[^1].SamplePosition)
            return;
        double semitones = SoundingSemitones(note.SourceNumber, @event.EffectivePitch);
        double midiNote = A4Anchor + semitones;
        // §30: collapse adjacent redundant pitch writes with no effective
        // pitch change (including against the note's initial pitch); the
        // trajectory keeps the minimum representation.
        double lastMidi = pitch.Count > 0
            ? pitch[^1].MidiNote
            : A4Anchor + note.InitialSemitones;
        if (Math.Abs(lastMidi - midiNote) < 1e-9)
            return;
        pitch.Add(new PitchChange(@event.SamplePosition, FrequencyHz(semitones), midiNote));
    }

    private void Close(int voice, long endSample)
    {
        SpcActiveNote? note = _notes[voice];
        if (note != null && endSample > note.StartSample)
        {
            _timeline.AddNote(
                new VoiceId(_device.Id, VoiceKind.PcmVoice, voice),
                note.StartSample,
                endSample,
                A4Anchor + note.InitialSemitones,
                FrequencyHz(note.InitialSemitones),
                note.InstrumentId,
                VisualizationNoteMode.Pcm,
                note.IsRetrigger,
                note.Pitch.ToArray(),
                _sampleIdBySource.TryGetValue(note.SourceNumber, out string sampleId) ? sampleId : null);
        }
        _notes[voice] = null;
        CloseNoisePeriod(voice, endSample);
        _voiceActive[voice] = false;
    }

    /// <summary>
    /// S-DSP pitch is relative to the source sample: 0x1000 plays at the
    /// natural rate. §13.3 maps it to semitones relative to that rate:
    /// 12 * log2(effectivePitch / 0x1000). A pitch of zero (not reported)
    /// falls back to the natural rate.
    /// </summary>
    internal static double RelativeSemitones(ushort effectivePitch)
    {
        ushort pitch = effectivePitch == 0 ? SpcSemanticEvent.UnityPitch : effectivePitch;
        return 12.0 * Math.Log2(pitch / (double)SpcSemanticEvent.UnityPitch);
    }

    /// <summary>
    /// Semitones relative to the A4 anchor including the source's estimated
    /// root offset (§25.3): 0 = A4 440 Hz. Timeline midi = 69 + this value.
    /// </summary>
    private double SoundingSemitones(int source, ushort effectivePitch)
        => (_rootOffsetBySource.TryGetValue(source, out double offset) ? offset : 0)
           + RelativeSemitones(effectivePitch);

    /// <summary>
    /// Display-only 440 Hz reference for a pitch expressed in relative
    /// semitones (440 * 2^(st/12)). Keeps the semantic distinction that the
    /// S-DSP pitch is relative, not an absolute musical pitch.
    /// </summary>
    internal static double FrequencyHz(double relativeSemitones) =>
        440.0 * Math.Pow(2.0, relativeSemitones / 12.0);

    /// <summary>
    /// An open unpitched noise period on one S-DSP voice. The S-DSP pitch
    /// register is meaningless while the voice uses noise, so periods are
    /// represented as unpitched noise blocks within the hardware voice.
    /// </summary>
    private sealed record SpcNoisePeriod(long StartSample);
}
