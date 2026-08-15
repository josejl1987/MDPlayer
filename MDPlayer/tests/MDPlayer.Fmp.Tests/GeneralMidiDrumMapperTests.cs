using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Unit tests for the semantic YM2608 rhythm → General MIDI drum-kit note
/// selection. Only the six rhythm:&lt;voice&gt; identities are mapped; any other
/// sample identity is unknown and must signal the exporter's unique-note
/// allocator (TryMap returns false rather than a fixed fallback note).
/// </summary>
public sealed class GeneralMidiDrumMapperTests
{
    private static RhythmEvent Rhythm(string voice, string instrumentId, float pan) =>
        new(voice, $"ym2608.0.rhythm.{voice}", 0, 1.0f, pan, InstrumentId: instrumentId);

    [Theory]
    [InlineData("rhythm:bd", 36)]  // Bass Drum 1
    [InlineData("rhythm.bd", 36)]  // legacy dot identity
    [InlineData("ym2608.0.rhythm.bd", 36)] // legacy qualified identity
    [InlineData("rhythm:sd", 38)]  // Acoustic Snare
    [InlineData("rhythm:rim", 37)] // Side Stick
    [InlineData("rhythm:hh", 42)]  // Closed Hi-Hat
    public void KnownFixedIdentities_MapToSemanticGmNotes(string instrumentId, int expected)
    {
        Assert.True(GeneralMidiDrumMapper.TryMap(Rhythm("?", instrumentId, 0f), out int note));
        Assert.Equal(expected, note);
    }

    [Theory]
    [InlineData(0, 0f, 36)]
    [InlineData(1, 0f, 38)]
    [InlineData(2, -1f, 49)]
    [InlineData(3, 0f, 42)]
    [InlineData(4, 0f, 48)]
    [InlineData(5, 0f, 37)]
    public void PhysicalYm2608DomainIndex_OverridesIdentity(
        int voiceIndex, float pan, int expected)
    {
        RhythmEvent rhythm = Rhythm("wrong", "rhythm:unknown", pan) with
        {
            Domain = new SourceDomainKey(
                new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, voiceIndex),
        };

        Assert.True(GeneralMidiDrumMapper.TryMap(rhythm, out int note));
        Assert.Equal(expected, note);
    }

    [Theory]
    [InlineData(-0.5f, 50)] // High Tom   (pan left)
    [InlineData(0.0f, 48)]  // Hi-Mid Tom (centered)
    [InlineData(0.5f, 45)]  // Low Tom    (pan right)
    public void Tom_PanAware_DescendingLeftToRight(float pan, int expected)
    {
        Assert.True(GeneralMidiDrumMapper.TryMap(Rhythm("tom", "rhythm:tom", pan), out int note));
        Assert.Equal(expected, note);
    }

    [Theory]
    [InlineData(-1.0f, 49)] // Crash Cymbal 1 (not right-panned)
    [InlineData(0.0f, 49)]  // Crash Cymbal 1 (centered)
    [InlineData(0.5f, 57)]  // Crash Cymbal 2 (pan right)
    public void Top_PanAwareOnlyRightPannedCrashes(float pan, int expected)
    {
        Assert.True(GeneralMidiDrumMapper.TryMap(Rhythm("top", "rhythm:top", pan), out int note));
        Assert.Equal(expected, note);
    }

    [Fact]
    public void UnknownIdentity_ReturnsFalse_NotFallback()
    {
        // There is NO defensive Map() fallback: a non-semantic identity (OPL
        // percussion, SSG noise, user samples) returns false with an unset note
        // (0) so it routes to the exporter's deterministic unknown-note
        // preallocation. It must never resolve to side stick (37) or any other
        // semantic GM role.
        var unknown = new RhythmEvent("drums", "drums", 0, 1.0f, 0f);
        Assert.False(GeneralMidiDrumMapper.TryMap(unknown, out int note));
        Assert.Equal(0, note); // unset sentinel — never a semantic GM note
        Assert.False(GeneralMidiDrumMapper.TryMap(
            new RhythmEvent("noise", "ay8910:0:noise:0", 0, 0.8f, 0f), out _),
            "non-YM2608 sample identities must not map semantically");
    }
}