using System.Buffers.Binary;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

internal abstract record XgmOperation;

internal sealed record XgmRegisterOperation(
    DeviceId Device,
    int Port,
    int Address,
    int Data) : XgmOperation;

internal sealed record XgmPcmOperation(
    int Channel,
    int Priority,
    int SampleId) : XgmOperation;

internal sealed record XgmFrame(
    int Address,
    IReadOnlyList<XgmOperation> Operations);

internal sealed class XgmDocument
{
    private readonly byte[] _sampleData;
    private readonly int[] _sampleOffsets;
    private readonly int[] _sampleSizes;

    private XgmDocument(
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<XgmFrame> frames,
        int frameRate,
        int loopFrameIndex,
        byte[] sampleData,
        int[] sampleOffsets,
        int[] sampleSizes,
        IReadOnlyList<string> warnings)
    {
        Devices = devices;
        Frames = frames;
        FrameRate = frameRate;
        LoopFrameIndex = loopFrameIndex;
        _sampleData = sampleData;
        _sampleOffsets = sampleOffsets;
        _sampleSizes = sampleSizes;
        Warnings = warnings;
    }

    public IReadOnlyList<DeviceDescriptor> Devices { get; }
    public IReadOnlyList<XgmFrame> Frames { get; }
    public int FrameRate { get; }
    public int LoopFrameIndex { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static XgmDocument Parse(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        if (data.Length < 0x108
            || BinaryPrimitives.ReadUInt32LittleEndian(data) != 0x204D4758)
            throw new XgmPlaybackException("input is not a valid XGM stream");

        int sampleBlockSize = checked(ReadUInt16(data, 0x100) * 0x100);
        int sampleBlockStart = 0x104;
        Require(data, sampleBlockStart, sampleBlockSize, "XGM sample data block");

        var sampleOffsets = new int[63];
        var sampleSizes = new int[63];
        for (int index = 0; index < 63; index++)
        {
            int tableOffset = 4 + index * 4;
            sampleOffsets[index] = checked(ReadUInt16(data, tableOffset) * 0x100);
            sampleSizes[index] = checked(ReadUInt16(data, tableOffset + 2) * 0x100);
            if (sampleOffsets[index] > sampleBlockSize
                || sampleSizes[index] > sampleBlockSize - sampleOffsets[index])
                throw new XgmPlaybackException($"XGM sample {index + 1} exceeds the sample data block");
        }

        int musicHeader = checked(sampleBlockStart + sampleBlockSize);
        Require(data, musicHeader, 4, "XGM music data header");
        uint encodedMusicSize = ReadUInt32(data, musicHeader);
        if (encodedMusicSize > int.MaxValue)
            throw new XgmPlaybackException("XGM music data block is too large");
        int musicStart = musicHeader + 4;
        int musicSize = checked((int)encodedMusicSize);
        Require(data, musicStart, musicSize, "XGM music data block");
        int musicEnd = musicStart + musicSize;
        bool ntsc = (data[0x103] & 0x01) == 0;

        var frames = new List<XgmFrame>();
        var warnings = new List<string>();
        int? loopAddress = null;
        bool ended = false;
        int cursor = musicStart;
        while (cursor < musicEnd)
        {
            int frameAddress = cursor;
            var operations = new List<XgmOperation>();
            bool frameEnded = false;
            bool looped = false;
            while (cursor < musicEnd)
            {
                byte command = data[cursor++];
                if (command == 0)
                {
                    frameEnded = true;
                    break;
                }
                if (command == 0x7F)
                {
                    ended = true;
                    break;
                }
                if (command == 0x7E)
                {
                    Require(data, cursor, 3, "XGM loop command");
                    loopAddress = ReadUInt24(data, cursor);
                    cursor += 3;
                    looped = true;
                    break;
                }

                int kind = command & 0xF0;
                int count = (command & 0x0F) + 1;
                switch (kind)
                {
                    case 0x10:
                        Require(data, cursor, count, "XGM PSG command");
                        for (int index = 0; index < count; index++)
                        {
                            operations.Add(new XgmRegisterOperation(
                                new DeviceId(ChipType.Sn76489, 0),
                                0,
                                0,
                                data[cursor++]));
                        }
                        break;
                    case 0x20:
                    case 0x30:
                        Require(data, cursor, checked(count * 2), "XGM YM2612 command");
                        int port = kind == 0x20 ? 0 : 1;
                        for (int index = 0; index < count; index++)
                        {
                            int address = data[cursor++];
                            int value = data[cursor++];
                            operations.Add(new XgmRegisterOperation(
                                new DeviceId(ChipType.Ym2612, 0),
                                port,
                                address,
                                value));
                        }
                        break;
                    case 0x40:
                        Require(data, cursor, count, "XGM YM2612 key command");
                        for (int index = 0; index < count; index++)
                        {
                            operations.Add(new XgmRegisterOperation(
                                new DeviceId(ChipType.Ym2612, 0),
                                0,
                                0x28,
                                data[cursor++]));
                        }
                        break;
                    case 0x50:
                        Require(data, cursor, count, "XGM PCM command");
                        int pcmChannel = command & 0x03;
                        int pcmPriority = command & 0x0C;
                        for (int index = 0; index < count; index++)
                        {
                            int sampleId = data[cursor++];
                            operations.Add(new XgmPcmOperation(
                                pcmChannel,
                                pcmPriority,
                                sampleId));
                        }
                        break;
                    default:
                        throw new XgmPlaybackException(
                            $"XGM command 0x{command:X2} is not supported by the portable backend");
                }
            }

            if (operations.Count > 0 || frameEnded || looped)
                frames.Add(new XgmFrame(frameAddress, operations));
            if (ended || looped)
                break;
            if (!frameEnded)
                warnings.Add("XGM music data ended without a frame wait marker");
        }

        if (!ended && !loopAddress.HasValue)
            warnings.Add("XGM stream reached the end of its music block without an explicit end command");

        int loopFrameIndex = -1;
        if (loopAddress.HasValue)
        {
            loopFrameIndex = frames
                .Select((frame, index) => (frame, index))
                .Where(value => value.frame.Address == loopAddress.Value)
                .Select(value => value.index)
                .DefaultIfEmpty(-1)
                .First();
            if (loopFrameIndex < 0)
                throw new XgmPlaybackException(
                    $"XGM loop address 0x{loopAddress.Value:X6} does not point to a frame");
        }

        return new XgmDocument(
            [
                VisualizationDeviceCatalog.Ym2612(),
                VisualizationDeviceCatalog.Sn76489(),
            ],
            frames,
            ntsc ? 60 : 50,
            loopFrameIndex,
            data.Slice(sampleBlockStart, sampleBlockSize).ToArray(),
            sampleOffsets,
            sampleSizes,
            warnings);
    }

    public ReadOnlyMemory<byte> GetSample(int sampleId)
    {
        if (sampleId <= 0 || sampleId > _sampleOffsets.Length)
            return ReadOnlyMemory<byte>.Empty;
        int index = sampleId - 1;
        return _sampleData.AsMemory(_sampleOffsets[index], _sampleSizes[index]);
    }

    private static int ReadUInt16(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 2, "XGM header");
        return BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "XGM header");
        return BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
    }

    private static int ReadUInt24(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 3, "XGM address");
        return data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16);
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new XgmPlaybackException($"{context} is truncated");
    }
}

