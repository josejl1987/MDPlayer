using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Spec §39/§40 regression protection: the renderer must branch on the
/// semantic pitched/unpitched distinction of the events it draws — SPC BRR
/// playback reads as pitched voices (no subordinate sample-state lanes),
/// while YM2612 DAC keeps reading as an unpitched sample/event stream.
/// These tests would fail against the old behaviour (sample identity rows
/// layered over every pitched SPC voice).
/// </summary>
public sealed class SpcPcmRenderingSemanticsTests
{
    private static VisualizationTimeline DecodeSpc()
    {
        var builder = new TimelineBuilder(32_000);
        var decoder = new SnesDspTimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.SnesDsp(), builder);
        byte[] encoded = new byte[9];
        encoded[0] = 0x01; // one terminal BRR block
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
                null,
                0),
        ]);

        // Voice 2: pitched BRR A with one pitch bend.
        decoder.Process(SpcSemanticEvent.KeyOn(100, 2, sourceNumber: 3, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(400, 2, 0x1800));
        decoder.Process(SpcSemanticEvent.VoiceEnd(900, 2));

        // Voice 7: pitched BRR A at another pitch (sample reuse across voices).
        decoder.Process(SpcSemanticEvent.KeyOn(150, 7, sourceNumber: 3, effectivePitch: 0x0800));
        decoder.Process(SpcSemanticEvent.VoiceEnd(800, 7));

        decoder.Complete(1_000);
        return builder.Build(1_000, "spc-render-test");
    }

    private static VisualizationTimeline DecodeDac()
    {
        var builder = new TimelineBuilder(44_100);
        var decoder = new Ym2612TimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.Ym2612(), builder);
        foreach (TimedChipWrite write in new[]
        {
            Write(0, 0x2B, 0x80),
            Write(0, 0x2A, 0x10),
            Write(8, 0x2A, 0x20),
            Write(16, 0x2A, 0x30),
            Write(24, 0x2A, 0x40),
            Write(40, 0x2B, 0x00),
        })
            decoder.Process(write);
        decoder.Complete(64);
        return builder.Build(64, "dac-render-test");
    }

    private static TimedChipWrite Write(long sample, int address, int data) => new(
        sample,
        new DeviceId(ChipType.Ym2612, 0),
        0,
        address,
        data);

    private static PanelOverlayRenderer CreateRenderer(VisualizationTimeline timeline, int width = 960)
        => new(
            timeline,
            RendererTestLayout.Build(timeline, width: width),
            new PanelOverlayRenderer.Options
            {
                FpsNumerator = 20,
                FpsDenominator = 1,
            });

    private static int PanelIndexOf(OverlayScene scene, string panelId)
    {
        for (int i = 0; i < scene.Panels.Length; i++)
        {
            if (scene.Panels[i].Id == panelId)
                return i;
        }
        return -1;
    }

    [Fact]
    public void SpcBrrPlayback_IsRenderedAsPitchedVoice_NotSampleStateLane()
    {
        VisualizationTimeline timeline = DecodeSpc();
        var layout = RendererTestLayout.Build(timeline, width: 1920);
        OverlayScene scene = OverlaySceneBuilder.Build(timeline, layout.Geometry);

        int voice2 = PanelIndexOf(scene, "snesdsp.0.pcmvoice.3");
        int voice7 = PanelIndexOf(scene, "snesdsp.0.pcmvoice.8");
        Assert.True(voice2 >= 0 && voice7 >= 0);

        // Semantic timeline: pitched notes with sample identity metadata.
        PreparedNote note = Assert.Single(scene.Panels[voice2].MainNotes);
        Assert.Equal(100L, note.StartSample);
        Assert.Equal(900L, note.EndSample);
        Assert.Equal("BRR abcdef01", note.SampleDisplayLabel);
        Assert.Equal(SamplePlaybackSemantics.Pitched,
            Assert.Single(scene.Panels[voice2].SamplePlayback).Semantics);
        Assert.Single(note.Pitch); // the one pitch bend stays a trajectory point
        Assert.Equal(1, scene.Panels[voice7].MainNotes.Length);

        using var renderer = CreateRenderer(timeline, width: 1920);
        // Pitched BRR playback never becomes a sample-identity lane.
        Assert.Empty(renderer.SampleRowLabelsOf(voice2));
        Assert.Empty(renderer.SampleRowLabelsOf(voice7));
        // The voice maps Y from musical pitch.
        Assert.True(renderer.HasPitchCamera(voice2));
        Assert.True(renderer.HasPitchCamera(voice7));
        // Smoke render over the pitched window.
        byte[] frame = renderer.RenderFrame(0);
        Assert.NotNull(frame);
    }

    [Fact]
    public void Ym2612DacPlayback_RemainsUnpitched()
    {
        VisualizationTimeline timeline = DecodeDac();
        var layout = RendererTestLayout.Build(timeline);
        OverlayScene scene = OverlaySceneBuilder.Build(timeline, layout.Geometry);

        int dac = PanelIndexOf(scene, "ym2612.0.pcm.dac");
        Assert.True(dac >= 0);

        // DAC stays an unpitched sample/event stream: no pitched notes, no
        // pitch-derived Y, sample identity rows preserved.
        Assert.Empty(scene.Panels[dac].MainNotes);
        Assert.All(scene.Panels[dac].SamplePlayback,
            playback => Assert.Equal(SamplePlaybackSemantics.Unpitched, playback.Semantics));

        using var renderer = CreateRenderer(timeline);
        Assert.NotNull(renderer.SampleRowLabelsOf(dac));
        Assert.NotEmpty(renderer.SampleRowLabelsOf(dac));
        Assert.False(renderer.HasPitchCamera(dac));
        byte[] frame = renderer.RenderFrame(0);
        Assert.NotNull(frame);
    }

    [Fact]
    public void SpcSampleSwitching_RetainsBothSampleIdentitiesInNotes()
    {
        // §38: source retriggers stay separated — no identity is lost.
        var builder = new TimelineBuilder(32_000);
        var decoder = new SnesDspTimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.SnesDsp(), builder);
        byte[] encoded = new byte[9];
        encoded[0] = 0x01;
        decoder.SetSamples([
            new SpcSampleEntry("1111111111111111", "11111111", [1], 0x2000, 0x2000, false, encoded, "relative", null, 0),
            new SpcSampleEntry("2222222222222222", "22222222", [4], 0x2000, 0x2000, false, encoded, "relative", null, 0),
        ]);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, sourceNumber: 1, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(100, 0));
        decoder.Process(SpcSemanticEvent.KeyOn(200, 0, sourceNumber: 4, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(300, 0));

        VisualizationTimeline result = builder.Build(400);
        Assert.Equal("sample:11111111", result.Notes[0].SampleId);
        Assert.Equal("sample:22222222", result.Notes[1].SampleId);
        Assert.NotEqual(result.Notes[0].SampleId, result.Notes[1].SampleId);
    }
}