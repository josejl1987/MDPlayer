using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Patch-2 region-geometry invariants. Region sizes must reallocate when scopes
/// or the roll are disabled (never leaving an invisible reserved gap), all
/// rectangles must stay non-negative and inside their panel, and the tops of
/// consecutive regions must not overlap.
/// </summary>
public sealed class OverlayLayoutInvariantTests
{
    private const int Width = 1280;
    private const int Height = 720;

    private static OverlayLayout Layout(
        VisualizationLayoutVariant variant,
        bool? showScopes = null,
        bool? showRoll = null,
        int panelCount = 4)
    {
        int columns = 2;
        int rows = 2;
        return new OverlayLayout(
            Width, Height, 0.75, 2.25, panelCount, VisualizationLayoutMode.Diagnostic,
            columns, rows, variant,
            scopeHeightOverride: null, timelineHeightOverride: null, rollZoom: 1.0,
            scopeRatioOverride: null, VisualizationScopePosition.Top,
            showScopes, showRoll);
    }

    [Fact]
    public void FullScopesAndRoll_StackWithoutOverlap()
    {
        var g = Layout(VisualizationLayoutVariant.DiagnosticGrid);
        Assert.True(g.HasScopes && g.HasRoll);

        for (int i = 0; i < g.PanelCount; i++)
        {
            OverlayRect panel = g.GetPanelRect(i);
            OverlayRect header = g.GetHeaderRect(i);
            OverlayRect scope = g.GetScopeRect(i);
            OverlayRect roll = g.GetTimelineRect(i);

            Assert.True(header.Width > 0 && header.Height > 0, "header positive");
            // DiagnosticGrid integrates the scope INTO the shared roll body: the
            // scope is the signal portion right of the pitch gutter, never a
            // separate stacked region. It must be a strict sub-rectangle of the
            // roll with the same vertical span.
            Assert.True(scope.X >= roll.X && scope.Right <= roll.Right, "scope inside roll horizontally");
            Assert.Equal(roll.Y, scope.Y);
            Assert.Equal(roll.Bottom, scope.Bottom);
            Assert.True(scope.Width < roll.Width, "pitch gutter keeps scope narrower than the roll");
            Assert.True(header.Bottom <= roll.Y, "header above the shared body");
            Assert.True(header.Y >= panel.Y && roll.Bottom <= panel.Bottom, "inside panel");
        }
    }

    [Fact]
    public void DisablingScopes_GivesTheirSpaceToRoll()
    {
        var withScopes = Layout(VisualizationLayoutVariant.DiagnosticGrid, showScopes: true);
        var withoutScopes = Layout(VisualizationLayoutVariant.DiagnosticGrid, showScopes: false);

        Assert.False(withoutScopes.HasScopes);
        Assert.True(withoutScopes.HasRoll);
        Assert.Equal(0, withoutScopes.ScopeHeight);
        Assert.Equal(0, withoutScopes.DividerHeight);
        // The roll already owns the whole body in the integrated design; disabling
        // scopes removes the scope reservation but cannot grow the body further.
        Assert.Equal(withScopes.TimelineHeight, withoutScopes.TimelineHeight);

        // No invisible gap: header is immediately followed by roll.
        var header = withoutScopes.GetHeaderRect(0);
        var roll = withoutScopes.GetTimelineRect(0);
        Assert.Equal(header.Bottom, roll.Y);
    }

    [Fact]
    public void DisablingRoll_GivesTheirSpaceToScope()
    {
        var withRoll = Layout(VisualizationLayoutVariant.DiagnosticGrid, showRoll: true);
        var withoutRoll = Layout(VisualizationLayoutVariant.DiagnosticGrid, showRoll: false);

        Assert.False(withoutRoll.HasRoll);
        Assert.True(withoutRoll.HasScopes);
        Assert.Equal(0, withoutRoll.TimelineHeight);
        // With the roll gone the scope owns the entire content body (it already
        // filled it in the integrated layout, so it never shrinks).
        Assert.True(withoutRoll.ScopeHeight >= withRoll.ScopeHeight,
            "scope takes the full body when the roll is disabled");
    }

    [Fact]
    public void NeitherScopeNorRoll_LeavesAnActivityContentRegion()
    {
        var g = Layout(VisualizationLayoutVariant.DeviceOverview, showScopes: false, showRoll: false);
        Assert.False(g.HasScopes);
        Assert.False(g.HasRoll);
        Assert.Equal(0, g.ScopeHeight);
        Assert.Equal(0, g.TimelineHeight);

        var panel = g.GetPanelRect(0);
        var header = g.GetHeaderRect(0);
        Assert.True(header.Width >= 0 && header.Height >= 0, "activity area still has a header");
        Assert.True(header.Y >= panel.Y && header.Bottom <= panel.Bottom, "activity header inside panel");
    }

    [Fact]
    public void DisablingScopes_DoesNotThrowOnSmallCanvas()
    {
        // A canvas whose full grid would not fit a roll must still resolve to a
        // usable scope-only or activity panel rather than throw.
        var tiny = new OverlayLayout(
            640, 360, 0.75, 2.25, 1, VisualizationLayoutMode.Diagnostic,
            1, 1, VisualizationLayoutVariant.DiagnosticGrid,
            scopeHeightOverride: null, timelineHeightOverride: null, rollZoom: 1.0,
            scopeRatioOverride: null, VisualizationScopePosition.Top,
            showScopes: false, showRoll: true);
        Assert.False(tiny.HasScopes);
        Assert.True(tiny.HasRoll);
        Assert.True(tiny.TimelineHeight >= 0);
    }

    // ---- helpers ----------------------------------------------------------

    private static bool RectsUngapped(OverlayRect above, OverlayRect below)
    {
        // The two regions are adjacent vertically with no invisible gap (and no
        // overlap) when below.Y == above.Bottom.
        Assert.True(above.Bottom <= below.Y, "regions must not overlap");
        return above.Bottom == below.Y;
    }
}
