namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// In-progress decoded register state for a single OPN FM channel. These
/// fields are mutated continuously as register writes land; a snapshot into
/// an <see cref="OpnFmInstrument"/> is taken only at a captured key-on.
/// </summary>
internal sealed class OpnChannelState
{
    public byte Algorithm;
    public byte Feedback;

    public readonly OpnOperatorState[] Operators = { new(), new(), new(), new() };
}

/// <summary>
/// Decoded values of the TFI-supported fields for one operator. Mirrors
/// <see cref="OpnFmOperator"/> but as mutable channel state cells.
/// </summary>
internal sealed class OpnOperatorState
{
    public byte Multiplier;
    public byte DetuneRegister;
    public byte TotalLevel;
    public byte RateScaling;
    public byte AttackRate;
    public byte DecayRate;
    public byte SustainRate;
    public byte ReleaseRate;
    public byte SustainLevel;
    public byte SsgEg;
}

/// <summary>
/// Tracks the decoded OPN FM instrument state of one physical chip instance
/// from the ordered stream of normalized register writes. It retains the most
/// recently written value of every relevant field (a driver may build an
/// instrument through many writes and rely on prior initialization), and it
/// produces an immutable instrument snapshot whenever a key-on write with at
/// least one operator enabled is observed.
///
/// A separate instance must be used per physical chip; state is never merged
/// between chip instances or register ports.
/// </summary>
internal sealed class OpnFmRegisterTracker
{
    private readonly OpnChannelState[] _channels;
    private readonly bool _supportsSsgEg;

