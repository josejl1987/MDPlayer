using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Spec §35: semantic classification must derive from playback semantics,
/// never from chip type or from the PCM/sample encoding.
/// </summary>
public sealed class SampleSemanticsTests
{
    private static SamplePlaybackEvent Playback(double? midiPitch, string sampleId = "sample:rX07")
        => new(
            "chip.0.pcm.voice",
            0,
            128,
            sampleId,
            midiPitch,
            1.0,
            1.0f,
            0.0f,
            false,
            false);

    [Fact]
    public void Ym2612DacEvent_IsUnpitched()
    {
        // The MD DAC stream carries no meaningful musical pitch.
        SamplePlaybackEvent dac = Playback(midiPitch: null);
        Assert.Equal(SamplePlaybackSemantics.Unpitched, dac.Semantics);
    }

    [Fact]
    public void SpcBrrVoiceWithValidPitch_IsPitched()
    {
        // Pitched BRR playback carries the S-DSP-derived continuous pitch.
        SamplePlaybackEvent spc = Playback(midiPitch: 69.447);
        Assert.Equal(SamplePlaybackSemantics.Pitched, spc.Semantics);
    }

    [Fact]
    public void NoiseModeVoice_IsNotPitchedBrr()
    {
        // An S-DSP voice in noise mode has no meaningful pitch: the period is
        // unpitched regardless of the pitch register contents.
        SamplePlaybackEvent noise = Playback(midiPitch: null);
        Assert.Equal(SamplePlaybackSemantics.Unpitched, noise.Semantics);
    }

    [Fact]
    public void SampleIdentityAlone_DoesNotImplyPitched()
    {
        // A stable sample/hash identity must never be treated as pitch.
        SamplePlaybackEvent eventWithSample = Playback(midiPitch: null, sampleId: "sample:BRR207a41");
        Assert.Equal(SamplePlaybackSemantics.Unpitched, eventWithSample.Semantics);
    }

    [Fact]
    public void PcmEncoding_DoesNotImplyUnpitched()
    {
        // PCM/sample encoding must not imply unpitched: the same encoding is
        // pitched when the decoder produces a musical pitch for it.
        SamplePlaybackEvent pitchedPcm = Playback(midiPitch: 69.0);
        Assert.Equal(SamplePlaybackSemantics.Pitched, pitchedPcm.Semantics);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFinitePitchValues_AreUnpitched(double value)
    {
        // §29: NaN/Infinity are not valid pitches; such events are unpitched.
        Assert.Equal(SamplePlaybackSemantics.Unpitched, Playback(value).Semantics);
    }
}