using System.Buffers.Binary;
using System.IO.Compression;
using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

internal sealed record S98RegisterWrite(
    long SourceSync,
    DeviceId Device,
    int Port,
    int Address,
    int Data);

internal sealed class S98Document
{
    private S98Document(
        int timerNumerator,
        int timerDenominator,
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<S98RegisterWrite> writes,
        long endSync,
        long? loopSync,
        IReadOnlyList<string> warnings)
    {
        TimerNumerator = timerNumerator;
        TimerDenominator = timerDenominator;
        Devices = devices;
        Writes = writes;
        EndSync = endSync;
        LoopSync = loopSync;
        Warnings = warnings;
    }

    public int TimerNumerator { get; }
    public int TimerDenominator { get; }
    public IReadOnlyList<DeviceDescriptor> Devices { get; }
    public IReadOnlyList<S98RegisterWrite> Writes { get; }
    public long EndSync { get; }
    public long? LoopSync { get; }
    public IReadOnlyList<string> Warnings { get; }

    public static S98Document Parse(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        if (data.Length < 0x20 || data[0] != (byte)'S' || data[1] != (byte)'9' || data[2] != (byte)'8')
            throw new S98PlaybackException("input is not a valid S98 stream");
        if (data[3] is not ((byte)'0' or (byte)'1' or (byte)'2' or (byte)'3'))
            throw new S98PlaybackException($"S98 version '{(char)data[3]}' is not supported");

        int timerNumerator = Read32(data, 0x04);
        int timerDenominator = Read32(data, 0x08);
        if (timerNumerator == 0) timerNumerator = 10;
        if (timerDenominator == 0) timerDenominator = 1000;
        int tagOffset = Read32(data, 0x10);
        int dataOffset = Read32(data, 0x14);
        int loopOffset = Read32(data, 0x18);
        int compressedSize = data[3] == (byte)'3' ? 0 : Read32(data, 0x0C);

        var devices = new List<DeviceDescriptor>();
        var commandDevices = new List<DeviceId>();
        if (data[3] == (byte)'3')
        {
            int deviceCount = Read32(data, 0x1C);
            if (deviceCount > 64)
                throw new S98PlaybackException("S98 device count exceeds the format limit");

            if (deviceCount == 0)
            {
                AddDefaultDevice(devices, commandDevices);
                if (dataOffset == 0)
                    dataOffset = 0x20;
            }
            else
            {
                int deviceTable = 0x20;
                Require(data, deviceTable, checked(deviceCount * 0x10), "S98 device table");
                var typeInstances = new Dictionary<ChipType, int>();
                for (int index = 0; index < deviceCount; index++)
                    AddDeviceInfo(data, deviceTable + index * 0x10, devices, commandDevices, typeInstances);
                if (dataOffset == 0)
                    dataOffset = deviceTable + deviceCount * 0x10;
            }
        }
        else if (data[3] == (byte)'2')
        {
            // S98 v2 has the same 16-byte device records as v3, terminated by
            // a zero type instead of carrying a count in the header.
            int deviceCursor = 0x20;
            var typeInstances = new Dictionary<ChipType, int>();
            while (deviceCursor + 0x10 <= data.Length
                && (dataOffset == 0 || deviceCursor < dataOffset))
            {
                if (Read32(data, deviceCursor) == 0)
                    break;
                AddDeviceInfo(data, deviceCursor, devices, commandDevices, typeInstances);
                deviceCursor += 0x10;
            }
            if (devices.Count == 0)
                AddDefaultDevice(devices, commandDevices);
            if (dataOffset == 0)
                dataOffset = deviceCursor;
        }
        else
        {
            // v0/v1 have no device table and are OPNA-compatible by design.
            AddDefaultDevice(devices, commandDevices);
        }

        if (dataOffset <= 0 || dataOffset > data.Length)
            throw new S98PlaybackException("S98 dump data offset is outside the file");

        byte[] expandedDump = null;
        if (compressedSize != 0)
        {
            int compressedOffset = Read32(data, 0x1C);
            if (compressedOffset == 0)
                compressedOffset = dataOffset;
            Require(data, compressedOffset, compressedSize, "S98 compressed dump data");
            try
            {
                using var compressed = new MemoryStream(data
                    .Slice(compressedOffset, compressedSize)
                    .ToArray());
                using var inflater = new DeflateStream(compressed, CompressionMode.Decompress);
                using var output = new MemoryStream();
                inflater.CopyTo(output);
                expandedDump = output.ToArray();
            }
            catch (InvalidDataException ex)
            {
                throw new S98PlaybackException($"S98 compressed dump data is invalid: {ex.Message}");
            }
        }

        ReadOnlySpan<byte> dump = expandedDump ?? data[dataOffset..];
        int loopCursor = loopOffset == 0
            ? -1
            : Math.Max(0, loopOffset - dataOffset);
        var writes = new List<S98RegisterWrite>();
        var warnings = new List<string>();
        long sync = 0;
        long? loopSync = null;
        int cursor = 0;
        bool ended = false;
        while (cursor < dump.Length)
        {
            if (cursor == loopCursor)
                loopSync = sync;
            byte command = dump[cursor++];
            if (command == 0xFF)
            {
                sync++;
                continue;
            }
            if (command == 0xFE)
            {
                long syncValue = ReadVariableSync(dump, ref cursor);
                sync += syncValue;
                continue;
            }
            if (command == 0xFD)
            {
                ended = true;
                break;
            }

            int deviceIndex = command / 2;
            int port = command & 1;
            if (deviceIndex < 0 || deviceIndex >= commandDevices.Count)
            {
                warnings.Add($"S98 write references device slot {deviceIndex}");
                break;
            }
            Require(dump, cursor, 2, "S98 register write");
            int address = dump[cursor++];
            int registerValue = dump[cursor++];
            writes.Add(new S98RegisterWrite(
                sync,
                commandDevices[deviceIndex],
                port,
                address,
                registerValue));
        }

        if (!ended)
            warnings.Add("S98 stream reached EOF without an explicit end command");
        if (tagOffset != 0 && tagOffset >= data.Length)
            warnings.Add("S98 tag offset points outside the file");

        return new S98Document(
            timerNumerator,
            timerDenominator,
            devices,
            writes,
            Math.Max(sync, writes.Count == 0 ? 0 : writes[^1].SourceSync),
            loopSync.HasValue && loopSync.Value >= 0 && loopSync.Value < sync ? loopSync : null,
            warnings);
    }

