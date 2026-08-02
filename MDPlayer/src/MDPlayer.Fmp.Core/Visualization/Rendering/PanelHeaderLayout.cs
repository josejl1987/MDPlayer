namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Fixed slots for the three strings drawn in a panel header. Each string is
/// drawn within its own clipped rectangle, so long channel-local text (name,
/// live pitch/state, patch/algorithm) can never collide with its neighbours or
/// cross the panel boundary.
/// </summary>
internal readonly record struct PanelHeaderLayout(
    OverlayRect Name,
    OverlayRect State,
    OverlayRect Patch);
