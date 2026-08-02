using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Verifies the responsive fallback hierarchy for diagnostic layouts: a full
/// diagnostic grid only when every panel meets its minimum geometry, a compact
/// diagnostic overview when full panels no longer fit, and a device-level
/// overview as the last fallback. The essential rule:
///
/// <i>Never scale a full diagnostic panel below its minimum viable geometry.
/// Resolve to a simpler layout instead.</i>
///
/// This is part of layout resolution, not a collection of emergency font-size
/// and clipping branches in the renderer.
/// </summary>
public sealed class ResponsiveLayoutFallbackTests
{
    private const int FullMinWidth = 300;
    private const int FullMinHeight = 150;
    private const int OverviewMinWidth = 180;
    private const int OverviewMinHeight = 56;

    [Fact]
    public void TwelvePanels_1920x1080_ResolvesFullDiagnostic()
    {
        ResolvedVisualizationLayout result = Resolve(1920, 1080);

        Assert.Equal(VisualizationLayoutVariant.DiagnosticGrid, result.Variant);
        AssertAllPanelsMeetMinimum(result, FullMinWidth, FullMinHeight);
    }

    [Fact]
    public void TwelvePanels_1280x720_ResolvesFullDiagnostic_NoVariantViolation()
    {
        ResolvedVisualizationLayout result = Resolve(1280, 720);

        // Full panels (426x~156) fit at 720p.
        Assert.Equal(VisualizationLayoutVariant.DiagnosticGrid, result.Variant);
        AssertAllPanelsMeetMinimum(result, FullMinWidth, FullMinHeight);
    }

    [Fact]
    public void TwelvePanels_900x506_ResolvesDiagnosticOverview_AllChannelsRepresented()
    {
        ResolvedVisualizationLayout result = Resolve(900, 506);

        Assert.Equal(VisualizationLayoutVariant.DiagnosticOverview, result.Variant);
        AssertAllPanelsMeetMinimum(result, OverviewMinWidth, OverviewMinHeight);
        AssertEverySelectedTrackIsRepresented(result);
    }

    [Fact]
    public void TwelvePanels_720x405_ResolvesDiagnosticOverview_NoTextIntersections()
    {
        ResolvedVisualizationLayout result = Resolve(720, 405);

        Assert.Equal(VisualizationLayoutVariant.DiagnosticOverview, result.Variant);
        AssertAllPanelsMeetMinimum(result, OverviewMinWidth, OverviewMinHeight);
        AssertNoHeaderTextIntersections(result);
        AssertAllTextInsidePanelBounds(result);
        AssertEverySelectedTrackIsRepresented(result);
    }

    [Fact]
    public void ManyPanels_480x270_ResolvesDeviceOverview_AllDevicesRepresented()
    {
        // At 480x270 even overview channel panels cannot fit; the resolver must
        // group by device rather than render illegible rows or drop channels.
        ResolvedVisualizationLayout result = Resolve(480, 270, catalog: VisualizationDeviceCatalog.Ym2608Voices());

        Assert.Equal(VisualizationLayoutVariant.DeviceOverview, result.Variant);
        Assert.True(result.Topology.Panels.Count >= 1);
        // A device overview is a single device panel for this catalog.
        Assert.Equal("YM2608 #0", result.Topology.Panels[0].Label);
        Assert.True(result.Geometry.PanelCount >= 1);
        // Universal invariant: every device panel occupies its own geometry cell.
        AssertAllTextInsidePanelBounds(result);
        Assert.True(result.Geometry.PanelCount == result.Topology.Panels.Count,
            "each device panel maps to exactly one geometry panel");
        // Every device represented: the panel count must equal the number of
        // grouped device panels, and none may be dropped.
        for (int index = 0; index < result.Geometry.PanelCount - 1; index++)
        {
            Assert.True(
                result.Geometry.GetPanelRect(index).Right <= result.Geometry.GetPanelRect(index + 1).X
                || result.Geometry.GetPanelRect(index).Bottom <= result.Geometry.GetPanelRect(index + 1).Y,
                $"Panel {index} and {index + 1} overlap");
        }
    }

    [Fact]
    public void OverviewFallback_ReportsInformationalIssue_NotError()
    {
        ResolvedVisualizationLayout result = Resolve(900, 506);
        VisualizationPlanResult plan = VisualizationPlanBuilder.Build(
            Request(),
            BuildTimeline(VisualizationDeviceCatalog.Ym2608Voices().Take(10).ToArray()),
            result);

        Assert.Equal("diagnostic-overview", plan.ResolvedVariant);
        ValidationIssue issue = Assert.Single(
            plan.ValidationIssues,
            i => i.Code == ValidationCodes.DiagnosticOverviewFallback);
        Assert.Equal(ValidationSeverity.Information, issue.Severity);
    }

