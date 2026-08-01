using Fmp.Core.Audio;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class GenericVisualizationArchitectureTests
{
    [Fact]
    public void GenericRenderingNamespaceDoesNotReferenceChipSpecificFormats()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string renderingRoot = Path.Combine(
            projectRoot, "src", "MDPlayer.Fmp.Core", "Visualization", "Rendering");
        string[] forbidden =
        [
            "YM2608", "PPZ8", "SegaPCM", "C140", "C352", "K051649",
            "HuC6280", "SNES",
        ];
        string[] files = Directory.EnumerateFiles(renderingRoot, "*.cs").ToArray();

        Assert.NotEmpty(files);
        foreach (string file in files)
        {
            string source = File.ReadAllText(file);
            foreach (string name in forbidden)
                Assert.DoesNotContain(name, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void PanelRendererDispatchDoesNotUseLegacyPanelKinds()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string renderingRoot = Path.Combine(
            projectRoot, "src", "MDPlayer.Fmp.Core", "Visualization", "Rendering");
        string[] files = Directory.EnumerateFiles(renderingRoot, "PanelOverlayRenderer*.cs").ToArray();

        Assert.NotEmpty(files);
        foreach (string file in files)
            Assert.DoesNotContain("PanelKind", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TopologyAndScenePreparationDoNotParseChannelIdStructure()
    {
        string projectRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        string renderingRoot = Path.Combine(
            projectRoot, "src", "MDPlayer.Fmp.Core", "Visualization", "Rendering");
        string[] files =
        [
            Path.Combine(renderingRoot, "VisualizationTopology.cs"),
            Path.Combine(renderingRoot, "OverlaySceneBuilder.cs"),
        ];

        foreach (string file in files)
            Assert.DoesNotContain("StartsWith", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void TypedIds_PreserveStablePanelStrings()
    {
        var ym = new DeviceId(ChipType.Ym2608, 0);

        Assert.Equal("ym2608.0", ym.ToString());
        Assert.Equal("ym2608.0.fm.1", new VoiceId(ym, VoiceKind.Fm, 0).ToString());
        Assert.Equal("ym2608.0.fm3.op.3", new VoiceId(ym, VoiceKind.Fm3Operator, 2).ToString());
        Assert.Equal("ym2608.0.rhythm", new VoiceId(ym, VoiceKind.Rhythm, 0, Name: "rhythm").ToString());
        Assert.Equal("ym2608.0.adpcm-b", new VoiceId(ym, VoiceKind.Adpcm, 0, Name: "adpcm-b").ToString());
        Assert.Equal("ppz8.0", new VoiceId(new DeviceId(ChipType.Ppz8, 0), VoiceKind.Pcm, 0, Name: "ppz8").ToString());
    }

    [Fact]
    public void TimelineBuilder_ProducesSchemaV2AndCapabilities()
    {
        var builder = new TimelineBuilder(44_100);
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        builder.AddDevice(device);
        VoiceId voice = new(device.Id, VoiceKind.Fm, 0);
        builder.AddVoice(new VoiceDescriptor(
            voice,
            "FM1",
            VoicePresentationKind.Fm,
            0,
            false,
            false,
            true));
        builder.AddNote(
            voice,
            100,
            900,
            60,
            261.63,
            "ym2608:test",
            VisualizationNoteMode.Fm,
            false,
            [new PitchChange(500, 262, 60.02)]);
        builder.AddInstrument(new InstrumentDefinition(
            "ym2608:test",
            "fm",
            null,
            null,
            null,
            null,
            Array.Empty<FmOperatorDefinition>()));
        builder.AddWarning("device warning");

        VisualizationTimeline timeline = builder.Build(1_000, "test", new TrackMetadata("vgm", "Example", "test"));

        Assert.Equal(2, timeline.SchemaVersion);
        Assert.Equal("vgm", timeline.Source.SourceFormat);
        Assert.Equal(new[] { "channelScopes", "continuousPitch", "instruments", "notes" }, timeline.Capabilities);
        Assert.Single(timeline.Devices);
        Assert.Single(timeline.Voices);
        Assert.Single(timeline.Notes);
        Assert.Equal("device warning", Assert.Single(timeline.Warnings));
    }

    [Fact]
    public void DecoderSink_UsesRegistryAndKeepsUnsupportedDevicesVisible()
    {
        var sink = new TimelineDecoderEventSink(44_100);
        sink.OnDevice(VisualizationDeviceCatalog.Ym2608());
        sink.OnDevice(new DeviceDescriptor(
            new DeviceId(ChipType.Unknown, 0),
            "Unknown #0",
            0,
            DeviceCapabilities.Notes));

        sink.OnChipWrite(new TimedChipWrite(0, new DeviceId(ChipType.Ym2608, 0), 0, 0xA0, 0x35));
        sink.OnChipWrite(new TimedChipWrite(0, new DeviceId(ChipType.Ym2608, 0), 0, 0xA4, 0x21));
        sink.OnChipWrite(new TimedChipWrite(100, new DeviceId(ChipType.Ym2608, 0), 0, 0x28, 0xF0));
        sink.OnChipWrite(new TimedChipWrite(500, new DeviceId(ChipType.Ym2608, 0), 0, 0x28, 0x00));

        VisualizationTimeline timeline = sink.Complete(1_000, "test");

        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Ym2608);
        Assert.Contains(timeline.Devices, device => device.Id.Type == ChipType.Unknown);
        Assert.Contains(timeline.Warnings, warning => warning.Contains("unknown.0", StringComparison.Ordinal));
        Assert.Contains(timeline.Notes, note => note.ChannelId == "ym2608.0.fm.1");
    }

    [Fact]
    public void DecoderSink_PreservesLoopStartAndRestartMarkers()
    {
        var sink = new TimelineDecoderEventSink(44_100);
        sink.OnLoopBoundary(new TimedLoopBoundary(0, 0));
        sink.OnLoopBoundary(new TimedLoopBoundary(1_000, 1));

        VisualizationTimeline timeline = sink.Complete(2_000, "test");
        Assert.Equal(LoopMarkerKind.Start, timeline.LoopMarkers[0].Kind);
        Assert.Equal(LoopMarkerKind.Restart, timeline.LoopMarkers[1].Kind);
        Assert.Equal(1, timeline.LoopMarkers[1].Iteration);
    }

    [Fact]
    public void FmpAdapter_EmitsNormalizedWritesAndForwardsAudioSinkCalls()
    {
        var events = new RecordingEventSink();
        var downstream = new RecordingChipSink();
        var adapter = new FmpPlaybackEventSinkAdapter(events, downstream);

        adapter.WriteYm2608(0, 1, 0xA0, 0x35, 12);
        adapter.WritePpz8(0, 3, 0x44, 20);

        Assert.Equal(2, events.Writes.Count);
        Assert.Equal(2, downstream.Writes.Count);
        Assert.Equal(new DeviceId(ChipType.Ym2608, 0), events.Writes[0].Device);
        Assert.Equal(new DeviceId(ChipType.Ppz8, 0), events.Writes[1].Device);
        Assert.Equal(2, events.Devices.Count);
    }

    [Fact]
    public void TopologyBuilder_UsesMixedDeviceVoicesAndDynamicGrid()
    {
        var sink = new TimelineDecoderEventSink(44_100);
        sink.OnDevice(VisualizationDeviceCatalog.Ym2612());
        sink.OnDevice(VisualizationDeviceCatalog.Sn76489());
        VisualizationTimeline timeline = sink.Complete(1_000, "test");

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(timeline);
        var layout = new OverlayLayout(960, 540, 0.75, 2.25, topology.Panels.Count);

        Assert.Equal(11, topology.Panels.Count);
        Assert.Equal(3, layout.ColumnCount);
        Assert.Equal(4, layout.RowCount);
        Assert.Equal("ym2612.0.fm.1", topology.Panels[0].Id);
        Assert.Equal("sn76489.0.psg.1", topology.Panels[^4].Id);
    }

    [Fact]
    public void TopologyBuilder_MapsPcmVoicesToPcmVoicePanels()
    {
        var builder = new TimelineBuilder(44_100);
        DeviceDescriptor device = VisualizationDeviceCatalog.Ymf278b();
        builder.AddDevice(device);
        VoiceId pitchedId = new(device.Id, VoiceKind.Pcm, 0);
        builder.AddVoice(new VoiceDescriptor(
            pitchedId, "PCM 1", VoicePresentationKind.Pcm, 23, false, false, true));
        VoiceId activityId = new(new DeviceId(ChipType.Okim6295, 0), VoiceKind.Pcm, 0);
        builder.AddVoice(new VoiceDescriptor(
            activityId, "Voice 1", VoicePresentationKind.Pcm, 0, true, false, false));

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            builder.Build(1_000, "test"));

        // Pcm presentation maps to the deliberate PcmVoice panel kind for any
        // pitch/percussion combination (spec §4.1); specialized renderers key
        // off the panel kind, not pitch capability flags.
        Assert.Equal(PreparedPanelKind.PcmVoice,
            Assert.Single(topology.Panels, panel => panel.Id == pitchedId.ToString()).Kind);
        Assert.Equal(PreparedPanelKind.PcmVoice,
            Assert.Single(topology.Panels, panel => panel.Id == activityId.ToString()).Kind);
    }

    [Fact]
    public void TopologyBuilder_FocusKeepsOnlyPanelsWithTrackActivity()
    {
        var device = new DeviceId(ChipType.Ym2608, 0);
        var fm1 = new VoiceId(device, VoiceKind.Fm, 0);
        var fm2 = new VoiceId(device, VoiceKind.Fm, 1);
        var timeline = new VisualizationTimeline
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Voices =
            [
                new VoiceDescriptor(fm1, "FM1", VoicePresentationKind.Fm, 0, false, false, true),
                new VoiceDescriptor(fm2, "FM2", VoicePresentationKind.Fm, 1, false, false, true),
            ],
            Notes =
            [
                new NoteEvent(
                    fm1.ToString(), 100, 900, 261.63, 60, "instrument",
                    VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>()),
            ],
        };

        VisualizationTopology focus = VisualizationTopologyBuilder.Build(
            timeline, VisualizationLayoutMode.Focus);

        Assert.Single(focus.Panels);
        Assert.Equal(fm1.ToString(), focus.Panels[0].Id);
        Assert.Equal(0, focus.Panels[0].Order);
    }

    [Fact]
    public void ScopePlanner_OnlyReturnsStrategiesWithExecutors()
    {
        DeviceDescriptor[] vgmDevices =
        [
            VisualizationDeviceCatalog.Ym2612(),
            VisualizationDeviceCatalog.Sn76489(),
        ];
        VoiceDescriptor[] vgmVoices =
        [
            .. VisualizationDeviceCatalog.Ym2612Voices(),
            .. VisualizationDeviceCatalog.Sn76489Voices(),
        ];

        StemPlan vgm = ScopePlanner.Plan("vgm", vgmDevices, vgmVoices, "auto");
        StemPlan rejected = ScopePlanner.Plan("mdplayer", vgmDevices, vgmVoices, "channel");
        StemPlan device = ScopePlanner.Plan("vgm", vgmDevices, vgmVoices, "device");
        StemPlan fmp = ScopePlanner.Plan(
            "fmp",
            [VisualizationDeviceCatalog.Ym2608(), VisualizationDeviceCatalog.Ppz8()],
            VisualizationDeviceCatalog.Ym2608Voices(),
            "channel");

        Assert.Equal(StemStrategy.VgmRenderedStems, vgm.Strategy);
        Assert.True(vgm.Supported);
        Assert.False(rejected.Supported);
        Assert.False(device.Supported);
        Assert.Equal(StemStrategy.FmpParallelSynthesis, fmp.Strategy);
        Assert.True(fmp.Supported);
    }

    [Fact]
    public void BackendRegistry_SelectsByProbeInsteadOfExtension()
    {
        var backend = new ProbeBackend();
        var registry = new PlaybackBackendRegistry([backend]);
        var input = new FileInfo(Path.Combine(Path.GetTempPath(), "track.not-a-known-extension"));

        bool selected = registry.TrySelect(
            input,
            new PlaybackEnvironment([Path.GetTempPath()]),
            out IPlaybackBackend selectedBackend,
            out PlaybackProbeResult probe);

        Assert.True(selected);
        Assert.Same(backend, selectedBackend);
        Assert.True(probe.Supported);
        Assert.Equal("probe", probe.Format);
    }

    private sealed class RecordingEventSink : IPlaybackEventSink
    {
        public List<DeviceDescriptor> Devices { get; } = [];
        public List<TimedChipWrite> Writes { get; } = [];

        public void OnDevice(in DeviceDescriptor device) => Devices.Add(device);
        public void OnChipWrite(in TimedChipWrite write) => Writes.Add(write);
        public void OnMidi(in TimedMidiMessage message) { }
        public void OnSampleAsset(in TimedSampleAssetEvent asset) { }
        public void OnLoopBoundary(in TimedLoopBoundary loop) { }
    }

    private sealed class RecordingChipSink : IFmpChipSink
    {
        public List<(int Port, int Address, int Value)> Writes { get; } = [];

        public void WriteYm2608(int chipId, int port, int address, int value, long samplePosition) =>
            Writes.Add((port, address, value));

        public void LoadPpz8Bank(int bank, int mode, ReadOnlyMemory<byte>[] samples, long samplePosition) { }

        public void WritePpz8(int port, int address, int value, long samplePosition) =>
            Writes.Add((port, address, value));
    }

    private sealed class ProbeBackend : IPlaybackBackend
    {
        public string Id => "probe";

        public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment) =>
            new(true, "probe", [], [], []) { Visualizable = true, Portable = true };

        public IPlaybackCaptureSession Open(
            FileInfo input,
            PlaybackOptions options,
            IPlaybackEventSink eventSink) => throw new NotSupportedException();
    }
}
