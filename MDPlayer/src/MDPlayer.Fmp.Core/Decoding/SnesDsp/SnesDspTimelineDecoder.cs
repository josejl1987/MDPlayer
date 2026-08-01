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
    private const int VoiceCount = 8;

    private readonly SpcActiveNote?[] _notes = new SpcActiveNote?[VoiceCount];
    private readonly int[] _latchedSources = new int[VoiceCount];

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
            default:
                // Volume, envelope-mode, noise, pitch-mod, echo-send, sample
                // loop and global transitions do not change the note timeline.
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
        for (int voice = 0; voice < VoiceCount; voice++)
            Close(voice, endSample);
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
            note?.InitialRelativeSemitones ?? 0,
            note?.Pitch.Count ?? 0);
    }

    private void OnKeyOn(in SpcSemanticEvent @event)
    {
        int voice = @event.Voice;
        bool retrigger = _notes[voice] != null;
        Close(voice, @event.SamplePosition);

        // The native core only emits effective transitions, so KEY_ON is
        // authoritative. The source number comes from the event itself, or
        // falls back to the last latched source for the voice.
        int source = @event.Value > 0 ? @event.Value : _latchedSources[voice];
        string instrument = $"spc:src{source}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument, "pcm", null, null, null, null, Array.Empty<FmOperatorDefinition>()));

        _notes[voice] = new SpcActiveNote
        {
            Active = true,
            Releasing = false,
            InitialCarryIn = false,
            StartSample = @event.SamplePosition,
            SourceNumber = source,
            InstrumentId = instrument,
            InitialRelativeSemitones = RelativeSemitones(@event.EffectivePitch),
            IsRetrigger = retrigger,
        };
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
        double semitones = RelativeSemitones(@event.EffectivePitch);
        pitch.Add(new PitchChange(@event.SamplePosition, FrequencyHz(semitones), semitones));
    }

    private void Close(int voice, long endSample)
    {
        SpcActiveNote? note = _notes[voice];
        if (note == null)
            return;
        if (endSample > note.StartSample)
        {
            _timeline.AddNote(
                new VoiceId(_device.Id, VoiceKind.PcmVoice, voice),
                note.StartSample,
                endSample,
                note.InitialRelativeSemitones,
                FrequencyHz(note.InitialRelativeSemitones),
                note.InstrumentId,
                VisualizationNoteMode.Pcm,
                note.IsRetrigger,
                note.Pitch.ToArray());
        }
        _notes[voice] = null;
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
    /// Display-only 440 Hz reference for a pitch expressed in relative
    /// semitones (440 * 2^(st/12)). Keeps the semantic distinction that the
    /// S-DSP pitch is relative, not an absolute musical pitch.
    /// </summary>
    internal static double FrequencyHz(double relativeSemitones) =>
        440.0 * Math.Pow(2.0, relativeSemitones / 12.0);
}
