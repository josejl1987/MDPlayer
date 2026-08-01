using System.Buffers.Binary;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

internal readonly record struct MidiFileEvent(
    long Tick,
    int Track,
    int Order,
    MidiMessageType Type,
    int Channel,
    int Data1,
    int Data2);

internal readonly record struct MidiTempoChange(long Tick, int MicrosecondsPerQuarter);

internal sealed class MidiDocument
{
    private MidiDocument(
        int division,
        IReadOnlyList<MidiFileEvent> events,
        IReadOnlyList<MidiTempoChange> tempos,
        long endTick)
    {
        Division = division;
        Events = events;
        Tempos = tempos;
        EndTick = endTick;
    }

    public int Division { get; }
    public IReadOnlyList<MidiFileEvent> Events { get; }
    public IReadOnlyList<MidiTempoChange> Tempos { get; }
    public long EndTick { get; }

    public static MidiDocument Parse(ReadOnlyMemory<byte> input)
    {
        ReadOnlySpan<byte> data = input.Span;
        int cursor = 0;
        Require(data, cursor, 8, "MThd");
        if (!HasMarker(data, cursor, "MThd"))
            throw new MidiPlaybackException("input is not a Standard MIDI File");
        int headerLength = ReadInt32(data, cursor + 4);
        cursor += 8;
        Require(data, cursor, headerLength, "MThd header");
        if (headerLength < 6)
            throw new MidiPlaybackException("MIDI header is shorter than six bytes");

        int format = ReadUInt16(data, cursor);
        int trackCount = ReadUInt16(data, cursor + 2);
        int division = ReadUInt16(data, cursor + 4);
        cursor += headerLength;
        if (format > 1)
            throw new MidiPlaybackException("MIDI format 2 is not supported by the offline backend");
        if ((division & 0x8000) != 0 || division == 0)
            throw new MidiPlaybackException("SMPTE MIDI timing is not supported by the offline backend");

        var events = new List<MidiFileEvent>();
        var tempos = new List<MidiTempoChange> { new(0, 500_000) };
        long endTick = 0;
        for (int track = 0; track < trackCount; track++)
        {
            Require(data, cursor, 8, $"track {track}");
            if (!HasMarker(data, cursor, "MTrk"))
                throw new MidiPlaybackException($"MIDI track {track} is missing its MTrk marker");
            int length = ReadInt32(data, cursor + 4);
            cursor += 8;
            Require(data, cursor, length, $"track {track} data");
            int trackEnd = checked(cursor + length);
            ParseTrack(data, ref cursor, trackEnd, track, events, tempos, ref endTick);
            cursor = trackEnd;
        }

        return new MidiDocument(
            division,
            events.OrderBy(evt => evt.Tick).ThenBy(evt => evt.Track).ThenBy(evt => evt.Order).ToArray(),
            tempos.OrderBy(tempo => tempo.Tick).ToArray(),
            endTick);
    }

    public DeviceDescriptor Device => VisualizationDeviceCatalog.Midi();

    public IReadOnlyList<TimedMidiMessage> ToTimedEvents(int sampleRate)
    {
        var result = new List<TimedMidiMessage>(Events.Count);
        foreach (MidiFileEvent evt in Events)
        {
            long sample = TickToSample(evt.Tick, sampleRate);
            result.Add(new TimedMidiMessage(
                sample,
                Device.Id,
                0,
                evt.Channel,
                evt.Type,
                evt.Data1,
                evt.Data2));
        }
        return result;
    }

    public long EndSample(int sampleRate) => TickToSample(EndTick, sampleRate);

    private long TickToSample(long tick, int sampleRate)
    {
        double elapsedMicros = 0;
        long previousTick = 0;
        int tempo = 500_000;
        foreach (MidiTempoChange change in Tempos)
        {
            if (change.Tick > tick)
                break;
            if (change.Tick > previousTick)
            {
                elapsedMicros += (change.Tick - previousTick) * (double)tempo / Division;
                previousTick = change.Tick;
            }
            tempo = change.MicrosecondsPerQuarter;
        }
        if (tick > previousTick)
            elapsedMicros += (tick - previousTick) * (double)tempo / Division;
        return checked((long)Math.Round(elapsedMicros * sampleRate / 1_000_000.0, MidpointRounding.AwayFromZero));
    }

