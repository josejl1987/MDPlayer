using Fmp.Core.Midi;
using Fmp.Core.Visualization;

namespace MDPlayer.Fmp.Tests;
internal static class IndependentMidiPitchValidator
{
    public static void Validate(
        VisualizationTimeline timeline,
        MidiTranscriptionResult export,
        int ppq)
    {
        IReadOnlyList<ParsedTrack> parsed = Read(export.Bytes);
        if (parsed.Count != export.Tracks.Count + 1)
            throw new InvalidOperationException("Serialized SMF track count does not match the export.");
        ValidateConductor(parsed[0]);
        foreach (ParsedTrack track in parsed)
            ValidateTrackSemantics(track);

        var endpointOwners = new Dictionary<(byte Port, int Channel), int>();
        for (int index = 0; index < parsed.Count; index++)
        {
            foreach (ParsedEvent evt in parsed[index].Events)
            {
                if (evt.Channel < 0)
                    continue;
                var endpoint = (parsed[index].Port, evt.Channel);
                if (endpointOwners.TryGetValue(endpoint, out int owner) && owner != index)
                    throw new InvalidOperationException(
                        $"Serialized endpoint {endpoint} is driven by multiple tracks.");
                endpointOwners[endpoint] = index;
            }
        }

        for (int trackIndex = 0; trackIndex < export.Tracks.Count; trackIndex++)
        {
            MidiTrack track = export.Tracks[trackIndex];
            if (track.VoiceDomain is not MidiVoiceDomain domain || domain.Notes.Count == 0)
                continue;

            ParsedTrack serialized = parsed[trackIndex + 1];
            var state = new ChannelState();
            var expectedNotes = domain.Notes;
            var observed = new SortedDictionary<long, double>();
            SourcePitchNote? activeSource = null;
            int activeBase = 0;
            int sourceIndex = 0;
            int bendsAtTick = 0;
            long currentTick = -1;
            int rangeSetCountAtEnd = 0;

            foreach (ParsedEvent evt in serialized.Events)
            {
                if (evt.Channel != domain.MidiChannel)
                    continue;
                if (evt.Tick != currentTick)
                {
                    currentTick = evt.Tick;
                    bendsAtTick = 0;
                }

                switch (evt.Kind)
                {
                    case ParsedKind.ControlChange:
                        state.ApplyControl(evt.Data1, evt.Data2);
                        rangeSetCountAtEnd = state.RangeSetCount;
                        break;

                    case ParsedKind.Program:
                        state.ApplyProgram(evt.Data1);
                        break;

                    case ParsedKind.PitchBend:
                        state.ApplyBend(evt.Data1, evt.Data2);
                        bendsAtTick++;
                        if (bendsAtTick > 1)
                            throw new InvalidOperationException(
                                $"Multiple same-tick pitch bends at track {trackIndex + 1}, tick {evt.Tick}.");
                        if (activeSource is null)
                            continue;
                        RequireRange(domain, state);
                        observed[evt.Tick] = DecodePitch(activeBase, state.Bend, state.BendRange);
                        break;

                    case ParsedKind.NoteOn:
                        if (activeSource is not null)
                            throw new InvalidOperationException("A physical MIDI domain has overlapping notes.");
                        if (sourceIndex >= expectedNotes.Count)
                            throw new InvalidOperationException("Serialized NoteOn count exceeds source-note count.");

                        SourcePitchNote source = expectedNotes[sourceIndex++];
                        long expectedTick = MidiTransportClock.SampleToTick(
                            timeline.StartSample, source.StartSample, timeline.SampleRate, ppq);
                        if (evt.Tick != expectedTick)
                            throw new InvalidOperationException(
                                $"Source NoteOn tick {expectedTick} became serialized tick {evt.Tick}.");

                        activeSource = source;
                        activeBase = evt.Data1;
                        if (activeBase != MidiPitchCompiler.SelectMinimaxBaseNote(source.PitchCurve))
                            throw new InvalidOperationException("Serialized NoteOn does not use the minimax base note.");
                        if (domain.BendRange > 0)
                            RequireRange(domain, state);
                        observed[evt.Tick] = DecodePitch(activeBase, state.Bend, state.BendRange);
                        break;

                    case ParsedKind.NoteOff:
                        if (activeSource is SourcePitchNote closing)
                        {
                            ValidateNote(timeline, closing, activeBase, state.BendRange, observed, ppq);
                            activeSource = null;
                            observed.Clear();
                        }
                        break;
                }
            }

            if (activeSource is SourcePitchNote last)
                ValidateNote(timeline, last, activeBase, state.BendRange, observed, ppq);
            if (sourceIndex != expectedNotes.Count)
                throw new InvalidOperationException("Serialized NoteOn count does not conserve source notes.");
            if (domain.BendRange > 0 && rangeSetCountAtEnd != 1)
                throw new InvalidOperationException("Serialized domain did not initialize one bend range.");
            if (domain.BendRange == 0 && rangeSetCountAtEnd != 0)
                throw new InvalidOperationException("Zero-range domain emitted bend-range configuration.");
            state.ValidateRangeSequence(domain.BendRange > 0);
        }
    }