    /// <param name="capabilities">Capabilities of the physical chip the
    /// tracker is assigned to.</param>
    public OpnFmRegisterTracker(OpnChipCapabilities capabilities)
    {
        if (capabilities.FmChannelCount != 3 && capabilities.FmChannelCount != 6)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capabilities),
                capabilities.FmChannelCount,
                "OPN FM channel count must be 3 or 6.");
        }

        _channels = new OpnChannelState[capabilities.FmChannelCount];
        for (int i = 0; i < _channels.Length; i++)
            _channels[i] = new OpnChannelState();

        _supportsSsgEg = capabilities.SupportsSsgEg;
    }

    /// <summary>The number of FM channels this chip exposes.</summary>
    public int ChannelCount => _channels.Length;

    /// <summary>
    /// Applies one normalized register write to the chip state. When the write
    /// is a key-on with at least one operator enabled, returns an immutable
    /// snapshot of the selected channel's current instrument; otherwise
    /// returns null.
    /// </summary>
    /// <param name="port">Register port/bank (0 or 1).</param>
    /// <param name="address">Register address (0..0xFF).</param>
    /// <param name="data">Register data byte.</param>
    public OpnFmInstrument? HandleWrite(int port, int address, byte data)
    {
        // Key-on/key-off command register (0x28) does not carry instrument
        // parameters but selects a channel and triggers a capture.
        if (address == 0x28)
        {
            return HandleKeyOn(port, data);
        }

        // Operator parameter regions 0x30..0x9F.
        if (address is >= 0x30 and <= 0x9F)
        {
            UpdateOperatorRegion(port, address, data);
            return null;
        }

        // Feedback/algorithm channel registers 0xB0..0xB2.
        if (address is >= 0xB0 and <= 0xB2)
        {
            UpdateChannelRegion(port, address, data);
            return null;
        }

        // All other registers (frequency, pan/AMS/FMS, LFO, timers, DAC, etc.)
        // are intentionally ignored: TFI has no fields for them and they must
        // never alter a captured instrument.
        return null;
    }

    /// <summary>Retrieves a live channel's current state (tests only).</summary>
    internal OpnChannelState GetChannel(int channel) => _channels[channel];

    private OpnFmInstrument? HandleKeyOn(int port, byte data)
    {
        byte operatorMask = (byte)((data >> 4) & 0x0F);
        if (operatorMask == 0)
        {
            // Pure key-off. Do not capture an instrument.
            return null;
        }

        if (!TryDecodeKeyOnChannel(data, _channels.Length, out int channel))
        {
            // Reserved/invalid channel code; nothing to capture.
            return null;
        }

        // Only the primary bank carries key-on commands. The port is not used
        // to infer a second-bank channel here.
        return SnapshotOf(_channels[channel]);
    }

    private void UpdateOperatorRegion(int port, int address, byte data)
    {
        int localChannel = address & 0x03;
        if (localChannel == 3)
        {
            // Reserved in the normal OPN operator layout; never an FM channel.
            return;
        }

        int channel = DecodeGlobalChannel(port, localChannel);
        if (channel < 0 || channel >= _channels.Length)
            return; // Unsupported port/address for this chip's channel count.

        if (!TryDecodeOperator(address, out OpnOperator op))
            return;

        OpnOperatorState state = _channels[channel].Operators[(int)op];

        switch (address & 0xF0)
        {
            case 0x30: // Detune and multiplier
                state.Multiplier = (byte)(data & 0x0F);
                state.DetuneRegister = (byte)((data >> 4) & 0x07);
                break;

            case 0x40: // Total level
                state.TotalLevel = (byte)(data & 0x7F);
                break;

            case 0x50: // Rate scaling and attack rate
                state.AttackRate = (byte)(data & 0x1F);
                state.RateScaling = (byte)((data >> 6) & 0x03);
                break;

            case 0x60: // AM enable (ignored) and decay rate
                state.DecayRate = (byte)(data & 0x1F);
                break;

            case 0x70: // Sustain rate
                state.SustainRate = (byte)(data & 0x1F);
                break;

            case 0x80: // Sustain level and release rate
                state.ReleaseRate = (byte)(data & 0x0F);
                state.SustainLevel = (byte)((data >> 4) & 0x0F);
                break;

            case 0x90: // SSG-EG
                state.SsgEg = !_supportsSsgEg ? (byte)0 : (byte)(data & 0x0F);
                break;
        }
    }

    private void UpdateChannelRegion(int port, int address, byte data)
    {
        int localChannel = address - 0xB0;
        int channel = port == 0 ? localChannel : localChannel + 3;
        if (channel < 0 || channel >= _channels.Length)
            return;

        _channels[channel].Algorithm = (byte)(data & 0x07);
        _channels[channel].Feedback = (byte)((data >> 3) & 0x07);
    }

    private static int DecodeGlobalChannel(int port, int localChannel)
    {
        return port switch
        {
            0 => localChannel,
            1 => localChannel + 3,
            _ => throw new ArgumentOutOfRangeException(nameof(port))
        };
    }

    private static bool TryDecodeOperator(int address, out OpnOperator op)
    {
        op = (address & 0x0C) switch
        {
            0x00 => OpnOperator.Op1,
            0x04 => OpnOperator.Op2,
            0x08 => OpnOperator.Op3,
            0x0C => OpnOperator.Op4,
            _ => default
        };
        return true;
    }

    private static bool TryDecodeKeyOnChannel(byte data, int channelCount, out int channel)
    {
        channel = (data & 0x07) switch
        {
            0 => 0,
            1 => 1,
            2 => 2,
            4 when channelCount >= 6 => 3,
            5 when channelCount >= 6 => 4,
            6 when channelCount >= 6 => 5,
            _ => -1
        };

        return channel >= 0;
    }

    /// <summary>Creates a full immutable copy of a channel's current state.</summary>
    private static OpnFmInstrument SnapshotOf(OpnChannelState channel)
    {
        return new OpnFmInstrument
        {
            Algorithm = channel.Algorithm,
            Feedback = channel.Feedback,

            Op1 = SnapshotOperator(channel.Operators[(int)OpnOperator.Op1]),
            Op2 = SnapshotOperator(channel.Operators[(int)OpnOperator.Op2]),
            Op3 = SnapshotOperator(channel.Operators[(int)OpnOperator.Op3]),
            Op4 = SnapshotOperator(channel.Operators[(int)OpnOperator.Op4])
        };
    }

    private static OpnFmOperator SnapshotOperator(OpnOperatorState s)
    {
        return new OpnFmOperator
        {
            Multiplier = s.Multiplier,
            DetuneRegister = s.DetuneRegister,
            TotalLevel = s.TotalLevel,
            RateScaling = s.RateScaling,
            AttackRate = s.AttackRate,
            DecayRate = s.DecayRate,
            SustainRate = s.SustainRate,
            ReleaseRate = s.ReleaseRate,
            SustainLevel = s.SustainLevel,
            SsgEg = s.SsgEg
        };
    }
}
