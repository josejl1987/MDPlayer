using Melanchall.DryWetMidi.Core;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Test-only semantic MIDI decoder (Patch F.1). Reads actual DryWetMIDI bytes and
/// reconstructs, keyed by MidiEndpoint (port, channel), the live playback state:
/// active pitch-bend range, active bend, active notes, program, bank and the tempo
/// map. Effective pitch = noteNumber + DecodeBend(signedBend, activeBendRange)
/// using the sign-symmetric convention (negative bend/8192*range, positive
/// bend/8191*range). This is the independent oracle the semantic round-trip tests
/// assert against.
/// </summary>
internal sealed class MidiSemanticDecoder
{
    internal sealed class EndpointState
    {
        public int BendRange = 24;
        public int ActiveBend;
        public readonly Dictionary<int, EffectiveNote> ActiveNotes = new();
        public int? Program;
        public int? Bank;
    }

    internal sealed record EffectiveNote(int Note, long OnTick, int Velocity);

    internal sealed record TrackInfo(int Index, string Name, int Port);

    internal sealed class Result
    {
        public readonly Dictionary<(int Port, int Channel), List<TimedEvent>> Events = new();
        public readonly Dictionary<(int Port, int Channel), EndpointState> State = new();
        public readonly List<(long Tick, int UsPerQuarter)> TempoMap = new();
        public readonly List<TrackInfo> Tracks = new();
    }

    internal sealed record TimedEvent(long Tick, int Port, int Channel, MidiEvent Event);

    public static Result Decode(byte[] bytes)
    {
        MidiFile file = MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
        {
            EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store,
        });
        var result = new Result();
        var pendingRpn = new Dictionary<(int, int), int>();
        long tick = 0;
        int trackIndex = 0;
        foreach (TrackChunk chunk in file.GetTrackChunks())
        {
            tick = 0;
            int port = 0;
            string name = "";
            foreach (MidiEvent evt in chunk.Events)
            {
                tick += evt.DeltaTime;
                switch (evt)
                {
                    case SequenceTrackNameEvent n:
                        name = n.Text;
                        continue;
                    case PortPrefixEvent p:
                        port = p.Port;
                        continue;
                    case SetTempoEvent tempo:
                        result.TempoMap.Add((tick, (int)tempo.MicrosecondsPerQuarterNote));
                        continue;
                    case NoteOnEvent on:
                        {
                            int note = on.NoteNumber;
                            TrackEvent(result, port, on.Channel, new TimedEvent(tick, port, on.Channel, on));
                            var state = State(result, port, on.Channel);
                            if (on.Velocity != 0)
                                state.ActiveNotes[note] = new EffectiveNote(note, tick, on.Velocity);
                            else
                                state.ActiveNotes.Remove(note);
                            continue;
                        }
                    case NoteOffEvent off:
                        {
                            int note = off.NoteNumber;
                            TrackEvent(result, port, off.Channel, new TimedEvent(tick, port, off.Channel, off));
                            State(result, port, off.Channel).ActiveNotes.Remove(note);
                            continue;
                        }
                    case PitchBendEvent bend:
                        TrackEvent(result, port, bend.Channel, new TimedEvent(tick, port, bend.Channel, bend));
                        State(result, port, bend.Channel).ActiveBend = bend.PitchValue - 8192;
                        continue;
                    case ProgramChangeEvent prog:
                        TrackEvent(result, port, prog.Channel, new TimedEvent(tick, port, prog.Channel, prog));
                        State(result, port, prog.Channel).Program = prog.ProgramNumber;
                        continue;
                    case ControlChangeEvent cc:
                            ApplyControl(result, port, cc, pendingRpn);
                            continue;
                    default:
                        continue;
                }
            }
            result.Tracks.Add(new TrackInfo(trackIndex, name, port));
            trackIndex++;
        }
        return result;
    }

    private static void ApplyControl(Result result, int port, ControlChangeEvent cc, Dictionary<(int, int), int> pendingRpn)
    {
        int channel = cc.Channel;
        var key = (port, channel);
        var state = State(result, port, channel);
        if (cc.ControlNumber == 0)
        {
            state.Bank = cc.ControlValue;
        }
        else if (cc.ControlNumber == 101)
        {
            pendingRpn[key] = cc.ControlValue;
        }
        else if (cc.ControlNumber == 100)
        {
            if (pendingRpn.TryGetValue(key, out int rpnMsb) && rpnMsb == 0 && cc.ControlValue == 0)
                pendingRpn[key] = 0; // selecting RPN 0 (pitch-bend range)
        }
        else if (cc.ControlNumber == 6 && pendingRpn.TryGetValue(key, out int rpn) && rpn == 0)
        {
            state.BendRange = cc.ControlValue;
        }
    }

    private static void TrackEvent(Result result, int port, int channel, TimedEvent te)
    {
        var key = (port, channel);
        if (!result.Events.TryGetValue(key, out var list))
        {
            list = new List<TimedEvent>();
            result.Events[key] = list;
        }
        list.Add(te);
    }

    private static EndpointState State(Result result, int port, int channel)
    {
        var key = (port, channel);
        if (!result.State.TryGetValue(key, out var s))
        {
            s = new EndpointState();
            result.State[key] = s;
        }
        return s;
    }

    /// <summary>Effective pitch = note + sign-symmetric bend decode.</summary>
    public static double EffectivePitch(int note, int signedBend, int bendRange) =>
        note + DecodeBend(signedBend, bendRange);

    /// <summary>Sign-symmetric bend decode: negative bend/8192*range, positive bend/8191*range.</summary>
    public static double DecodeBend(int signedBend, int bendRange) =>
        signedBend < 0 ? signedBend / 8192.0 * bendRange : signedBend / 8191.0 * bendRange;
}