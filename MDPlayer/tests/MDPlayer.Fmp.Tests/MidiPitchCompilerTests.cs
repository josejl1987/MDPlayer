using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiPitchCompilerTests
{
    [Fact]
    public void MinimaxBaseNote_UsesEntirePitchInterval()
    {
        int baseNote = MidiPitchCompiler.SelectMinimaxBaseNote(new[]
        {
            new SourcePitchPoint(0, 60.25),
            new SourcePitchPoint(1, 61.75),
        });

        Assert.Equal(61, baseNote);
    }

    [Fact]
    public void MinimaxBaseNote_UsesLowerNeighborOnExactTie()
    {
        int baseNote = MidiPitchCompiler.SelectMinimaxBaseNote(new[]
        {
            new SourcePitchPoint(0, 60.0),
            new SourcePitchPoint(1, 61.0),
        });

        Assert.Equal(60, baseNote);
    }

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

    [Theory]
    [InlineData(0, 8192)]
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

    [Theory]
    [InlineData(0, 0)]
    [InlineData(12, 1)]
    [InlineData(48, 2)]
    [InlineData(96, 3)]
    [InlineData(97, 4)]
    public void BendRange_IsClassifiedWithoutSilentClamping(
        int range, int expected)
    {
        Assert.Equal((MidiBendRangeClassification)expected,
            MidiPitchCompiler.ClassifyBendRange(range));
    }
}
