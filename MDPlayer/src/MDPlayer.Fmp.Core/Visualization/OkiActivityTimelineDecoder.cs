namespace Fmp.Core.Visualization;

/// <summary>
/// Converts OKI playback commands into synchronized activity events. These
/// chips expose sample activity and channel state, but not a validated musical
/// pitch in the keyboard monitor, so this decoder intentionally emits activity
/// lanes instead of fabricated piano-roll notes.
/// </summary>
internal sealed class OkiActivityTimelineDecoder : IChipTimelineDecoder
{
    private readonly ChipType _chipType;
    private readonly bool[] _active;
    private readonly MutableActivity?[] _current;
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private int? _pendingSample;
    private bool _completed;

    public OkiActivityTimelineDecoder(ChipType chipType)
    {
        if (chipType is not (ChipType.Okim6258 or ChipType.Okim6295))
            throw new ArgumentOutOfRangeException(nameof(chipType));
        _chipType = chipType;
        _active = new bool[chipType == ChipType.Okim6295 ? 4 : 1];
        _current = new MutableActivity?[_active.Length];
    }

    public ChipType ChipType => _chipType;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != _chipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        _timeline.AddDevice(device);
        IReadOnlyList<VoiceDescriptor> voices = _chipType == ChipType.Okim6295
            ? VisualizationDeviceCatalog.Okim6295Voices(device.Id.Instance)
            : VisualizationDeviceCatalog.Okim6258Voices(device.Id.Instance);
        foreach (VoiceDescriptor voice in voices)
            _timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id)
            return;

        if (_chipType == ChipType.Okim6258)
        {
            if (write.Address != 0)
                return;
            bool active = (write.Data & 0x02) != 0;
            if (active && !_active[0])
                Emit(0, "stream", write.SamplePosition, 1.0f);
            _active[0] = active;
            return;
        }

        if (write.Address != 0)
            return;
        if ((write.Data & 0x80) != 0)
        {
            _pendingSample = write.Data & 0x7F;
            return;
        }
        if (!_pendingSample.HasValue)
            return;

        int channelMask = (write.Data >> 4) & 0x0F;
        float strength = 1.0f - (write.Data & 0x0F) / 15.0f;
        for (int channel = 0; channel < _active.Length; channel++)
        {
            if ((channelMask & (1 << channel)) == 0)
                continue;
            Emit(channel, $"sample:{_pendingSample.Value:X2}", write.SamplePosition, strength);
            _active[channel] = true;
        }
        _pendingSample = null;
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int channel = 0; channel < _current.Length; channel++)
            Close(channel, endSample);
    }

    private void Emit(int channel, string voice, long sample, float strength)
    {
        VoiceId voiceId = new(_device.Id, VoiceKind.Pcm, channel);
        Close(channel, sample);
        string sampleId = $"sample:oki:{_chipType.ToString().ToLowerInvariant()}:{voice}";
        _timeline.AddSample(VisualizationAssetBuilder.CreateSyntheticSample(
            sampleId, "adpcm", 0, displayName: voice));
        _timeline.AddRhythm(new RhythmEvent(
            voice,
            voiceId.ToString(),
            sample,
            Math.Clamp(strength, 0.05f, 1.0f),
            0.5f));
        _current[channel] = new MutableActivity(sample, voiceId.ToString(), sampleId, strength);
    }

    private void Close(int channel, long endSample)
    {
        MutableActivity? current = _current[channel];
        if (current == null)
            return;
        if (endSample > current.StartSample)
            _timeline.AddSamplePlayback(new SamplePlaybackEvent(
                current.VoiceId,
                current.StartSample,
                endSample,
                current.SampleId,
                null,
                1.0,
                Math.Clamp(current.Strength, 0.05f, 1.0f),
                0,
                false,
                false));
        _current[channel] = null;
    }

    private sealed record MutableActivity(long StartSample, string VoiceId, string SampleId, float Strength);
}
