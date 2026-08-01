using System.Security.Cryptography;
using Fmp.Cli;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

public sealed class VisualizationV3ContractTests
{
    [Theory]
    [InlineData(640, 360)]
    [InlineData(960, 540)]
    [InlineData(1280, 720)]
    [InlineData(1920, 1080)]
    [InlineData(2560, 1440)]
    [InlineData(3840, 2160)]
    public void PublishingLayoutMatrixEitherMeetsMinimumsOrFailsActionably(
        int width,
        int height)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        foreach (VisualizationLayoutMode mode in new[]
        {
            VisualizationLayoutMode.Diagnostic,
        })
        {
            try
            {
                var renderer = new PanelOverlayRenderer(
                    timeline,
                    new PanelOverlayRenderer.Options
                    {
                        Width = width,
                        Height = height,
                        FpsNumerator = 20,
                        LayoutMode = mode,
                        Channels = VisualizationChannelFilter.All,
                    });

                Assert.True(renderer.Layout.PanelWidth >= 300);
            }
            catch (ArgumentException ex)
            {
                Assert.Contains("reduce", ex.Message, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public void AllV3Layouts_RandomAndSequentialCompositeFramesMatch()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var cases = new[]
        {
            (VisualizationLayoutMode.Diagnostic, VisualizationScopePosition.Top),
        };

        foreach ((VisualizationLayoutMode mode, VisualizationScopePosition position) in cases)
        {
            var renderer = new PanelOverlayRenderer(
                timeline,
                new PanelOverlayRenderer.Options
                {
                    Width = 960,
                    Height = 540,
                    FpsNumerator = 20,
                    LayoutMode = mode,
                    ScopePosition = position,
                    Channels = VisualizationChannelFilter.All,
                });

            int[] queriedFrames = [0, 1, 7, 17, 61, 99];
            var expected = new Dictionary<long, byte[]>(queriedFrames.Length);
            byte[] scope = new byte[renderer.ScopeFrameByteCount];
            byte[] destination = new byte[renderer.FrameByteCount];
            byte[] direct = new byte[renderer.FrameByteCount];
            foreach (int frame in queriedFrames)
            {
                FillScope(scope, frame);
                renderer.RenderCompositeFrame(frame, scope, direct);
                expected[frame] = SHA256.HashData(direct);
            }

            var session = renderer.CreateSequentialSession();
            session.Initialize(destination);
            for (int frame = 0; frame <= queriedFrames[^1]; frame++)
            {
                FillScope(scope, frame);
                session.RenderNext(frame, scope, destination);
                if (expected.TryGetValue(frame, out byte[] expectedHash))
                {
                    byte[] actual = SHA256.HashData(destination);
                    if (!expectedHash.AsSpan().SequenceEqual(actual))
                    {
                        int diff = -1;
                        for (int k = 0; k < destination.Length; k++)
                        {
                            if (direct[k] != destination[k]) { diff = k; break; }
                        }
                        int px = diff / 4;
                        int pxX = px % renderer.Width;
                        int pxY = px / renderer.Width;
                        Console.WriteLine($"CASE mode={mode} pos={position} frame={frame} firstDiff={diff} pixel=({pxX},{pxY})");
                        var field = typeof(PanelOverlayRenderer).GetField("_dynamicRestoreRects",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                        var rects = (OverlayRect[])field!.GetValue(renderer)!;
                        foreach (var r in rects)
                        {
                            if (pxX >= r.X && pxX < r.Right && pxY >= r.Y && pxY < r.Bottom)
                                Console.WriteLine($"  contains: X={r.X} Y={r.Y} W={r.Width} H={r.Height}");
                        }
                    }
                    Assert.Equal(expectedHash, actual);
                }
            }
        }
    }

    [Fact]
    public void MotionBlurRenderingRemainsRandomAccessDeterministic()
    {
        var renderer = new PanelOverlayRenderer(
            VisualizationTimelineFixture.Create(),
            new PanelOverlayRenderer.Options
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 30,
                LayoutMode = VisualizationLayoutMode.Diagnostic,
                MotionBlurSamples = 3,
            });
        byte[] scope = new byte[renderer.ScopeFrameByteCount];
        FillScope(scope, 12);
        byte[] direct = new byte[renderer.FrameByteCount];
        renderer.RenderCompositeFrame(12, scope, direct);

        byte[] sequential = new byte[renderer.FrameByteCount];
        var session = renderer.CreateSequentialSession();
        session.Initialize(sequential);
        for (int frame = 0; frame <= 12; frame++)
        {
            FillScope(scope, frame);
            session.RenderNext(frame, scope, sequential);
        }

        Assert.Equal(SHA256.HashData(direct), SHA256.HashData(sequential));

        byte[][] parallel = [new byte[renderer.FrameByteCount], new byte[renderer.FrameByteCount]];
        Parallel.For(0, parallel.Length, index =>
            renderer.RenderCompositeFrame(12, scope, parallel[index]));
        Assert.Equal(SHA256.HashData(direct), SHA256.HashData(parallel[0]));
        Assert.Equal(SHA256.HashData(direct), SHA256.HashData(parallel[1]));
    }

    [Fact]
    public void DefaultPalettePassesContrastAndColorVisionChecks()
    {
        Assert.True(VisualizationAccessibility.ContrastRatio(
            VisualizationPalette.Default.BrightText,
            VisualizationPalette.Default.CanvasBackground) >= 4.5);

        OverlayColor[] colors = Enumerable.Range(0, 12)
            .Select(index => InstrumentColorResolver.ResolveChannelAccent($"track:{index}"))
            .ToArray();
        foreach (ColorVisionDeficiency deficiency in Enum.GetValues<ColorVisionDeficiency>())
        {
            double minimumDistance = deficiency is ColorVisionDeficiency.Protanopia
                or ColorVisionDeficiency.Deuteranopia ? 12 : 6;
            for (int index = 0; index < colors.Length - 1; index++)
            {
                Assert.True(
                    VisualizationAccessibility.Distinguishable(
                        colors[index],
                        colors[index + 1],
                        deficiency,
                        minimumDistance),
                    $"Adjacent channel colors are not distinguishable under {deficiency}.");
            }
        }
    }

    [Fact]
    public void CustomPaletteChangesPreparedPixels()
    {
        VisualizationPalette palette = VisualizationPalette.ParseJson("""
        {"canvasBackground":"#010203","headerBackground":"#040506","timelineBackground":"#070809","brightText":"#ffffff","noteColors":["#ff0000"],"accentColors":["#00ff00"]}
        """);
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var layout = new PanelOverlayRenderer(
            timeline,
            new PanelOverlayRenderer.Options
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 30,
                LayoutMode = VisualizationLayoutMode.Diagnostic,
                Palette = palette,
            });
        var baseline = new PanelOverlayRenderer(
            timeline,
            new PanelOverlayRenderer.Options
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 30,
                LayoutMode = VisualizationLayoutMode.Diagnostic,
            });
        byte[] customFrame = new byte[layout.FrameByteCount];
        byte[] baselineFrame = new byte[baseline.FrameByteCount];
        layout.RenderFrame(30, customFrame);
        baseline.RenderFrame(30, baselineFrame);
        Assert.NotEqual(SHA256.HashData(customFrame), SHA256.HashData(baselineFrame));
    }

