using Fmp.Core.Audio;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

/// <summary>
/// Deterministic headless MXDRV/MDX subset. The parser consumes the native
/// MDX track format and emits YM2151 register events; the visualization layer
/// therefore remains independent of MDX commands. Deterministic repeat blocks
/// are expanded with a safety limit; commands that would need the full MXDRV
/// state machine (LFO, PCM, sync and portamento) are rejected by the probe
/// instead of being silently approximated.
/// </summary>
internal sealed class MdxPlaybackBackend : IPlaybackBackend
{
    public string Id => "mdx";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(environment);
        if (!input.Exists)
            return new PlaybackProbeResult(false, "mdx", [], [], [$"input not found: {input.FullName}"]);

        try
        {
            MdxDocument document = MdxDocument.Parse(File.ReadAllBytes(input.FullName));
            if (!string.IsNullOrWhiteSpace(document.PdxName))
                return PdxUnavailable(input, environment, document.PdxName);

            MdxCompilation compilation = document.Compile(environment.SampleRate, loopCount: 2);
            if (!compilation.Supported)
                return new PlaybackProbeResult(false, "mdx", [], [], compilation.Warnings);

            return new PlaybackProbeResult(true, "mdx", [], [], compilation.Warnings)
            {
                Visualizable = compilation.Events.Any(evt => evt.Address == 0x08),
                Portable = true,
                Availability = PlaybackAvailability.Available,
            };
        }
        catch (Exception ex) when (ex is IOException or MdxPlaybackException)
        {
            return new PlaybackProbeResult(false, "mdx", [], [], [ex.Message]);
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
        MdxDocument document = MdxDocument.Parse(File.ReadAllBytes(input.FullName));
        if (!string.IsNullOrWhiteSpace(document.PdxName))
            throw new MdxPlaybackException("MDX PDX/PCM playback is not available in the portable backend");
        MdxCompilation compilation = document.Compile(options.SampleRate, options.LoopCount);
        if (!compilation.Supported)
            throw new MdxPlaybackException(compilation.Warnings[^1]);
        return new MdxCaptureSession(document, compilation, options, eventSink);
    }

    private static PlaybackProbeResult PdxUnavailable(
        FileInfo input,
        PlaybackEnvironment environment,
        string pdxName)
    {
        string[] searchNames = [pdxName, Path.GetFileName(pdxName)];
        bool found = (new[] { input.DirectoryName ?? "." }
            .Concat(environment.SearchPaths ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(path => searchNames.Select(name => Path.Combine(path, name)))
            .Any(File.Exists));
        string assetWarning = found
            ? $"PDX asset '{pdxName}' was found, but PCM8 rendering is not yet linked"
            : $"required PDX asset is missing: {pdxName}";
        var asset = new RequiredAsset(pdxName, AssetKind.Pcm, true, searchNames);
        return new PlaybackProbeResult(
            false,
            "mdx",
            [asset],
            found ? [] : [pdxName],
            [assetWarning, "MDX PCM/PDX playback is not available in the portable backend"])
        {
            Portable = false,
            Availability = found
                ? PlaybackAvailability.PlatformSpecific
                : PlaybackAvailability.AssetMissing,
        };
    }
}

internal sealed class MdxCaptureSession : IPlaybackCaptureSession
{
    private readonly MdxDocument _document;
    private readonly MdxCompilation _compilation;
    private readonly PlaybackOptions _options;
    private readonly IPlaybackEventSink _events;
    private bool _stopped;

    public MdxCaptureSession(
        MdxDocument document,
        MdxCompilation compilation,
        PlaybackOptions options,
        IPlaybackEventSink events)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        Timing = new CaptureTimingInfo(_options.SampleRate, 1).Validate();
    }

    public CaptureTimingInfo Timing { get; }
    public IReadOnlyList<DeviceDescriptor> Devices => [VisualizationDeviceCatalog.Ym2151()];
    public long SamplePosition { get; private set; }
    public bool IsComplete { get; private set; }