    public long ToSamples(long sync, int sampleRate) => checked((long)Math.Round(
        sync * (double)TimerNumerator / TimerDenominator * sampleRate,
        MidpointRounding.AwayFromZero));

    private static DeviceDescriptor CreateDevice(ChipType type, int instance, long clock) => type switch
    {
        ChipType.Ym2608 => VisualizationDeviceCatalog.Ym2608(instance) with { ClockHz = clock },
        ChipType.Ym2203 => VisualizationDeviceCatalog.Ym2203(instance, clock),
        ChipType.Ym2413 => VisualizationDeviceCatalog.Ym2413(instance, clock),
        ChipType.Ym3526 => VisualizationDeviceCatalog.Ym3526(instance, clock),
        ChipType.Ym3812 => VisualizationDeviceCatalog.Ym3812(instance, clock),
        ChipType.Ymf262 => VisualizationDeviceCatalog.Ymf262(instance, clock),
        ChipType.Ym2612 => VisualizationDeviceCatalog.Ym2612(instance, clock),
        ChipType.Sn76489 => VisualizationDeviceCatalog.Sn76489(instance, clock),
        ChipType.Ay8910 => VisualizationDeviceCatalog.Ay8910(instance, clock),
        _ => new DeviceDescriptor(new DeviceId(type, instance), $"{type} #{instance}", clock, DeviceCapabilities.None),
    };

    private static void AddDefaultDevice(
        List<DeviceDescriptor> devices,
        List<DeviceId> commandDevices)
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608(0);
        devices.Add(device);
        commandDevices.Add(device.Id);
    }

    private static void AddDeviceInfo(
        ReadOnlySpan<byte> data,
        int offset,
        List<DeviceDescriptor> devices,
        List<DeviceId> commandDevices,
        Dictionary<ChipType, int> typeInstances)
    {
        int typeCode = Read32(data, offset);
        long clock = (uint)Read32(data, offset + 4);
        ChipType type = MapType(typeCode);
        int instance = typeInstances.TryGetValue(type, out int previous) ? previous : 0;
        typeInstances[type] = instance + 1;
        DeviceDescriptor device = CreateDevice(type, instance, clock);
        devices.Add(device);
        commandDevices.Add(device.Id);
    }

    private static ChipType MapType(int type) => type switch
    {
        1 => ChipType.Ay8910,
        2 => ChipType.Ym2203,
        3 => ChipType.Ym2612,
        4 => ChipType.Ym2608,
        5 => ChipType.Ym2151,
        6 => ChipType.Ym2413,
        7 => ChipType.Ym3526,
        8 => ChipType.Ym3812,
        9 => ChipType.Ymf262,
        15 => ChipType.Ay8910,
        16 => ChipType.Sn76489,
        _ => ChipType.Unknown,
    };

    private static long ReadVariableSync(ReadOnlySpan<byte> data, ref int cursor)
    {
        long value = 0;
        int shift = 0;
        while (true)
        {
            if (cursor >= data.Length || shift > 56)
                throw new S98PlaybackException("S98 variable sync value is truncated");
            byte current = data[cursor++];
            value |= (long)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return value + 2;
            shift += 7;
        }
    }

    private static int Read32(ReadOnlySpan<byte> data, int offset)
    {
        Require(data, offset, 4, "S98 header");
        return checked((int)BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]));
    }

    private static void Require(ReadOnlySpan<byte> data, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new S98PlaybackException($"{context} is truncated");
    }
}

