namespace Fmp.Core.Visualization;

using Fmp.Core.Decoding.SnesDsp;

internal sealed class ChipTimelineDecoderRegistry
{
    private readonly Dictionary<ChipType, Func<IChipTimelineDecoder>> _factories = [];
    private readonly Dictionary<ChipType, Func<IMidiTimelineDecoder>> _midiFactories = [];

    public void Register(ChipType chipType, Func<IChipTimelineDecoder> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories[chipType] = factory;
    }

    public bool TryCreate(ChipType chipType, out IChipTimelineDecoder decoder)
    {
        if (_factories.TryGetValue(chipType, out Func<IChipTimelineDecoder> factory))
        {
            decoder = factory();
            return true;
        }

        decoder = null;
        return false;
    }

    public bool HasDecoder(ChipType chipType) =>
        chipType == ChipType.Midi
            ? _midiFactories.ContainsKey(chipType)
            : _factories.ContainsKey(chipType);

    public void RegisterMidi(ChipType chipType, Func<IMidiTimelineDecoder> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _midiFactories[chipType] = factory;
    }

    public bool TryCreateMidi(ChipType chipType, out IMidiTimelineDecoder decoder)
    {
        if (_midiFactories.TryGetValue(chipType, out Func<IMidiTimelineDecoder> factory))
        {
            decoder = factory();
            return true;
        }

        decoder = null;
        return false;
    }

    public static ChipTimelineDecoderRegistry CreateDefault()
    {
        var registry = new ChipTimelineDecoderRegistry();
        registry.Register(ChipType.Ym2608, static () => new Ym2608TimelineDecoderAdapter());
        registry.Register(ChipType.Ym2203, static () => new Ym2203TimelineDecoder());
        registry.Register(ChipType.Ym2610, static () => new Ym2610TimelineDecoder());
        registry.Register(ChipType.Ym2413, static () => new Ym2413TimelineDecoder());
        registry.Register(ChipType.Ym3526, static () => new OplTimelineDecoder(ChipType.Ym3526, 9));
        registry.Register(ChipType.Ym3812, static () => new OplTimelineDecoder(ChipType.Ym3812, 9));
        registry.Register(ChipType.Y8950, static () => new OplTimelineDecoder(ChipType.Y8950, 9));
        registry.Register(ChipType.Ymf262, static () => new OplTimelineDecoder(ChipType.Ymf262, 18));
        registry.Register(ChipType.Ymf278b, static () => new Ymf278bTimelineDecoder());
        registry.Register(ChipType.Ymz280b, static () => new Ymz280bTimelineDecoder());
        registry.Register(ChipType.SegaPcm, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.SegaPcm));
        registry.Register(ChipType.Rf5c68, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.Rf5c68));
        registry.Register(ChipType.Rf5c164, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.Rf5c164));
        registry.Register(ChipType.C140, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.C140));
        registry.Register(ChipType.C352, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.C352));
        registry.Register(ChipType.K054539, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.K054539));
        registry.Register(ChipType.Ga20, static () => new PcmChipTimelineDecoder(PcmChipTimelineDecoder.Profile.Ga20));
        registry.Register(ChipType.Okim6258, static () => new OkiActivityTimelineDecoder(ChipType.Okim6258));
        registry.Register(ChipType.Okim6295, static () => new OkiActivityTimelineDecoder(ChipType.Okim6295));
        registry.Register(ChipType.MultiPcm, static () => new MultiPcmTimelineDecoder());
        registry.Register(ChipType.Ym2612, static () => new Ym2612TimelineDecoder());
        registry.Register(ChipType.Ym2151, static () => new Ym2151TimelineDecoder());
        registry.Register(ChipType.Sn76489, static () => new Sn76489TimelineDecoder());
        registry.Register(ChipType.Ay8910, static () => new Ay8910TimelineDecoder());
        registry.Register(ChipType.Dmg, static () => new DmgTimelineDecoder());
        registry.Register(ChipType.NesApu, static () => new NesApuTimelineDecoder());
        registry.Register(ChipType.Huc6280, static () => new Huc6280TimelineDecoder());
        registry.Register(ChipType.K051649, static () => new K051649TimelineDecoder());
        registry.Register(ChipType.Ppz8, static () => new Ppz8TimelineDecoder());
        registry.Register(ChipType.SnesDsp, static () => new Fmp.Core.Decoding.SnesDsp.SnesDspTimelineDecoder());
        registry.RegisterMidi(ChipType.Midi, static () => new MidiTimelineDecoder());
        return registry;
    }
}

/// <summary>
/// Compatibility adapter while the mature FMP decoder is extracted. This
/// keeps its existing parity-tested implementation as the single source of
/// YM2608 note semantics and exposes it through the generic decoder contract.
/// </summary>
internal sealed class Ym2608TimelineDecoderAdapter : IChipTimelineDecoder
{
    private Ym2608TimelineDecoder _decoder;
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;

    public ChipType ChipType => ChipType.Ym2608;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));
        if (device.Id.Instance != 0)
            throw new NotSupportedException("The current YM2608 decoder only supports instance 0.");

        _device = device;
        _timeline = timeline;
        _decoder = new Ym2608TimelineDecoder();
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.Ym2608Voices(device.Id.Instance))
            timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_decoder == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id)
            return;
        _decoder.ApplyYm2608(
            write.Device.Instance,
            write.Port,
            write.Address,
            write.Data,
            write.SamplePosition);
    }

    public void Complete(long endSample)
    {
        if (_decoder == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        _timeline.Merge(_decoder.Complete(endSample, _timeline.SampleRate, "decoder"));
    }
}