    private static void ValidateConductor(ParsedTrack conductor)
    {
        if (!conductor.Events.Any(evt => evt.Kind == ParsedKind.Tempo && evt.Tick == 0))
            throw new InvalidOperationException("Serialized conductor has no tick-zero tempo.");
        if (conductor.Events.Any(evt => evt.Kind == ParsedKind.TimeSignature && evt.Tick != 0))
            throw new InvalidOperationException("Serialized time signature is not at tick zero.");
    }

    private static void ValidateTrackSemantics(ParsedTrack track)
    {
        var active = new Dictionary<(int Channel, int Note), int>();
        long currentTick = -1;
        int previousRank = -1;
        foreach (ParsedEvent evt in track.Events)
        {
            if (evt.Tick != currentTick)
            {
                currentTick = evt.Tick;
                previousRank = -1;
            }
            int rank = EventRank(evt);
            if (rank < previousRank)
                throw new InvalidOperationException(
                    $"Serialized same-tick event order is not deterministic at tick {evt.Tick}.");
            previousRank = rank;

            if (evt.Kind == ParsedKind.NoteOn)
                active[(evt.Channel, evt.Data1)] = active.GetValueOrDefault((evt.Channel, evt.Data1)) + 1;
            else if (evt.Kind == ParsedKind.NoteOff)
            {
                (int Channel, int Note) key = (evt.Channel, evt.Data1);
                if (!active.TryGetValue(key, out int count) || count == 0)
                    throw new InvalidOperationException(
                        $"Serialized NoteOff has no preceding NoteOn at tick {evt.Tick}.");
                if (count == 1)
                    active.Remove(key);
                else
                    active[key] = count - 1;
            }
        }
        if (active.Count != 0)
            throw new InvalidOperationException("Serialized track ends with active notes.");
    }

    private static int EventRank(ParsedEvent evt) => evt.Kind switch
    {
        ParsedKind.NoteOff => 0,
        ParsedKind.Program or ParsedKind.Bank => 1,
        ParsedKind.ControlChange when evt.Data1 is 101 or 100 or 6 or 38 => 2,
        ParsedKind.PitchBend => 3,
        ParsedKind.NoteOn => 4,
        ParsedKind.ControlChange => 5,
        _ => 6,
    };
    private static void ValidateNote(
        VisualizationTimeline timeline,
        SourcePitchNote source,
        int baseNote,
        int bendRange,
        SortedDictionary<long, double> observed,
        int ppq)
    {
        var expected = new SortedDictionary<long, double>();
        foreach (SourcePitchPoint point in source.PitchCurve)
        {
            long tick = MidiTransportClock.SampleToTick(
                timeline.StartSample, point.Sample, timeline.SampleRate, ppq);
            expected[tick] = point.MidiNote;
        }

        foreach ((long tick, double sourcePitch) in expected)
        {
            if (tick >= MidiTransportClock.SampleToTick(
                    timeline.StartSample, source.EndSample, timeline.SampleRate, ppq))
                continue;
            KeyValuePair<long, double>? actual = observed
                .Where(pair => pair.Key <= tick)
                .Select(pair => (KeyValuePair<long, double>?)pair)
                .LastOrDefault();
            if (actual is null)
                throw new InvalidOperationException($"No serialized pitch state reaches source tick {tick}.");

            double sourceDelta = sourcePitch - baseNote;
            double step = bendRange == 0
                ? 0
                : bendRange / (sourceDelta < 0 ? 8192.0 : 8191.0);
            double allowed = step + 1e-9;
            if (Math.Abs(actual.Value.Value - sourcePitch) > allowed)
                throw new InvalidOperationException(
                    $"Serialized pitch error at tick {tick} is "
                    + $"{Math.Abs(actual.Value.Value - sourcePitch):0.########}, allowed {allowed:0.########}.");
        }
    }
    private static void RequireRange(MidiVoiceDomain domain, ChannelState state)
    {
        if (state.BendRange != domain.BendRange)
            throw new InvalidOperationException(
                $"Serialized event uses bend range {state.BendRange}, expected {domain.BendRange}.");
    }

