using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Spec §4/§5 topology gap closure: every defined voice presentation maps to a
/// deliberate panel kind (placeholder is a true fallback for external values
/// only), and every built-in ChipType has an explicit device priority.
/// </summary>
public sealed class VisualizationTopologyGapTests
{
    private static VoiceDescriptor Voice(
        VoicePresentationKind presentation,
        VoiceKind kind = VoiceKind.Fm,
        bool isPercussion = false,
        bool supportsPitch = true) =>
        new(
            new VoiceId(new DeviceId(ChipType.Ym2608, 0), kind, 0),
            "V",
            presentation,
            0,
            isPercussion,
            kind == VoiceKind.Noise,
            supportsPitch);

    [Theory]
    [InlineData((int)VoicePresentationKind.Fm3, (int)PreparedPanelKind.Fm3)]
    [InlineData((int)VoicePresentationKind.Psg, (int)PreparedPanelKind.Ssg)]
    [InlineData((int)VoicePresentationKind.Percussion, (int)PreparedPanelKind.Rhythm)]
    [InlineData((int)VoicePresentationKind.Aggregate, (int)PreparedPanelKind.Aggregate)]
    [InlineData((int)VoicePresentationKind.Noise, (int)PreparedPanelKind.Noise)]
    [InlineData((int)VoicePresentationKind.Pcm, (int)PreparedPanelKind.PcmVoice)]
    [InlineData((int)VoicePresentationKind.Fm, (int)PreparedPanelKind.Pitched)]
    [InlineData((int)VoicePresentationKind.Pitched, (int)PreparedPanelKind.Pitched)]
    [InlineData((int)VoicePresentationKind.Midi, (int)PreparedPanelKind.Pitched)]
    public void MapKind_PresentationMapsToDeliberatePanel(
        int presentation,
        int expected) =>
        Assert.Equal((PreparedPanelKind)expected,
            VisualizationTopologyBuilder.MapKind(Voice((VoicePresentationKind)presentation)));

    [Fact]
    public void MapKind_PcmMapsToPcmVoiceRegardlessOfPitchOrPercussion()
    {
        Assert.Equal(PreparedPanelKind.PcmVoice,
            VisualizationTopologyBuilder.MapKind(Voice(VoicePresentationKind.Pcm, VoiceKind.Pcm, supportsPitch: true)));
        Assert.Equal(PreparedPanelKind.PcmVoice,
            VisualizationTopologyBuilder.MapKind(Voice(VoicePresentationKind.Pcm, VoiceKind.Pcm, isPercussion: true, supportsPitch: false)));
    }

    [Theory]
    [InlineData((int)VoicePresentationKind.Pitched)]
    [InlineData((int)VoicePresentationKind.Fm)]
    [InlineData((int)VoicePresentationKind.Midi)]
    public void MapKind_PitchedNoiseVoiceMapsToNoise(int presentation) =>
        Assert.Equal(PreparedPanelKind.Noise,
            VisualizationTopologyBuilder.MapKind(Voice((VoicePresentationKind)presentation, VoiceKind.Noise)));

    [Theory]
    [InlineData((int)VoicePresentationKind.Pitched)]
    [InlineData((int)VoicePresentationKind.Fm)]
    [InlineData((int)VoicePresentationKind.Midi)]
    public void MapKind_PitchedWavetableVoiceMapsToWavetable(int presentation) =>
        Assert.Equal(PreparedPanelKind.Wavetable,
            VisualizationTopologyBuilder.MapKind(Voice((VoicePresentationKind)presentation, VoiceKind.Wavetable)));

    [Fact]
    public void NoKnownRenderableVoiceMapsToPlaceholder()
    {
        // Every defined (presentation x kind) pair must map to a deliberate
        // panel kind; Placeholder is reserved for out-of-range external values.
        foreach (VoicePresentationKind presentation in Enum.GetValues<VoicePresentationKind>())
        foreach (VoiceKind kind in Enum.GetValues<VoiceKind>())
        {
            Assert.NotEqual(PreparedPanelKind.Placeholder,
                VisualizationTopologyBuilder.MapKind(Voice(presentation, kind)));
        }
    }

    [Fact]
    public void BuiltInCatalogVoicesNeverMapToPlaceholder()
    {
        var voices = new List<VoiceDescriptor>();
        voices.AddRange(VisualizationDeviceCatalog.Ym2203Voices());
        voices.AddRange(VisualizationDeviceCatalog.Ym2608Voices());
        voices.AddRange(VisualizationDeviceCatalog.Ym2610Voices());
        voices.AddRange(VisualizationDeviceCatalog.Ym2612Voices());
        voices.AddRange(VisualizationDeviceCatalog.Ym2151Voices());
        voices.AddRange(VisualizationDeviceCatalog.Ym2413Voices());
        foreach (ChipType type in new[] { ChipType.Ym3526, ChipType.Ym3812, ChipType.Y8950, ChipType.Ymf262, ChipType.Ymf278b })
            voices.AddRange(VisualizationDeviceCatalog.OplVoices(type));
        voices.AddRange(VisualizationDeviceCatalog.Ymz280bVoices());
        foreach ((ChipType type, int count) in new[]
        {
            (ChipType.SegaPcm, 16), (ChipType.Rf5c68, 8), (ChipType.Rf5c164, 8),
            (ChipType.C140, 24), (ChipType.C352, 32), (ChipType.K054539, 8),
            (ChipType.Ga20, 4),
        })
            voices.AddRange(VisualizationDeviceCatalog.PcmVoices(type, 0, count));
        voices.AddRange(VisualizationDeviceCatalog.Okim6258Voices());
        voices.AddRange(VisualizationDeviceCatalog.Okim6295Voices());
        voices.AddRange(VisualizationDeviceCatalog.MultiPcmVoices());
        voices.AddRange(VisualizationDeviceCatalog.Sn76489Voices());
        voices.AddRange(VisualizationDeviceCatalog.Ay8910Voices());
        voices.AddRange(VisualizationDeviceCatalog.DmgVoices());
        voices.AddRange(VisualizationDeviceCatalog.NesApuVoices());
        voices.AddRange(VisualizationDeviceCatalog.Huc6280Voices());
        voices.AddRange(VisualizationDeviceCatalog.K051649Voices());
        voices.AddRange(VisualizationDeviceCatalog.MidiVoices());
        voices.AddRange(VisualizationDeviceCatalog.Ppz8Voices());
        voices.AddRange(VisualizationDeviceCatalog.PcmVoices(ChipType.Pcm, 0, 1));
        voices.AddRange(VisualizationDeviceCatalog.SnesDspVoices());

        Assert.NotEmpty(voices);
        Assert.All(voices, voice => Assert.NotEqual(
            PreparedPanelKind.Placeholder,
            VisualizationTopologyBuilder.MapKind(voice)));
    }

