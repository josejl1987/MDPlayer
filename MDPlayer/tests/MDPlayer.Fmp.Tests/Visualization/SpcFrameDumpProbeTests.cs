using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// TEMPORARY diagnostic probe: renders a controlled SPC scene (two pitched
/// BRR notes at known pitches) through the CPU overlay renderer and dumps the
/// raw RGBA frame plus camera/note state to /tmp so the pitched rendering can
/// be inspected at the pixel level. Not a product test.
/// </summary>
public sealed class SpcFrameDumpProbeTests
{
    [Fact]
    public void DumpSpcPitchedFrames_ToPng()
    {
        var builder = new TimelineBuilder(32_000);
        var decoder = new SnesDspTimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.SnesDsp(), builder);
        byte[] encoded = new byte[9];
        encoded[0] = 0x01;
        decoder.SetSamples([
            new SpcSampleEntry(
                "abcdef0123456789",
                "abcdef01",
                [3],
                0x2000,
                0x2000,
                false,
                encoded,
                "relative",
                440.0,
                1),
        ]);

        decoder.Process(SpcSemanticEvent.KeyOn(10_000, 0, sourceNumber: 3, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(90_000, 0, 0x1800));
        decoder.Process(SpcSemanticEvent.VoiceEnd(190_000, 0));
        decoder.Process(SpcSemanticEvent.KeyOn(40_000, 1, sourceNumber: 3, effectivePitch: 0x2000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(120_000, 1));
        decoder.Complete(320_000);

        VisualizationTimeline timeline = builder.Build(320_000, "probe");
        var layout = RendererTestLayout.Build(timeline, width: 1280, height: 720);
        using var renderer = new PanelOverlayRenderer(
            timeline,
            layout,
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 30,
                FpsDenominator = 1,
                EnablePerformanceMetrics = true,
            });

        long sample = 33_000; // t≈1.03s → both notes inside window
        int frameIndex = (int)(sample * 30 / 32_000);
        byte[] frame = renderer.RenderFrame(frameIndex);
        File.WriteAllBytes("/tmp/spc-probe-frame.rgba", frame);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"frameIndex={frameIndex} sample={sample} rate={timeline.SampleRate} " +
                      $"past={layout.Geometry.PastSeconds} future={layout.Geometry.FutureSeconds} " +
                      $"window=[{layout.Geometry.WindowStartSample(sample, timeline.SampleRate)}," +
                      $"{layout.Geometry.WindowEndSample(sample, timeline.SampleRate)}]");
        sb.AppendLine($"metrics: {renderer.Performance}");
        sb.AppendLine("topology:");
        for (int i = 0; i < renderer.Topology.Panels.Count; i++)
            sb.AppendLine($"  [{i}] {renderer.Topology.Panels[i].Id} {renderer.Topology.Panels[i].Kind}");
        sb.AppendLine($"scene panels: {OverlaySceneBuilder.Build(timeline, layout.Geometry).Panels.Length}");
        sb.AppendLine("notes:");
        foreach (NoteEvent n in timeline.Notes)
        {
            sb.AppendLine($"  {n.ChannelId} {n.StartSample}..{n.EndSample} midi={n.InitialMidiNote:F2} " +
                          $"pitch={string.Join(";", n.Pitch.Select(p => $"{p.SamplePosition}:{p.MidiNote:F2}"))} " +
                          $"sampleId={n.SampleId ?? "-"}");
        }
        sb.AppendLine("camera ranges:");
        for (int i = 0; i < renderer.Topology.Panels.Count; i++)
        {
            var r = renderer.PitchRangeAt(i, sample);
            sb.AppendLine($"  [{i}] {(r.HasValue ? $"{r.Value.MinMidi:F2}..{r.Value.MaxMidi:F2}" : "no camera")}");
        }
        File.WriteAllText("/tmp/spc-probe-state.txt", sb.ToString());
        Assert.NotNull(frame);
    }
}