    public void Run(CancellationToken cancellationToken = default)
    {
        foreach (DeviceDescriptor device in Devices)
            _events.OnDevice(device);
        if (_events is TimelineDecoderEventSink timelineSink)
        {
            foreach (string warning in _compilation.Warnings)
                timelineSink.ReportWarning(warning);
        }

        using var audio = new VgmAudioRenderer(Devices, Timing.SampleRate);
        WavWriter writer = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.OutputAudioPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(
                    Path.GetFullPath(_options.OutputAudioPath)) ?? ".");
                writer = new WavWriter(_options.OutputAudioPath, Timing.SampleRate, 2);
            }

            long baseEnd = Math.Max(1, _compilation.EndSample);
            long tail = Math.Max(0, (long)Math.Round(
                _options.TailSeconds * Timing.SampleRate));
            long end = checked(baseEnd + tail);
            long fade = Math.Max(0, (long)Math.Round(
                _options.FadeSeconds * Timing.SampleRate));
            long fadeStart = Math.Max(0, baseEnd - fade);
            long rendered = 0;

            foreach (MdxTimedWrite source in _compilation.Events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_stopped)
                    break;
                RenderUntil(audio, writer, ref rendered, source.SamplePosition, fadeStart, baseEnd);
                var write = new TimedChipWrite(
                    source.SamplePosition,
                    new DeviceId(ChipType.Ym2151, 0),
                    0,
                    source.Address,
                    source.Data);
                _events.OnChipWrite(write);
                audio.Write(write);
                SamplePosition = source.SamplePosition;
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
        VgmAudioRenderer audio,
        WavWriter writer,
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd)
    {
        if (target < rendered)
            throw new InvalidOperationException("MDX events are not monotonic after compilation.");
        while (rendered < target)
        {
            int count = (int)Math.Min(1024, target - rendered);
            short[] pcm = audio.Render(count);
            ApplyFade(pcm, rendered, fadeStart, baseEnd);
            writer?.Write(pcm);
            rendered += count;
        }
    }

    private static void ApplyFade(short[] pcm, long start, long fadeStart, long baseEnd)
    {
        for (int index = 0; index < pcm.Length / 2; index++)
        {
            long sample = start + index;
            double gain = sample >= baseEnd
                ? 0
                : sample <= fadeStart || fadeStart >= baseEnd
                    ? 1
                    : (baseEnd - sample) / (double)(baseEnd - fadeStart);
            pcm[index * 2] = (short)Math.Clamp(pcm[index * 2] * gain, short.MinValue, short.MaxValue);
            pcm[index * 2 + 1] = (short)Math.Clamp(pcm[index * 2 + 1] * gain, short.MinValue, short.MaxValue);
        }
    }
}

internal sealed record MdxTimedWrite(long SamplePosition, int Address, int Data, int Order);

internal sealed record MdxCompilation(
    bool Supported,
    long EndSample,
    IReadOnlyList<MdxTimedWrite> Events,
    IReadOnlyList<string> Warnings);

internal sealed record MdxVoice(byte Id, byte[] Data);

internal sealed record MdxTrack(int Channel, byte[] Data);

internal sealed class MdxDocument
{
    private static readonly byte[] KeyCodes =
    [
        0x00, 0x01, 0x02, 0x04, 0x05, 0x06, 0x08, 0x09,
        0x0a, 0x0c, 0x0d, 0x0e,
    ];

    private MdxDocument(
        string title,
        string pdxName,
        IReadOnlyDictionary<byte, MdxVoice> voices,
        IReadOnlyList<MdxTrack> tracks)
    {
        Title = title;
        PdxName = pdxName;
        Voices = voices;
        Tracks = tracks;
    }

    public string Title { get; }
    public string PdxName { get; }
    public IReadOnlyDictionary<byte, MdxVoice> Voices { get; }
    public IReadOnlyList<MdxTrack> Tracks { get; }