    private static double DecodePitch(int baseNote, int signedBend, int bendRange)
    {
        double denominator = signedBend < 0 ? 8192.0 : 8191.0;
        return baseNote + signedBend / denominator * bendRange;
    }

    private static IReadOnlyList<ParsedTrack> Read(byte[] bytes)
    {
        int position = 0;
        Require(bytes, ref position, "MThd");
        uint headerLength = ReadUInt32(bytes, ref position);
        if (headerLength != 6 || ReadUInt16(bytes, ref position) != 1)
            throw new InvalidOperationException("Serialized SMF is not Format 1.");
        ushort trackCount = ReadUInt16(bytes, ref position);
        _ = ReadUInt16(bytes, ref position);
        var tracks = new List<ParsedTrack>(trackCount);
        for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
            tracks.Add(ReadTrack(bytes, ref position));
        return tracks;
    }

    private static ParsedTrack ReadTrack(byte[] bytes, ref int position)
    {
        Require(bytes, ref position, "MTrk");
        int length = checked((int)ReadUInt32(bytes, ref position));
        int end = checked(position + length);
        long tick = 0;
        byte port = 0;
        byte running = 0;
        var events = new List<ParsedEvent>();
        bool endOfTrack = false;
        while (position < end)
        {
            tick += ReadVlq(bytes, ref position, end);
            byte status = bytes[position++];
            if (status < 0x80)
            {
                if (running == 0)
                    throw new InvalidOperationException("SMF uses data bytes without running status.");
                position--;
                status = running;
            }
            else if (status < 0xF0)
            {
                running = status;
            }

            if (status == 0xFF)
            {
                byte meta = bytes[position++];
                int metaLength = checked((int)ReadVlq(bytes, ref position, end));
                if (position + metaLength > end)
                    throw new InvalidOperationException("SMF meta event exceeds its track chunk.");
                if (meta == 0x21 && metaLength == 1)
                    port = bytes[position];
                if (meta == 0x51 && metaLength == 3)
                {
                    int tempo = bytes[position] << 16 | bytes[position + 1] << 8 | bytes[position + 2];
                    events.Add(new ParsedEvent(tick, -1, tempo, 0, ParsedKind.Tempo));
                }
                else if (meta == 0x58 && metaLength == 4)
                    events.Add(new ParsedEvent(tick, -1, bytes[position], bytes[position + 1], ParsedKind.TimeSignature));
                if (meta == 0x2F)
                {
                    if (metaLength != 0 || position + metaLength != end)
                        throw new InvalidOperationException("SMF End of Track is not the final event.");
                    endOfTrack = true;
                    position = end;
                    break;
                }
                position = checked(position + metaLength);
                continue;
            }
            if (status is 0xF0 or 0xF7)
            {
                int sysexLength = checked((int)ReadVlq(bytes, ref position, end));
                position = checked(position + sysexLength);
                continue;
            }

            int channel = status & 0x0F;
            int data1 = bytes[position++];
            int data2 = (status & 0xE0) is 0xC0 or 0xD0 ? 0 : bytes[position++];
            ParsedKind kind = (status & 0xF0) switch
            {
                0x80 => ParsedKind.NoteOff,
                0x90 when data2 > 0 => ParsedKind.NoteOn,
                0x90 => ParsedKind.NoteOff,
                0xB0 => ParsedKind.ControlChange,
                0xC0 => ParsedKind.Program,
                0xE0 => ParsedKind.PitchBend,
                _ => ParsedKind.Other,
            };
            if (kind == ParsedKind.ControlChange && data1 is 0 or 32)
                kind = ParsedKind.Bank;
            if (kind is not ParsedKind.Other)
                events.Add(new ParsedEvent(tick, channel, data1, data2, kind));
        }
        if (!endOfTrack)
            throw new InvalidOperationException("SMF track is missing End of Track.");
        return new ParsedTrack(port, events);
    }

