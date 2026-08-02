using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// The single authority that chooses the layout variant, density and optional
/// visual regions (scopes, roll, pitch labels, detailed headers, instrument
/// text) for a resolved canvas. All other code — the overlay renderer, the
/// Corrscope compositor and the diagnostics rail — consumes the resulting
/// <see cref="ResolvedVisualizationLayout"/> instead of re-deriving fallback or
/// capability decisions from pixel dimensions.
///
/// Density degrades rather than throwing: Full requires every panel to reach
/// the full 300×180 geometry, Compact requires 220×120, and anything smaller
/// resolves to Minimal (at most a roll, or aggregate activity when even a roll
/// cannot fit). The selected <see cref="VisualizationLayoutVariant"/> still
/// drives the render grammar; Density/Capabilities describe what is rendered.
/// </summary>
internal static class VisualizationLayoutResolver
{
    // Spec-determined panel thresholds for each density tier.
    private const int FullMinWidth = 300;
    private const int FullMinHeight = 180;
    private const int CompactMinWidth = 220;
    private const int CompactMinHeight = 120;
    private const int MinimumRollContentHeight = 32;

    /// <summary>Spec-mandated resolve surface (used directly by tests).</summary>
    public static ResolvedVisualizationLayout Resolve(
        int width,
        int height,
        double pastSeconds,
        double futureSeconds,
        VisualizationLayoutMode mode,
        VisualizationTopology topology,
        int panelCount,
        VisualizationScopePosition scopePosition)
        => Resolve(
            width,
            height,
            pastSeconds,
            futureSeconds,
            mode,
            topology,
            panelCount,
            scopePosition,
            scopeHeightOverride: null,
            timelineHeightOverride: null,
            rollZoom: 1.0,
            scopeRatioOverride: null);

    /// <summary>
    /// Full resolution surface used by the layout builder so the resolver stays
    /// the single authority even when settings carry explicit scope/roll/zoom
    /// overrides.
    /// </summary>
    public static ResolvedVisualizationLayout Resolve(
        int width,
        int height,
        double pastSeconds,
        double futureSeconds,
        VisualizationLayoutMode mode,
        VisualizationTopology topology,
        int panelCount,
        VisualizationScopePosition scopePosition,
        int? scopeHeightOverride,
        int? timelineHeightOverride,
        double rollZoom,
        double? scopeRatioOverride)
    {
        ArgumentNullException.ThrowIfNull(topology);
        if (mode != VisualizationLayoutMode.Diagnostic)
            throw new ArgumentOutOfRangeException(nameof(mode), "Only the diagnostic composition is supported.");

        int gridHeight = GridHeight(height);

        // Prefer the full diagnostic grid, then the compact overview, and only
        // fall back to a device-level aggregate when neither can fit every
        // panel at its minimum viable geometry. This keeps a supported
        // resolution / panel count from ever throwing just because scopes or
        // detailed labels do not fit.
        ResolvedPanelGrid? full = OverlayLayout.FindGrid(
            panelCount, width, gridHeight, minimumPanelWidth: 300, minimumPanelHeight: 150);
        if (full is { } f && IsFullGridViable(width, height, f))
        {
            return ComposeFull(width, height, pastSeconds, futureSeconds, mode, topology,
                pool: f, panelCount, scopePosition, scopeHeightOverride, timelineHeightOverride,
                rollZoom, scopeRatioOverride);
        }

        ResolvedPanelGrid? overview = OverlayLayout.FindGrid(
            panelCount, width, gridHeight, minimumPanelWidth: 180, minimumPanelHeight: 56);
        if (overview is { } o)
        {
            return ComposeOverview(width, height, pastSeconds, futureSeconds, mode, topology,
                pool: o, panelCount, scopePosition, scopeHeightOverride, timelineHeightOverride,
                rollZoom, scopeRatioOverride);
        }

        return ComposeDeviceOverview(width, height, pastSeconds, futureSeconds, mode, topology,
            panelCount, scopePosition, scopeHeightOverride, timelineHeightOverride,
            rollZoom, scopeRatioOverride);
    }

    /// <summary>
    /// Whether the full diagnostic grid can actually be constructed on this
    /// canvas: the wide full-grid variant reserves wider minimums (480 wide,
    /// 240 grid height) that a nominal 300×150 panel layout can otherwise
    /// over-promise for very small canvases.
    /// </summary>
    private static bool IsFullGridViable(int width, int height, ResolvedPanelGrid grid)
    {
        if (width < 480)
            return false;
        int gridHeight = OverlayGridHeight(height, grid.Rows);
        return gridHeight >= 240;
    }

