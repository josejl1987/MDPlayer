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
    {
        var device = new DeviceId(ChipType.Unknown, 0);
        var voice = new VoiceDescriptor(new VoiceId(device, VoiceKind.Fm, 0), "VOICE 1", VoicePresentationKind.Fm, 0, false, false, true);
        string channel = voice.Id.ToString();
        var notes = new List<NoteEvent>();
        long start = 800;
        long end = 3200;
        double midi = 60.5;
        var pitchChanges = new List<PitchChange>();
        if (!flat)
        {
            for (int i = 0; i < pitchBends; i++)
            {
                long sample = start + (i + 1) * (end - start) / (pitchBends + 1);
                double bendMidi = midi + (i % 2 == 0 ? 2 : -2);
                double freq = 440 * Math.Pow(2, (bendMidi - 69) / 12);
                pitchChanges.Add(new PitchChange(sample, freq, bendMidi));
            }
        }
        notes.Add(new NoteEvent(channel, start, end, 440 * Math.Pow(2, (midi - 69)/12), midi, "inst", VisualizationNoteMode.Fm, false, pitchChanges.ToArray()));
        return new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 10000,
            Voices = new[] { voice },
            Notes = notes,
            Instruments = new[] { new InstrumentDefinition("inst", "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>()) },
        };
    }
}