/// <summary>
/// Routes normalized playback events to the decoder selected by device type.
/// Unsupported devices remain in the timeline and become actionable warnings.
/// </summary>
internal sealed class TimelineDecoderEventSink : IPlaybackEventSink
{
    private readonly ChipTimelineDecoderRegistry _registry;
    private readonly TimelineBuilder _timeline;
    private readonly Dictionary<DeviceId, IChipTimelineDecoder> _decoders = [];
    private readonly Dictionary<DeviceId, IMidiTimelineDecoder> _midiDecoders = [];
    private readonly Dictionary<DeviceId, DeviceDescriptor> _devices = [];

    public TimelineDecoderEventSink(int sampleRate, ChipTimelineDecoderRegistry registry = null)
    {
        _timeline = new TimelineBuilder(sampleRate);
        _registry = registry ?? ChipTimelineDecoderRegistry.CreateDefault();
    }

    public TimelineBuilder Timeline => _timeline;
    public IReadOnlyDictionary<DeviceId, IChipTimelineDecoder> Decoders => _decoders;

    public void ReportWarning(string warning) => _timeline.AddWarning(warning);

    public void OnDevice(in DeviceDescriptor device)
    {
        _devices[device.Id] = device;
        _timeline.AddDevice(device);

        if (_decoders.ContainsKey(device.Id) || _midiDecoders.ContainsKey(device.Id))
            return;

        if (device.Id.Type == ChipType.Midi)
        {
            if (!_registry.TryCreateMidi(device.Id.Type, out IMidiTimelineDecoder midiDecoder))
            {
                _timeline.AddWarning($"{device.Id}: no registered MIDI decoder");
                return;
            }

            midiDecoder.Initialize(device, _timeline);
            _midiDecoders.Add(device.Id, midiDecoder);
            return;
        }

        if (!_registry.TryCreate(device.Id.Type, out IChipTimelineDecoder decoder))
        {
            _timeline.AddWarning($"{device.Id}: no registered note decoder");
            return;
        }

        try
        {
            decoder.Initialize(device, _timeline);
            _decoders.Add(device.Id, decoder);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException)
        {
            _timeline.AddWarning($"{device.Id}: decoder unavailable ({ex.Message})");
        }
    }

    public void OnChipWrite(in TimedChipWrite write)
    {
        write.Validate();
        if (!_devices.ContainsKey(write.Device))
        {
            OnDevice(new DeviceDescriptor(
                write.Device,
                write.Device.ToString(),
                0,
                DeviceCapabilities.None));
        }

        if (_decoders.TryGetValue(write.Device, out IChipTimelineDecoder decoder))
            decoder.Process(write);
    }

    public void OnMidi(in TimedMidiMessage message)
    {
        message.Validate();
        if (!_devices.ContainsKey(message.Device))
        {
            OnDevice(VisualizationDeviceCatalog.Midi(message.Device.Instance));
        }

        if (_midiDecoders.TryGetValue(message.Device, out IMidiTimelineDecoder decoder))
            decoder.Process(message);
        else
            _timeline.AddWarning($"{message.Device}: MIDI decoder not registered");
    }

    public void OnSampleAsset(in TimedSampleAssetEvent asset)
    {
        if (string.IsNullOrWhiteSpace(asset.AssetId))
            _timeline.AddWarning($"{asset.Device}: sample asset event has no stable asset id");

        if (_decoders.TryGetValue(asset.Device, out IChipTimelineDecoder decoder)
            && decoder is Ppz8TimelineDecoder ppz8)
            ppz8.ObserveBank(asset.AssetId);
    }

    public void OnLoopBoundary(in TimedLoopBoundary loop)
    {
        if (loop.SamplePosition < 0 || loop.LoopIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(loop));
        _timeline.AddLoopMarker(new LoopMarker(
            loop.SamplePosition,
            loop.LoopIndex == 0 ? LoopMarkerKind.Start : LoopMarkerKind.Restart,
            loop.LoopIndex));
    }

    /// <summary>
    /// Routes native S-DSP semantic events to the SPC timeline decoder (§9.2).
    /// Non-SPC decoders never receive these; the default interface method is
    /// a no-op, so only this sink actually forwards them.
    /// </summary>
    public void OnSpcEvent(in SpcSemanticEvent @event)
    {
        foreach (KeyValuePair<DeviceId, IChipTimelineDecoder> kv in _decoders)
        {
            if (kv.Value is SnesDspTimelineDecoder spc)
            {
                spc.Process(in @event);
                return;
            }
        }
    }

    public VisualizationTimeline Complete(
        long endSample,
        string stopReason = "",
        TrackMetadata source = null)
    {
        foreach (IChipTimelineDecoder decoder in _decoders.Values)
            decoder.Complete(endSample);
        foreach (IMidiTimelineDecoder decoder in _midiDecoders.Values)
            decoder.Complete(endSample);
        return _timeline.Build(endSample, stopReason, source);
    }
}
