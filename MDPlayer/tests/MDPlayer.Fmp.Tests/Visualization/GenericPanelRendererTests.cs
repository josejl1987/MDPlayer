using System.Security.Cryptography;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class GenericPanelRendererTests
{
    private const int SampleRate = 1_000;

    [Fact]
    public void WavetablePanelDrawsWaveformAndKeepsNoteActivity()
    {
        VoiceDescriptor voice = Voice(ChipType.Huc6280, VoiceKind.Wavetable,
            VoicePresentationKind.Wavetable, "Wave");
        WaveformDefinition waveform = VisualizationAssetBuilder.CreateWaveform(
            "wavetable", [0, 1, 0, -1], "WAVE 1");
        string voiceId = voice.Id.ToString();
        VisualizationTimeline withWaveform = Timeline(
            voice,
            notes: [Note(voiceId, 0, 1_000, 60)],
            waveforms: [waveform],
            waveformChanges: [new WaveformChangeEvent(voiceId, 0, waveform.Id)]);
        VisualizationTimeline withoutWaveform = Timeline(
            voice,
            notes: [Note(voiceId, 0, 1_000, 60)]);

        byte[] rendered = Render(withWaveform, 5);
        byte[] unavailable = Render(withoutWaveform, 5);

        Assert.NotEqual(Hash(rendered), Hash(unavailable));
    }

    [Fact]
    public void PcmPanelShowsSampleIdentityAndPlaybackCursor()
    {
        VoiceDescriptor voice = Voice(ChipType.SnesDsp, VoiceKind.PcmVoice,
            VoicePresentationKind.Pcm, "VOICE 1");
        SampleDefinition sample = VisualizationAssetBuilder.CreateSample(
            "brr", [-1, -0.5f, 0, 0.5f, 1], 32_000, 2, 5, SampleLoopMode.Forward, "BRR 1234");
        string voiceId = voice.Id.ToString();
        VisualizationTimeline timeline = Timeline(
            voice,
            samples: [sample],
            samplePlayback: [new SamplePlaybackEvent(
                voiceId, 0, 1_000, sample.Id, 60, 1, 0.8f, 0.25f, false, true)]);

        byte[] beforeLoop = Render(timeline, 1);
        byte[] inLoop = Render(timeline, 7);

        Assert.NotEqual(Hash(beforeLoop), Hash(inLoop));
    }

    [Fact]
    public void NoisePanelDoesNotDependOnRandomState()
    {
        VoiceDescriptor voice = Voice(ChipType.NesApu, VoiceKind.Noise,
            VoicePresentationKind.Noise, "Noise");
        VisualizationTimeline timeline = Timeline(
            voice,
            noise: [new NoiseStateEvent(
                voice.Id.ToString(), 0, 1_000, 8_200, 4, 0.75f, NoiseMode.Periodic)]);

        byte[] first = Render(timeline, 4);
        byte[] second = Render(timeline, 4);

        Assert.Equal(Hash(first), Hash(second));
    }

    [Fact]
    public void AggregateTopologySplitsStableSubvoicesAfterSixteen()
    {
        VoiceDescriptor voice = Voice(ChipType.Unknown, VoiceKind.Aggregate,
            VoicePresentationKind.Aggregate, "Pads");
        string voiceId = voice.Id.ToString();
        AggregateHitEvent[] hits = Enumerable.Range(0, 17)
            .Select(index => new AggregateHitEvent(
                voiceId,
                $"pad-{index:00}",
                $"Pad {index:00}",
                100 + index,
                0.5f,
                0,
                null))
            .ToArray();
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            Timeline(voice, aggregateHits: hits));

        Assert.Equal(2, topology.Panels.Count);
        Assert.Equal($"{voiceId}.group.1", topology.Panels[0].Id);
        Assert.Equal($"{voiceId}.group.2", topology.Panels[1].Id);
        Assert.Equal(16, topology.Panels[0].AggregateSubVoiceIds.Count);
        Assert.Single(topology.Panels[1].AggregateSubVoiceIds);
    }

    private static VisualizationTimeline Timeline(
        VoiceDescriptor voice,
        NoteEvent[] notes = null,
        WaveformDefinition[] waveforms = null,
        WaveformChangeEvent[] waveformChanges = null,
        SampleDefinition[] samples = null,
        SamplePlaybackEvent[] samplePlayback = null,
        NoiseStateEvent[] noise = null,
        AggregateHitEvent[] aggregateHits = null) => new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate,
            Devices = [new DeviceDescriptor(voice.Id.Device, voice.Id.Device.ToString(), 1, DeviceCapabilities.Notes)],
            Voices = [voice],
            Notes = notes ?? [],
            Waveforms = waveforms ?? [],
            WaveformChanges = waveformChanges ?? [],
            Samples = samples ?? [],
            SamplePlayback = samplePlayback ?? [],
            NoiseStates = noise ?? [],
            AggregateHits = aggregateHits ?? [],
        };

    private static VoiceDescriptor Voice(
        ChipType chip,
        VoiceKind kind,
        VoicePresentationKind presentation,
        string label) => new(
            new VoiceId(new DeviceId(chip, 0), kind, 0),
            label,
            presentation,
            0,
            false,
            kind == VoiceKind.Noise,
            presentation is VoicePresentationKind.Pitched or VoicePresentationKind.Wavetable
                or VoicePresentationKind.Pcm);

    private static NoteEvent Note(string voiceId, long start, long end, double midi) => new(
        voiceId,
        start,
        end,
        440 * Math.Pow(2, (midi - 69) / 12),
        midi,
        "instrument",
        VisualizationNoteMode.Pcm,
        false,
        []);

    private static byte[] Render(VisualizationTimeline timeline, long frame)
    {
        var renderer = new PanelOverlayRenderer(timeline, RendererTestLayout.Build(timeline), new PanelOverlayRenderer.Options
        {
            FpsNumerator = 10,
            FpsDenominator = 1,
        });
        return renderer.RenderFrame(frame);
    }

    private static string Hash(byte[] frame) => Convert.ToHexString(SHA256.HashData(frame));
}
