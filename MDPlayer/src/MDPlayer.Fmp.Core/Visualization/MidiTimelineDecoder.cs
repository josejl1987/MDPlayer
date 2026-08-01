namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes native MIDI messages into polyphonic channel timelines. MIDI note
/// identity is preserved until note-off; pitch bend and sustain are applied to
/// every active note on the affected channel.
/// </summary>
internal sealed class MidiTimelineDecoder : IMidiTimelineDecoder
{
    private readonly ChannelState[] _channels = Enumerable.Range(0, 16)
        .Select(_ => new ChannelState())
        .ToArray();
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType.Midi)
            throw new ArgumentException("The device does not match the MIDI decoder.", nameof(device));

        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.MidiVoices(device.Id.Instance))
            timeline.AddVoice(voice);
    }

    public void Process(in TimedMidiMessage message)
    {
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (message.Device != _device.Id)
            return;

        ChannelState channel = _channels[message.Channel];
        switch (message.Type)
        {
            case MidiMessageType.NoteOn when message.Data2 > 0:
                Start(channel, message.Channel, message.Data1, message.Data2, message.SamplePosition);
                break;
            case MidiMessageType.NoteOn:
            case MidiMessageType.NoteOff:
                Release(channel, message.Channel, message.Data1, message.SamplePosition);
                break;
            case MidiMessageType.ControlChange when message.Data1 == 64:
                channel.Sustain = message.Data2 >= 64;
                if (!channel.Sustain)
                    ReleaseSustained(channel, message.Channel, message.SamplePosition);
                break;
            case MidiMessageType.ProgramChange:
                channel.Program = Math.Clamp(message.Data1, 0, 127);
                break;
            case MidiMessageType.PitchBend:
                channel.BendSemitones = ((message.Data2 << 7) | message.Data1) - 8192;
                foreach (ActiveNote note in channel.Active)
                    note.AddPitch(message.SamplePosition, Pitch(note.MidiNote + Bend(channel)));
                break;
        }
    }

    public void Complete(long endSample)
    {
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        for (int channelIndex = 0; channelIndex < _channels.Length; channelIndex++)
        {
            ChannelState channel = _channels[channelIndex];
            foreach (ActiveNote note in channel.Active.ToArray())
                Close(channel, note, channelIndex, endSample, retrigger: false);
        }
    }

    private void Start(ChannelState channel, int channelIndex, int midiNote, int velocity, long sample)
    {
        midiNote = Math.Clamp(midiNote, 0, 127);
        velocity = Math.Clamp(velocity, 1, 127);
        foreach (ActiveNote existing in channel.Active
            .Where(note => note.MidiNote == midiNote)
            .ToArray())
        {
            Close(channel, existing, channelIndex, sample, retrigger: true);
        }

        string instrumentId = InstrumentId(channelIndex, channel.Program);
        _timeline.AddInstrument(new InstrumentDefinition(
            instrumentId,
            "midi",
            null,
            null,
            null,
            null,
            Array.Empty<FmOperatorDefinition>()));

        double currentMidi = midiNote + Bend(channel);
        var active = new ActiveNote(midiNote, velocity, sample, instrumentId, currentMidi);
        channel.Active.Add(active);
    }

    private void Release(ChannelState channel, int channelIndex, int midiNote, long sample)
    {
        ActiveNote note = channel.Active.LastOrDefault(candidate =>
            candidate.MidiNote == midiNote && candidate.KeyDown);
        if (note == null)
            return;

        note.KeyDown = false;
        if (!channel.Sustain)
            Close(channel, note, channelIndex, sample, retrigger: false);
    }

    private void ReleaseSustained(ChannelState channel, int channelIndex, long sample)
    {
        foreach (ActiveNote note in channel.Active.Where(note => !note.KeyDown).ToArray())
            Close(channel, note, channelIndex, sample, retrigger: false);
    }

    private void Close(ChannelState channel, ActiveNote note, int channelIndex, long sample, bool retrigger)
    {
        channel.Active.Remove(note);
        long endSample = Math.Max(note.StartSample, sample);
        _timeline.AddNote(
            new VoiceId(_device.Id, VoiceKind.MidiChannel, channelIndex),
            note.StartSample,
            endSample,
            note.InitialMidiNote,
            Frequency(note.InitialMidiNote),
            note.InstrumentId,
            VisualizationNoteMode.Midi,
            retrigger,
            note.Pitch);
    }

    private static string InstrumentId(int channel, int program) =>
        $"midi:channel-{channel + 1}:program-{program}";

    private static double Bend(ChannelState channel) => channel.BendSemitones * 2.0 / 8192.0;

    private static (double FrequencyHz, double MidiNote) Pitch(double midiNote) =>
        (Frequency(midiNote), midiNote);

    private static double Frequency(double midiNote) =>
        440.0 * Math.Pow(2.0, (midiNote - 69.0) / 12.0);

    private sealed class ChannelState
    {
        public int Program;
        public int BendSemitones;
        public bool Sustain;
        public List<ActiveNote> Active { get; } = [];
    }

    private sealed class ActiveNote
    {
        public ActiveNote(int midiNote, int velocity, long startSample, string instrumentId, double initialMidiNote)
        {
            MidiNote = midiNote;
            Velocity = velocity;
            StartSample = startSample;
            InstrumentId = instrumentId;
            InitialMidiNote = initialMidiNote;
        }

        public int MidiNote { get; }
        public int Velocity { get; }
        public long StartSample { get; }
        public string InstrumentId { get; }
        public double InitialMidiNote { get; }
        public bool KeyDown { get; set; } = true;
        public List<PitchChange> Pitch { get; } = [];

        public void AddPitch(long sample, (double FrequencyHz, double MidiNote) pitch)
        {
            if (sample < StartSample)
                return;
            if (Pitch.Count > 0 && Math.Abs(Pitch[^1].MidiNote - pitch.MidiNote) < 0.0001)
                return;
            Pitch.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}
