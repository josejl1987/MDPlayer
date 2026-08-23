using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiVoiceDomainTests
{
    [Fact]
    public void DifferentSourcesCannotShareOneChannelStateDomain()
    {
        var first = new MidiVoiceDomain(
            new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0),
            channel: 0, bendRangeSemitones: 12);
        var second = new MidiVoiceDomain(
            new SourceDomainKey(new DeviceId(ChipType.Ym2608, 1), VoiceKind.Fm, 0),
            channel: 0, bendRangeSemitones: 24);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => first.EnsureCompatible(second));
        Assert.Contains("Conflicting MIDI voice-domain ownership", error.Message);
    }

    [Fact]
    public void SameSourceWithDifferentBendFailsAtSharedDomainBoundary()
    {
        var source = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0);
        var first = new MidiVoiceDomain(source, channel: 0, bendRangeSemitones: 12);
        var conflicting = new MidiVoiceDomain(source, channel: 0, bendRangeSemitones: 24);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => first.EnsureCompatible(conflicting));
        Assert.Contains("Conflicting MIDI voice-domain ownership", error.Message);
    }
}