    private static void ParseTrack(
        ReadOnlySpan<byte> data,
        ref int cursor,
        int end,
        int track,
        List<MidiFileEvent> events,
        List<MidiTempoChange> tempos,
        ref long endTick)
    {
        long tick = 0;
        int runningStatus = 0;
        int order = 0;
        while (cursor < end)
        {
            tick += ReadVlq(data, ref cursor, end);
            if (cursor >= end)
                throw new MidiPlaybackException($"MIDI track {track} ends inside an event");

            int status = data[cursor++];
            if (status < 0x80)
            {
                if (runningStatus < 0x80)
                    throw new MidiPlaybackException($"MIDI track {track} uses running status before a channel event");
                cursor--;
                status = runningStatus;
            }
            else if (status < 0xF0)
            {
                runningStatus = status;
            }

            if (status == 0xFF)
            {
                if (cursor >= end)
                    throw new MidiPlaybackException("MIDI meta event is truncated");
                int metaType = data[cursor++];
                int length = checked((int)ReadVlq(data, ref cursor, end));
                RequireWithinTrack(cursor, length, end, "MIDI meta event");
                if (metaType == 0x51 && length == 3)
                {
                    int tempo = (data[cursor] << 16) | (data[cursor + 1] << 8) | data[cursor + 2];
                    if (tempo > 0)
                        tempos.Add(new MidiTempoChange(tick, tempo));
                }
                cursor += length;
                runningStatus = 0;
                endTick = Math.Max(endTick, tick);
                if (metaType == 0x2F)
                    break;
                continue;
            }

            if (status is 0xF0 or 0xF7)
            {
                int length = checked((int)ReadVlq(data, ref cursor, end));
                RequireWithinTrack(cursor, length, end, "MIDI sysex event");
                cursor += length;
                runningStatus = 0;
                endTick = Math.Max(endTick, tick);
                continue;
            }

            int kind = status & 0xF0;
            int channel = status & 0x0F;
            int data1 = ReadByte(data, ref cursor, end, "MIDI channel event");
            int data2 = kind is 0xC0 or 0xD0
                ? 0
                : ReadByte(data, ref cursor, end, "MIDI channel event");
            MidiMessageType? messageType = kind switch
            {
                0x80 => MidiMessageType.NoteOff,
                0x90 => data2 == 0 ? MidiMessageType.NoteOff : MidiMessageType.NoteOn,
                0xA0 => MidiMessageType.PolyphonicAftertouch,
                0xB0 => MidiMessageType.ControlChange,
                0xC0 => MidiMessageType.ProgramChange,
                0xD0 => MidiMessageType.ChannelPressure,
                0xE0 => MidiMessageType.PitchBend,
                _ => null,
            };
            if (messageType.HasValue)
            {
                events.Add(new MidiFileEvent(tick, track, order++, messageType.Value, channel, data1, data2));
                endTick = Math.Max(endTick, tick);
            }
        }
        endTick = Math.Max(endTick, tick);
    }

    private static int ReadByte(ReadOnlySpan<byte> data, ref int cursor, int end, string context)
    {
        if (cursor >= end)
            throw new MidiPlaybackException($"{context} is truncated");
        return data[cursor++];
    }

    private static long ReadVlq(ReadOnlySpan<byte> data, ref int cursor, int end)
    {
        long value = 0;
        for (int count = 0; count < 4; count++)
        {
            int current = ReadByte(data, ref cursor, end, "MIDI variable-length value");
            value = (value << 7) | (uint)(current & 0x7F);
            if ((current & 0x80) == 0)
                return value;
        }
        throw new MidiPlaybackException("MIDI variable-length value exceeds four bytes");
    }