    public static MdxDocument Parse(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        MdxHeader header = MdxHeader.Parse(data);
        int offsetStart = header.DataOffset;
        if (offsetStart + 2 > data.Length)
            throw new MdxPlaybackException("MDX offset table is truncated");

        var offsets = new List<int>();
        for (int index = 0; index < 17 && offsetStart + index * 2 + 2 <= data.Length; index++)
            offsets.Add(ReadWord(data, offsetStart + index * 2));

        int minOffset = int.MaxValue;
        for (int index = 0; index < Math.Min(10, offsets.Count); index++)
        {
            int offset = offsets[index];
            if (offset > 0 && offsetStart + offset < data.Length)
                minOffset = Math.Min(minOffset, offset);
        }
        if (minOffset == int.MaxValue || minOffset < 2)
            throw new MdxPlaybackException("MDX has no valid track offset table");

        int trackCount = Math.Clamp((minOffset - 2) / 2, 1, 16);
        if (offsets.Count < trackCount + 1)
            throw new MdxPlaybackException("MDX track offset table is truncated");

        int dataLength = data.Length;
        var boundaries = offsets
            .Take(trackCount + 1)
            .Where(offset => offset > 0 && offsetStart + offset <= dataLength)
            .Distinct()
            .ToArray();
        if (offsets[0] <= 0 || offsetStart + offsets[0] > data.Length)
            throw new MdxPlaybackException("MDX voice data offset is outside the file");

        int voiceStart = offsetStart + offsets[0];
        int voiceEnd = FindChunkEnd(offsetStart, data.Length, voiceStart, boundaries);
        var voices = new Dictionary<byte, MdxVoice>();
        for (int position = voiceStart; position + 27 <= voiceEnd; position += 27)
        {
            byte[] voiceData = data.Slice(position, 27).ToArray();
            voices[voiceData[0]] = new MdxVoice(voiceData[0], voiceData);
        }

        var tracks = new List<MdxTrack>();
        for (int channel = 0; channel < trackCount; channel++)
        {
            int relativeStart = offsets[channel + 1];
            if (relativeStart <= 0 || offsetStart + relativeStart >= data.Length)
                continue;
            int trackStart = offsetStart + relativeStart;
            int trackEnd = FindChunkEnd(offsetStart, data.Length, trackStart, boundaries);
            if (trackEnd <= trackStart)
                continue;
            tracks.Add(new MdxTrack(channel, data.Slice(trackStart, trackEnd - trackStart).ToArray()));
        }

        return new MdxDocument(header.Title, header.PdxName, voices, tracks);
    }

    public MdxCompilation Compile(int sampleRate, int loopCount = 2)
    {
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (loopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(loopCount));
        var events = new List<MdxTimedWrite>();
        var warnings = new List<string>();
        var order = new OrderCounter();
        double maxTime = 0;

        foreach (MdxTrack track in Tracks)
        {
            MdxTrackCompiler compiler = new(track, Voices, sampleRate, loopCount, events, warnings, order);
            if (!compiler.Compile(out double endTime, out string error))
                return new MdxCompilation(false, 0, [], [error]);
            maxTime = Math.Max(maxTime, endTime);
        }

        events.Sort(static (left, right) =>
        {
            int comparison = left.SamplePosition.CompareTo(right.SamplePosition);
            return comparison != 0 ? comparison : left.Order.CompareTo(right.Order);
        });
        return new MdxCompilation(
            true,
            Math.Max(1, (long)Math.Ceiling(maxTime * sampleRate)),
            events,
            warnings);
    }

    private static int FindChunkEnd(
        int offsetStart,
        int dataLength,
        int chunkStart,
        IReadOnlyList<int> relativeBoundaries)
    {
        int end = dataLength;
        foreach (int relative in relativeBoundaries)
        {
            int candidate = offsetStart + relative;
            if (candidate > chunkStart && candidate < end)
                end = candidate;
        }
        return end;
    }

    private static int ReadWord(ReadOnlySpan<byte> data, int offset) =>
        (data[offset] << 8) | data[offset + 1];

    private sealed class OrderCounter
    {
        public int Value;
    }

    private sealed class MdxTrackCompiler
    {
        private readonly MdxTrack _track;
        private readonly IReadOnlyDictionary<byte, MdxVoice> _voices;
        private readonly int _sampleRate;
        private readonly int _loopCount;
        private readonly List<MdxTimedWrite> _events;
        private readonly List<string> _warnings;
        private readonly OrderCounter _order;
        private int _position;
        private double _time;
        private byte _timerB = 0xC8;
        private byte _voiceId;
        private byte _slotMask = 0x0F;
        private bool _disableKeyOff;
        private int _performanceLoopCount;
        private readonly List<RepeatFrame> _repeatStack = [];

        public MdxTrackCompiler(
            MdxTrack track,
            IReadOnlyDictionary<byte, MdxVoice> voices,
            int sampleRate,
            int loopCount,
            List<MdxTimedWrite> events,
            List<string> warnings,
            OrderCounter order)
        {
            _track = track;
            _voices = voices;
            _sampleRate = sampleRate;
            _loopCount = loopCount;
            _events = events;
            _warnings = warnings;
            _order = order;
        }

