using Fmp.Cli;
using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// MIDI export option surface. Raw transport remains the default, while explicit
/// musical overrides opt into the source-time-preserving musical map.
/// </summary>
public sealed class MidiCliOptionTests
{
    private const string Input = "song.vgz";

    private static MidiOptions Parse(params string[] args) => MidiOptionsParser.Parse(args);

    [Fact]
    public void Ppq_DefaultsTo960()
    {
        MidiOptions o = Parse("--output", "out.mid", Input);
        Assert.Equal(960, o.Ppq);
    }

    [Fact]
    public void MusicalGrid_IsExplicitlyOptIn()
    {
        MidiOptions o = Parse("--musical-grid", "--output", "out.mid", Input);
        Assert.True(o.MusicalGrid);
    }

    [Fact]
    public void MusicalOverrides_AreAcceptedAndEnableGridMode()
    {
        MidiOptions o = Parse(
            "--bpm", "112",
            "--meter", "4/4",
            "--beat-offset", "-22050",
            "--strict-timing",
            "--output", "out.mid", Input);

        Assert.True(o.MusicalGrid);
        Assert.Equal(112, o.FixedBpm);
        Assert.Equal(new Meter(4, 4), o.Meter);
        Assert.Equal(-22050, o.BeatOffsetSamples);
        Assert.True(o.StrictTiming);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(480)]
    [InlineData(960)]
    [InlineData(32767)]
    public void Ppq_PositiveMidiValid_Accepted(int ppq)
    {
        MidiOptions o = Parse("--ppq", ppq.ToString(), "--output", "out.mid", Input);
        Assert.Equal(ppq, o.Ppq);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ppq_NonPositive_Rejected(int ppq)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--ppq", ppq.ToString(), "--output", "out.mid", Input));
        Assert.Contains("--ppq", ex.Message);
    }

    [Theory]
    [InlineData(32768)]
    [InlineData(65535)]
    public void Ppq_AboveMidiMax_Rejected(int ppq)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--ppq", ppq.ToString(), "--output", "out.mid", Input));
        Assert.Contains("32767", ex.Message);
    }

    [Theory]
    [InlineData("--tempo-source")]
    [InlineData("--first-downbeat-sample")]
    [InlineData("--quantize")]
    [InlineData("--no-pitch-bend")]
    [InlineData("--bend-range")]
    [InlineData("--no-percussion-channel")]
    [InlineData("--track-layout")]
    [InlineData("--pitch-normalization")]
    public void RemovedMusicalTransformOption_IsRejected(string option)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse(option, "--output", "out.mid", Input));
        Assert.Contains("unknown option", ex.Message);
        Assert.Contains(option, ex.Message);
    }

    [Theory]
    [InlineData("--tempo-source", "auto")]
    [InlineData("--quantize", "1/8")]
    [InlineData("--track-layout", "physical")]
    [InlineData("--pitch-normalization", "fidelity")]
    public void RemovedMusicalTransformOption_WithValue_IsRejected(string option, string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse(option, value, "--output", "out.mid", Input));
        Assert.Contains("unknown option", ex.Message);
        Assert.Contains(option, ex.Message);
    }

    [Fact]
    public void RawReports_SurviveAsPaths()
    {
        MidiOptions o = Parse("--timing-report", "timing.txt", "--pitch-report", "pitch.txt", "--output", "out.mid", Input);
        Assert.Equal("timing.txt", o.TimingReport);
        Assert.Equal("pitch.txt", o.PitchReport);
    }

    [Fact]
    public void Timeline_ReuseAccepted()
    {
        MidiOptions o = Parse("--timeline", "track.visualization/timeline.json", "--output", "out.mid");
        Assert.Equal("track.visualization/timeline.json", o.Timeline);
    }

    [Fact]
    public void TimelineOut_CaptureRetentionAccepted()
    {
        MidiOptions o = Parse(
            "--timeline-out", "captured/timeline.json", "--output", "out.mid", Input);
        Assert.Equal("captured/timeline.json", o.TimelineOut);
    }
}
