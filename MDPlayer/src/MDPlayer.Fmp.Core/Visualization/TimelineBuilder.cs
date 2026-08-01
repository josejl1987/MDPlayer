namespace Fmp.Core.Visualization;

/// <summary>
/// Mutable preparation-time accumulator shared by chip and MIDI decoders.
/// It owns ordering and capability derivation; renderers consume the immutable
/// <see cref="VisualizationTimeline"/> returned by <see cref="Build"/>.
/// </summary>
internal sealed class TimelineBuilder
{
    private readonly Dictionary<DeviceId, DeviceDescriptor> _devices = [];
    private readonly Dictionary<VoiceId, VoiceDescriptor> _voices = [];
    private readonly List<NoteEvent> _notes = [];
    private readonly List<RhythmEvent> _rhythm = [];
    private readonly List<Ppz8Event> _ppz8 = [];
    private readonly List<AdpcmBEvent> _adpcmB = [];
    private readonly Dictionary<string, WaveformDefinition> _waveforms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SampleDefinition> _samples = new(StringComparer.Ordinal);
    private readonly List<WaveformChangeEvent> _waveformChanges = [];
    private readonly Dictionary<string, string> _lastWaveformByVoice = new(StringComparer.Ordinal);
    private readonly List<SamplePlaybackEvent> _samplePlayback = [];
    private readonly HashSet<SamplePlaybackEvent> _samplePlaybackSeen = [];
    private readonly List<SpcVoiceStateEvent> _spcVoiceStates = [];
    private readonly List<NoiseStateEvent> _noiseStates = [];
    private readonly HashSet<NoiseStateEvent> _noiseStatesSeen = [];
    private readonly List<AggregateHitEvent> _aggregateHits = [];
    private readonly HashSet<AggregateHitEvent> _aggregateHitsSeen = [];
    private readonly List<DriverTimingEvent> _timing = [];
    private readonly List<BeatEvent> _beats = [];
    private readonly List<LoopMarker> _loopMarkers = [];
    private readonly Dictionary<string, InstrumentDefinition> _instruments = new(StringComparer.Ordinal);
    private readonly List<string> _warnings = [];

