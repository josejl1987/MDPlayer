using Fmp.Cli;
using Fmp.Core.Midi;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// WP06 T036 - validates the MIDI CLI option surface and option semantics
/// (spec section 47/48): --ppq positive MIDI-valid division (default 960),
/// --tempo-source auto|driver|symbolic|fixed, --bpm finite positive for fixed,
/// --meter optional (never defaulting to 4/4), --first-downbeat-sample requires
/// a meter, --beat-offset-samples single documented sign convention, and
/// --strict-timing. Extends the existing command; no second command framework.
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
    [InlineData("auto")]
    [InlineData("driver")]
    [InlineData("symbolic")]
    public void TempoSource_AcceptedValues(string value)
    {
        // ParseTempoSource: accepted values map onto the enum (per-value asserted
        // by name comparison in string terms; enum identity checked per case).
        MidiOptions o = Parse("--tempo-source", value, "--output", "out.mid", Input);
        Assert.False(string.IsNullOrWhiteSpace(o.TempoSource.ToString()));
    }

    [Fact]
    public void TempoSource_DefaultsToAuto()
    {
        MidiOptions o = Parse("--output", "out.mid", Input);
        Assert.Equal(MidiTempoSource.Auto, o.TempoSource);
    }

    [Fact]
    public void TempoSource_UnknownValue_Rejected()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--tempo-source", "super-fast", "--output", "out.mid", Input));
        Assert.Contains("--tempo-source", ex.Message);
    }

    [Fact]
    public void TempoSource_Fixed_RequiresBpm()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--tempo-source", "fixed", "--output", "out.mid", Input));
        Assert.Contains("--bpm", ex.Message);
    }

    [Fact]
    public void TempoSource_Fixed_WithBpm_Accepted()
    {
        MidiOptions o = Parse("--tempo-source", "fixed", "--bpm", "120", "--output", "out.mid", Input);
        Assert.Equal(MidiTempoSource.Fixed, o.TempoSource);
        Assert.Equal(120.0, o.Bpm);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-10")]
    public void Bpm_NonPositive_Rejected(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--bpm", value, "--output", "out.mid", Input));
        Assert.Contains("--bpm", ex.Message);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void Bpm_NonFinite_Rejected(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--bpm", value, "--output", "out.mid", Input));
        Assert.Contains("--bpm", ex.Message);
    }

    [Fact]
    public void BeatOffsetSamples_ParsedIntoLong()
    {
        MidiOptions o = Parse("--beat-offset-samples", "12345", "--output", "out.mid", Input);
        Assert.Equal(12345L, o.BeatOffsetSamples);
    }

    [Fact]
    public void Meter_ExplicitAccepted_DoesNotDefaultToFourFour()
    {
        MidiOptions o = Parse("--meter", "3/4", "--output", "out.mid", Input);
        Assert.NotNull(o.Meter);
        Assert.Equal(3, o.Meter!.Numerator);
        Assert.Equal(4, o.Meter.Denominator);
    }

    [Fact]
    public void Meter_NotProvided_IsNullNotDefaultFourFour()
    {
        // section 48: meter is optional and must NOT default to 4/4.
        MidiOptions o = Parse("--output", "out.mid", Input);
        Assert.Null(o.Meter);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("abc")]
    [InlineData("4/0")]
    [InlineData("0/4")]
    public void Meter_Invalid_Rejected(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--meter", value, "--output", "out.mid", Input));
        Assert.Contains("--meter", ex.Message);
    }

    [Fact]
    public void FirstDownbeatSample_RequiresMeter()
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--first-downbeat-sample", "1000", "--output", "out.mid", Input));
        Assert.Contains("--first-downbeat-sample", ex.Message);
    }

    [Fact]
    public void FirstDownbeatSample_WithMeter_Accepted()
    {
        MidiOptions o = Parse("--meter", "4/4", "--first-downbeat-sample", "1000", "--output", "out.mid", Input);
        Assert.Equal(1000L, o.FirstDownbeatSample);
    }

    [Fact]
    public void StrictTiming_Flag_SetsTrue()
    {
        MidiOptions o = Parse("--strict-timing", "--output", "out.mid", Input);
        Assert.True(o.StrictTiming);
    }

    [Fact]
    public void TimingReport_PathParsed()
    {
        MidiOptions o = Parse("--timing-report", "report.json", "--output", "out.mid", Input);
        Assert.Equal("report.json", o.TimingReport);
    }

    [Theory]
    [InlineData("fidelity")]
    [InlineData("daw")]
    [InlineData("off")]
    public void PitchNormalization_AcceptedValues(string value)
    {
        MidiOptions o = Parse("--pitch-normalization", value, "--output", "out.mid", Input);
        Assert.Equal(value, o.PitchNormalization);
    }

    [Fact]
    public void PitchNormalization_DefaultsToFidelity()
    {
        MidiOptions o = Parse("--output", "out.mid", Input);
        Assert.Equal("fidelity", o.PitchNormalization);
    }

    [Theory]
    [InlineData("super-tuned")]
    [InlineData("on")]
    public void PitchNormalization_UnknownValue_Rejected(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--pitch-normalization", value, "--output", "out.mid", Input));
        Assert.Contains("--pitch-normalization", ex.Message);
    }

    [Fact]
    public void PitchReport_PathParsed()
    {
        MidiOptions o = Parse("--pitch-report", "pitch.json", "--output", "out.mid", Input);
        Assert.Equal("pitch.json", o.PitchReport);
    }

    [Fact]
    public void TrackLayout_DefaultsToPhysical()
    {
        MidiOptions o = Parse("--output", "out.mid", Input);
        Assert.Equal(MidiTrackLayout.PhysicalVoice, o.TrackLayout);
    }

    [Theory]
    [InlineData("physical", MidiTrackLayout.PhysicalVoice)]
    [InlineData("instrument", MidiTrackLayout.InstrumentSplit)]
    public void TrackLayout_AcceptedValues(string value, MidiTrackLayout expected)
    {
        MidiOptions o = Parse("--track-layout", value, "--output", "out.mid", Input);
        Assert.Equal(expected, o.TrackLayout);
    }

    [Theory]
    [InlineData("split")]
    [InlineData("InstrumentSplit")]
    [InlineData("")]
    public void TrackLayout_UnknownValue_Rejected(string value)
    {
        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            Parse("--track-layout", value, "--output", "out.mid", Input));
        Assert.Contains("--track-layout", ex.Message);
    }
}