internal sealed class XgmPlaybackBackend : IPlaybackBackend
{
    public string Id => "xgm";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Exists)
            return new PlaybackProbeResult(false, "xgm", [], [], [$"input not found: {input.FullName}"]);
        try
        {
            XgmDocument document = XgmDocument.Parse(File.ReadAllBytes(input.FullName));
            ChipTimelineDecoderRegistry decoderRegistry = ChipTimelineDecoderRegistry.CreateDefault();
            return new PlaybackProbeResult(true, "xgm", [], [], document.Warnings)
            {
                Portable = true,
                Visualizable = document.Devices.Any(device =>
                    decoderRegistry.HasDecoder(device.Id.Type)),
                Availability = PlaybackAvailability.Available,
            };
        }
        catch (Exception ex) when (ex is IOException or XgmPlaybackException)
        {
            return new PlaybackProbeResult(false, "xgm", [], [], [ex.Message]);
        }
    }

    public IPlaybackCaptureSession Open(
        FileInfo input,
        PlaybackOptions options,
        IPlaybackEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);
        return new XgmCaptureSession(
            XgmDocument.Parse(File.ReadAllBytes(input.FullName)),
            options,
            eventSink);
    }
}

internal sealed class XgmCaptureSession : IPlaybackCaptureSession
{
    private readonly XgmDocument _document;
    private readonly PlaybackOptions _options;
    private readonly IPlaybackEventSink _events;
    private readonly XgmPcmState[] _pcm = [new(), new(), new(), new()];
    private bool _dacEnabled;
    private bool _stopped;