    [Fact]
    public void MapKind_ExternalPresentationOnOrdinaryVoiceStaysPlaceholder()
    {
        var voice = Voice((VoicePresentationKind)999);
        Assert.Equal(PreparedPanelKind.Placeholder, VisualizationTopologyBuilder.MapKind(voice));
    }

    [Fact]
    public void DevicePriorityCoversEveryChipType()
    {
        ChipType[] values = Enum.GetValues<ChipType>();

        Assert.Equal(values.Length, DeviceOrdering.BuiltInDevicePriority.Count);

        foreach (ChipType value in values)
        {
            Assert.True(DeviceOrdering.BuiltInDevicePriority.ContainsKey(value), $"{value} missing");
            Assert.NotEqual(DeviceOrdering.UnknownExternalPriority, DeviceOrdering.Priority(value));
            Assert.False(DeviceOrdering.IsExternal(value));
        }
    }

    [Fact]
    public void DevicePriority_ExternalValueSortsLastAndIsFlagged()
    {
        var corrupted = (ChipType)999;
        Assert.Equal(DeviceOrdering.UnknownExternalPriority, DeviceOrdering.Priority(corrupted));
        Assert.True(DeviceOrdering.IsExternal(corrupted));
    }

    [Fact]
    public void DevicePriority_GroupRangesFollowPanelSemantics()
    {
        Assert.InRange(DeviceOrdering.Priority(ChipType.Ym2608), 100, 199);
        Assert.InRange(DeviceOrdering.Priority(ChipType.Ay8910), 200, 299);
        Assert.InRange(DeviceOrdering.Priority(ChipType.K051649), 300, 399);
        Assert.InRange(DeviceOrdering.Priority(ChipType.Huc6280), 300, 399);
        Assert.InRange(DeviceOrdering.Priority(ChipType.SnesDsp), 400, 499);
        Assert.InRange(DeviceOrdering.Priority(ChipType.Midi), 600, 699);
    }

    [Fact]
    public void Topology_PanelOrderIsDeterministicAcrossRuns()
    {
        VisualizationTimeline BuildTimeline() => new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices =
            [
                new DeviceDescriptor(new DeviceId(ChipType.SnesDsp, 0), "S-DSP", 1, DeviceCapabilities.Notes),
                new DeviceDescriptor(new DeviceId(ChipType.Ym2608, 0), "OPNA", 1, DeviceCapabilities.Notes),
                new DeviceDescriptor(new DeviceId(ChipType.Sn76489, 0), "PSG", 1, DeviceCapabilities.Notes),
            ],
            Voices =
            [
                new VoiceDescriptor(new VoiceId(new DeviceId(ChipType.SnesDsp, 0), VoiceKind.PcmVoice, 0),
                    "V1", VoicePresentationKind.Pcm, 0, false, false, true),
                new VoiceDescriptor(new VoiceId(new DeviceId(ChipType.Sn76489, 0), VoiceKind.Psg, 0),
                    "P1", VoicePresentationKind.Pitched, 0, false, false, true),
                new VoiceDescriptor(new VoiceId(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0),
                    "F1", VoicePresentationKind.Fm, 0, false, false, true),
            ],
        };

        VisualizationTopology first = VisualizationTopologyBuilder.Build(BuildTimeline());
        VisualizationTopology second = VisualizationTopologyBuilder.Build(BuildTimeline());

        string[] firstOrder = first.Panels.Select(panel => panel.Id).ToArray();
        Assert.Equal(second.Panels.Select(panel => panel.Id).ToArray(), firstOrder);
        // FM (1xx) precedes PSG (2xx) precedes PCM (4xx).
        Assert.Equal(["ym2608.0.fm.1", "sn76489.0.psg.1", "snesdsp.0.pcmvoice.1"], firstOrder);
    }

    [Fact]
    public void TimelineBuilder_ExternalChipTypeEmitsWarningAndSortsLast()
    {
        var builder = new TimelineBuilder(44_100);
        builder.AddDevice(new DeviceDescriptor(new DeviceId(ChipType.Ym2608, 0), "OPNA", 1, DeviceCapabilities.Notes));
        builder.AddDevice(new DeviceDescriptor(new DeviceId((ChipType)999, 0), "Ext", 1, DeviceCapabilities.Notes));

        VisualizationTimeline timeline = builder.Build(1_000, "test");

        Assert.Equal(ChipType.Ym2608, timeline.Devices[0].Id.Type);
        Assert.Equal((ChipType)999, timeline.Devices[1].Id.Type);
        Assert.Contains(timeline.Warnings, warning => warning.Contains("no built-in device priority", StringComparison.Ordinal));
    }
}