internal sealed class S98PlaybackBackend : IPlaybackBackend
{
    public string Id => "s98";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        if (!input.Exists)
            return new PlaybackProbeResult(false, "s98", [], [], [$"input not found: {input.FullName}"]);
        try
        {
            S98Document document = S98Document.Parse(File.ReadAllBytes(input.FullName));
            ChipTimelineDecoderRegistry decoderRegistry = ChipTimelineDecoderRegistry.CreateDefault();
            string[] unsupported = document.Devices
                .Where(device => !decoderRegistry.HasDecoder(device.Id.Type))
                .Select(device => $"{device.Id}: no offline audio path is registered")
                .ToArray();
            bool visualizable = document.Devices.Any(device =>
                decoderRegistry.HasDecoder(device.Id.Type));
            bool audioSupported = document.Devices.Count(device => device.Id.Type == ChipType.Ym2608) <= 1;
            if (!audioSupported)
                unsupported = [.. unsupported, "multiple YM2608 devices are not supported by the portable S98 audio path"];
            // An unsupported device must not hide decodable devices in a
            // mixed track. The capture path preserves the full register stream
            // and renders supported devices while the timeline reports the
            // unsupported ones as warnings.
            // Keep a syntactically valid S98 selectable even when it has no
            // decoded note device, so the CLI can return the dedicated
            // no-visualizable-devices status instead of a generic backend
            // rejection. A device mix that the audio path cannot instantiate
            // remains rejected with its concrete warning.
            bool supported = document.Devices.Count > 0 && audioSupported;
            return new PlaybackProbeResult(
                supported,
                "s98",
                [],
                [],
                document.Warnings.Concat(unsupported).ToArray())
            {
                Portable = true,
                Visualizable = visualizable,
                Availability = PlaybackAvailability.Available,
            };
        }
        catch (Exception ex) when (ex is IOException or S98PlaybackException)
        {
            return new PlaybackProbeResult(false, "s98", [], [], [ex.Message]);
        }
    }

    public IPlaybackCaptureSession Open(FileInfo input, PlaybackOptions options, IPlaybackEventSink eventSink) =>
        new S98CaptureSession(S98Document.Parse(File.ReadAllBytes(input.FullName)), options, eventSink);
}

internal sealed class S98CaptureSession : IPlaybackCaptureSession
{
    private readonly S98Document _document;
    private readonly PlaybackOptions _options;
    private readonly IPlaybackEventSink _events;
    private bool _stopped;