    private static bool HasMarker(ReadOnlySpan<byte> data, int offset, string marker) =>
        offset + 4 <= data.Length
        && data[offset] == marker[0]
        && data[offset + 1] == marker[1]
        && data[offset + 2] == marker[2]
        && data[offset + 3] == marker[3];

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) => checked((int)BinaryPrimitives.ReadUInt32BigEndian(data[offset..]));
    private static int ReadUInt16(ReadOnlySpan<byte> data, int offset) => BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);

    private static void Require(ReadOnlySpan<byte> data, int offset, int length, string context)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
            throw new MidiPlaybackException($"{context} is truncated");
    }

    private static void RequireWithinTrack(int offset, int length, int end, string context)
    {
        if (offset < 0 || length < 0 || offset > end - length)
            throw new MidiPlaybackException($"{context} exceeds its track");
    }
}

internal sealed class MidiPlaybackBackend : IPlaybackBackend
{
    public string Id => "midi";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        if (!input.Exists)
            return new PlaybackProbeResult(false, "midi", [], [], [$"input not found: {input.FullName}"]);
        try
        {
            MidiDocument document = MidiDocument.Parse(File.ReadAllBytes(input.FullName));
            return new PlaybackProbeResult(true, "midi", [], [], [])
            {
                Portable = true,
                Visualizable = document.Events.Any(evt =>
                    evt.Type == MidiMessageType.NoteOn && evt.Data2 > 0),
                Availability = PlaybackAvailability.Available,
            };
        }
        catch (Exception ex) when (ex is IOException or MidiPlaybackException)
        {
            return new PlaybackProbeResult(false, "midi", [], [], [ex.Message]);
        }
    }

    public IPlaybackCaptureSession Open(FileInfo input, PlaybackOptions options, IPlaybackEventSink eventSink)
    {
        MidiDocument document = MidiDocument.Parse(File.ReadAllBytes(input.FullName));
        return new MidiCaptureSession(document, options, eventSink);
    }
}

internal sealed class MidiCaptureSession : IPlaybackCaptureSession
{
    private readonly MidiDocument _document;
    private readonly PlaybackOptions _options;
    private readonly IPlaybackEventSink _events;
    private bool _stopped;

    public MidiCaptureSession(MidiDocument document, PlaybackOptions options, IPlaybackEventSink events)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        if (_options.LoopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.LoopCount));
        Timing = new CaptureTimingInfo(_options.SampleRate, 1).Validate();
    }

    public CaptureTimingInfo Timing { get; }
    public IReadOnlyList<DeviceDescriptor> Devices => [_document.Device];
    public long SamplePosition { get; private set; }
    public bool IsComplete { get; private set; }

    public void Run(CancellationToken cancellationToken = default)
    {
        foreach (DeviceDescriptor device in Devices)
            _events.OnDevice(device);

        IReadOnlyList<TimedMidiMessage> sourceEvents = _document.ToTimedEvents(Timing.SampleRate);
        long songEnd = _document.EndSample(Timing.SampleRate);
        long repeatedEnd = checked(songEnd * _options.LoopCount);
        long maxSamples = _options.MaxDurationSeconds.HasValue
            ? Math.Max(1, (long)Math.Round(_options.MaxDurationSeconds.Value * Timing.SampleRate))
            : long.MaxValue;
        long baseEnd = Math.Min(repeatedEnd, maxSamples);
        long tailSamples = Math.Max(0, (long)Math.Round(_options.TailSeconds * Timing.SampleRate));
        long endSample = Math.Min(maxSamples, checked(baseEnd + tailSamples));
        long fadeSamples = Math.Max(0, (long)Math.Round(_options.FadeSeconds * Timing.SampleRate));
        long fadeStart = Math.Max(0, baseEnd - fadeSamples);

        using var audio = new MidiAudioRenderer(Timing.SampleRate);
        WavWriter writer = null;
        long rendered = 0;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.OutputAudioPath))
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(Path.GetFullPath(_options.OutputAudioPath)) ?? ".");
                writer = new WavWriter(_options.OutputAudioPath, Timing.SampleRate, 2);
            }

            for (int pass = 0; pass < _options.LoopCount && !_stopped; pass++)
            {
                long offset = songEnd * pass;
                if (pass > 0)
                    _events.OnLoopBoundary(new TimedLoopBoundary(offset, pass));
                foreach (TimedMidiMessage source in sourceEvents)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    long target = source.SamplePosition + offset;
                    if (target >= baseEnd)
                        break;
                    RenderUntil(audio, writer, ref rendered, target, fadeStart, baseEnd);
                    _events.OnMidi(source with { SamplePosition = target });
                    audio.Apply(source);
                    SamplePosition = target;
                }
            }

            RenderUntil(audio, writer, ref rendered, endSample, fadeStart, baseEnd);
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
        MidiAudioRenderer audio,
        WavWriter writer,
        ref long rendered,
        long target,
        long fadeStart,
        long baseEnd)
    {
        if (target < rendered)
            throw new InvalidOperationException("MIDI events are not monotonic after loop expansion.");
        while (rendered < target)
        {
            int count = (int)Math.Min(1024, target - rendered);
            writer?.Write(audio.Render(count, rendered, fadeStart, baseEnd));
            rendered += count;
        }
    }
}

