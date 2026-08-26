using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// MIDI export option surface. The transport is fixed (120 BPM / 960 PPQ) and
/// every musical-inference override is rejected as unknown.
/// </summary>
public sealed class MidiCliOptionTests
{
    private const string Input = "song.vgz";

    private static MidiOptions Parse(params string[] args) => MidiOptionsParser.Parse(args);

    [Theory]
    [InlineData("--ppq")]
    [InlineData("--musical-grid")]
    [InlineData("--bpm")]
    [InlineData("--meter")]
    [InlineData("--beat-offset")]
    [InlineData("--strict-timing")]
    [InlineData("--timing-report")]
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
    [InlineData("--bpm", "112")]
    [InlineData("--meter", "4/4")]
    [InlineData("--beat-offset", "-22050")]
    [InlineData("--tempo-source", "auto")]
    [InlineData("--quantize", "1/8")]
    [InlineData("--track-layout", "physical")]
    [InlineData("--pitch-normalization", "fidelity")]
    [InlineData("--timing-report", "timing.txt")]
    public void RemovedMusicalTransformOption_WithValue_IsRejected(string option, string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse(option, value, "--output", "out.mid", Input));
        Assert.Contains("unknown option", ex.Message);
        Assert.Contains(option, ex.Message);
    }

    [Fact]
    public void PitchReport_SurvivesAsPath()
    {
        MidiOptions o = Parse("--pitch-report", "pitch.txt", "--output", "out.mid", Input);
        Assert.Equal("pitch.txt", o.PitchReport);
    }

    [Fact]
    public void Channels_IsAcceptedAsFlag()
    {
        MidiOptions o = Parse("--channels", "--output", "out.mid", Input);
        Assert.True(o.Channels);
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