    [Fact]
    public void RandomAccessRenderingIsSafeAcrossParallelQueries()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var renderer = new PanelOverlayRenderer(
            timeline,
            new PanelOverlayRenderer.Options
            {
                Width = 1280,
                Height = 720,
                FpsNumerator = 20,
                LayoutMode = VisualizationLayoutMode.Diagnostic,
                ScopePosition = VisualizationScopePosition.Bottom,
                Channels = VisualizationChannelFilter.All,
            });

        int[] frames = [0, 11, 37, 73, 99];
        var expected = new Dictionary<int, byte[]>(frames.Length);
        foreach (int frame in frames)
        {
            byte[] scope = new byte[renderer.ScopeFrameByteCount];
            byte[] destination = new byte[renderer.FrameByteCount];
            FillScope(scope, frame);
            renderer.RenderCompositeFrame(frame, scope, destination);
            expected[frame] = SHA256.HashData(destination);
        }

        var actual = new byte[frames.Length][];
        Parallel.For(0, frames.Length, index =>
        {
            int frame = frames[index];
            byte[] scope = new byte[renderer.ScopeFrameByteCount];
            byte[] destination = new byte[renderer.FrameByteCount];
            FillScope(scope, frame);
            renderer.RenderCompositeFrame(frame, scope, destination);
            actual[index] = SHA256.HashData(destination);
        });

