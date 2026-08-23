namespace Fmp.Core.Midi;

/// <summary>
/// The single ordered event stream that owns state for one MIDI endpoint.
/// Channel state is endpoint state, not track-layout state.
/// </summary>
internal sealed class MidiChannelProgram
{
    private readonly List<MidiEventBase> _orderedEvents = [];

    public MidiChannelProgram(string sourceVoiceId, int midiChannel, int bendRange)
    {
        if (string.IsNullOrWhiteSpace(sourceVoiceId))
            throw new ArgumentException("A MIDI channel program requires a source voice id.", nameof(sourceVoiceId));
        if (midiChannel is < 0 or > 15)
            throw new ArgumentOutOfRangeException(nameof(midiChannel));
        if (bendRange is < 0 or > 127)
            throw new ArgumentOutOfRangeException(nameof(bendRange));

        SourceVoiceId = sourceVoiceId;
        MidiChannel = midiChannel;
        BendRange = bendRange;
    }

    public string SourceVoiceId { get; }

    public int MidiChannel { get; }

    public int BendRange { get; }

    public IReadOnlyList<MidiEventBase> OrderedEvents => _orderedEvents;

    internal List<MidiEventBase> MutableEvents => _orderedEvents;

    internal void Append(MidiEventBase evt)
    {
        ArgumentNullException.ThrowIfNull(evt);
        evt.SourceOrder = _orderedEvents.Count;
        _orderedEvents.Add(evt);
    }

    /// <summary>
    /// Applies the one global same-tick order before the stream is handed to the
    /// SMF writer. SourceOrder remains the deterministic tie-breaker for events
    /// with the same MIDI priority.
    /// </summary>
    internal void Seal()
    {
        _orderedEvents.Sort(MidiEventOrder.Compare);
        Validate();
    }

    internal void Validate()
    {
        long previousTick = 0;
        MidiEventBase? previous = null;
        int rangeEvents = 0;
        bool pitchBendSeen = false;
        bool noteOnSeen = false;

        foreach (MidiEventBase evt in _orderedEvents)
        {
            if (evt.Tick < 0 || evt.Tick < previousTick)
                throw new InvalidOperationException(
                    $"MIDI channel program '{SourceVoiceId}' is not ordered by non-negative tick.");

            if (previous is not null && evt.Tick == previous.Tick)
            {
                int order = MidiEventOrder.Compare(previous, evt);
                if (order >= 0)
                    throw new InvalidOperationException(
                        $"MIDI channel program '{SourceVoiceId}' has ambiguous same-tick event ordering.");
            }

            if (TryGetChannel(evt, out int channel) && channel != MidiChannel)
                throw new InvalidOperationException(
                    $"MIDI channel program '{SourceVoiceId}' owns channel {MidiChannel}, "
                    + $"but an event targets channel {channel}.");

            switch (evt)
            {
                case MidiBendRangeEvent range:
                    rangeEvents++;
                    if (range.Tick != 0)
                        throw new InvalidOperationException(
                            $"MIDI channel program '{SourceVoiceId}' initializes bend range away from tick 0.");
                    if (range.Semitones != BendRange)
                        throw new InvalidOperationException(
                            $"MIDI channel program '{SourceVoiceId}' emitted bend range {range.Semitones}, "
                            + $"expected {BendRange}.");
                    if (pitchBendSeen || noteOnSeen)
                        throw new InvalidOperationException(
                            $"MIDI channel program '{SourceVoiceId}' changes bend range after musical state.");
                    break;

                case MidiPitchBendEvent:
                    pitchBendSeen = true;
                    break;

                case MidiNoteEvent note when note.NoteOn:
                    noteOnSeen = true;
                    break;
            }

            previousTick = evt.Tick;
            previous = evt;
        }

        if (rangeEvents > 1)
            throw new InvalidOperationException(
                $"MIDI channel program '{SourceVoiceId}' initializes bend range more than once.");

        if (pitchBendSeen && rangeEvents != 1)
            throw new InvalidOperationException(
                $"MIDI channel program '{SourceVoiceId}' emits pitch bends without exactly one range initialization.");
    }

    private static bool TryGetChannel(MidiEventBase evt, out int channel)
    {
        switch (evt)
        {
            case MidiNoteEvent note:
                channel = note.Channel;
                return true;
            case MidiProgramEvent program:
                channel = program.Channel;
                return true;
            case MidiBankEvent bank:
                channel = bank.Channel;
                return true;
            case MidiControlChangeEvent controlChange:
                channel = controlChange.Channel;
                return true;
            case MidiPitchBendEvent bend:
                channel = bend.Channel;
                return true;
            case MidiBendRangeEvent range:
                channel = range.Channel;
                return true;
            case MidiTuningEvent tuning:
                channel = tuning.Channel;
                return true;
            default:
                channel = default;
                return false;
        }
    }
}
