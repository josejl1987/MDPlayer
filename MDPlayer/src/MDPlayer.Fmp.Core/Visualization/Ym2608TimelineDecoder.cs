using System.Security.Cryptography;
using Fmp.Core.PlaybackAssets.Opn;

namespace Fmp.Core.Visualization;

/// <summary>
/// Decodes the YM2608 register stream into the compact musical timeline used by
/// the panel overlay. It deliberately does not render or inspect PCM.
/// </summary>
internal sealed class Ym2608TimelineDecoder
{
    private const int RegisterBankSize = 0x100;
    private const int DefaultMasterClock = 7_987_200;

    private static readonly int[] OperatorOffsets = [0, 8, 4, 12];
    private static readonly string[] RhythmVoiceNames = ["bd", "sd", "top", "hh", "tom", "rim"];

    private readonly byte[] _registers = new byte[RegisterBankSize * 2];
    private readonly long _masterClock;
    private readonly MutableNote?[] _fmNotes = new MutableNote?[6];
    private readonly MutableNote?[] _fm3OperatorNotes = new MutableNote?[4];
    private readonly MutableNote?[] _ssgNotes = new MutableNote?[3];
    private readonly int[] _ssgPrevVolume = new int[3];
    private readonly List<MutableNote> _notes = [];
    private readonly List<RhythmEvent> _rhythm = [];
    private readonly List<AdpcmBEvent> _adpcmB = [];
    private readonly List<DriverTimingEvent> _timing = [];
    private MutableAdpcmB _adpcmCurrent;
    private readonly Dictionary<string, InstrumentDefinition> _instruments = new(StringComparer.Ordinal);
    private readonly DeterministicIdentityTable _identityTable = new();

    private int _fmDivider = 6;
    private double _ssgMultiplier = 1.0;
    private bool _fm3SpecialMode;
    private int _fm3KeyMask;
    private bool _completed;

    public Ym2608TimelineDecoder(long masterClock = DefaultMasterClock)
    {
        _masterClock = masterClock > 0 ? masterClock : DefaultMasterClock;
    }

    /// <summary>
    /// Computes a validated BPM from a YM2608 Timer-B register (<c>0x26</c>) value.
    /// The OPNA Timer B interrupt period (in seconds) is
    ///   ((0x100 - value) &lt;&lt; 4) / (masterClock / 144)
    /// per the OPN timer core (see MNDRV FMTimer step = master/72/2). One timer
    /// interrupt is treated as one quarter note. Returns null when the value maps to
    /// a non-physical (zero/very fast) period — the consumer then falls back.
    /// </summary>
    private double? ComputeTimerBpm(int value)
    {
        long ticks = (0x100 - value) << 4;
        if (ticks <= 0 || _masterClock <= 0)
            return null;
        double interruptHz = (_masterClock / 144.0) / ticks;
        double bpm = 60.0 * interruptHz;
        return bpm > 0 && bpm is >= 20 and <= 400 ? Math.Round(bpm, 2) : null;
    }

    public void ApplyYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (chipId != 0 || port is < 0 or > 1 || address is < 0 or > 0xFF)
            return;

        int registerIndex = port * RegisterBankSize + address;
        _registers[registerIndex] = (byte)value;

        if (port == 0 && address is >= 0x2D and <= 0x2F)
            ApplyPrescaler(address);

        if (port == 0 && address == 0x27)
            ApplyFm3Mode(samplePosition, value);

        if (port == 0 && address == 0x28)
            ApplyFmKey(samplePosition, value);

        if (IsFmPitchRegister(port, address))
            ApplyFmPitchChange(samplePosition, port, address);

        if (port == 0 && address <= 0x0F)
            AnalyzeSsg(samplePosition);

        if (port == 0 && address == 0x10)
            ApplyRhythmKey(samplePosition, value);

        if (port == 0 && address == 0x26)
            _timing.Add(new DriverTimingEvent(samplePosition, value, ComputeTimerBpm(value)));

