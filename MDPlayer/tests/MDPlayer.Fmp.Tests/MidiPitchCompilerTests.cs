using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiPitchCompilerTests
{
    [Fact]
    public void RequiredBendRange_AllowsZeroAndRoundsUp()
    {
        Assert.Equal(0, MidiPitchCompiler.RequiredBendRange(new[]
        {
            ((IReadOnlyList<SourcePitchPoint>)new[] { new SourcePitchPoint(0, 60.0) }, 60),
        }));
        Assert.Equal(2, MidiPitchCompiler.RequiredBendRange(new[]
        {
            ((IReadOnlyList<SourcePitchPoint>)new[] { new SourcePitchPoint(0, 60.0), new SourcePitchPoint(1, 61.5) }, 60),
        }));
    }

    [Fact]
    public void RequiredBendRange_RejectsUnrepresentableExcursion()
    {
        Assert.Throws<InvalidOperationException>(() => MidiPitchCompiler.RequiredBendRange(new[]
        {
            ((IReadOnlyList<SourcePitchPoint>)new[] { new SourcePitchPoint(0, 128.1) }, 0),
        }));
    }

    [Fact]
    public void RequiredBendRange_RejectsBaseNoteOutsideMidiRange()
    {
        Assert.Throws<InvalidOperationException>(() => MidiPitchCompiler.RequiredBendRange(new[]
        {
            ((IReadOnlyList<SourcePitchPoint>)new[] { new SourcePitchPoint(0, 60.0) }, 128),
        }));
    }

    [Theory]
    [InlineData(-8192, 0)]
    [InlineData(-1, 8191)]
    [InlineData(0, 8192)]
    [InlineData(8191, 16383)]
    public void UnsignedBend_UsesTheAsymmetric14BitEndpoints(int signed, int unsigned)
    {
        double delta = signed < 0
            ? signed / 8192.0 * 12.0
            : signed / 8191.0 * 12.0;
        Assert.Equal(unsigned, MidiPitchCompiler.EncodeUnsigned14(delta, 12));
        Assert.Equal(signed, MidiPitchCompiler.DecodeUnsigned14(unsigned));
    }

    [Fact]
    public void SignedBend_HitsExactEndpointsAtRangeBoundary()
    {
        Assert.Equal(8191, MidiPitchCompiler.EncodeSignedBend(12.0, 12));
        Assert.Equal(-8192, MidiPitchCompiler.EncodeSignedBend(-12.0, 12));
        Assert.Equal(0, MidiPitchCompiler.EncodeSignedBend(0.0, 12));
    }

    [Fact]
    public void SignedBend_RejectsOffsetBeyondRange()
    {
        Assert.Throws<InvalidOperationException>(() => MidiPitchCompiler.EncodeSignedBend(12.5, 12));
        Assert.Throws<InvalidOperationException>(() => MidiPitchCompiler.EncodeSignedBend(-12.5, 12));
    }

    [Fact]
    public void SignedBend_ZeroRangeRejectsAnyOffset()
    {
        Assert.Equal(0, MidiPitchCompiler.EncodeSignedBend(0.0, 0));
        Assert.Throws<InvalidOperationException>(() => MidiPitchCompiler.EncodeSignedBend(0.5, 0));
    }

    [Fact]
    public void DecodePitch_IsTheExactInverseOfQuantizedEncoding()
    {
        const int range = 12;
        const int baseNote = 61;
        foreach (double delta in new[] { -12.0, -3.25, 0.0, 4.5, 12.0 })
        {
            int encoded = MidiPitchCompiler.EncodeUnsigned14(delta, range);
            double decoded = MidiPitchCompiler.DecodePitch(baseNote, encoded, range);
            Assert.Equal(baseNote + MidiPitchCompiler.DecodeUnsigned14(encoded)
                / (MidiPitchCompiler.DecodeUnsigned14(encoded) < 0 ? 8192.0 : 8191.0) * range,
                decoded);
        }
    }
}