        for (int index = 0; index < frames.Length; index++)
            Assert.Equal(expected[frames[index]], actual[index]);
    }

    private static void FillScope(Span<byte> scope, int frame)
    {
        for (int index = 0; index < scope.Length; index++)
            scope[index] = (byte)((index + frame * 17) % 251);
    }

    [Fact]
    public void VisualizeDefaultsUseBalancedAutoPublishingSettings()
    {
        var options = VisualizeCommand.ParseArgs(["song.ovi"]);

        Assert.NotNull(options);
        Assert.Equal(1280, options.Width);
        Assert.Equal(720, options.Height);
        Assert.Equal(60, options.Fps);
        Assert.Equal(VisualizationLayoutMode.Auto, options.LayoutMode);
        Assert.Equal(EffectsMode.Minimal, options.Effects);
        Assert.Equal(VisualizationChannelFilter.Active, options.Channels);
    }

    [Fact]
    public void MetadataSafeAreasScaleWithOutputResolution()
    {
        var layout = new OverlayLayout(1920, 1080, 0.75, 2.25);
        Assert.Equal(32, layout.SafeHorizontalMargin);
        Assert.Equal(24, layout.SafeVerticalMargin);

        var preview = new OverlayLayout(960, 540, 0.75, 2.25);
        Assert.Equal(16, preview.SafeHorizontalMargin);
        Assert.Equal(12, preview.SafeVerticalMargin);
    }

    [Fact]
    public void PresetsAndTimeWindowFlagsOverrideBalancedDefaults()
    {
        var options = VisualizeCommand.ParseArgs([
            "song.ovi",
            "--preset", "final",
            "--width", "1600",
            "--time-window", "1.25:3.5",
            "--channels", "semantic",
            "--effects", "diagnostic",
            "--layout", "diagnostic",
            "--scope-ratio", "0.32",
            "--time-grid", "authoritative",
            "--layout-json", "plan.json",
        ]);

        Assert.NotNull(options);
        Assert.Equal(1600, options.Width);
        Assert.Equal(1080, options.Height);
        Assert.Equal(60, options.Fps);
        Assert.Equal(1.25, options.PastSeconds);
        Assert.Equal(3.5, options.FutureSeconds);
        Assert.Equal(VisualizationChannelFilter.Semantic, options.Channels);
        Assert.Equal(EffectsMode.Diagnostic, options.Effects);
        Assert.Equal(VisualizationLayoutMode.Diagnostic, options.LayoutMode);
        Assert.Equal(0.32, options.ScopeRatio);
        Assert.Equal(VisualizationTimeGrid.Authoritative, options.TimeGrid);
        Assert.Equal("plan.json", options.LayoutJson);
    }

    [Fact]
    public void ScopeRatioIsAppliedToAvailableContent()
    {
        var layout = new OverlayLayout(
            960,
            720,
            0.75,
            2.25,
            1,
            VisualizationLayoutMode.Diagnostic,
            scopeRatioOverride: 0.32);

        Assert.Equal(
            (int)Math.Round((layout.PanelHeight - layout.PanelHeaderHeight) * 0.32),
            layout.ScopeHeight);
        Assert.True(layout.TimelineHeight >= 16);
    }

    [Fact]
    public void AutoResolvesToDiagnosticWhenPitchedVoicesExist()
    {
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [VisualizationDeviceCatalog.Ym2608()],
            Voices = VisualizationDeviceCatalog.Ym2608Voices(),
            Notes =
            [
                new NoteEvent(
                    "ym2608.0.fm.1", 0, 1_000, 440, 69, "i",
                    VisualizationNoteMode.Fm, false, []),
            ],
        };

        Assert.Equal(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutModeResolver.Resolve(timeline, VisualizationLayoutMode.Auto));
    }

    [Fact]
    public void AutoKeepsEveryVoiceInTheDiagnosticTopology()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [device],
            Voices = VisualizationDeviceCatalog.Ym2608Voices(),
        };

        Assert.Equal(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutModeResolver.Resolve(timeline, VisualizationLayoutMode.Auto));
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Auto,
            VisualizationChannelFilter.All);
        Assert.Equal(VisualizationDeviceCatalog.Ym2608Voices().Count, topology.Panels.Count);
    }

    [Fact]
    public void AutoResolvesToDiagnosticForIncompatiblePitchModels()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        VoiceDescriptor first = VisualizationDeviceCatalog.Ym2608Voices()[0] with
        {
            PitchSystem = PitchCoordinateSystem.RelativeSemitone,
        };
        VoiceDescriptor second = VisualizationDeviceCatalog.Ym2608Voices()[1] with
        {
            PitchSystem = PitchCoordinateSystem.AbsoluteMidi,
        };
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [device],
            Voices = [first, second],
            Notes =
            [
                new NoteEvent(first.Id.ToString(), 0, 1_000, 440, 60, "a", VisualizationNoteMode.Fm, false, []),
                new NoteEvent(second.Id.ToString(), 0, 1_000, 494, 71, "b", VisualizationNoteMode.Fm, false, []),
            ],
        };

        // Auto never substitutes; it resolves to the single Diagnostic
        // composition whose per-panel grid needs no unified pitch model.
        Assert.Equal(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutModeResolver.Resolve(timeline, VisualizationLayoutMode.Auto));
    }

    [Fact]
    public void AutoFallsBackToDiagnosticWhenNothingIsReliable()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        VoiceDescriptor first = VisualizationDeviceCatalog.Ym2608Voices()[0] with
        {
            PitchSystem = PitchCoordinateSystem.RelativeSemitone,
        };
        VoiceDescriptor second = VisualizationDeviceCatalog.Ym2608Voices()[1] with
        {
            PitchSystem = PitchCoordinateSystem.RelativeSemitone,
        };
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            // No waveform/scope source and no rhythm/sample/noise semantics:
            // only the per-panel Diagnostic grid can represent the capture.
            Devices = [],
            Voices = [first, second],
            Notes =
            [
                new NoteEvent(first.Id.ToString(), 0, 1_000, 440, 60, "a", VisualizationNoteMode.Fm, false, []),
                new NoteEvent(second.Id.ToString(), 0, 1_000, 494, 71, "b", VisualizationNoteMode.Fm, false, []),
            ],
        };

        Assert.Equal(
            VisualizationLayoutMode.Diagnostic,
            VisualizationLayoutModeResolver.Resolve(timeline, VisualizationLayoutMode.Auto));
    }

    [Fact]
    public void PitchCoordinateConverterRejectsUnanchoredRelativePitch()
    {
        Assert.True(PitchCoordinateConverter.TryConvertToMidi(
            PitchCoordinateSystem.FrequencyHz, 440, null, out double midi));
        Assert.Equal(69, midi);
        Assert.False(PitchCoordinateConverter.TryConvertToMidi(
            PitchCoordinateSystem.RelativeSemitone, 2, null, out _));
        Assert.True(PitchCoordinateConverter.TryConvertToMidi(
            PitchCoordinateSystem.RelativeSemitone, 2, 60, out double anchored));
        Assert.Equal(62, anchored);
    }

    [Fact]
    public void SemanticPreparationConvertsFrequencyAndAnchoredRelativePitch()
    {
        DeviceId deviceId = new(ChipType.Unknown, 0);
        VoiceId voiceId = new(deviceId, VoiceKind.Fm, 0);
        VoiceDescriptor voice = new(
            voiceId,
            "VOICE",
            VoicePresentationKind.Fm,
            0,
            false,
            false,
            true)
        {
            PitchSystem = PitchCoordinateSystem.FrequencyHz,
        };
        VisualizationTimeline frequencyTimeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Voices = [voice],
            Notes =
            [
                new NoteEvent(
                    voiceId.ToString(), 0, 1_000, 440, double.NaN, "i",
                    VisualizationNoteMode.Fm, false,
                    [new PitchChange(500, 880, 880)]),
            ],
        };
        VisualizationTrackDescriptor frequencyDescriptor = new()
        {
            Id = voiceId.ToString(),
            DisplayName = "VOICE",
            Kind = VisualizationTrackKind.Pitched,
            PitchSystem = PitchCoordinateSystem.FrequencyHz,
            SourceVoiceIds = [voiceId.ToString()],
        };

        VisualizationSemanticTrack frequencyTrack = Assert.Single(
            VisualizationSemanticSceneBuilder.Build(
                frequencyTimeline,
                [frequencyDescriptor]).Tracks);
        PitchedNoteEvent pitched = Assert.IsType<PitchedNoteEvent>(
            Assert.Single(frequencyTrack.Events.Where(value => value is PitchedNoteEvent)));
        Assert.Equal(69, pitched.Midi, precision: 6);
        PitchCurveEvent curve = Assert.IsType<PitchCurveEvent>(
            Assert.Single(frequencyTrack.Events.Where(value => value is PitchCurveEvent)));
        Assert.Equal(81, Assert.Single(curve.Points).Midi, precision: 6);

        VisualizationTrackDescriptor relativeDescriptor = frequencyDescriptor with
        {
            PitchSystem = PitchCoordinateSystem.RelativeSemitone,
            PitchAnchorMidi = 60,
        };
        VisualizationTimeline relativeTimeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Voices = [voice],
            Notes =
            [
                new NoteEvent(
                    voiceId.ToString(), 0, 1_000, 0, 2, "i",
                    VisualizationNoteMode.Fm, false, []),
            ],
        };
        PitchedNoteEvent relative = Assert.IsType<PitchedNoteEvent>(
            Assert.Single(
                Assert.Single(VisualizationSemanticSceneBuilder.Build(
                    relativeTimeline,
                    [relativeDescriptor]).Tracks).Events));
        Assert.Equal(62, relative.Midi, precision: 6);
    }

    [Fact]
    public void TimelineBuilderNormalizesFrequencyOnlyNotesBeforeJsonBoundaries()
    {
        DeviceId deviceId = new(ChipType.Unknown, 0);
        VoiceId voiceId = new(deviceId, VoiceKind.Fm, 0);
        var builder = new TimelineBuilder(1_000);
        builder.AddVoice(new VoiceDescriptor(
            voiceId,
            "VOICE",
            VoicePresentationKind.Fm,
            0,
            false,
            false,
            true)
        {
            PitchSystem = PitchCoordinateSystem.FrequencyHz,
        });
        builder.AddNote(new NoteEvent(
            voiceId.ToString(),
            0,
            1_000,
            440,
            double.NaN,
            "i",
            VisualizationNoteMode.Fm,
            false,
            [new PitchChange(500, 880, double.NaN)]));

        VisualizationTimeline timeline = builder.Build(
            1_000,
            "frequency-only",
            new TrackMetadata("test", "frequency-only", "test"));
        NoteEvent note = Assert.Single(timeline.Notes);

        Assert.Equal(69, note.InitialMidiNote, precision: 6);
        Assert.Equal(81, Assert.Single(note.Pitch).MidiNote, precision: 6);
    }

    [Fact]
    public void TimelinePreparationRemovesInvalidNotesAndRedundantPitchSamples()
    {
        var builder = new TimelineBuilder(1_000);
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        VoiceDescriptor voice = VisualizationDeviceCatalog.Ym2612Voices()[0];
        builder.AddDevice(device);
        builder.AddVoice(voice);
        builder.AddNote(new NoteEvent(
            voice.Id.ToString(),
            100,
            100,
            440,
            69,
            "invalid",
            VisualizationNoteMode.Fm,
            false,
            []));
        builder.AddNote(new NoteEvent(
            voice.Id.ToString(),
            100,
            900,
            double.NaN,
            69,
            null,
            VisualizationNoteMode.Fm,
            false,
            [
                new PitchChange(500, 0, double.NaN),
                new PitchChange(600, double.NaN, 70),
                new PitchChange(600, 0, 71),
            ]));

        VisualizationTimeline timeline = builder.Build(1_000, "test");
        NoteEvent note = Assert.Single(timeline.Notes);
        Assert.Equal("", note.InstrumentId);
        Assert.Equal(new[] { 71d }, note.Pitch.Select(point => point.MidiNote));
        Assert.Equal(0, note.InitialFrequencyHz);
    }

    [Fact]
    public void TimeWindowParserRejectsOutOfRangeValues()
    {
        Assert.Null(VisualizeCommand.ParseArgs(["song.ovi", "--past-seconds", "0.01"]));
        Assert.Null(VisualizeCommand.ParseArgs(["song.ovi", "--time-window", "15:6"]));
    }

    [Fact]
    public void AuthoritativeTimeGridUsesBeatMarkersAndMeasureHierarchy()
    {
        var builder = new TimelineBuilder(1_000);
        builder.AddBeat(new BeatEvent(0, 0));
        builder.AddBeat(new BeatEvent(500, 1));
        builder.AddBeat(new BeatEvent(1_000, 4));
        var timeline = builder.Build(1_500, "complete", null);

        var lines = VisualizationTimeGridBuilder.Build(
            timeline,
            VisualizationTimeGrid.Authoritative);

        Assert.Equal(9, lines.Length);
        Assert.False(lines[0].Analytical);
        Assert.Equal(VisualizationTimeGridLineKind.Measure, lines[0].Kind);
        Assert.Equal(VisualizationTimeGridLineKind.Subdivision, lines[1].Kind);
        Assert.Contains(lines, line => line.Kind == VisualizationTimeGridLineKind.Beat);
        Assert.Equal(VisualizationTimeGridLineKind.Measure, lines[^1].Kind);
    }

    [Fact]
    public void PreparedPanelsCarryRendererNeutralTrackDescriptors()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.All);
        var layout = new OverlayLayout(
            960,
            720,
            0.75,
            2.25,
            topology.Panels.Count,
            VisualizationLayoutMode.Diagnostic);
        OverlayScene scene = OverlaySceneBuilder.Build(timeline, layout, topology);

        Assert.Contains(scene.Panels, panel => panel.Track.Kind == VisualizationTrackKind.Pitched);
        Assert.Contains(scene.Panels, panel => panel.Track.Kind == VisualizationTrackKind.Percussion);
        Assert.All(scene.Panels, panel => Assert.NotEmpty(panel.Track.SourceVoiceIds));
        Assert.Equal(scene.Panels.Length, scene.Semantic.Tracks.Count);
        Assert.Contains(
            scene.Semantic.Tracks,
            track => track.Events.Any(eventValue => eventValue is PitchedNoteEvent));
        Assert.All(scene.Panels, panel => Assert.InRange(panel.Track.LeadRoleConfidence, 0, 1));
        Assert.All(scene.Panels, panel => Assert.InRange(panel.Track.SalienceScore, 1, 2));
    }

    [Fact]
    public void PreviewAndDiagnosticPageExportsAreSelfContainedAndPaginated()
    {
        string root = Path.Combine(Path.GetTempPath(), $"mdplayer-v3-export-{Guid.NewGuid():N}");
        string previewPath = Path.Combine(root, "preview.html");
        string pagesPath = Path.Combine(root, "diagnostic");
        try
        {
            VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
            VisualizationTopology topology = VisualizationTopologyBuilder.Build(
                timeline,
                VisualizationLayoutMode.Diagnostic,
                VisualizationChannelFilter.All);
            var layout = new OverlayLayout(
                960,
                540,
                0.75,
                2.25,
                topology.Panels.Count,
                VisualizationLayoutMode.Diagnostic);
            VisualizationLayoutPlan plan = VisualizationLayoutPlan.Create(
                timeline,
                topology,
                layout,
                VisualizationLayoutMode.Auto,
                VisualizationChannelFilter.Active,
                fpsNumerator: 30);
            VisualizationLayoutTrackPlan[] pageTracks = Enumerable.Range(0, 13)
                .Select(index => new VisualizationLayoutTrackPlan(
                    $"track-{index}",
                    $"Track {index}",
                    VisualizationTrackKind.Pitched.ToString(),
                    PitchCoordinateSystem.AbsoluteMidi.ToString(),
                    true,
                    null,
                    [$"track-{index}"],
                    [],
                    48,
                    72,
                    48,
                    72,
                    0.5,
                    1.2))
                .ToArray();
            VisualizationLayoutPlan pagePlan = plan with { Tracks = pageTracks };

            VisualizationPreviewWriter.Write(previewPath, timeline, plan);
            VisualizationDiagnosticPagesWriter.Write(pagesPath, timeline, pagePlan);

            string html = File.ReadAllText(previewPath);
            Assert.Contains("<canvas", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("input id=\"time\"", html, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("const plan=", html, StringComparison.Ordinal);
            Assert.Contains("plan.width", html, StringComparison.Ordinal);
            Assert.Contains("\"selectedLayout\"", html, StringComparison.Ordinal);
            Assert.True(File.Exists(Path.Combine(pagesPath, "index.html")));
            Assert.Contains("2 pages", File.ReadAllText(Path.Combine(pagesPath, "index.html")));
            Assert.True(File.Exists(Path.Combine(pagesPath, "page-001.svg")));
            Assert.True(File.Exists(Path.Combine(pagesPath, "page-002.svg")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ScopePositionFlowsIntoScopeFrameGeometry()
    {
        var renderer = new PanelOverlayRenderer(
            VisualizationTimelineFixture.Create(),
            new PanelOverlayRenderer.Options
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 20,
                LayoutMode = VisualizationLayoutMode.Diagnostic,
                ScopePosition = VisualizationScopePosition.Left,
                Channels = VisualizationChannelFilter.All,
            });

        // The per-panel scope rect uses the full canvas width in the
        // Diagnostic grid (Corrscope agreement), and the frame geometry
        // matches it exactly.
        Assert.Equal(renderer.Layout.CorrscopeGridWidth, renderer.Layout.Width);
        Assert.Equal(renderer.Layout.CorrscopeGridHeight, renderer.Layout.ScopeHeight * renderer.Layout.RowCount);
        Assert.Equal(
            renderer.Layout.CorrscopeGridWidth * renderer.Layout.CorrscopeGridHeight * 4,
            renderer.ScopeFrameByteCount);
    }

    [Fact]
    public void GroupByDeviceCombinesCompatibleVoicesBeforeLayout()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        VoiceDescriptor[] voices = VisualizationDeviceCatalog.Ym2612Voices().ToArray();
        var timeline = new VisualizationTimeline
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [device],
            Voices = voices,
            Notes =
            [
                new NoteEvent(voices[0].Id.ToString(), 0, 1_000, 440, 69, "i", VisualizationNoteMode.Fm, false, []),
                new NoteEvent(voices[1].Id.ToString(), 0, 1_000, 494, 71, "i", VisualizationNoteMode.Fm, false, []),
            ],
        };

        VisualizationTopology ungrouped = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.All,
            VisualizationGroupBy.None);
        VisualizationTopology grouped = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.All,
            VisualizationGroupBy.Device);

        Assert.True(grouped.Panels.Count < ungrouped.Panels.Count);
        Assert.Contains(grouped.Panels, panel => panel.VoiceIds.Count > 1);
    }

    [Fact]
    public void ActiveFilterOmitsSilentDescriptorsButAllKeepsThem()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        VoiceDescriptor[] voices = VisualizationDeviceCatalog.Ym2612Voices().Take(2).ToArray();
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [device],
            Voices = voices,
            Notes =
            [
                new NoteEvent(voices[0].Id.ToString(), 0, 1_000, 440, 69, "i",
                    VisualizationNoteMode.Fm, false, []),
            ],
        };

        VisualizationTopology active = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.Active);
        VisualizationTopology diagnostic = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.All);

        Assert.Single(active.Panels);
        Assert.Equal(2, diagnostic.Panels.Count);
    }

    [Fact]
    public void AllFilterPreservesSilentDescriptorsInDiagnosticLayout()
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2612();
        VoiceDescriptor[] voices = VisualizationDeviceCatalog.Ym2612Voices().Take(2).ToArray();
        VisualizationTimeline timeline = new()
        {
            SampleRate = 1_000,
            EndSample = 2_000,
            Devices = [device],
            Voices = voices,
            Notes =
            [
                new NoteEvent(voices[0].Id.ToString(), 0, 1_000, 440, 69, "i",
                    VisualizationNoteMode.Fm, false, []),
            ],
        };

        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.All);

        Assert.Equal(2, topology.Panels.Count);
    }

    [Fact]
    public void LayoutPlanExportsSemanticTracksRegionsAndFrameEstimate()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationTopology topology = VisualizationTopologyBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            VisualizationChannelFilter.All,
            VisualizationGroupBy.None);
        var layout = new OverlayLayout(
            1280,
            720,
            0.75,
            2.25,
            topology.Panels.Count,
            VisualizationLayoutMode.Diagnostic);

        VisualizationLayoutPlan plan = VisualizationLayoutPlan.Create(
            timeline,
            topology,
            layout,
            VisualizationLayoutMode.Auto,
            VisualizationChannelFilter.All,
            VisualizationGroupBy.None,
            VisualizationTimeGrid.Automatic,
            VisualizationScopePosition.Bottom,
            null,
            60,
            1,
            "libx264",
            "none");

        Assert.Equal("diagnostic", plan.SelectedLayout);
        Assert.True(plan.EstimatedFrameCount > 0);
        Assert.Contains(plan.Tracks, track => track.Selected && track.PitchSystem == "AbsoluteMidi");
        Assert.Contains(plan.Tracks, track => track.CameraMinimumMidi.HasValue
            && track.CameraMaximumMidi.HasValue);
        Assert.Contains(plan.Regions, region => region.Kind == "metadata");
        Assert.Contains(plan.Regions, region => region.Kind == "semantic");
    }
}
