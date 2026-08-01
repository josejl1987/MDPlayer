namespace Fmp.Core.Audio;

/// <summary>
/// Wraps an <see cref="IFmpChipSink"/> and applies per-channel muting
/// by intercepting YM2608 and SSG register writes.
///
/// For FM channels: TL (Total Level) writes are overwritten with max
/// (0x7F, silence) for muted channels; key-on writes are suppressed
/// for muted channels.
///
/// For SSG channels: amplitude register writes are zeroed for muted
/// channels.
///
/// For Rhythm/ADPCM/PPZ8 groups: writes are passed through but group
/// volumes can be muted externally via the SetVolume methods.
/// </summary>
internal class MaskedChipSink : IFmpChipSink
{
    private readonly IFmpChipSink _inner;
    private ChannelGroup _activeChannels;

    /// <summary>
    /// Creates a new masked chip sink wrapping the given inner sink.
    /// </summary>
    /// <param name="inner">The underlying chip sink (e.g. MdsoundFmpChipSink).</param>
    /// <param name="activeChannels">Which channels are active. Muted channels are silenced.</param>
    public MaskedChipSink(IFmpChipSink inner, ChannelGroup activeChannels)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _activeChannels = activeChannels;
    }

    /// <summary>
    /// Update the active channel mask. Call before a render pass.
    /// </summary>
    public ChannelGroup ActiveChannels
    {
        get => _activeChannels;
        set => _activeChannels = value;
    }

    public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition)
    {
        // Delegate to inner for register-level muting
        int maskedValue = ApplyFmMuting(port, address, value);
        if (maskedValue >= 0)
            _inner.WriteYm2608(chipId, port, address, maskedValue, samplePosition);
    }

    public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition)
    {
        // PPZ8 group muting is handled externally via MDSound volume.
        // Always pass through; the renderer controls volume at the MDSound level.
        _inner.LoadPpz8Bank(bank, mode, samples, samplePosition);
    }

    public void WritePpz8(int port, int address, int value, long samplePosition)
    {
        // PPZ8 group muting is handled externally via MDSound volume.
        _inner.WritePpz8(port, address, value, samplePosition);
    }

    // ---------------------------------------------------------------
    // FM channel mapping helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Determine the FM channel (0-5) for a given YM2608 register write.
    /// Returns -1 if the address doesn't correspond to a specific FM channel.
    /// </summary>
    private static int GetFmChannel(int port, int address)
    {
        // Per-operator parameter blocks (0x30-0x9F):
        // Each block covers 4 operators × 4 channels, but only 3 channels per port.
        // Channel = (address & 3) yields 0,1,2,3 (3 = unused).
        if (address is >= 0x30 and <= 0x9F)
        {
            int ch = address & 3;
            if (ch > 2) return -1; // unused (operator slot for channel 3 which doesn't exist in a 3-channel bank)
            return port * 3 + ch;
        }

        // Channel-level frequency registers
        if (address is >= 0xA0 and <= 0xA2)
            return port * 3 + (address - 0xA0);
        if (address is >= 0xA4 and <= 0xA6)
            return port * 3 + (address - 0xA4);

        // Channel-level algorithm/feedback registers
        if (address is >= 0xB0 and <= 0xB2)
            return port * 3 + (address - 0xB0);

        // Key-on is handled specially via data value
        if (address == 0x28)
            return -1; // handled in ApplyFmMuting

        return -1; // not an FM-specific register
    }

    /// <summary>
    /// Check if a TL (Total Level) address is for the FM channel muting.
    /// TL at max (0x7F) silences the operator.
    /// </summary>
    private static bool IsTlAddress(int address)
    {
        return address is >= 0x40 and <= 0x4F;
    }

    /// <summary>
    /// Map an FM channel index (0-5) to the corresponding ChannelGroup flag.
    /// </summary>
    private static ChannelGroup FmChannelToGroup(int channel)
    {
        return channel switch
        {
            0 => ChannelGroup.Fm1,
            1 => ChannelGroup.Fm2,
            2 => ChannelGroup.Fm3,
            3 => ChannelGroup.Fm4,
            4 => ChannelGroup.Fm5,
            5 => ChannelGroup.Fm6,
            _ => ChannelGroup.None
        };
    }

    // ---------------------------------------------------------------
    // SSG channel mapping helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Determine the SSG channel (0-2) for a given YM2608 register write
    /// on port 0. SSG amplitude registers are at addresses 0x08-0x0A.
    /// Returns -1 if not an SSG amplitude register.
    /// </summary>
    private static int GetSsgChannel(int address)
    {
        if (address is >= 0x08 and <= 0x0A)
            return address - 0x08;
        return -1;
    }

    /// <summary>
    /// Map an SSG channel index (0-2) to the corresponding ChannelGroup flag.
    /// </summary>
    private static ChannelGroup SsgChannelToGroup(int channel)
    {
        return channel switch
        {
            0 => ChannelGroup.Ssg1,
            1 => ChannelGroup.Ssg2,
            2 => ChannelGroup.Ssg3,
            _ => ChannelGroup.None
        };
    }

    // ---------------------------------------------------------------
    // Muting logic
    // ---------------------------------------------------------------

    /// <summary>
    /// Apply FM-channel muting to a YM2608 register write.
    /// Returns the (possibly modified) value, or -1 to suppress the write entirely.
    /// </summary>
    private int ApplyFmMuting(int port, int address, int value)
    {
        // Handle SSG amplitude registers
        if (port == 0)
        {
            int ssgCh = GetSsgChannel(address);
            if (ssgCh >= 0)
            {
                var group = SsgChannelToGroup(ssgCh);
                if (!_activeChannels.HasFlag(group))
                {
                    // Zero the amplitude (lower 5 bits)
                    return value & ~0x1F;
                }
                return value;
            }
        }

        // Handle key-on register (0x28 on either port)
        // YM2608 key-on format: bits 0-1 = channel, bit 2 = port select (0=port A/0-2, 1=port B/3-5)
        if (address == 0x28)
        {
            int ch = value & 3;
            int portSel = (value >> 2) & 1;
            int globalCh = portSel * 3 + ch;
            var keyOnGroup = FmChannelToGroup(globalCh);
            if (!_activeChannels.HasFlag(keyOnGroup))
            {
                // Suppress key-on by clearing bits 4-7 (YM2608 key-on/off flags).
                byte masked = (byte)(value & 0x0F);
                return masked;
            }
            return value;
        }

        // Handle FM per-operator registers
        int fmCh = GetFmChannel(port, address);
        if (fmCh >= 0)
        {
            var fmGroup = FmChannelToGroup(fmCh);
            if (!_activeChannels.HasFlag(fmGroup))
            {
                // For TL registers, set max volume (0x7F = silence)
                if (IsTlAddress(address))
                {
                    // The TL value is in bits 0-6. 0x7F = maximum attenuation = silent.
                    return value | 0x7F;
                }
                // For key-on related registers (0xB0-0xB2 algorithm/FB), suppress
                if (address is >= 0xB0 and <= 0xB8)
                {
                    // Don't suppress algorithm changes as they affect all channels
                    return value;
                }
                // For other operator parameters, let them through;
                // without key-on and with TL at max they produce no output.
                return value;
            }
        }

        return value;
    }
}
