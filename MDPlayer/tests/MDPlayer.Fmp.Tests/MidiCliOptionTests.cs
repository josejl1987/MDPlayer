using Fmp.Cli;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Raw MIDI export option surface. --ppq (default 960, MIDI-valid 1..32767) is
/// the only transcription control; the musical vocabulary (tempo source, BPM,
/// meter, downbeat, beat offset, quantization, pitch bend, velocity, track
/// layout, pitch normalization) is gone and every removed option must be
/// rejected as unknown so stale scripts fail loudly instead of silently
/// degrading fidelity.
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
    [InlineData("--bpm")]
    [InlineData("--meter")]
    [InlineData("--first-downbeat-sample")]
    [InlineData("--beat-offset-samples")]
    [InlineData("--strict-timing")]
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
    [InlineData("--bpm", "120")]
    [InlineData("--meter", "4/4")]
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
}