    private static uint ReadUInt32(byte[] bytes, ref int position) =>
        (uint)(bytes[position++] << 24 | bytes[position++] << 16
            | bytes[position++] << 8 | bytes[position++]);

    private static ushort ReadUInt16(byte[] bytes, ref int position) =>
        (ushort)(bytes[position++] << 8 | bytes[position++]);

    private static long ReadVlq(byte[] bytes, ref int position, int end)
    {
        long result = 0;
        for (int count = 0; count < 4; count++)
        {
            if (position >= end)
                throw new InvalidOperationException("Truncated SMF variable-length value.");
            byte value = bytes[position++];
            result = (result << 7) | (value & 0x7F);
            if ((value & 0x80) == 0)
                return result;
        }
        throw new InvalidOperationException("SMF variable-length value exceeds four bytes.");
    }

    private static void Require(byte[] bytes, ref int position, string text)
    {
        foreach (char character in text)
            if (position >= bytes.Length || bytes[position++] != (byte)character)
                throw new InvalidOperationException($"Serialized SMF is missing {text}.");
    }

    private sealed class ChannelState
    {
        public int BendRange { get; private set; } = 2;
        public int Bend { get; private set; }
        public int Program { get; private set; }
        public int Bank { get; private set; }
        public int RangeSetCount { get; private set; }
        private int _rpnMsb = 127;
        private int _rpnLsb = 127;
        private bool _rangeDataLsb;
        private bool _rangeNullRpn;

        public void ApplyControl(int control, int value)
        {
            switch (control)
            {
                case 101:
                    _rpnMsb = value;
                    break;
                case 100:
                    _rpnLsb = value;
                    break;
                case 6 when _rpnMsb == 0 && _rpnLsb == 0:
                    BendRange = value;
                    RangeSetCount++;
                    _rangeDataLsb = false;
                    _rangeNullRpn = false;
                    break;
                case 38 when _rpnMsb == 0 && _rpnLsb == 0:
                    _rangeDataLsb = value == 0;
                    break;
                case 0:
                case 32:
                    Bank = value;
                    break;
            }
            if (_rpnMsb == 127 && _rpnLsb == 127)
                _rangeNullRpn = true;
        }

        public void ApplyBend(int lsb, int msb) => Bend = ((msb << 7) | lsb) - 8192;

        public void ApplyProgram(int value) => Program = value;

        public void ValidateRangeSequence(bool expected)
        {
            if (expected && (!_rangeDataLsb || !_rangeNullRpn))
                throw new InvalidOperationException(
                    "Serialized RPN range setup did not write CC38=0 and null the RPN.");
            if (!expected && RangeSetCount != 0)
                throw new InvalidOperationException("Zero-range channel changed pitch sensitivity.");
        }
    }

    private readonly record struct ParsedTrack(byte Port, IReadOnlyList<ParsedEvent> Events);
    private readonly record struct ParsedEvent(
        long Tick, int Channel, int Data1, int Data2, ParsedKind Kind);

    private enum ParsedKind
    {
        Other,
        NoteOn,
        NoteOff,
        ControlChange,
        Bank,
        Program,
        PitchBend,
        Tempo,
        TimeSignature,
    }
}