internal sealed class MidiAudioRenderer : IDisposable
{
    private readonly List<ActiveTone> _tones = [];
    private readonly int _sampleRate;

    public MidiAudioRenderer(int sampleRate)
    {
        _sampleRate = sampleRate;
    }

    public void Apply(in TimedMidiMessage message)
    {
        if (message.Type == MidiMessageType.NoteOn && message.Data2 > 0)
        {
            _tones.Add(new ActiveTone(message.Channel, message.Data1, message.Data2, _sampleRate));
            return;
        }
        if (message.Type == MidiMessageType.NoteOff
            || message.Type == MidiMessageType.NoteOn && message.Data2 == 0)
        {
            int channel = message.Channel;
            int noteNumber = message.Data1;
            ActiveTone tone = _tones.LastOrDefault(value =>
                value.Channel == channel && value.Note == noteNumber && value.KeyDown);
            if (tone != null)
            {
                tone.KeyDown = false;
                tone.Audible = tone.Sustain;
            }
            return;
        }
        if (message.Type == MidiMessageType.ControlChange && message.Data1 == 64)
        {
            bool sustain = message.Data2 >= 64;
            int channel = message.Channel;
            foreach (ActiveTone tone in _tones.Where(value => value.Channel == channel))
            {
                tone.Sustain = sustain;
                if (!sustain && !tone.KeyDown)
                    tone.Audible = false;
            }
        }
    }

    public short[] Render(int samples, long startSample, long fadeStart, long baseEnd)
    {
        var output = new short[samples * 2];
        for (int sample = 0; sample < samples; sample++)
        {
            long absolute = startSample + sample;
            double gain = absolute >= baseEnd
                ? 0
                : absolute <= fadeStart || fadeStart >= baseEnd
                    ? 1
                    : (baseEnd - absolute) / (double)(baseEnd - fadeStart);
            double value = 0;
            foreach (ActiveTone tone in _tones)
            {
                if (!tone.Audible)
                    continue;
                value += Math.Sin(tone.Phase) * tone.Amplitude;
                tone.Phase += tone.Increment;
                if (tone.Phase >= Math.PI * 2)
                    tone.Phase -= Math.PI * 2;
            }
            short pcm = (short)Math.Clamp(value * gain * short.MaxValue, short.MinValue, short.MaxValue);
            output[sample * 2] = pcm;
            output[sample * 2 + 1] = pcm;
        }
        return output;
    }

    public void Dispose() => _tones.Clear();

    private sealed class ActiveTone
    {
        public ActiveTone(int channel, int note, int velocity, int sampleRate)
        {
            Channel = channel;
            Note = note;
            Amplitude = 0.10 * velocity / 127.0;
            Increment = 2 * Math.PI * 440.0 * Math.Pow(2, (note - 69) / 12.0) / sampleRate;
        }

        public int Channel { get; }
        public int Note { get; }
        public double Amplitude { get; }
        public double Increment { get; }
        public double Phase { get; set; }
        public bool KeyDown { get; set; } = true;
        public bool Sustain { get; set; }
        public bool Audible { get; set; } = true;
    }
}

internal sealed class MidiPlaybackException : Exception
{
    public MidiPlaybackException(string message) : base(message) { }
}
