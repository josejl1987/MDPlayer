namespace Fmp.Core.Visualization;

/// <summary>
/// Shared decoder for MDPlayer's PCM chips whose keyboard monitors expose a
/// stable playback frequency. The register layouts remain profile-specific;
/// the note lifecycle and pitch representation are deliberately shared.
/// </summary>
internal sealed class PcmChipTimelineDecoder : IChipTimelineDecoder
{
    internal enum Profile
    {
        SegaPcm,
        Rf5c68,
        Rf5c164,
        C140,
        C352,
        K054539,
        Ga20,
    }

    private readonly Profile _profile;
    private readonly int _channelCount;
    private readonly int[] _registers = new int[0x400];
    private readonly MutableNote?[] _notes;
    private TimelineBuilder _timeline;
    private DeviceDescriptor _device;
    private bool _completed;
    private int _selectedChannel;
    private bool _rfEnabled;
    private byte _rfChannelMask;

    public PcmChipTimelineDecoder(Profile profile)
    {
        _profile = profile;
        _channelCount = profile switch
        {
            Profile.SegaPcm => 16,
            Profile.Rf5c68 or Profile.Rf5c164 => 8,
            Profile.C140 => 24,
            Profile.C352 => 32,
            Profile.K054539 => 8,
            Profile.Ga20 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(profile)),
        };
        _notes = new MutableNote?[_channelCount];
    }

    public ChipType ChipType => _profile switch
    {
        Profile.SegaPcm => ChipType.SegaPcm,
        Profile.Rf5c68 => ChipType.Rf5c68,
        Profile.Rf5c164 => ChipType.Rf5c164,
        Profile.C140 => ChipType.C140,
        Profile.C352 => ChipType.C352,
        Profile.K054539 => ChipType.K054539,
        Profile.Ga20 => ChipType.Ga20,
        _ => ChipType.Unknown,
    };

    public void Initialize(DeviceDescriptor device, TimelineBuilder timeline)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(timeline);
        if (device.Id.Type != ChipType)
            throw new ArgumentException("The device does not match the decoder family.", nameof(device));

        _device = device;
        _timeline = timeline;
        timeline.AddDevice(device);
        foreach (VoiceDescriptor voice in VisualizationDeviceCatalog.PcmVoices(
            ChipType,
            device.Id.Instance,
            _channelCount))
            timeline.AddVoice(voice);
    }

    public void Process(in TimedChipWrite write)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (_timeline == null)
            throw new InvalidOperationException("Decoder has not been initialized.");
        if (write.Device != _device.Id || write.Address is < 0 or >= 0x400)
            return;

        if (_profile is Profile.Rf5c68 or Profile.Rf5c164)
        {
            if (write.Address == 8)
            {
                if ((write.Data & 0x40) != 0)
                {
                    _selectedChannel = write.Data & 0x07;
                }
                else
                {
                    _rfEnabled = (write.Data & 0x80) != 0;
                    _rfChannelMask = (byte)~write.Data;
                    for (int rfChannel = 0; rfChannel < _channelCount; rfChannel++)
                        UpdateChannel(rfChannel, write.SamplePosition);
                }
                return;
            }

            if (write.Address >= 8)
                return;
            _registers[_selectedChannel * 8 + write.Address] = write.Data;
            UpdateChannel(_selectedChannel, write.SamplePosition);
            return;
        }

        _registers[write.Address] = write.Data;
        int channel = ChannelFor(write.Address);
        if (channel < 0)
            return;

        UpdateChannel(channel, write.SamplePosition);
    }

    public void Complete(long endSample)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (endSample < 0)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        _completed = true;
        for (int channel = 0; channel < _notes.Length; channel++)
            Close(ref _notes[channel], endSample);
    }

    private int ChannelFor(int address) => _profile switch
    {
        Profile.SegaPcm when address < 0x80 => (address & 0x7F) / 8,
        Profile.C140 when address < 0x180 => address / 16,
        Profile.C352 when address < 0x100 => address / 8,
        Profile.K054539 when address < 0x100 => address / 0x20,
        Profile.K054539 when address is 0x214 or 0x215 or 0x22C => 0,
        Profile.Ga20 when address < 0x20 => address / 8,
        _ => -1,
    };

    private void UpdateChannel(int channel, long sample)
    {
        if (channel < 0 || channel >= _channelCount)
            return;

        ChannelState state = ReadState(channel);
        if (!state.Active || state.Pitch.MidiNote < 0)
        {
            if (_profile != Profile.Ga20 || state.StopRequested)
                Close(ref _notes[channel], sample);
            return;
        }

        if (_notes[channel] != null)
        {
            _notes[channel]!.AddPitch(sample, state.Pitch);
            return;
        }

        string instrument = $"{ChipType.ToString().ToLowerInvariant()}:pcm:{channel + 1}";
        _timeline.AddInstrument(new InstrumentDefinition(
            instrument,
            "pcm",
            null,
            null,
            null,
            null,
            Array.Empty<FmOperatorDefinition>()));
        _notes[channel] = new MutableNote(
            new VoiceId(_device.Id, VoiceKind.Pcm, channel),
            sample,
            state.Pitch,
            instrument);
    }

    private ChannelState ReadState(int channel)
    {
        return _profile switch
        {
            Profile.SegaPcm => ReadSegaPcm(channel),
            Profile.Rf5c68 or Profile.Rf5c164 => ReadRf(channel),
            Profile.C140 => ReadC140(channel),
            Profile.C352 => ReadC352(channel),
            Profile.K054539 => ReadK054539(channel),
            Profile.Ga20 => ReadGa20(channel),
            _ => ChannelState.Inactive,
        };
    }

    private ChannelState ReadSegaPcm(int channel)
    {
        int offset = channel * 8;
        double multiplier = _registers[offset + 7] / 256.0;
        bool stopped = (_registers[offset + 6] & 1) != 0;
        return new ChannelState(
            !stopped && multiplier > 0,
            Pitch.FromMultiplier(multiplier),
            stopped);
    }

    private ChannelState ReadRf(int channel)
    {
        int offset = channel * 8;
        int step = _registers[offset + 2] | (_registers[offset + 3] << 8);
        bool channelEnabled = (_rfChannelMask & (1 << channel)) != 0;
        bool active = _rfEnabled && channelEnabled && step > 0 && _registers[offset] > 0;
        return new ChannelState(
            active,
            Pitch.FromRegister(
                step,
                value => 0x0800
                    * PcmPitchTable.Multiplier(value)
                    * Math.Pow(2, value / 12 - 4)),
            !active);
    }

    private ChannelState ReadC140(int channel)
    {
        int offset = channel * 16;
        int frequency = (_registers[offset + 2] << 8) | _registers[offset + 3];
        bool active = (_registers[offset + 5] & 0x80) != 0 && frequency > 0;
        return new ChannelState(
            active,
            Pitch.FromRegister(
                frequency,
                value => 65_536.0 / 2.0
                    / Math.Max(1, _device.ClockHz >= 1_000_000 ? _device.ClockHz / 384 : _device.ClockHz)
                    * 8_000.0
                    * PcmPitchTable.Multiplier(value)
                    * Math.Pow(2, value / 12 - 3)),
            !active);
    }

    private ChannelState ReadC352(int channel)
    {
        int offset = channel * 8;
        int frequency = _registers[offset + 2];
        bool active = (_registers[offset + 3] & 0x4000) != 0 && frequency > 0;
        return new ChannelState(
            active,
            Pitch.FromRegister(
                frequency,
                value => 0x10000
                    * 8_000.0
                    * PcmPitchTable.Multiplier(value)
                    * Math.Pow(2, value / 12 - 1)
                    / Math.Max(1, _device.ClockHz / 288)),
            !active);
    }

    private ChannelState ReadK054539(int channel)
    {
        int offset = channel * 0x20;
        int frequency = _registers[offset]
            | (_registers[offset + 1] << 8)
            | (_registers[offset + 2] << 16);
        bool active = (_registers[0x22C] & (1 << channel)) != 0 && frequency > 0;
        return new ChannelState(
            active,
            Pitch.FromFrequency(
                (_device.ClockHz >= 1_000_000 ? _device.ClockHz / 384.0 : _device.ClockHz)
                / (0x10000 / (double)frequency)),
            !active);
    }

    private ChannelState ReadGa20(int channel)
    {
        int offset = channel * 8;
        int frequency = _registers[offset + 4];
        bool triggered = _registers[offset + 6] != 0;
        bool active = frequency > 0 && (triggered || _notes[channel] != null);
        return new ChannelState(
            active,
            Pitch.FromFrequency((_device.ClockHz / 4.0) / Math.Max(1, 256 - frequency)),
            frequency <= 0);
    }

    private void Close(ref MutableNote? note, long endSample)
    {
        if (note == null)
            return;
        if (endSample > note.StartSample)
        {
            _timeline.AddNote(
                note.Voice,
                note.StartSample,
                endSample,
                note.Pitch.MidiNote,
                note.Pitch.FrequencyHz,
                note.InstrumentId,
                VisualizationNoteMode.Pcm,
                false,
                note.PitchChanges.ToArray());
        }
        note = null;
    }

    private readonly record struct ChannelState(bool Active, Pitch Pitch, bool StopRequested)
    {
        public static readonly ChannelState Inactive = new(false, Pitch.Unpitched, true);
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);

        public static Pitch FromMultiplier(double multiplier)
        {
            if (!double.IsFinite(multiplier) || multiplier <= 0)
                return Unpitched;
            int index = PcmPitchTable.ClosestIndex(multiplier, static value =>
                PcmPitchTable.Multiplier(value));
            return FromIndex(index);
        }

        public static Pitch FromRegister(int register, Func<int, double> target)
        {
            if (register <= 0)
                return Unpitched;
            int index = PcmPitchTable.ClosestIndex(register, target);
            return FromIndex(index);
        }

        public static Pitch FromFrequency(double frequency) =>
            !double.IsFinite(frequency) || frequency <= 0
                ? Unpitched
                : new(frequency, 69 + 12 * Math.Log2(frequency / 440.0));

        private static Pitch FromIndex(int index)
        {
            double midi = index + 12;
            return new(440.0 * Math.Pow(2.0, (midi - 69.0) / 12.0), midi);
        }
    }

    private sealed class MutableNote
    {
        public MutableNote(VoiceId voice, long startSample, Pitch pitch, string instrumentId)
        {
            Voice = voice;
            StartSample = startSample;
            Pitch = pitch;
            InstrumentId = instrumentId;
        }

        public VoiceId Voice { get; }
        public long StartSample { get; }
        public Pitch Pitch { get; }
        public string InstrumentId { get; }
        public List<PitchChange> PitchChanges { get; } = [];

        public void AddPitch(long sample, Pitch pitch)
        {
            if (pitch.MidiNote < 0)
                return;
            double previous = PitchChanges.Count == 0
                ? Pitch.MidiNote
                : PitchChanges[^1].MidiNote;
            if (Math.Abs(previous - pitch.MidiNote) >= 0.0001)
                PitchChanges.Add(new PitchChange(sample, pitch.FrequencyHz, pitch.MidiNote));
        }
    }
}

internal static class PcmPitchTable
{
    private static readonly double[] Multipliers =
    [
        1.0, 1.05947557526183, 1.122467701246082, 1.189205718217262,
        1.259918966439875, 1.334836786178427, 1.414226741074841,
        1.498318171393624, 1.587416864154117, 1.681828606375659,
        1.781820961700176, 1.887776163901842,
    ];

    public static double Multiplier(int index) => Multipliers[index % 12];

    public static int ClosestIndex(double value, Func<int, double> target)
    {
        int best = 0;
        double distance = double.MaxValue;
        for (int index = 0; index < 12 * 8; index++)
        {
            double candidate = target(index);
            double current = Math.Abs(value - candidate);
            if (current < distance)
            {
                distance = current;
                best = index;
            }
        }
        return best;
    }
}
