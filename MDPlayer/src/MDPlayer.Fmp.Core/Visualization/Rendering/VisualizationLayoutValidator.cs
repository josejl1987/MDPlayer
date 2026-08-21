namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Validates content-dependent geometry after topology selection. The layout
/// constructor can calculate rectangles without knowing whether they will be
/// used for pitch, trigger, or scope content; this validator is the boundary
/// where those semantic minimums become actionable errors.
/// </summary>
internal static class VisualizationLayoutValidator
{
    public static void Validate(OverlayLayout layout, VisualizationTopology topology)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(topology);

        int minimumPanelWidth = 300;
        int minimumScopeHeight = layout.Height >= 720 ? 56 : Math.Max(24, (int)Math.Round(56 * layout.Height / 720.0));
        // Semantic minimums depend on the grammar. Grid panels share height
        // with a header and (historically) a scope, so they need generous
        // pitch lanes. Performance lanes are dedicated full-width ribbons —
        // the whole band is pitch content — so a much smaller floor keeps
        // many-channel songs renderable on small canvases.
        bool lanes = layout.Variant == VisualizationLayoutVariant.PerformanceLanes;
        int minimumPitchedLaneHeight = lanes
            ? Math.Max(8, (int)Math.Round(12.0 * layout.Height / 1080.0))
            : layout.Height >= 720 ? 72 : Math.Max(32, (int)Math.Round(72 * layout.Height / 720.0));
        int minimumPercussionRowHeight = lanes
            ? Math.Max(3, (int)Math.Round(6.0 * layout.Height / 1080.0))
            : layout.Height >= 720 ? 14 : Math.Max(8, (int)Math.Round(14 * layout.Height / 720.0));

        if (layout.PanelWidth < minimumPanelWidth)
            throw new ArgumentException(
                $"{layout.Mode} needs at least {minimumPanelWidth}px per panel; "
                + "reduce channels/group tracks, increase the output width, or choose a shared layout.", nameof(layout));

        for (int index = 0; index < topology.Panels.Count; index++)
        {
            VisualizationPanel panel = topology.Panels[index];
            if (layout.HasScopes && layout.ScopeHeight < minimumScopeHeight)
                throw new ArgumentException(
                    $"Panel {panel.Label} has only {layout.ScopeHeight}px of scope height; "
                    + $"at least {minimumScopeHeight}px is required. Reduce channels or increase output height.", nameof(layout));

            if (!layout.HasRoll)
                continue;

            OverlayRect timeline = layout.GetTimelineRect(index);
            // FM operator groups have a deliberately reserved compact ribbon
            // region. Their main lane is validated by the shared semantic
            // region rules; applying the ordinary split-lane minimum after
            // subtracting the operator ribbon would reject the supported
            // 540p diagnostic fixtures.
            bool pitched = panel.Schema is PanelPresentationSchema.PitchedLane
                or PanelPresentationSchema.WaveTableLane;
            if (pitched && layout.GetPitchedLaneRect(index,
                    panel.Schema == PanelPresentationSchema.FmOperatorGroup).Height < minimumPitchedLaneHeight)
                throw new ArgumentException(
                    $"Panel {panel.Label} has too little pitch-lane height; "
                    + $"at least {minimumPitchedLaneHeight}px is required. Reduce channels or increase the output height.", nameof(layout));

            if (panel.Schema == PanelPresentationSchema.PercussionRows && panel.Rows.Count > 0
                && timeline.Height / panel.Rows.Count < minimumPercussionRowHeight)
                throw new ArgumentException(
                    $"Panel {panel.Label} has percussion rows below {minimumPercussionRowHeight}px; "
                    + "reduce percussion rows or increase output height.", nameof(layout));
        }
    }
}