        public bool Compile(out double endTime, out string error)
        {
            endTime = 0;
            error = null;
            if (_track.Channel >= 8)
                return Fail(0, "PCM/Mercury channels are not supported by the YM2151-only subset", out error);
            while (_position < _track.Data.Length)
            {
                if (++_commandCount > 1_000_000)
                    return Fail(_position, "MDX repeat expansion exceeded the safety limit", out error);
                int commandPosition = _position;
                byte command = _track.Data[_position++];
                if (command <= 0x7F)
                {
                    Advance(command + 1);
                    continue;
                }
                if (command <= 0xDF)
                {
                    if (!TryReadByte(out byte duration))
                        return Fail(commandPosition, "MDX note duration is truncated", out error);
                    EmitNote(command - 0x80, duration + 1);
                    continue;
                }

                switch (command)
                {
                    case 0xF0:
                        if (!TrySkip(1)) return Fail(commandPosition, "MDX key-on delay is truncated", out error);
                        break;
                    case 0xF1:
                        if (!TryReadWord(out ushort loopOffset))
                            return Fail(commandPosition, "MDX performance-end command is truncated", out error);
                        if (loopOffset != 0)
                        {
                            int target = _position + (short)loopOffset;
                            if (target < 0 || target >= _track.Data.Length)
                                return Fail(commandPosition, "MDX performance loop target is outside the track", out error);
                            if (_performanceLoopCount + 1 < _loopCount)
                            {
                                _performanceLoopCount++;
                                _position = target;
                                break;
                            }
                        }
                        endTime = _time;
                        return true;
                    case 0xF2:
                    case 0xF3:
                        if (!TrySkip(2)) return Fail(commandPosition, "MDX pitch command is truncated", out error);
                        return Fail(commandPosition, $"MDX command 0x{command:X2} requires MXDRV pitch state", out error);
                    case 0xF4:
                        if (!TryReadWord(out ushort escape))
                            return Fail(commandPosition, "MDX repeat escape command is truncated", out error);
                        if (_repeatStack.Count == 0)
                            return Fail(commandPosition, "MDX repeat escape has no active repeat", out error);
                        if (_repeatStack[^1].Remaining == 1)
                        {
                            int target = _position + (short)escape;
                            if (target < 0 || target > _track.Data.Length)
                                return Fail(commandPosition, "MDX repeat escape target is outside the track", out error);
                            _position = target;
                        }
                        break;
                    case 0xF5:
                        if (!TryReadWord(out ushort repeatOffset))
                            return Fail(commandPosition, "MDX repeat end command is truncated", out error);
                        if (_repeatStack.Count == 0)
                            return Fail(commandPosition, "MDX repeat end has no active repeat", out error);
                        RepeatFrame repeat = _repeatStack[^1];
                        if (repeat.Remaining > 1)
                        {
                            repeat.Remaining--;
                            _repeatStack[^1] = repeat;
                            int target = _position + (short)repeatOffset;
                            if (target < 0 || target >= _track.Data.Length)
                                return Fail(commandPosition, "MDX repeat target is outside the track", out error);
                            _position = target;
                        }
                        else
                        {
                            _repeatStack.RemoveAt(_repeatStack.Count - 1);
                        }
                        break;
                    case 0xF6:
                        if (!TryReadByte(out byte repeatCount) || !TryReadByte(out byte repeatTerminator))
                            return Fail(commandPosition, "MDX repeat command is truncated", out error);
                        if (repeatTerminator != 0)
                            return Fail(commandPosition, "MDX repeat command has an invalid terminator", out error);
                        if (repeatCount == 0)
                            return Fail(commandPosition, "MDX infinite repeats are not deterministic", out error);
                        _repeatStack.Add(new RepeatFrame(repeatCount));
                        break;
                    case 0xF7:
                        _disableKeyOff = true;
                        break;
                    case 0xF8:
                        if (!TrySkip(1)) return Fail(commandPosition, "MDX sound-length command is truncated", out error);
                        break;
                    case 0xF9:
                    case 0xFA:
                        break;
                    case 0xFB:
                        if (!TrySkip(1)) return Fail(commandPosition, "MDX volume command is truncated", out error);
                        break;
                    case 0xFC:
                        if (!TryReadByte(out byte pan)) return Fail(commandPosition, "MDX pan command is truncated", out error);
                        Emit(0x20 + _track.Channel, pan);
                        break;
                    case 0xFD:
                        if (!TryReadByte(out _voiceId)) return Fail(commandPosition, "MDX voice command is truncated", out error);
                        if (!_voices.TryGetValue(_voiceId, out MdxVoice voice))
                            return Fail(commandPosition, $"MDX references missing voice 0x{_voiceId:X2}", out error);
                        _slotMask = (byte)(voice.Data[2] & 0x0F);
                        EmitVoice(voice);
                        break;
                    case 0xFE:
                        if (!TryReadByte(out byte address) || !TryReadByte(out byte value))
                            return Fail(commandPosition, "MDX OPM register command is truncated", out error);
                        Emit(address, value);
                        if (address == 0x12)
                            _timerB = value;
                        break;
                    case 0xFF:
                        if (!TryReadByte(out byte tempo)) return Fail(commandPosition, "MDX tempo command is truncated", out error);
                        _timerB = tempo;
                        Emit(0x12, tempo);
                        break;
                    default:
                        return Fail(commandPosition, $"MDX command 0x{command:X2} requires unsupported MXDRV state", out error);
                }
            }

            endTime = _time;
            return true;
        }

