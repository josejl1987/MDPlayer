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
}
