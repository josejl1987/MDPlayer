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
        sb.AppendLine($"[synthetic scene] frameIndex={frameIndex} sample={sample} rate={timeline.SampleRate} " +
                      $"past={layout.Geometry.PastSeconds} future={layout.Geometry.FutureSeconds} " +
                      $"window=[{layout.Geometry.WindowStartSample(sample, timeline.SampleRate)}," +
                      $"{layout.Geometry.WindowEndSample(sample, timeline.SampleRate)}]");
        sb.AppendLine($"metrics: {renderer.Performance}");
        sb.AppendLine("topology:");
        for (int i = 0; i < renderer.Topology.Panels.Count; i++)
            sb.AppendLine($"  [{i}] {renderer.Topology.Panels[i].Id} {renderer.Topology.Panels[i].Kind}");
        OverlayScene scene = OverlaySceneBuilder.Build(timeline, layout.Geometry);
        PreparedNote note0 = scene.Panels[0].MainNotes[0];
        sb.AppendLine($"note0 fill={note0.Fill} accent={scene.Panels[0].Accent} " +
                      $"label='{note0.SampleDisplayLabel}' instrument={note0.InstrumentId}");
        sb.AppendLine($"sample playback [0]: midiPitch={scene.Panels[0].SamplePlayback[0].MidiPitch} " +
                      $"semantics={scene.Panels[0].SamplePlayback[0].Semantics}");
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

        // ---- REAL FILE DIAGNOSTIC ----
        string realTimelinePath = Environment.GetEnvironmentVariable("SPC_PROBE_TIMELINE");
        if (!string.IsNullOrWhiteSpace(realTimelinePath) && File.Exists(realTimelinePath))
        {
            string json = File.ReadAllText(realTimelinePath);
            VisualizationTimeline real = VisualizationJsonWriter.Read(realTimelinePath);
            var realLayout = RendererTestLayout.Build(real, width: 1280, height: 720);
            using var realRenderer = new PanelOverlayRenderer(
                real,
                realLayout,
                new PanelOverlayRenderer.Options
                {
                    FpsNumerator = 30,
                    FpsDenominator = 1,
                    EnablePerformanceMetrics = true,
                });
            long realSample = 800_000; // t=25s into the capture
            int realFrame = (int)(realSample * 30 / real.SampleRate);
            byte[] realFrameBytes = realRenderer.RenderFrame(realFrame);
            File.WriteAllBytes("/tmp/spc-real-frame.rgba", realFrameBytes);
            var rsb = new System.Text.StringBuilder();
            rsb.AppendLine($"[real file] sampleRate={real.SampleRate} sample={realSample} " +
                           $"window=[{realLayout.Geometry.WindowStartSample(realSample, real.SampleRate)}," +
                           $"{realLayout.Geometry.WindowEndSample(realSample, real.SampleRate)}] " +
                           $"notes={real.Notes.Count} samplePlayback={real.SamplePlayback.Length}");
            rsb.AppendLine($"metrics: {realRenderer.Performance}");
            rsb.AppendLine("notes in window:");
            long ws = realLayout.Geometry.WindowStartSample(realSample, real.SampleRate);
            long we = realLayout.Geometry.WindowEndSample(realSample, real.SampleRate);
            foreach (NoteEvent n in real.Notes)
            {
                if (n.EndSample <= ws || n.StartSample >= we)
                    continue;
                rsb.AppendLine($"  {n.ChannelId} {n.StartSample}..{n.EndSample} midi={n.InitialMidiNote:F2} " +
                              $"pitchPts={n.Pitch.Count} fill?");
            }
            rsb.AppendLine("camera ranges:");
            for (int i = 0; i < realRenderer.Topology.Panels.Count; i++)
            {
                var r = realRenderer.PitchRangeAt(i, realSample);
                rsb.AppendLine($"  [{i}] {(r.HasValue ? $"{r.Value.MinMidi:F2}..{r.Value.MaxMidi:F2}" : "no camera")}");
            }
            rsb.AppendLine("panel fill colors:");
            OverlayScene realScene = OverlaySceneBuilder.Build(real, realLayout.Geometry);
            for (int i = 0; i < realScene.Panels.Length; i++)
            {
                var notes = realScene.Panels[i].MainNotes;
                rsb.AppendLine($"  [{i}] {realScene.Panels[i].Id} notes={notes.Length} " +
                               $"accent={realScene.Panels[i].Accent} " +
                               (notes.Length > 0 ? $"firstFill={notes[0].Fill} firstMidi={notes[0].StartMidiNote:F2} " +
                                   $"firstLabel={notes[0].SampleDisplayLabel ?? "-"}" : "no notes"));
            }
            File.WriteAllText("/tmp/spc-real-state.txt", rsb.ToString());
        }
        Assert.NotNull(frame);
    }
}