    /// <summary>Extracts density and capability flags for a resolved geometry.</summary>
    internal static (VisualizationLayoutDensity Density, VisualizationLayoutCapabilities Capabilities)
        Decide(int panelWidth, int panelHeight, int scopeHeight, bool rollPossible)
    {
        // Device/aggregate panels can never present the full diagnostic
        // grammar; they cap at the minimal density regardless of panel size.
        if (!rollPossible)
        {
            return (VisualizationLayoutDensity.Minimal, new VisualizationLayoutCapabilities(
                ShowScopes: false,
                ShowRoll: false,
                ShowPitchLabels: false,
                ShowDetailedHeaders: false,
                ShowInstrumentText: false));
        }

        if (panelWidth >= FullMinWidth && panelHeight >= FullMinHeight)
        {
            return (VisualizationLayoutDensity.Full, new VisualizationLayoutCapabilities(
                ShowScopes: true,
                ShowRoll: true,
                ShowPitchLabels: true,
                ShowDetailedHeaders: true,
                ShowInstrumentText: true));
        }

        if (panelWidth >= CompactMinWidth && panelHeight >= CompactMinHeight)
        {
            // Compact keeps scopes (when at least a minimum scope height can be
            // allocated) and the roll, but drops the detailed/semantic text.
            return (VisualizationLayoutDensity.Compact, new VisualizationLayoutCapabilities(
                ShowScopes: scopeHeight >= 32,
                ShowRoll: rollPossible,
                ShowPitchLabels: false,
                ShowDetailedHeaders: false,
                ShowInstrumentText: false));
        }

        // Minimal: no scope region. A roll is kept only when it can retain a
        // readable content height; otherwise the panel degrades to aggregate
        // activity.
        bool roll = rollPossible && (panelHeight - scopeHeight) >= MinimumRollContentHeight;
        return (VisualizationLayoutDensity.Minimal, new VisualizationLayoutCapabilities(
            ShowScopes: false,
            ShowRoll: roll,
            ShowPitchLabels: false,
            ShowDetailedHeaders: false,
            ShowInstrumentText: false));
    }

    // ---- composition helpers ---------------------------------------------

    private static ResolvedVisualizationLayout ComposeFull(
        int width, int height, double pastSeconds, double futureSeconds,
        VisualizationLayoutMode mode, VisualizationTopology topology, ResolvedPanelGrid pool,
        int panelCount, VisualizationScopePosition scopePosition,
        int? scopeHeightOverride, int? timelineHeightOverride, double rollZoom, double? scopeRatioOverride)
    {
        var geometry = new OverlayLayout(
            width, height, pastSeconds, futureSeconds,
            topology.Panels.Count, mode, pool.Columns, pool.Rows,
            VisualizationLayoutVariant.DiagnosticGrid,
            scopeHeightOverride, timelineHeightOverride, rollZoom, scopeRatioOverride, scopePosition);

        int panelWidth = geometry.PanelWidth;
        int panelHeight = geometry.PanelHeight;
        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            Decide(panelWidth, panelHeight, geometry.ScopeHeight, rollPossible: true);

        return new ResolvedVisualizationLayout(
            mode, VisualizationLayoutVariant.DiagnosticGrid, topology, geometry, density, caps);
    }

    private static ResolvedVisualizationLayout ComposeOverview(
        int width, int height, double pastSeconds, double futureSeconds,
        VisualizationLayoutMode mode, VisualizationTopology topology, ResolvedPanelGrid pool,
        int panelCount, VisualizationScopePosition scopePosition,
        int? scopeHeightOverride, int? timelineHeightOverride, double rollZoom, double? scopeRatioOverride)
    {
        var geometry = new OverlayLayout(
            width, height, pastSeconds, futureSeconds,
            topology.Panels.Count, mode, pool.Columns, pool.Rows,
            VisualizationLayoutVariant.DiagnosticOverview,
            scopeHeightOverride, timelineHeightOverride, rollZoom, scopeRatioOverride, scopePosition);

        int panelWidth = geometry.PanelWidth;
        int panelHeight = geometry.PanelHeight;
        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            Decide(panelWidth, panelHeight, geometry.ScopeHeight, rollPossible: true);

        return new ResolvedVisualizationLayout(
            mode, VisualizationLayoutVariant.DiagnosticOverview, topology, geometry, density, caps);
    }