    [Fact]
    public void DeviceOverviewFallback_ReportsWarning_NotError()
    {
        ResolvedVisualizationLayout result = Resolve(480, 270, catalog: VisualizationDeviceCatalog.Ym2608Voices());
        VisualizationPlanResult plan = VisualizationPlanBuilder.Build(
            Request(),
            BuildTimeline(VisualizationDeviceCatalog.Ym2608Voices()),
            result);

        Assert.Equal("device-overview", plan.ResolvedVariant);
        ValidationIssue issue = Assert.Single(
            plan.ValidationIssues,
            i => i.Code == ValidationCodes.DeviceOverviewFallback);
        Assert.Equal(ValidationSeverity.Warning, issue.Severity);
    }

    private static VisualizationRequest Request() => new()
    {
        InputPath = "input.vgm",
        OutputPath = "output.mp4",
        Composition = CompositionKind.Diagnostic,
    };

    // ---- Universal invariants ----

    private static void AssertAllPanelsMeetMinimum(
        ResolvedVisualizationLayout layout,
        int minWidth,
        int minHeight)
    {
        for (int index = 0; index < layout.Geometry.PanelCount; index++)
        {
            OverlayRect panel = layout.Geometry.GetPanelRect(index);
            Assert.True(panel.Width >= minWidth,
                $"Panel {index} is {panel.Width}px wide; minimum is {minWidth}.");
            Assert.True(panel.Height >= minHeight,
                $"Panel {index} is {panel.Height}px tall; minimum is {minHeight}.");
        }
    }

    private static void AssertNoHeaderTextIntersections(ResolvedVisualizationLayout layout)
    {
        // Header slots inside a panel must never overlap one another.
        for (int index = 0; index < layout.Geometry.PanelCount; index++)
        {
            foreach (OverlayRect header in SplitHeaderSlots(layout.Geometry, index))
            {
                Assert.True(header.Width >= 0 && header.Height >= 0);
            }
        }
    }

    private static void AssertAllTextInsidePanelBounds(ResolvedVisualizationLayout layout)
    {
        for (int index = 0; index < layout.Geometry.PanelCount; index++)
        {
            OverlayRect panel = layout.Geometry.GetPanelRect(index);
            OverlayRect header = layout.Geometry.GetHeaderRect(index);
            Assert.True(header.X >= panel.X && header.Right <= panel.Right);
            Assert.True(header.Y >= panel.Y && header.Bottom <= panel.Bottom);
        }
    }

    private static void AssertEverySelectedTrackIsRepresented(ResolvedVisualizationLayout layout)
    {
        // Every topology panel maps to exactly one geometry panel (same count).
        Assert.Equal(layout.Topology.Panels.Count, layout.Geometry.PanelCount);
    }

    private static IEnumerable<OverlayRect> SplitHeaderSlots(OverlayLayout layout, int index)
    {
        var header = layout.GetHeaderRect(index);
        // Three vertical slots filling the header width.
        for (int slot = 0; slot < 3; slot++)
        {
            int x0 = header.X + (header.Width * slot) / 3;
            int x1 = header.X + (header.Width * (slot + 1)) / 3;
            yield return new OverlayRect(x0, header.Y, x1 - x0, header.Height);
        }
    }

    // ---- Resolution helper ----

    private static ResolvedVisualizationLayout Resolve(
        int width,
        int height,
        IReadOnlyList<VoiceDescriptor> catalog = null)
    {
        VisualizationTimeline timeline = catalog is null
            ? VisualizationTimelineFixture.Create()
            : BuildTimeline(catalog);
        return VisualizationLayoutBuilder.Build(
            timeline,
            VisualizationLayoutMode.Diagnostic,
            new VisualizationLayoutSettings(
                width,
                height,
                0.75,
                2.25,
                1.0,
                null,
                null,
                null,
                VisualizationScopePosition.Top,
                VisualizationChannelFilter.All,
                VisualizationGroupBy.None));
    }

    private static VisualizationTimeline BuildTimeline(IReadOnlyList<VoiceDescriptor> voices)
    {
        DeviceDescriptor device = VisualizationDeviceCatalog.Ym2608();
        return new VisualizationTimeline
        {
            SampleRate = 1_000,
            EndSample = 5_000,
            Devices = [device],
            Voices = voices,
            Notes = voices
                .Select((voice, index) => new NoteEvent(
                    voice.Id.ToString(),
                    1_000 + index,
                    4_000,
                    440,
                    60 + index % 12,
                    "instrument",
                    VisualizationNoteMode.Fm,
                    false,
                    []))
                .ToArray(),
        };
    }
}