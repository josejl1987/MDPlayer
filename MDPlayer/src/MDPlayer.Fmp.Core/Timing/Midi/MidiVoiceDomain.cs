using Fmp.Core.Visualization;

namespace Fmp.Core.Midi;

/// <summary>Authoritative ownership and MIDI state for one source voice domain.</summary>
internal readonly record struct MidiVoiceDomain(
    DeviceId Device,
    VoiceKind VoiceFamily,
    int SourceIndex,
    int Channel,
    int? Program,
    int? Bank,
    int BendRangeSemitones,
    byte Port)
{
    public MidiVoiceDomain(SourceDomainKey source, int channel, int? program = null,
        int? bank = null, int bendRangeSemitones = 24, byte port = 0)
        : this(source.Device, source.VoiceFamily, source.Index, channel, program, bank,
            bendRangeSemitones, port) { }

    public SourceDomainKey Source => new(Device, VoiceFamily, SourceIndex);

    public void EnsureCompatible(MidiVoiceDomain other)
    {
        if (Source != other.Source || Port != other.Port || Channel != other.Channel)
            return;
        if (Program != other.Program || Bank != other.Bank
            || BendRangeSemitones != other.BendRangeSemitones)
            throw new InvalidOperationException(
                $"Conflicting MIDI voice-domain ownership for '{Source}': "
                + $"{Channel}/{Program?.ToString() ?? "none"}/{Bank?.ToString() ?? "none"}/"
                + $"{BendRangeSemitones} versus {other.Channel}/{other.Program?.ToString() ?? "none"}/"
                + $"{other.Bank?.ToString() ?? "none"}/{other.BendRangeSemitones}.");
    }
}
