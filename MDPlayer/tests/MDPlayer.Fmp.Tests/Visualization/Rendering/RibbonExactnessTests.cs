using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

public sealed class RibbonExactnessTests
{
    private const int SampleRate = 48000;
    private const int Fps = 60;



    [Fact]
    public void ZohRuns_PixelIdenticalToPerColumn_ForFlatNote()
    {
        var timeline = CreateDenseTimelineForRibbonTest(flat: true, pitchBends: 0);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] withZoh = new byte[renderer.FrameByteCount];
        byte[] withoutZoh = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, withZoh);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, withoutZoh);
        AssertEqualWithDiff(withZoh, withoutZoh, renderer.Width, "Flat note");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForPitchBend()
    {
        var timeline = CreateDenseTimelineForRibbonTest(flat: false, pitchBends: 1);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] withZoh = new byte[renderer.FrameByteCount];
        byte[] withoutZoh = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, withZoh);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, withoutZoh);
        AssertEqualWithDiff(withZoh, withoutZoh, renderer.Width, "Pitch bend");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForMultiplePitchChanges()
    {
        var timeline = CreateDenseTimelineForRibbonTest(flat: false, pitchBends: 3);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] withZoh = new byte[renderer.FrameByteCount];
        byte[] withoutZoh = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, withZoh);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, withoutZoh);
        AssertEqualWithDiff(withZoh, withoutZoh, renderer.Width, "Multiple pitch changes");
    }

    private static void AssertEqualWithDiff(byte[] a, byte[] b, int width, string label)
    {
        if (a.AsSpan().SequenceEqual(b)) return;
        int first = -1;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { first = i; break; }
        int pixel = first / 4;
        int x = pixel % width;
        int y = pixel / width;
        int diffCount = 0;
        for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) diffCount++;
        throw new Xunit.Sdk.XunitException($"{label} ZOH vs per-column differ at byte {first} (pixel {x},{y} channel {first%4}) diffBytes={diffCount} a={a[first]} b={b[first]}");
    }

    private static VisualizationTimeline CreateDenseTimelineForRibbonTest(bool flat, int pitchBends)
        => CreateTimelineWithNote(800, 3200, 60.5, flat ? 0 : pitchBends, VisualizationNoteMode.Fm);

    private static VisualizationTimeline CreateTimelineWithNote(long start, long end, double midi, int pitchBends, VisualizationNoteMode mode = VisualizationNoteMode.Fm, NoteReleaseStyle release = NoteReleaseStyle.Normal, bool isRetrigger = false)
    {
        var device = new DeviceId(ChipType.Unknown, 0);
        var voice = new VoiceDescriptor(new VoiceId(device, VoiceKind.Fm, 0), "VOICE 1", VoicePresentationKind.Fm, 0, false, false, true);
        string channel = voice.Id.ToString();
        var pitchChanges = new List<PitchChange>();
        for (int i = 0; i < pitchBends; i++)
        {
            long sample = start + (i + 1) * (end - start) / (pitchBends + 1);
            double bendMidi = midi + (i % 2 == 0 ? 2 : -2);
            double freq = 440 * Math.Pow(2, (bendMidi - 69) / 12);
            pitchChanges.Add(new PitchChange(sample, freq, bendMidi));
        }
        var note = new NoteEvent(channel, start, end, 440 * Math.Pow(2, (midi - 69)/12), midi, "inst", mode, isRetrigger, pitchChanges.ToArray());
        // NoteEvent doesn't carry ReleaseStyle directly; it is derived from instrument? For test we set via reflection if needed
        return new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 10000,
            Voices = new[] { voice },
            Notes = new[] { note },
            Instruments = new[] { new InstrumentDefinition("inst", "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>()) },
        };
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForBlackKeyBand()
    {
        var timeline = CreateTimelineWithNote(800, 3200, 61, 0); // C# black key
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "Black-key band");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForGridLine()
    {
        var timeline = CreateTimelineWithNote(800, 3200, 60, 0); // C grid line
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "Grid line");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForFractionalEdges()
    {
        // Start/end at fractional X (801 vs 800 gives 1px shift with different coverage)
        var timeline = CreateTimelineWithNote(801, 3201, 60.5, 0);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "Fractional edges");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForPastAndFutureOpacity()
    {
        // Past note (entirely before currentSample) vs future note (entirely after)
        var pastTimeline = CreateTimelineWithNote(0, 500, 60, 0);
        var futureTimeline = CreateTimelineWithNote(3000, 4000, 60, 0);
        foreach (var timeline in new[] { pastTimeline, futureTimeline })
        {
            var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
                new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
            byte[] a = new byte[renderer.FrameByteCount];
            byte[] b = new byte[renderer.FrameByteCount];
            var scope = new byte[renderer.ScopeFrameByteCount];
            renderer.RenderCompositeFrame(2, scope, a);
            renderer.TestDisableZohRuns = true;
            renderer.RenderCompositeFrame(2, scope, b);
            string label = timeline == pastTimeline ? "Past opacity" : "Future opacity";
            AssertEqualWithDiff(a, b, renderer.Width, label);
        }
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForPlayheadContact()
    {
        // Note covering playhead (playhead at lane center, note spanning it)
        var timeline = CreateTimelineWithNote(1500, 1700, 60, 0);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        // Frame 2's playhead is at 1600, note 1500-1700 covers it
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "Playhead contact");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForReleaseTaper()
    {
        // Long note with Normal release where taper applies at tail
        var timeline = CreateTimelineWithNote(0, 5000, 60, 0);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        // Frame where note's tail is visible
        renderer.RenderCompositeFrame(5, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(5, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "Release taper");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForSsgStripe()
    {
        var timeline = CreateTimelineWithNote(800, 3200, 60, 0, VisualizationNoteMode.SsgEnvelopeTone);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        // SSG stripe is not handled by ZOH (falls back to per-column), so both paths are per-column and should match
        AssertEqualWithDiff(a, b, renderer.Width, "SSG stripe");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForSsgStipple()
    {
        var timeline = CreateTimelineWithNote(800, 3200, 60, 0, VisualizationNoteMode.SsgToneNoise);
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "SSG stipple");
    }

    [Fact]
    public void ZohRuns_PixelIdentical_ForFm3Operator()
    {
        var device = new DeviceId(ChipType.Unknown, 0);
        var voice = new VoiceDescriptor(new VoiceId(device, VoiceKind.Fm, 0), "VOICE 1", VoicePresentationKind.Fm, 0, false, false, true);
        string channel = voice.Id.ToString();
        var notes = new[] { new NoteEvent(channel, 800, 3200, 440, 60, "fm:op", VisualizationNoteMode.Fm3Operator, false, Array.Empty<PitchChange>()) };
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 10000,
            Voices = new[] { voice },
            Notes = notes,
            Instruments = new[] { new InstrumentDefinition("fm:op", "fm", 4, 3, 0, 2, new[] { new FmOperatorDefinition(31, 10, 5, 4, 8, 20, 1, 2, 0, false, 0) }) },
        };
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline, 960, 540, channels: VisualizationChannelFilter.Active),
            new PanelOverlayRenderer.Options { FpsNumerator = Fps, EnablePerformanceMetrics = false });
        byte[] a = new byte[renderer.FrameByteCount];
        byte[] b = new byte[renderer.FrameByteCount];
        var scope = new byte[renderer.ScopeFrameByteCount];
        renderer.RenderCompositeFrame(2, scope, a);
        renderer.TestDisableZohRuns = true;
        renderer.RenderCompositeFrame(2, scope, b);
        AssertEqualWithDiff(a, b, renderer.Width, "FM3 operator");
    }
}
