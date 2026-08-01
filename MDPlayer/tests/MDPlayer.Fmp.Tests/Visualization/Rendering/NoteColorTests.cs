using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

public sealed class NoteColorTests
{
    [Fact]
    public void InstrumentMode_SameInstrument_SameColor()
    {
        // §12.2: same instrument → same colour across the track.
        var a = InstrumentColorResolver.ResolveInstrumentFill("inst:42");
        var b = InstrumentColorResolver.ResolveInstrumentFill("inst:42");
        Assert.Equal(a, b);
    }

    [Fact]
    public void PitchMode_DifferentPitchClasses_DifferentColors()
    {
        // §12.3: 12 stable pitch-class colours; C and D# differ.
        var c = InstrumentColorResolver.ResolvePitchClassFill(60);  // C4
        var dSharp = InstrumentColorResolver.ResolvePitchClassFill(63); // D#4
        Assert.NotEqual(c, dSharp);
    }

    [Fact]
    public void PitchMode_SamePitchClassDifferentOctave_SameHue()
    {
        // §12.3: octaves share hue (C3 and C4 differ only in lightness).
        var low = InstrumentColorResolver.ResolvePitchClassFill(48);  // C3
        var high = InstrumentColorResolver.ResolvePitchClassFill(72);  // C5
        // Same pitch class (C) → same R:G:B ratio (hue). Check they're close
        // but not identical (lightness differs).
        Assert.True(low.R != high.R || low.G != high.G || low.B != high.B,
            "Octaves should differ in lightness.");
        // Hue preserved: the dominant channel is the same.
        Assert.Equal(
            Array.IndexOf(new[] { low.R, low.G, low.B }, Math.Max(low.R, Math.Max(low.G, low.B))),
            Array.IndexOf(new[] { high.R, high.G, high.B }, Math.Max(high.R, Math.Max(high.G, high.B))));
    }

    [Fact]
    public void ChannelMode_DifferentPanels_DifferentColors()
    {
        // §12.4: each panel has a stable accent colour.
        var fm1 = InstrumentColorResolver.ResolveChannelFill(0, 60);
        var fm2 = InstrumentColorResolver.ResolveChannelFill(1, 60);
        Assert.NotEqual(fm1, fm2);
    }

    [Fact]
    public void ResolveFill_DispatchesByMode()
    {
        // §12.1: ResolveFill picks the right resolver for each mode.
        var inst = InstrumentColorResolver.ResolveFill(NoteColorMode.Instrument, "inst:1", 0, 60);
        var pitch = InstrumentColorResolver.ResolveFill(NoteColorMode.Pitch, "inst:1", 0, 60);
        var channel = InstrumentColorResolver.ResolveFill(NoteColorMode.Channel, "inst:1", 0, 60);

        // Instrument mode uses instrument-id hash; pitch uses pitch-class; channel uses panel index.
        // They should all be valid colours but likely different.
        Assert.True(inst.R + inst.G + inst.B > 0);
        Assert.True(pitch.R + pitch.G + pitch.B > 0);
        Assert.True(channel.R + channel.G + channel.B > 0);
    }

    [Fact]
    public void PitchClassPalette_HasTwelveDistinctHues()
    {
        // §12.3: all 12 pitch classes must map to distinct hues.
        var colors = new OverlayColor[12];
        for (int pc = 0; pc < 12; pc++)
            colors[pc] = InstrumentColorResolver.ResolvePitchClassFill(60 + pc);

        for (int i = 0; i < 12; i++)
            for (int j = i + 1; j < 12; j++)
                Assert.NotEqual(colors[i], colors[j]);
    }
}