    public S98CaptureSession(S98Document document, PlaybackOptions options, IPlaybackEventSink events)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        Timing = new CaptureTimingInfo(_options.SampleRate, 1).Validate();
    }

    public CaptureTimingInfo Timing { get; }
    public IReadOnlyList<DeviceDescriptor> Devices => _document.Devices;
    public long SamplePosition { get; private set; }
    public bool IsComplete { get; private set; }

    public void Run(CancellationToken cancellationToken = default)
    {
        foreach (DeviceDescriptor device in _document.Devices)
            _events.OnDevice(device);
        if (_events is TimelineDecoderEventSink timelineSink)
        {
            foreach (string warning in _document.Warnings)
                timelineSink.ReportWarning(warning);
        }

        using var audio = new S98AudioRenderer(_document.Devices, Timing.SampleRate);
        WavWriter writer = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.OutputAudioPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(
                    Path.GetFullPath(_options.OutputAudioPath)) ?? ".");
                writer = new WavWriter(_options.OutputAudioPath, Timing.SampleRate, 2);
            }

            long sourceEnd = _document.ToSamples(_document.EndSync, Timing.SampleRate);
            long loopStart = _document.LoopSync.HasValue
                ? _document.ToSamples(_document.LoopSync.Value, Timing.SampleRate)
                : 0;
            long loopBody = _document.LoopSync.HasValue ? sourceEnd - loopStart : 0;
            long repeatedEnd = _document.LoopSync.HasValue
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

            for (int pass = 0; pass < (_document.LoopSync.HasValue ? _options.LoopCount : 1)
                && !_stopped; pass++)
            {
                long offset = pass == 0 || !_document.LoopSync.HasValue
                    ? 0
                    : loopBody * pass;
                if (pass > 0)
                    _events.OnLoopBoundary(new TimedLoopBoundary(loopStart + offset, pass));
                foreach (S98RegisterWrite source in _document.Writes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long sourceSample = _document.ToSamples(source.SourceSync, Timing.SampleRate);
                    if (pass > 0 && _document.LoopSync.HasValue && sourceSample < loopStart)
                        continue;
                    long target = sourceSample + offset;
                    if (target >= baseEnd)
                        break;
                    RenderUntil(audio, writer, ref rendered, target, fadeStart, baseEnd);
                    var write = new TimedChipWrite(target, source.Device, source.Port, source.Address, source.Data);
                    _events.OnChipWrite(write);
                    audio.Write(write);
                    SamplePosition = target;
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

    private static void RenderUntil(
        S98AudioRenderer audio,
        WavWriter writer,
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd)
    {
        if (target < rendered)
            throw new InvalidOperationException("S98 events are not monotonic after loop expansion.");
        while (rendered < target)
        {
            int count = (int)Math.Min(1024, target - rendered);
            short[] pcm = audio.Render(count, rendered, fadeStart, baseEnd);
            writer?.Write(pcm);
            rendered += count;
        }
    }
}

internal sealed class S98AudioRenderer : IDisposable
{
    private readonly VgmAudioRenderer _registerAudio;
    private readonly MdsoundFmpChipSink _opna;
    private readonly int _sampleRate;

    public S98AudioRenderer(IReadOnlyList<DeviceDescriptor> devices, int sampleRate)
    {
        _sampleRate = sampleRate;
        if (devices.Any(device => device.Id.Type is ChipType.Ym2151 or ChipType.Ym2203 or ChipType.Ym2413 or ChipType.Ym3526 or ChipType.Ym3812 or ChipType.Ymf262 or ChipType.Ym2612 or ChipType.Sn76489 or ChipType.Ay8910))
            _registerAudio = new VgmAudioRenderer(devices, sampleRate);
        if (devices.Any(device => device.Id.Type == ChipType.Ym2608))
        {
            if (devices.Count(device => device.Id.Type == ChipType.Ym2608) > 1)
                throw new S98PlaybackException("multiple YM2608 devices are not supported by the portable S98 audio path");
            _opna = new MdsoundFmpChipSink(sampleRate);
            _opna.Start();
        }
    }

    public void Write(in TimedChipWrite write)
    {
        if (write.Device.Type == ChipType.Ym2608)
            _opna?.WriteYm2608(write.Device.Instance, write.Port, write.Address, write.Data, write.SamplePosition);
        else
            _registerAudio?.Write(write);
    }

    public short[] Render(int samples, long startSample, long fadeStart, long baseEnd)
    {
        short[] register = _registerAudio?.Render(samples) ?? new short[samples * 2];
        if (_opna == null)
            return ApplyFade(register, startSample, fadeStart, baseEnd);

        int[][] outputs = [new int[samples], new int[samples]];
        _opna.Render(outputs, samples);
        for (int index = 0; index < samples; index++)
        {
            register[index * 2] = (short)Math.Clamp(register[index * 2] + outputs[0][index], short.MinValue, short.MaxValue);
            register[index * 2 + 1] = (short)Math.Clamp(register[index * 2 + 1] + outputs[1][index], short.MinValue, short.MaxValue);
        }
        return ApplyFade(register, startSample, fadeStart, baseEnd);
    }

    public void Dispose()
    {
        _registerAudio?.Dispose();
        _opna?.Dispose();
    }

    private static short[] ApplyFade(short[] pcm, long startSample, long fadeStart, long baseEnd)
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
        return pcm;
    }
}

internal sealed class S98PlaybackException : Exception
{
    public S98PlaybackException(string message) : base(message) { }
}
