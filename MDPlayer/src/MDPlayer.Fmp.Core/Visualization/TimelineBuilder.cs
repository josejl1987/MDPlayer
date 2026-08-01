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
    private readonly List<DriverTimingEvent> _timing = [];
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
        IReadOnlyList<PitchChange> pitch)
    {
        if (startSample < 0 || endSample < startSample)
            throw new ArgumentOutOfRangeException(nameof(startSample));
        EnsureVoice(voice);
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
    }

    public void AddNote(NoteEvent note)
    {
        ArgumentNullException.ThrowIfNull(note);
        _notes.Add(note);
    }

    public void AddRhythm(RhythmEvent rhythm)
    {
        ArgumentNullException.ThrowIfNull(rhythm);
        _rhythm.Add(rhythm);
    }

    public void AddPpz8(Ppz8Event value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _ppz8.Add(value);
    }

    public void AddAdpcmB(AdpcmBEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _adpcmB.Add(value);
    }

    public void AddTiming(DriverTimingEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _timing.Add(value);
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
        foreach (DriverTimingEvent value in timeline.Timing)
            AddTiming(value);
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

        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        if (_notes.Count > 0)
            capabilities.Add("notes");
        if (_notes.Any(note => note.InitialMidiNote >= 0 || note.Pitch.Any(point => point.MidiNote >= 0)))
            capabilities.Add("continuousPitch");
        if (_instruments.Count > 0)
            capabilities.Add("instruments");
        if (_rhythm.Count > 0)
            capabilities.Add("percussion");
        if (_ppz8.Count > 0 || _adpcmB.Count > 0)
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
                .OrderBy(device => DevicePriority(device.Id.Type))
                .ThenBy(device => device.Id.Instance)
                .ToArray(),
            Voices = _voices.Values
                .OrderBy(voice => DevicePriority(voice.Id.Device.Type))
                .ThenBy(voice => voice.Order)
                .ThenBy(voice => voice.Id.ToString(), StringComparer.Ordinal)
                .ToArray(),
            Notes = _notes
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
            Timing = _timing
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
            Warnings = _warnings.ToArray(),
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

    private static int DevicePriority(ChipType type) => type switch
    {
        ChipType.Ym2203 or ChipType.Ym2413 or ChipType.Ym2608 or ChipType.Ym2610 or ChipType.Ym2612 or ChipType.Ym2151
            or ChipType.Ym3526 or ChipType.Ym3812 or ChipType.Ymf262 or ChipType.Ymf278b or ChipType.Ymz280b
            or ChipType.Y8950 => 0,
        ChipType.Sn76489 or ChipType.Ay8910 or ChipType.Dmg or ChipType.NesApu or ChipType.Huc6280 or ChipType.K051649 => 10,
        ChipType.Okim6258 or ChipType.Okim6295 or ChipType.MultiPcm
            or ChipType.SegaPcm or ChipType.Rf5c68 or ChipType.Rf5c164
            or ChipType.C140 or ChipType.C352 or ChipType.K054539 or ChipType.Ga20 => 30,
        ChipType.Midi => 20,
        ChipType.Ppz8 or ChipType.Pcm => 30,
        _ => 100,
    };
}