        if (port == 1 && address <= 0x0A)
            ApplyAdpcmB(samplePosition, address, value);
    }

    public VisualizationTimeline Complete(long finalSample, int sampleRate, string stopReason)
    {
        if (_completed)
            throw new InvalidOperationException("The decoder has already been completed.");
        if (finalSample < 0)
            throw new ArgumentOutOfRangeException(nameof(finalSample));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));

        _completed = true;

        for (int channel = 0; channel < _fmNotes.Length; channel++)
            CloseNote(ref _fmNotes[channel], finalSample);
        for (int op = 0; op < _fm3OperatorNotes.Length; op++)
            CloseNote(ref _fm3OperatorNotes[op], finalSample);
        for (int channel = 0; channel < _ssgNotes.Length; channel++)
            CloseNote(ref _ssgNotes[channel], finalSample);
        CloseAdpcmB(finalSample);

        var notes = _notes
            .Where(note => note.EndSample > note.StartSample)
            .OrderBy(note => note.StartSample)
            .ThenBy(note => note.ChannelId, StringComparer.Ordinal)
            .Select(note => note.ToImmutable())
            .ToArray();

        SampleDefinition[] samples = _adpcmB
            .Select(value => new
            {
                Id = $"sample:adpcm-b:{value.StartAddress:X6}-{value.EndAddress:X6}".ToLowerInvariant(),
                Value = value,
            })
            .GroupBy(value => value.Id, StringComparer.Ordinal)
            .Select(group => VisualizationAssetBuilder.CreateSyntheticSample(
                group.Key,
                "adpcm",
                Math.Max(0, group.First().Value.EndAddress - group.First().Value.StartAddress),
                displayName: "ADPCM-B"))
            .OrderBy(value => value.Id, StringComparer.Ordinal)
            .ToArray();
        SamplePlaybackEvent[] samplePlayback = _adpcmB
            .Select(value => new SamplePlaybackEvent(
                "ym2608.0.adpcm-b",
                value.StartSample,
                value.EndSample,
                $"sample:adpcm-b:{value.StartAddress:X6}-{value.EndAddress:X6}".ToLowerInvariant(),
                value.FrequencyHz is > 0 and double frequency
                    ? 69 + 12 * Math.Log2(frequency / 440.0)
                    : null,
                value.DeltaN > 0 ? value.DeltaN / 0x10000d : 1.0,
                Math.Clamp(value.Level, 0, 1),
                Math.Clamp(value.Pan, -1, 1),
                value.IsRetrigger,
                false))
            .ToArray();

        return new VisualizationTimeline
        {
            SchemaVersion = 2,
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = finalSample,
            Devices =
            [
                VisualizationDeviceCatalog.Ym2608(clockHz: _masterClock),
                VisualizationDeviceCatalog.Ppz8(),
            ],
            Voices = VisualizationDeviceCatalog.Ym2608Voices(),
            Notes = notes,
            Rhythm = _rhythm
                .OrderBy(evt => evt.SamplePosition)
                .ThenBy(evt => evt.ChannelId, StringComparer.Ordinal)
                .ToArray(),
            AdpcmB = _adpcmB
                .Where(value => value.EndSample > value.StartSample)
                .OrderBy(value => value.StartSample)
                .ToArray(),
            Samples = samples,
            SamplePlayback = samplePlayback,
            Timing = _timing
                .OrderBy(value => value.SamplePosition)
                .ToArray(),
            Instruments = _instruments.Values
                .OrderBy(instrument => instrument.Id, StringComparer.Ordinal)
                .ToArray(),
            Capabilities =
            new[] { "channelScopes", "continuousPitch", "instruments", "notes", "percussion" }
                .Concat(_adpcmB.Count > 0 ? new[] { "sampleEvents" } : Array.Empty<string>())
                .Concat(_timing.Count > 0 ? new[] { "driverTiming" } : Array.Empty<string>())
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private void ApplyAdpcmB(long samplePosition, int address, int value)
    {
        switch (address)
        {
            case 0x00:
                // YM2608 ADPCM-B control: 0x80 starts playback. Bit zero and
                // zero-valued control writes are emitted by FMP as stop.
                if ((value & 0x80) != 0)
                {
                    bool retrigger = _adpcmCurrent != null;
                    CloseAdpcmB(samplePosition);
                    _adpcmCurrent = new MutableAdpcmB(
                        samplePosition,
                        DecodeAdpcmAddress(0x02),
                        DecodeAdpcmAddress(0x04),
                        DecodeDeltaN(),
                        null,
                        DecodeAdpcmLevel(),
                        DecodeAdpcmPan(),
                        retrigger);
                }
                else if ((value & 0x01) != 0 || value == 0)
                {
                    CloseAdpcmB(samplePosition);
                }
                break;
            case 0x01:
            case 0x02:
            case 0x03:
            case 0x04:
            case 0x05:
            case 0x09:
            case 0x0A:
                _adpcmCurrent?.Update(
                    DecodeAdpcmAddress(0x02),
                    DecodeAdpcmAddress(0x04),
                    DecodeDeltaN(),
                    null,
                    DecodeAdpcmLevel(),
                    DecodeAdpcmPan());
                break;
        }
    }

    private int DecodeAdpcmAddress(int lowAddress)
        => _registers[RegisterBankSize + lowAddress]
            | (_registers[RegisterBankSize + lowAddress + 1] << 8);

    private int DecodeDeltaN()
        => _registers[RegisterBankSize + 0x09]
            | (_registers[RegisterBankSize + 0x0A] << 8);

    private float DecodeAdpcmLevel()
        => 1.0f - (_registers[RegisterBankSize + 0x01] & 0x3F) / 63.0f;

    private float DecodeAdpcmPan()
    {
        int bits = (_registers[RegisterBankSize + 0x01] >> 6) & 0x03;
        return bits switch
        {
            0x02 => -1,
            0x01 => 1,
            _ => 0,
        };
    }

    private void CloseAdpcmB(long endSample)
    {
        if (_adpcmCurrent == null)
            return;
        if (endSample > _adpcmCurrent.StartSample)
        {
            _adpcmB.Add(new AdpcmBEvent(
                _adpcmCurrent.StartSample,
                endSample,
                _adpcmCurrent.StartAddress,
                _adpcmCurrent.EndAddress,
                _adpcmCurrent.DeltaN,
                _adpcmCurrent.FrequencyHz,
                _adpcmCurrent.Level,
                _adpcmCurrent.Pan,
                _adpcmCurrent.IsRetrigger));
        }
        _adpcmCurrent = null;
    }

    private void ApplyPrescaler(int address)
    {
        int index = address - 0x2D;
        _fmDivider = index switch
        {
            0 => 6,
            1 => 3,
            _ => 2,
        };
        _ssgMultiplier = index switch
        {
            0 => 1.0,
            1 => 2.0,
            _ => 4.0,
        };
    }

    private void ApplyFmKey(long samplePosition, int value)
    {
        int encodedChannel = value & 0x07;
        int channel = encodedChannel switch
        {
            0 or 1 or 2 => encodedChannel,
            4 or 5 or 6 => encodedChannel - 1,
            _ => -1,
        };
        if (channel < 0)
            return;

        int slotMask = value & 0xF0;

        if (channel == 2 && _fm3SpecialMode)
        {
            ApplyFm3OperatorKeys(samplePosition, slotMask);
            return;
        }

        if (channel == 2)
            _fm3KeyMask = slotMask;

        bool keyOn = slotMask != 0;
        if (!keyOn)
        {
            CloseNote(ref _fmNotes[channel], samplePosition);
            return;
        }

        bool retrigger = _fmNotes[channel] != null;
        CloseNote(ref _fmNotes[channel], samplePosition);

        if (channel == 5 && (_registers[0x2B] & 0x80) != 0)
            return;

        _fmNotes[channel] = OpenFmNote(channel, samplePosition, retrigger, VisualizationNoteMode.Fm);
    }

    private void ApplyFm3Mode(long samplePosition, int value)
    {
        bool newMode = (value & 0x40) != 0;
        if (newMode == _fm3SpecialMode)
            return;

        if (newMode)
        {
            CloseNote(ref _fmNotes[2], samplePosition);
            _fm3SpecialMode = true;
            OpenFm3OperatorsFromCurrentMask(samplePosition);
        }
        else
        {
            for (int op = 0; op < _fm3OperatorNotes.Length; op++)
                CloseNote(ref _fm3OperatorNotes[op], samplePosition);
            _fm3SpecialMode = false;

            if (_fm3KeyMask != 0)
                _fmNotes[2] = OpenFmNote(2, samplePosition, false, VisualizationNoteMode.Fm);
        }
    }


    private void OpenFm3OperatorsFromCurrentMask(long samplePosition)
    {
        for (int op = 0; op < 4; op++)
        {
            int bit = 0x10 << op;
            if ((_fm3KeyMask & bit) != 0)
                _fm3OperatorNotes[op] = OpenFm3OperatorNote(op, samplePosition, false);
        }
    }

    private void ApplyFm3OperatorKeys(long samplePosition, int slotMask)
    {
        int previousMask = _fm3KeyMask;
        _fm3KeyMask = slotMask;

        for (int op = 0; op < 4; op++)
        {
            int bit = 0x10 << op;
            bool wasOn = (previousMask & bit) != 0;
            bool isOn = (slotMask & bit) != 0;

            if (!isOn)
            {
                CloseNote(ref _fm3OperatorNotes[op], samplePosition);
                continue;
            }

            if (wasOn || _fm3OperatorNotes[op] != null)
                CloseNote(ref _fm3OperatorNotes[op], samplePosition);

            _fm3OperatorNotes[op] = OpenFm3OperatorNote(op, samplePosition, wasOn);
        }
    }

    private void ApplyFmPitchChange(long samplePosition, int port, int address)
    {
        int ordinaryChannel = GetOrdinaryFmPitchChannel(port, address);
        if (ordinaryChannel >= 0)
        {
            if (ordinaryChannel == 2 && _fm3SpecialMode)
            {
                AddFm3OperatorPitchPoint(0, samplePosition);
            }
            else
            {
                AddFmPitchPoint(ordinaryChannel, samplePosition);
            }
        }

        if (port == 0 && _fm3SpecialMode)
        {
            int op = GetFm3SpecialPitchOperator(address);
            if (op >= 0)
                AddFm3OperatorPitchPoint(op, samplePosition);
        }
    }

    private static bool IsFmPitchRegister(int port, int address)
    {
        bool ordinary = (address >= 0xA0 && address <= 0xA2)
            || (address >= 0xA4 && address <= 0xA6);
        bool fm3Special = port == 0
            && ((address >= 0xA8 && address <= 0xAA)
                || (address >= 0xAC && address <= 0xAE));
        return ordinary || fm3Special;
    }

    private static int GetOrdinaryFmPitchChannel(int port, int address)
    {
        int localChannel = address switch
        {
            >= 0xA0 and <= 0xA2 => address - 0xA0,
            >= 0xA4 and <= 0xA6 => address - 0xA4,
            _ => -1,
        };
        if (localChannel < 0)
            return -1;
        return localChannel + port * 3;
    }

    private static int GetFm3SpecialPitchOperator(int address)
    {
        return address switch
        {
            0xA8 or 0xAC => 1,
            0xA9 or 0xAD => 2,
            0xAA or 0xAE => 3,
            _ => -1,
        };
    }

    private MutableNote OpenFmNote(int channel, long samplePosition, bool retrigger, VisualizationNoteMode mode)
    {
        var pitch = DecodeFmPitch(channel);
        var instrument = GetOrAddFmInstrument(channel);
        var note = new MutableNote(
            $"ym2608.0.fm.{channel + 1}",
            samplePosition,
            pitch.FrequencyHz,
            pitch.MidiNote,
            instrument.Id,
            mode,
            retrigger);
        _notes.Add(note);
        return note;
    }

    private MutableNote OpenFm3OperatorNote(int op, long samplePosition, bool retrigger)
    {
        var pitch = DecodeFm3OperatorPitch(op);
        var instrument = GetOrAddFmInstrument(2);
        var note = new MutableNote(
            $"ym2608.0.fm3.op.{op + 1}",
            samplePosition,
            pitch.FrequencyHz,
            pitch.MidiNote,
            instrument.Id,
            VisualizationNoteMode.Fm3Operator,
            retrigger);
        _notes.Add(note);
        return note;
    }

    private void AddFmPitchPoint(int channel, long samplePosition)
    {
        var note = _fmNotes[channel];
        if (note == null)
            return;
        var pitch = DecodeFmPitch(channel);
        note.AddPitch(samplePosition, pitch.FrequencyHz, pitch.MidiNote);
    }

    private void AddFm3OperatorPitchPoint(int op, long samplePosition)
    {
        var note = _fm3OperatorNotes[op];
        if (note == null)
            return;
        var pitch = DecodeFm3OperatorPitch(op);
        note.AddPitch(samplePosition, pitch.FrequencyHz, pitch.MidiNote);
    }

    private Pitch DecodeFmPitch(int channel)
    {
        int portOffset = channel >= 3 ? RegisterBankSize : 0;
        int localChannel = channel % 3;
        int fNumber = _registers[portOffset + 0xA0 + localChannel]
            | ((_registers[portOffset + 0xA4 + localChannel] & 0x07) << 8);
        int block = (_registers[portOffset + 0xA4 + localChannel] >> 3) & 0x07;
        return DecodeFmPitch(fNumber, block);
    }

    private Pitch DecodeFm3OperatorPitch(int op)
    {
        int lowAddress = op switch
        {
            0 => 0xA2,
            1 => 0xA8,
            2 => 0xA9,
            _ => 0xAA,
        };
        int highAddress = op switch
        {
            0 => 0xA6,
            1 => 0xAC,
            2 => 0xAD,
            _ => 0xAE,
        };
        int fNumber = _registers[lowAddress] | ((_registers[highAddress] & 0x07) << 8);
        int block = (_registers[highAddress] >> 3) & 0x07;
        return DecodeFmPitch(fNumber, block);
    }

    private Pitch DecodeFmPitch(int fNumber, int block)
    {
        if (fNumber <= 0)
            return Pitch.Unpitched;
        double frequency = fNumber * (double)_masterClock * (1 << block)
            / ((1 << 20) * 24.0 * _fmDivider);
        return Pitch.FromFrequency(frequency);
    }

    private void AnalyzeSsg(long samplePosition)
    {
        int mixer = _registers[0x07];
        int envelopeShape = _registers[0x0D] & 0x0F;

        for (int channel = 0; channel < 3; channel++)
        {
            bool toneEnabled = (mixer & (1 << channel)) == 0;
            bool noiseEnabled = (mixer & (1 << (channel + 3))) == 0;
            int volumeRegister = _registers[0x08 + channel];
            int volume = volumeRegister & 0x0F;
            bool envelopeEnabled = (volumeRegister & 0x10) != 0;
            int period = _registers[channel * 2] | ((_registers[channel * 2 + 1] & 0x0F) << 8);

            bool audible = (toneEnabled || noiseEnabled)
                && (envelopeEnabled || volume > 0)
                && (!toneEnabled || period > 0);

            if (!audible)
            {
                CloseNote(ref _ssgNotes[channel], samplePosition);
                continue;
            }

            VisualizationNoteMode mode = GetSsgMode(toneEnabled, noiseEnabled, envelopeEnabled);
            string instrumentId = GetSsgInstrumentId(mode, envelopeShape);
            GetOrAddSsgInstrument(instrumentId);
            Pitch pitch = toneEnabled ? DecodeSsgPitch(period) : Pitch.Unpitched;

            MutableNote? active = _ssgNotes[channel];
            bool modeChanged = active == null
                || active.Mode != mode
                || !string.Equals(active.InstrumentId, instrumentId, StringComparison.Ordinal);

            // The SSG has no key-on register. A volume rise (e.g. 9→11) is the
            // FMP driver's way of retriggering a note — equivalent to the FM
            // 0x28 key-on. Close the old note and open a new one.
            bool volumeRetriggered = active != null
                && !envelopeEnabled
                && volume > _ssgPrevVolume[channel]
                && volume >= 3
                && _ssgPrevVolume[channel] > 0;

            if (modeChanged || volumeRetriggered)
            {
                CloseNote(ref _ssgNotes[channel], samplePosition);
                var note = new MutableNote(
                    $"ym2608.0.ssg.{channel + 1}",
                    samplePosition,
                    pitch.FrequencyHz,
                    pitch.MidiNote,
                    instrumentId,
                    mode,
                    false);
                _notes.Add(note);
                _ssgNotes[channel] = note;
                _ssgPrevVolume[channel] = volume;
                continue;
            }

            _ssgPrevVolume[channel] = volume;
            if (toneEnabled)
                active.AddPitch(samplePosition, pitch.FrequencyHz, pitch.MidiNote);
        }
    }

    private Pitch DecodeSsgPitch(int period)
    {
        if (period <= 0)
            return Pitch.Unpitched;
        double frequency = _masterClock / (64.0 * period) * _ssgMultiplier;
        return Pitch.FromFrequency(frequency);
    }

    private static VisualizationNoteMode GetSsgMode(bool toneEnabled, bool noiseEnabled, bool envelopeEnabled)
    {
        if (envelopeEnabled && noiseEnabled && !toneEnabled)
            return VisualizationNoteMode.SsgEnvelopeNoise;
        if (envelopeEnabled && noiseEnabled)
            return VisualizationNoteMode.SsgEnvelopeToneNoise;
        if (envelopeEnabled)
            return VisualizationNoteMode.SsgEnvelopeTone;
        if (noiseEnabled && !toneEnabled)
            return VisualizationNoteMode.SsgNoise;
        if (noiseEnabled)
            return VisualizationNoteMode.SsgToneNoise;
        return VisualizationNoteMode.SsgTone;
    }

    private static string GetSsgInstrumentId(VisualizationNoteMode mode, int envelopeShape)
    {
        return mode switch
        {
            VisualizationNoteMode.SsgTone => "ssg:tone",
            VisualizationNoteMode.SsgToneNoise => "ssg:tone-noise",
            VisualizationNoteMode.SsgNoise => "ssg:noise",
            VisualizationNoteMode.SsgEnvelopeTone => $"ssg:envelope:{envelopeShape:X1}",
            VisualizationNoteMode.SsgEnvelopeToneNoise => $"ssg:envelope-tone-noise:{envelopeShape:X1}",
            VisualizationNoteMode.SsgEnvelopeNoise => $"ssg:envelope-noise:{envelopeShape:X1}",
            _ => "ssg:unknown",
        };
    }

    private void ApplyRhythmKey(long samplePosition, int value)
    {
        if ((value & 0x80) != 0)
            return;

        int totalLevel = _registers[0x11] & 0x3F;
        for (int voice = 0; voice < RhythmVoiceNames.Length; voice++)
        {
            if ((value & (1 << voice)) == 0)
                continue;

            int voiceState = _registers[0x18 + voice];
            int voiceLevel = voiceState & 0x1F;
            float totalGain = 1.0f - totalLevel / 63.0f;
            float voiceGain = 1.0f - voiceLevel / 31.0f;
            int panBits = (voiceState >> 6) & 0x03;
            float pan = panBits switch
            {
                0x02 => -1.0f,
                0x01 => 1.0f,
                _ => 0.0f,
            };
            string name = RhythmVoiceNames[voice];
            _rhythm.Add(new RhythmEvent(
                name,
                $"ym2608.0.rhythm.{name}",
                samplePosition,
                Math.Clamp(totalGain * voiceGain, 0, 1),
                pan,
                "ym2608.0.rhythm",
                $"rhythm:{name}"));
        }
    }

    private InstrumentDefinition GetOrAddFmInstrument(int channel)
    {
        int portOffset = channel >= 3 ? RegisterBankSize : 0;
        int localChannel = channel % 3;

        var operatorsByOp = new OpnFmOperator[4];
        int setByIdentity = 0; // bitmask of initialized operator slots

        for (int op = 0; op < 4; op++)
        {
            int offset = OperatorOffsets[op] + localChannel;
            int dtMul = _registers[portOffset + 0x30 + offset];
            int totalLevel = _registers[portOffset + 0x40 + offset];
            int keyAttack = _registers[portOffset + 0x50 + offset];
            int amDecay = _registers[portOffset + 0x60 + offset];
            int sustainRate = _registers[portOffset + 0x70 + offset];
            int sustainRelease = _registers[portOffset + 0x80 + offset];
            int ssgEnvelope = _registers[portOffset + 0x90 + offset];

            operatorsByOp[op] = new OpnFmOperator
            {
                Multiplier = (byte)(dtMul & 0x0F),
                DetuneRegister = (byte)((dtMul >> 4) & 0x07),
                TotalLevel = (byte)(totalLevel & 0x7F),
                RateScaling = (byte)((keyAttack >> 6) & 0x03),
                AttackRate = (byte)(keyAttack & 0x1F),
                DecayRate = (byte)(amDecay & 0x1F),
                SustainRate = (byte)(sustainRate & 0x1F),
                ReleaseRate = (byte)(sustainRelease & 0x0F),
                SustainLevel = (byte)((sustainRelease >> 4) & 0x0F),
                SsgEg = (byte)(ssgEnvelope & 0x0F),
            };
            setByIdentity |= 1 << op;
        }

        int algorithmFeedback = _registers[portOffset + 0xB0 + localChannel] & 0x3F;
        int amsFms = _registers[portOffset + 0xB4 + localChannel] & 0x37;
        int algorithm = algorithmFeedback & 0x07;
        int feedback = (algorithmFeedback >> 3) & 0x07;
        int ams = (amsFms >> 4) & 0x03;
        int fms = amsFms & 0x07;

        var snapshot = new OpnFmInstrument
        {
            Algorithm = (byte)algorithm,
            Feedback = (byte)feedback,
            Op1 = operatorsByOp[(int)OpnOperator.Op1] ?? OpnFmOperator.Empty,
            Op2 = operatorsByOp[(int)OpnOperator.Op2] ?? OpnFmOperator.Empty,
            Op3 = operatorsByOp[(int)OpnOperator.Op3] ?? OpnFmOperator.Empty,
            Op4 = operatorsByOp[(int)OpnOperator.Op4] ?? OpnFmOperator.Empty,
        };

        // TL-agnostic normalized identity via the OPN writer (reused read-only),
        // with AMS/FMS folded in so those sensitivity bits remain significant —
        // identical normalized patches (modulo carrier-TL) dedupe across chips.
        byte[] identityBytes = OpnFmTimbreIdentityWriter.Write(snapshot);
        var identityStream = new List<byte>(identityBytes.Length + 2);
        identityStream.AddRange(identityBytes);
        identityStream.Add((byte)ams);
        identityStream.Add((byte)fms);
        string identityHash = DeterministicIdentityTable.HashBytes(identityStream.ToArray());

        InstrumentIdentity identity = _identityTable.GetOrAdd(IdentityFamily.Fm, identityHash, "fm");
        string id = identity.Canonical;
        if (_instruments.TryGetValue(id, out var existing))
            return existing;

        // Canonical normalized operators (loudest carrier at TL=0) for the
        // timeline's InstrumentDefinition list.
        OpnFmInstrument canonical = OpnFmAlgorithm.Canonicalize(snapshot);
        var operators = new FmOperatorDefinition[4];
        for (int op = 0; op < 4; op++)
        {
            OpnFmOperator o = canonical.GetOperator(operatorOf(op));
            operators[op] = new FmOperatorDefinition(
                o.AttackRate,
                o.DecayRate,
                o.SustainRate,
                o.ReleaseRate,
                o.SustainLevel,
                o.TotalLevel,
                o.RateScaling,
                o.Multiplier,
                o.DetuneRegister,
                (o.DecayRate & 0) != 0, // AM bit not tracked in the identity model
                o.SsgEg);
        }

        var created = new InstrumentDefinition(id, "fm", algorithm, feedback, ams, fms, operators);
        _instruments.Add(id, created);
        return created;
    }

    private static OpnOperator operatorOf(int op) => op switch
    {
        0 => OpnOperator.Op1,
        1 => OpnOperator.Op2,
        2 => OpnOperator.Op3,
        _ => OpnOperator.Op4,
    };

    private void GetOrAddSsgInstrument(string id)
    {
        if (_instruments.ContainsKey(id))
            return;
        _instruments.Add(id, new InstrumentDefinition(
            id,
            "ssg",
            null,
            null,
            null,
            null,
            Array.Empty<FmOperatorDefinition>()));
    }

    private static void CloseNote(ref MutableNote? note, long samplePosition)
    {
        if (note == null)
            return;
        note.EndSample = Math.Max(note.StartSample, samplePosition);
        note = null;
    }

    private sealed class MutableAdpcmB
    {
        public MutableAdpcmB(
            long startSample,
            int startAddress,
            int endAddress,
            int deltaN,
            double? frequencyHz,
            float level,
            float pan,
            bool isRetrigger)
        {
            StartSample = startSample;
            StartAddress = startAddress;
            EndAddress = endAddress;
            DeltaN = deltaN;
            FrequencyHz = frequencyHz;
            Level = level;
            Pan = pan;
            IsRetrigger = isRetrigger;
        }

        public long StartSample { get; }
        public int StartAddress { get; private set; }
        public int EndAddress { get; private set; }
        public int DeltaN { get; private set; }
        public double? FrequencyHz { get; private set; }
        public float Level { get; private set; }
        public float Pan { get; private set; }
        public bool IsRetrigger { get; }

        public void Update(
            int startAddress,
            int endAddress,
            int deltaN,
            double? frequencyHz,
            float level,
            float pan)
        {
            StartAddress = startAddress;
            EndAddress = endAddress;
            DeltaN = deltaN;
            FrequencyHz = frequencyHz;
            Level = level;
            Pan = pan;
        }
    }

    private readonly record struct Pitch(double FrequencyHz, double MidiNote)
    {
        public static readonly Pitch Unpitched = new(0, -1);

        public static Pitch FromFrequency(double frequency)
        {
            if (!double.IsFinite(frequency) || frequency <= 0)
                return Unpitched;
            double midi = 69.0 + 12.0 * Math.Log2(frequency / 440.0);
            return new Pitch(frequency, midi);
        }
    }

    private sealed class MutableNote
    {
        private readonly List<PitchChange> _pitch = [];

        public MutableNote(
            string channelId,
            long startSample,
            double frequencyHz,
            double midiNote,
            string instrumentId,
            VisualizationNoteMode mode,
            bool isRetrigger)
        {
            ChannelId = channelId;
            StartSample = startSample;
            EndSample = startSample;
            InitialFrequencyHz = frequencyHz;
            InitialMidiNote = midiNote;
            InstrumentId = instrumentId;
            Mode = mode;
            IsRetrigger = isRetrigger;
        }

        public string ChannelId { get; }
        public long StartSample { get; }
        public long EndSample { get; set; }
        public double InitialFrequencyHz { get; private set; }
        public double InitialMidiNote { get; private set; }
        public string InstrumentId { get; }
        public VisualizationNoteMode Mode { get; }
        public bool IsRetrigger { get; }

        public void AddPitch(long samplePosition, double frequencyHz, double midiNote)
        {
            if (midiNote < 0)
                return;

            // If the first pitch update arrives at the same sample as the note's
            // start, it means the period register was written multiple times in
            // the same batch. Correct the initial pitch instead of recording a
            // divergent pitch-bend from the note's own start position.
            if (_pitch.Count == 0 && samplePosition == StartSample)
            {
                if (Math.Abs(InitialMidiNote - midiNote) < 0.0001)
                    return;
                InitialMidiNote = midiNote;
                InitialFrequencyHz = frequencyHz;
                return;
            }

            double previousMidi = _pitch.Count > 0 ? _pitch[^1].MidiNote : InitialMidiNote;
            if (Math.Abs(previousMidi - midiNote) < 0.0001)
                return;

            if (_pitch.Count > 0 && _pitch[^1].SamplePosition == samplePosition)
                _pitch[^1] = new PitchChange(samplePosition, frequencyHz, midiNote);
            else
                _pitch.Add(new PitchChange(samplePosition, frequencyHz, midiNote));
        }

        public NoteEvent ToImmutable()
        {
            return new NoteEvent(
                ChannelId,
                StartSample,
                EndSample,
                InitialFrequencyHz,
                InitialMidiNote,
                InstrumentId,
                Mode,
                IsRetrigger,
                _pitch.ToArray());
        }
    }
}