    private static ResolvedVisualizationLayout ComposeDeviceOverview(
        int width, int height, double pastSeconds, double futureSeconds,
        VisualizationLayoutMode mode, VisualizationTopology topology, int panelCount,
        VisualizationScopePosition scopePosition,
        int? scopeHeightOverride, int? timelineHeightOverride, double rollZoom, double? scopeRatioOverride)
    {
        // Collapse to one panel per device (aggregate activity) — the last
        // fallback; render an aggregate rather than illegible rows.
        VisualizationTopology deviceTopology = GroupByDevice(topology);
        int devicePanelCount = deviceTopology.Panels.Count;

        int gridHeight = GridHeight(height);
        ResolvedPanelGrid? grid = OverlayLayout.FindGrid(
            devicePanelCount, width, gridHeight, minimumPanelWidth: 180, minimumPanelHeight: 56);

        // When even the device-level panels cannot keep a usable height (e.g.
        // dozens of devices on a very small canvas), collapse further to a
        // single aggregate activity panel rather than throw or clip.
        int columns, rows;
        int basePanel = gridHeight / Math.Max(1, grid?.Rows ?? devicePanelCount);
        if (grid is { } g && basePanel >= MinimumDevicePanelHeight)
        {
            columns = g.Columns;
            rows = g.Rows;
        }
        else
        {
            deviceTopology = SingleAggregateTopology(topology);
            columns = 1;
            rows = 1;
        }

        var geometry = new OverlayLayout(
            width, height, pastSeconds, futureSeconds,
            deviceTopology.Panels.Count, mode, columns, rows,
            VisualizationLayoutVariant.DeviceOverview,
            scopeHeightOverride, timelineHeightOverride, rollZoom, scopeRatioOverride, scopePosition);

        int panelWidth = geometry.PanelWidth;
        int panelHeight = geometry.PanelHeight;
        (VisualizationLayoutDensity density, VisualizationLayoutCapabilities caps) =
            Decide(panelWidth, panelHeight, geometry.ScopeHeight, rollPossible: false);

        return new ResolvedVisualizationLayout(
            mode, VisualizationLayoutVariant.DeviceOverview, deviceTopology, geometry, density, caps);
    }

    /// <summary>Minimum panel height at which a device-level aggregate panel can
    /// show meaningful content without clipping.</summary>
    private const int MinimumDevicePanelHeight = 40;

    private static int GridHeight(int height)
    {
        int top = Math.Clamp((int)Math.Round(height * (OverlayLayout.DefaultTopBarHeight / 1080.0)), 32, 96);
        int bottom = Math.Clamp((int)Math.Round(height * (OverlayLayout.DefaultBottomBarHeight / 1080.0)), 24, 64);
        return height - top - bottom;
    }

    /// <summary>Grid height on the canvas after the bottom bar absorbs the
    /// shortfall so the grid divides evenly into <paramref name="rows"/>.</summary>
    private static int OverlayGridHeight(int height, int rows)
    {
        int top = Math.Clamp((int)Math.Round(height * (OverlayLayout.DefaultTopBarHeight / 1080.0)), 32, 96);
        int bottom = Math.Clamp((int)Math.Round(height * (OverlayLayout.DefaultBottomBarHeight / 1080.0)), 24, 64);
        int grid = height - top - bottom;
        int remainder = grid % Math.Max(1, rows);
        return remainder == 0 ? grid : grid - remainder;
    }

    // ---- device aggregation -----------------------------------------------

    /// <summary>Collapses every device to one aggregate activity panel.</summary>
    private static VisualizationTopology SingleAggregateTopology(VisualizationTopology topology)
    {
        string[] voiceIds = topology.Panels
            .SelectMany(p => p.VoiceIds)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var panel = new VisualizationPanel(
            "device.all",
            "All devices",
            PreparedPanelKind.Aggregate,
            PanelContentKind.DeviceAggregate,
            0,
            voiceIds,
            OperatorVoiceIds: Array.Empty<string>())
        {
            Schema = PanelPresentationSchema.AggregateActivity,
        };
        return new VisualizationTopology([panel]);
    }

    private static VisualizationTopology GroupByDevice(VisualizationTopology topology)
    {
        return topology.Panels
            .GroupBy(panel => panel.Id, StringComparer.Ordinal)
            .OrderBy(group => group.Min(panel => panel.Order))
            .Select((group, index) =>
            {
                VisualizationPanel first = group.First();
                string[] voiceIds = group.SelectMany(p => p.VoiceIds)
                    .Distinct(StringComparer.Ordinal).ToArray();
                string[] operatorIds = group.SelectMany(p => p.OperatorVoiceIds)
                    .Distinct(StringComparer.Ordinal).ToArray();
                return new VisualizationPanel(
                    $"device.{index + 1}",
                    first.Label,
                    PreparedPanelKind.Aggregate,
                    PanelContentKind.DeviceAggregate,
                    index,
                    voiceIds,
                    operatorIds)
                {
                    Schema = PanelPresentationSchema.AggregateActivity,
                    Rows = Array.Empty<PanelRowDefinition>(),
                };
            })
            .ToArray() is { Length: > 0 } panels
                ? new VisualizationTopology(panels)
                : throw new InvalidOperationException("Device overview requires at least one panel.");
    }
}