    public XgmCaptureSession(XgmDocument document, PlaybackOptions options, IPlaybackEventSink events)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        if (_options.LoopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.LoopCount));
        Timing = new CaptureTimingInfo(_options.SampleRate, 1).Validate();
    }

    public CaptureTimingInfo Timing { get; }
    public IReadOnlyList<DeviceDescriptor> Devices => _document.Devices;
    public long SamplePosition { get; private set; }
    public bool IsComplete { get; private set; }

    public void Run(CancellationToken cancellationToken = default)
    {
        if (IsComplete)
            throw new InvalidOperationException("The XGM capture session has already completed.");

        foreach (DeviceDescriptor device in _document.Devices)
            _events.OnDevice(device);
        if (_events is TimelineDecoderEventSink timelineSink)
        {
            foreach (string warning in _document.Warnings)
                timelineSink.ReportWarning(warning);
        }

        using var audio = new VgmAudioRenderer(_document.Devices, Timing.SampleRate);
        WavWriter writer = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.OutputAudioPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(
                    Path.GetFullPath(_options.OutputAudioPath)) ?? ".");
                writer = new WavWriter(_options.OutputAudioPath, Timing.SampleRate, 2);
            }

            int frameCount = _document.Frames.Count;
            long sourceEnd = ToSamples(frameCount);
            long loopStart = _document.LoopFrameIndex >= 0
                ? ToSamples(_document.LoopFrameIndex)
                : 0;
            long loopBody = _document.LoopFrameIndex >= 0
                ? sourceEnd - loopStart
                : 0;
            long repeatedEnd = _document.LoopFrameIndex >= 0
                ? sourceEnd + loopBody * Math.Max(0, _options.LoopCount - 1)
                : sourceEnd;
            long maxSamples = _options.MaxDurationSeconds.HasValue
                ? Math.Max(1, (long)Math.Round(_options.MaxDurationSeconds.Value * Timing.SampleRate))
                : long.MaxValue;
            long baseEnd = Math.Min(repeatedEnd, maxSamples);
            long tail = Math.Max(0, (long)Math.Round(_options.TailSeconds * Timing.SampleRate));
            long end = Math.Min(maxSamples, checked(baseEnd + tail));
            long fade = Math.Max(0, (long)Math.Round(_options.FadeSeconds * Timing.SampleRate));
            long fadeStart = Math.Max(0, baseEnd - fade);
            long rendered = 0;
            double nextPcmSample = 0;
            double pcmStep = Timing.SampleRate / 14_000.0;
            int passCount = _document.LoopFrameIndex >= 0 ? _options.LoopCount : 1;

            for (int pass = 0; pass < passCount && !_stopped; pass++)
            {
                int firstFrame = pass == 0 || _document.LoopFrameIndex < 0
                    ? 0
                    : _document.LoopFrameIndex;
                long offset = pass == 0 || _document.LoopFrameIndex < 0
                    ? 0
                    : sourceEnd + loopBody * (pass - 1) - loopStart;
                if (pass > 0)
                    _events.OnLoopBoundary(new TimedLoopBoundary(sourceEnd + loopBody * (pass - 1), pass));

                for (int frameIndex = firstFrame; frameIndex < frameCount; frameIndex++)
                {
                    long frameStart = offset + ToSamples(frameIndex);
                    long frameEnd = offset + ToSamples(frameIndex + 1);
                    if (frameStart >= baseEnd)
                        break;
                    frameEnd = Math.Min(frameEnd, baseEnd);
                    RenderUntil(audio, writer, ref rendered, frameStart, fadeStart, baseEnd);

                    foreach (XgmOperation operation in _document.Frames[frameIndex].Operations)
                        Apply(operation, frameStart, audio);

                    while (nextPcmSample < frameEnd && nextPcmSample < baseEnd)
                    {
                        long target = Math.Max(rendered,
                            (long)Math.Round(nextPcmSample, MidpointRounding.AwayFromZero));
                        if (_dacEnabled)
                        {
                            RenderUntil(audio, writer, ref rendered, target, fadeStart, baseEnd);
                            byte sample = NextPcmSample();
                            var write = new TimedChipWrite(
                                target,
                                new DeviceId(ChipType.Ym2612, 0),
                                0,
                                0x2A,
                                sample);
                            _events.OnChipWrite(write);
                            audio.Write(write);
                        }
                        nextPcmSample += pcmStep;
                    }

                    RenderUntil(audio, writer, ref rendered, frameEnd, fadeStart, baseEnd);
                }
            }

            RenderUntil(audio, writer, ref rendered, end, fadeStart, baseEnd);
            SamplePosition = rendered;
            IsComplete = true;
        }
        finally
        {
            writer?.Close();
        }
    }

    public void Stop() => _stopped = true;
    public void Dispose() => Stop();

    private long ToSamples(int frame) => checked((long)Math.Round(
        frame * (double)Timing.SampleRate / _document.FrameRate,
        MidpointRounding.AwayFromZero));

    private void Apply(XgmOperation operation, long sample, VgmAudioRenderer audio)
    {
        switch (operation)
        {
            case XgmRegisterOperation register:
                if (register.Device.Type == ChipType.Ym2612 && register.Address == 0x2B)
                    _dacEnabled = (register.Data & 0x80) != 0;
                var write = new TimedChipWrite(
                    sample,
                    register.Device,
                    register.Port,
                    register.Address,
                    register.Data);
                _events.OnChipWrite(write);
                audio.Write(write);
                break;
            case XgmPcmOperation pcm:
                if (pcm.Channel is < 0 or >= 4)
                    return;
                XgmPcmState state = _pcm[pcm.Channel];
                ReadOnlyMemory<byte> data = _document.GetSample(pcm.SampleId);
                if (pcm.SampleId == 0 || data.IsEmpty)
                {
                    state.Stop();
                    return;
                }
                if (state.Priority <= pcm.Priority || !state.Active)
                {
                    state.Start(pcm.Priority, data);
                    _events.OnSampleAsset(new TimedSampleAssetEvent(
                        sample,
                        new DeviceId(ChipType.Ym2612, 0),
                        $"xgm:sample:{pcm.SampleId}",
                        AssetKind.Pcm,
                        data.Length));
                }
                break;
        }
    }

    private byte NextPcmSample()
    {
        int mixed = 0;
        foreach (XgmPcmState state in _pcm)
        {
            if (!state.Active)
                continue;
            mixed += state.Next();
        }
        return (byte)(Math.Clamp(mixed, -127, 127) + 0x80);
    }

    private static void RenderUntil(
        VgmAudioRenderer audio,
        WavWriter writer,
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd)
    {
        if (target < rendered)
            throw new InvalidOperationException("XGM events are not monotonic after loop expansion.");
        while (rendered < target)
        {
            int count = (int)Math.Min(1024, target - rendered);
            short[] pcm = audio.Render(count);
            ApplyFade(pcm, rendered, fadeStart, baseEnd);
            writer?.Write(pcm);
            rendered += count;
        }
    }

    private static void ApplyFade(short[] pcm, long startSample, long fadeStart, long baseEnd)
    {
        for (int index = 0; index < pcm.Length / 2; index++)
        {
            long absolute = startSample + index;
            double gain = absolute >= baseEnd
                ? 0
                : absolute <= fadeStart || fadeStart >= baseEnd
                    ? 1
                    : (baseEnd - absolute) / (double)(baseEnd - fadeStart);
            pcm[index * 2] = (short)Math.Clamp(pcm[index * 2] * gain, short.MinValue, short.MaxValue);
            pcm[index * 2 + 1] = (short)Math.Clamp(pcm[index * 2 + 1] * gain, short.MinValue, short.MaxValue);
        }
    }

    private sealed class XgmPcmState
    {
        private ReadOnlyMemory<byte> _data;
        private int _position;

        public int Priority { get; private set; }
        public bool Active => !_data.IsEmpty && _position < _data.Length;

        public void Start(int priority, ReadOnlyMemory<byte> data)
        {
            Priority = priority;
            _data = data;
            _position = 0;
        }

        public void Stop()
        {
            Priority = 0;
            _data = ReadOnlyMemory<byte>.Empty;
            _position = 0;
        }

        public int Next()
        {
            if (!Active)
                return 0;
            int value = unchecked((sbyte)_data.Span[_position++]);
            if (_position >= _data.Length)
                Stop();
            return value;
        }
    }
}

internal sealed class XgmPlaybackException : Exception
{
    public XgmPlaybackException(string message) : base(message) { }
}