    public TimelineBuilder(int sampleRate)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }
    public IReadOnlyCollection<DeviceDescriptor> Devices => _devices.Values;
    public IReadOnlyCollection<VoiceDescriptor> Voices => _voices.Values;
    public IReadOnlyList<string> Warnings => _warnings;

    public void AddDevice(DeviceDescriptor device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (_devices.TryGetValue(device.Id, out DeviceDescriptor existing))
        {
            _devices[device.Id] = existing with
            {
                DisplayName = string.IsNullOrWhiteSpace(device.DisplayName)
                    ? existing.DisplayName
                    : device.DisplayName,
                ClockHz = device.ClockHz > 0 ? device.ClockHz : existing.ClockHz,
                Capabilities = existing.Capabilities | device.Capabilities,
                ScopeSupport = (ScopeSupport)Math.Max(
                    (int)existing.ScopeSupport,
                    (int)device.ScopeSupport),
            };
            return;
        }
        _devices.Add(device.Id, device);
    }

    public void AddVoice(VoiceDescriptor voice)
    {
        ArgumentNullException.ThrowIfNull(voice);
        AddDevice(new DeviceDescriptor(
            voice.Id.Device,
            voice.Id.Device.ToString(),
            0,
            DeviceCapabilities.None));
        _voices[voice.Id] = voice;
    }

    public void AddNote(
        VoiceId voice,
        long startSample,
        long endSample,
        double initialMidiNote,
        double initialFrequencyHz,
        string instrumentId,
        VisualizationNoteMode mode,
        bool isRetrigger,
        IReadOnlyList<PitchChange> pitch,
        string sampleId = null)
    {
        if (startSample < 0 || endSample < startSample)
            throw new ArgumentOutOfRangeException(nameof(startSample));
        EnsureVoice(voice);
        if (voice.Kind == VoiceKind.Noise)
        {
            AddNoiseState(new NoiseStateEvent(
                voice.ToString(), startSample, endSample,
                initialFrequencyHz > 0 ? initialFrequencyHz : null,
                null, 1.0f, NoiseMode.HardwareDefined));
            return;
        }
        if (IsPcmKind(voice.Kind) && !HasTonalPitch(initialMidiNote, initialFrequencyHz, pitch))
        {
            AddGenericPcmPlayback(
                voice.ToString(), startSample, endSample, initialMidiNote, instrumentId, isRetrigger, sampleId);
            return;
        }
        _notes.Add(new NoteEvent(
            voice.ToString(),
            startSample,
            endSample,
            initialFrequencyHz,
            initialMidiNote,
            instrumentId ?? "",
            mode,
            isRetrigger,
            pitch ?? Array.Empty<PitchChange>()));
        AddGenericPcmPlayback(
            voice.ToString(), startSample, endSample, initialMidiNote, instrumentId, isRetrigger, sampleId);
    }

    public void AddNote(NoteEvent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        VoiceDescriptor voice = _voices.Values.FirstOrDefault(value =>
            string.Equals(value.Id.ToString(), note.ChannelId, StringComparison.Ordinal));
        if (voice?.Kind == VoiceKind.Noise)
        {
            AddNoiseState(new NoiseStateEvent(
                note.ChannelId, note.StartSample, note.EndSample,
                note.InitialFrequencyHz > 0 ? note.InitialFrequencyHz : null,
                null, 1.0f, NoiseMode.HardwareDefined));
            return;
        }
        if (voice != null && IsPcmKind(voice.Kind)
            && !HasTonalPitch(note.InitialMidiNote, note.InitialFrequencyHz, note.Pitch))
        {
            AddGenericPcmPlayback(
                note.ChannelId, note.StartSample, note.EndSample,
                note.InitialMidiNote, note.InstrumentId, note.IsRetrigger);
            return;
        }
        _notes.Add(note);
        if (voice != null)
        {
            AddGenericPcmPlayback(note.ChannelId, note.StartSample, note.EndSample,
                note.InitialMidiNote, note.InstrumentId, note.IsRetrigger);
        }
    }

    public void AddRhythm(RhythmEvent rhythm)
    {
        ArgumentNullException.ThrowIfNull(rhythm);
        _rhythm.Add(rhythm);
        VoiceDescriptor voice = _voices.Values.FirstOrDefault(value =>
            string.Equals(value.Id.ToString(), rhythm.ChannelId, StringComparison.Ordinal));
        if (voice?.Kind == VoiceKind.Noise)
        {
            long duration = Math.Max(1, (long)Math.Round(SampleRate * 0.080));
            AddNoiseState(new NoiseStateEvent(
                rhythm.ChannelId,
                rhythm.SamplePosition,
                rhythm.SamplePosition + duration,
                null,
                null,
                Math.Clamp(rhythm.Strength, 0, 1),
                NoiseMode.HardwareDefined));
        }
    }

    public void AddPpz8(Ppz8Event value, string sampleId = null, string voiceId = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ppz8.Add(value);
        sampleId ??= PpzSampleId(value.Bank, value.SampleNumber);
        if (!_samples.ContainsKey(sampleId))
        {
            AddSample(VisualizationAssetBuilder.CreateSyntheticSample(
                sampleId,
                "pcm",
                0,
                displayName: value.SampleNumber is int number ? $"SMP {number:X2}" : "SMP UNKNOWN"));
        }
        AddSamplePlayback(new SamplePlaybackEvent(
            voiceId ?? "ppz8.0", value.StartSample, value.EndSample, sampleId, value.MidiNote,
            1.0, Math.Clamp(value.Volume, 0, 1), Math.Clamp(value.Pan, -1, 1),
            value.IsRetrigger, false));
    }

    public void AddAdpcmB(AdpcmBEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _adpcmB.Add(value);
        string sampleId = $"sample:adpcm-b:{value.StartAddress:X6}-{value.EndAddress:X6}".ToLowerInvariant();
        AddSample(VisualizationAssetBuilder.CreateSyntheticSample(
            sampleId, "adpcm", Math.Max(0, value.EndAddress - value.StartAddress), displayName: "ADPCM-B"));
        AddSamplePlayback(new SamplePlaybackEvent(
            "ym2608.0.adpcm-b", value.StartSample, value.EndSample, sampleId,
            value.FrequencyHz is > 0 and double frequency
                ? 69 + 12 * Math.Log2(frequency / 440.0)
                : null,
            value.DeltaN > 0 ? value.DeltaN / 0x10000d : 1.0,
            Math.Clamp(value.Level, 0, 1), Math.Clamp(value.Pan, -1, 1),
            value.IsRetrigger, false));
    }

    public void AddWaveform(WaveformDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_waveforms.TryGetValue(value.Id, out WaveformDefinition existing))
        {
            if (!WaveformEquals(existing, value))
                throw new InvalidOperationException($"Waveform id '{value.Id}' has conflicting content.");
            return;
        }
        _waveforms.Add(value.Id, value);
    }

    public void AddSample(SampleDefinition value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (_samples.TryGetValue(value.Id, out SampleDefinition existing))
        {
            if (!SampleEquals(existing, value))
                throw new InvalidOperationException($"Sample id '{value.Id}' has conflicting content.");
            return;
        }
        _samples.Add(value.Id, value);
    }

    public void AddWaveformChange(WaveformChangeEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SamplePosition < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        if (_lastWaveformByVoice.TryGetValue(value.VoiceId, out string previous)
            && previous == value.WaveformId)
            return;
        _lastWaveformByVoice[value.VoiceId] = value.WaveformId;
        _waveformChanges.Add(value);
    }

    public void AddSamplePlayback(SamplePlaybackEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_samplePlaybackSeen.Add(value))
            return;
        _samplePlayback.Add(value);
    }

    public void AddSpcVoiceState(SpcVoiceStateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SamplePosition < 0)
            throw new ArgumentOutOfRangeException(nameof(value));
        _spcVoiceStates.Add(value);
    }

    public void AddNoiseState(NoiseStateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_noiseStatesSeen.Add(value))
            return;
        _noiseStates.Add(value);
    }

    public void AddAggregateHit(AggregateHitEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!_aggregateHitsSeen.Add(value))
            return;
        _aggregateHits.Add(value);
    }

    public void AddTiming(DriverTimingEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _timing.Add(value);
    }

    public void AddBeat(BeatEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _beats.Add(value);
    }

    public void AddLoopMarker(LoopMarker value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _loopMarkers.Add(value);
    }

    public void AddInstrument(InstrumentDefinition instrument)
    {
        ArgumentNullException.ThrowIfNull(instrument);
        _instruments[instrument.Id] = instrument;
    }

    public void AddWarning(string warning)
    {
        if (!string.IsNullOrWhiteSpace(warning)
            && !_warnings.Contains(warning, StringComparer.Ordinal))
            _warnings.Add(warning);
    }

    public void Merge(VisualizationTimeline timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        foreach (DeviceDescriptor device in timeline.Devices)
            AddDevice(device);
        foreach (VoiceDescriptor voice in timeline.Voices)
            AddVoice(voice);
        foreach (NoteEvent note in timeline.Notes)
            AddNote(note);
        foreach (RhythmEvent rhythm in timeline.Rhythm)
            AddRhythm(rhythm);
        foreach (Ppz8Event value in timeline.Ppz8)
            AddPpz8(value);
        foreach (AdpcmBEvent value in timeline.AdpcmB)
            AddAdpcmB(value);
        foreach (WaveformDefinition value in timeline.Waveforms)
            AddWaveform(value);
        foreach (SampleDefinition value in timeline.Samples)
            AddSample(value);
        foreach (WaveformChangeEvent value in timeline.WaveformChanges)
            AddWaveformChange(value);
        foreach (SamplePlaybackEvent value in timeline.SamplePlayback)
            AddSamplePlayback(value);
        foreach (SpcVoiceStateEvent value in timeline.SpcVoiceStates)
            AddSpcVoiceState(value);
        foreach (NoiseStateEvent value in timeline.NoiseStates)
            AddNoiseState(value);
        foreach (AggregateHitEvent value in timeline.AggregateHits)
            AddAggregateHit(value);
        foreach (DriverTimingEvent value in timeline.Timing)
            AddTiming(value);
        foreach (BeatEvent value in timeline.Beats)
            AddBeat(value);
        foreach (LoopMarker value in timeline.LoopMarkers)
            AddLoopMarker(value);
        foreach (InstrumentDefinition instrument in timeline.Instruments)
            AddInstrument(instrument);
        foreach (string warning in timeline.Warnings)
            AddWarning(warning);
    }

    public VisualizationTimeline Build(
        long endSample,
        string stopReason = "",
        TrackMetadata source = null)
    {
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));

        foreach (ChipType external in _devices.Values
            .Select(device => device.Id.Type)
            .Concat(_voices.Values.Select(voice => voice.Id.Device.Type))
            .Where(DeviceOrdering.IsExternal)
            .Distinct()
            .OrderBy(value => value.ToString(), StringComparer.Ordinal))
        {
            AddWarning($"chip type '{external}' has no built-in device priority; ordering it after all built-in devices");
        }

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        if (_notes.Count > 0)
            capabilities.Add("notes");
        if (_notes.Any(note => note.InitialMidiNote >= 0 || note.Pitch.Any(point => point.MidiNote >= 0)))
            capabilities.Add("continuousPitch");
        if (_instruments.Count > 0)
            capabilities.Add("instruments");
        if (_rhythm.Count > 0)
            capabilities.Add("percussion");
        if (_ppz8.Count > 0 || _adpcmB.Count > 0 || _samplePlayback.Count > 0)
            capabilities.Add("sampleEvents");
        if (_timing.Count > 0)
            capabilities.Add("driverTiming");
        if (_loopMarkers.Count > 0)
            capabilities.Add("loopMarkers");
        if (_devices.Values.Any(device => device.Capabilities.HasFlag(DeviceCapabilities.VoiceMasking)))
            capabilities.Add("channelScopes");
        if (_devices.Values.Any(device => device.ScopeSupport == ScopeSupport.Device))
            capabilities.Add("deviceScopes");
        if (_devices.Values.Any(device => device.ScopeSupport == ScopeSupport.Master))
            capabilities.Add("masterScope");

        return new VisualizationTimeline
        {
            SchemaVersion = 2,
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = endSample,
            Source = source,
            StopReason = stopReason ?? "",
            Devices = _devices.Values
                .OrderBy(device => DeviceOrdering.Priority(device.Id.Type))
                .ThenBy(device => device.Id.Instance)
                .ThenBy(device => device.Id.ToString(), StringComparer.Ordinal)
                .ToArray(),
            Voices = _voices.Values
                .OrderBy(voice => DeviceOrdering.Priority(voice.Id.Device.Type))
                .ThenBy(voice => voice.Id.Device.Instance)
                .ThenBy(voice => voice.Id.Device.ToString(), StringComparer.Ordinal)
                .ThenBy(voice => voice.Order)
                .ThenBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
                .ToArray(),
            Notes = _notes
                .Where(IsRenderableNote)
                .Select(NormalizeNote)
                .OrderBy(note => note.StartSample)
                .ThenBy(note => note.ChannelId, StringComparer.Ordinal)
                .ToArray(),
            Rhythm = _rhythm
                .OrderBy(evt => evt.SamplePosition)
                .ThenBy(evt => evt.ChannelId, StringComparer.Ordinal)
                .ToArray(),
            Ppz8 = _ppz8
                .Where(value => value.EndSample > value.StartSample)
                .OrderBy(value => value.StartSample)
                .ThenBy(value => value.Channel)
                .ToArray(),
            AdpcmB = _adpcmB
                .Where(value => value.EndSample > value.StartSample)
                .OrderBy(value => value.StartSample)
                .ToArray(),
            Waveforms = _waveforms.Values
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray(),
            Samples = _samples.Values
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .ToArray(),
            WaveformChanges = _waveformChanges
                .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
                .ThenBy(value => value.SamplePosition)
                .ThenBy(value => value.WaveformId, StringComparer.Ordinal)
                .ToArray(),
            SamplePlayback = _samplePlayback
                .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
                .ThenBy(value => value.StartSample)
                .ThenBy(value => value.SampleId, StringComparer.Ordinal)
                .ThenBy(value => value.EndSample)
                .ThenBy(value => value.PlaybackRate)
                .ThenBy(value => value.Gain)
                .ThenBy(value => value.Pan)
                .ThenBy(value => value.Retrigger)
                .ToArray(),
            SpcVoiceStates = _spcVoiceStates
                .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
                .ThenBy(value => value.SamplePosition)
                .ThenBy(value => value.State, StringComparer.Ordinal)
                .ToArray(),
            NoiseStates = _noiseStates
                .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
                .ThenBy(value => value.StartSample)
                .ThenBy(value => value.EndSample)
                .ThenBy(value => value.Mode)
                .ToArray(),
            AggregateHits = _aggregateHits
                .OrderBy(value => value.VoiceId, StringComparer.Ordinal)
                .ThenBy(value => value.SamplePosition)
                .ThenBy(value => value.SubVoiceId, StringComparer.Ordinal)
                .ThenBy(value => value.Label, StringComparer.Ordinal)
                .ThenBy(value => value.AssetId, StringComparer.Ordinal)
                .ToArray(),
            Timing = _timing
                .OrderBy(value => value.SamplePosition)
                .ToArray(),
            Beats = _beats
                .OrderBy(value => value.SamplePosition)
                .ToArray(),
            LoopMarkers = _loopMarkers
                .OrderBy(value => value.SamplePosition)
                .ThenBy(value => value.Iteration)
                .ToArray(),
            Instruments = _instruments.Values
                .OrderBy(instrument => instrument.Id, StringComparer.Ordinal)
                .ToArray(),
            Capabilities = capabilities.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Warnings = _warnings.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
        };
    }

    private void EnsureVoice(VoiceId voice)
    {
        if (_voices.ContainsKey(voice))
            return;

        AddVoice(new VoiceDescriptor(
            voice,
            voice.ToString(),
            VoicePresentationKind.Pitched,
            _voices.Count,
            voice.Kind == VoiceKind.Rhythm,
            voice.Kind == VoiceKind.Noise,
            voice.Kind != VoiceKind.Noise));
    }

    private static string PpzSampleId(int? bank, int? sample) =>
        $"sample:ppz8:{(bank is int b ? b.ToString("X4") : "bank")}:" +
        $"{(sample is int s ? s.ToString("X4") : "slot")}".ToLowerInvariant();

    private void AddGenericPcmPlayback(
        string voiceId,
        long startSample,
        long endSample,
        double midiPitch,
        string instrumentId,
        bool retrigger,
        string sampleId = null)
    {
        VoiceDescriptor voice = _voices.Values.FirstOrDefault(value =>
            string.Equals(value.Id.ToString(), voiceId, StringComparison.Ordinal));
        if (voice == null || !IsPcmKind(voice.Kind))
            return;
        if (string.IsNullOrWhiteSpace(sampleId))
        {
            string token = string.IsNullOrWhiteSpace(instrumentId) ? voiceId : instrumentId;
            sampleId = "sample:voice:" + StableToken(token);
            AddSample(VisualizationAssetBuilder.CreateSyntheticSample(
                sampleId,
                "pcm",
                0,
                displayName: token));
        }
        else if (!_samples.ContainsKey(sampleId))
        {
            AddSample(VisualizationAssetBuilder.CreateSyntheticSample(
                sampleId,
                "pcm",
                0,
                displayName: sampleId,
                identityKind: AssetIdentityKind.RuntimeHandle));
        }
        bool looping = _samples.TryGetValue(sampleId, out SampleDefinition sample)
            && sample.LoopMode is SampleLoopMode.Forward or SampleLoopMode.PingPong;
        AddSamplePlayback(new SamplePlaybackEvent(
            voiceId,
            startSample,
            endSample,
            sampleId,
            double.IsFinite(midiPitch) && midiPitch >= 0 ? midiPitch : null,
            1.0,
            1.0f,
            0,
            retrigger,
            looping));
    }

    private static bool IsPcmKind(VoiceKind kind) =>
        kind is VoiceKind.Adpcm or VoiceKind.Pcm or VoiceKind.PcmVoice or VoiceKind.Dpcm;

    private static bool HasTonalPitch(
        double initialMidiNote,
        double initialFrequencyHz,
        IReadOnlyList<PitchChange> pitch)
    {
        if ((double.IsFinite(initialMidiNote) && initialMidiNote >= 0)
            || (double.IsFinite(initialFrequencyHz) && initialFrequencyHz > 0))
            return true;
        return pitch?.Any(value =>
            (double.IsFinite(value.MidiNote) && value.MidiNote >= 0)
            || (double.IsFinite(value.FrequencyHz) && value.FrequencyHz > 0)) == true;
    }

    private static bool IsRenderableNote(NoteEvent note)
    {
        if (note.EndSample <= note.StartSample)
            return false;

        return note.Mode is VisualizationNoteMode.Pcm
            or VisualizationNoteMode.SsgNoise
            or VisualizationNoteMode.SsgEnvelopeNoise
            || double.IsFinite(note.InitialMidiNote)
            || (double.IsFinite(note.InitialFrequencyHz) && note.InitialFrequencyHz > 0);
    }

    private NoteEvent NormalizeNote(NoteEvent note)
    {
        VoiceDescriptor voice = _voices.Values.FirstOrDefault(value =>
            string.Equals(value.Id.ToString(), note.ChannelId, StringComparison.Ordinal));
        bool frequencyCoordinates = voice?.PitchSystem == PitchCoordinateSystem.FrequencyHz;
        double initialMidi = note.InitialMidiNote;
        if (frequencyCoordinates
            && PitchCoordinateConverter.TryConvertToMidi(
                PitchCoordinateSystem.FrequencyHz,
                note.InitialFrequencyHz,
                null,
                out double convertedInitial))
        {
            initialMidi = convertedInitial;
        }

        PitchChange[] pitch = (note.Pitch ?? Array.Empty<PitchChange>())
            .Where(value => value.SamplePosition >= note.StartSample
                && value.SamplePosition <= note.EndSample
                && ((double.IsFinite(value.MidiNote) && value.MidiNote >= 0)
                    || (double.IsFinite(value.FrequencyHz) && value.FrequencyHz > 0)))
            .OrderBy(value => value.SamplePosition)
            .GroupBy(value => value.SamplePosition)
            .Select(group => group.Last() with
            {
                FrequencyHz = double.IsFinite(group.Last().FrequencyHz)
                    ? group.Last().FrequencyHz
                    : 0,
                MidiNote = frequencyCoordinates
                    && PitchCoordinateConverter.TryConvertToMidi(
                        PitchCoordinateSystem.FrequencyHz,
                        group.Last().FrequencyHz,
                        null,
                        out double converted)
                    ? converted
                    : group.Last().MidiNote,
            })
            .ToArray();
        return note with
        {
            InitialMidiNote = initialMidi,
            InitialFrequencyHz = double.IsFinite(note.InitialFrequencyHz)
                ? note.InitialFrequencyHz
                : 0,
            InstrumentId = note.InstrumentId ?? "",
            Pitch = pitch,
        };
    }

    private static string StableToken(string value)
    {
        uint hash = 2166136261;
        for (int index = 0; index < value.Length; index++)
            hash = (hash ^ value[index]) * 16777619;
        return hash.ToString("X8", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant();
    }

    private static bool WaveformEquals(WaveformDefinition left, WaveformDefinition right) =>
        left.Family == right.Family
        && left.SourceLength == right.SourceLength
        && left.DisplayName == right.DisplayName
        && left.Preview.AsSpan().SequenceEqual(right.Preview);

    private static bool SampleEquals(SampleDefinition left, SampleDefinition right) =>
        left.Family == right.Family
        && left.SourceLengthSamples == right.SourceLengthSamples
        && left.NativeSampleRate == right.NativeSampleRate
        && left.LoopStart == right.LoopStart
        && left.LoopEnd == right.LoopEnd
        && left.LoopMode == right.LoopMode
        && left.DisplayName == right.DisplayName
        && left.IdentityKind == right.IdentityKind
        && left.Preview.Length == right.Preview.Length
        && left.Preview.Zip(right.Preview).All(pair =>
            pair.First.Minimum == pair.Second.Minimum
            && pair.First.Maximum == pair.Second.Maximum);

}