        private int _commandCount;

        private sealed class RepeatFrame
        {
            public RepeatFrame(int remaining) => Remaining = remaining;
            public int Remaining { get; set; }
        }

        private void EmitNote(int note, int duration)
        {
            int octave = Math.Clamp(note / 12, 0, 7);
            int key = (octave << 4) | KeyCodes[note % 12];
            long start = ToSample(_time);
            Advance(duration);
            long end = ToSample(_time);
            EmitAt(start, 0x28 + _track.Channel, key);
            EmitAt(start, 0x30 + _track.Channel, 0);
            EmitAt(start, 0x08, (_slotMask << 3) | (_track.Channel & 0x07));
            if (!_disableKeyOff)
                EmitAt(end, 0x08, _track.Channel & 0x07);
            _disableKeyOff = false;
        }

        private void EmitVoice(MdxVoice voice)
        {
            byte[] data = voice.Data;
            int channel = _track.Channel & 0x07;
            Emit(0x20 + channel, data[1]);
            for (int operatorIndex = 0; operatorIndex < 4; operatorIndex++)
            {
                Emit(0x40 + operatorIndex * 8 + channel, data[3 + operatorIndex]);
                Emit(0x60 + operatorIndex * 8 + channel, data[7 + operatorIndex]);
                Emit(0x80 + operatorIndex * 8 + channel, data[11 + operatorIndex]);
                Emit(0xA0 + operatorIndex * 8 + channel, data[15 + operatorIndex]);
                Emit(0xC0 + operatorIndex * 8 + channel, data[19 + operatorIndex]);
                Emit(0xE0 + operatorIndex * 8 + channel, data[23 + operatorIndex]);
            }
        }

        private void Advance(int units)
        {
            double secondsPerUnit = Math.Max(1, 256 - _timerB) / 4000.0;
            _time += units * secondsPerUnit;
        }

        private long ToSample(double time) => Math.Max(0, (long)Math.Round(time * _sampleRate));

        private void Emit(int address, int data) => EmitAt(ToSample(_time), address, data);

        private void EmitAt(long sample, int address, int data)
        {
            _events.Add(new MdxTimedWrite(sample, address & 0xFF, data & 0xFF, _order.Value++));
        }

        private bool TryReadByte(out byte value)
        {
            if (_position >= _track.Data.Length)
            {
                value = 0;
                return false;
            }
            value = _track.Data[_position++];
            return true;
        }

        private bool TryReadWord(out ushort value)
        {
            if (_position + 2 > _track.Data.Length)
            {
                value = 0;
                return false;
            }
            value = (ushort)((_track.Data[_position] << 8) | _track.Data[_position + 1]);
            _position += 2;
            return true;
        }

        private bool TrySkip(int count)
        {
            if (_position + count > _track.Data.Length)
                return false;
            _position += count;
            return true;
        }

        private bool Fail(int position, string message, out string error)
        {
            error = $"MDX channel {(char)('A' + _track.Channel)} at 0x{position:X}: {message}";
            return false;
        }
    }
}

internal class MdxPlaybackException : Exception
{
    public MdxPlaybackException(string message) : base(message) { }
}
