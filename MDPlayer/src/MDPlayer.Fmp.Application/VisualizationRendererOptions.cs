using Fmp.Application.Contracts;
using Fmp.Core.Analysis;
using Fmp.Core.Visualization.Rendering;
#nullable enable

namespace Fmp.Cli;

/// <summary>
/// Builds the shared <see cref="PanelOverlayRenderer.Options"/> for a request.
/// This is the ONE place the productive overlay options are derived from a
/// request + presentation, so final composition, preview and review all match.
/// </summary>
internal static class VisualizationRendererOptions
{
    public static PanelOverlayRenderer.Options Build(
        VisualizationRequest request,
        VisualizationPresentation presentation,
        bool introOutro,
        IReadOnlyList<ChannelEnergyEnvelope> energy)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(presentation);

        OutputSettings output = request.Output;
        ViewSettings view = request.View;
        StyleSettings style = request.Style;
        PlaybackSettings playback = request.Playback;

        return new PanelOverlayRenderer.Options
        {
            FpsNumerator = output.FpsNumerator,
            FpsDenominator = output.FpsDenominator,
            TimeGrid = MapTimeGrid(view.TimeGrid),
            Presentation = presentation,
            FontPath = request.Presentation.FontPath,
            PreferAntialiasedText = output.Quality != RenderQuality.Draft,
            Effects = MapEffects(style.Effects),
            NoteColor = MapNoteColor(style.NoteColor),
            Palette = MapPalette(style.Palette),
            ScopeOpacity = style.ScopeOpacity,
            MotionBlurSamples = 1,
            IntroSeconds = introOutro ? 0.75 : 0,
            OutroSeconds = introOutro ? Math.Min(0.45, playback.TailSeconds) : 0,
            AnalysisOverlay = AnalysisOverlayScene.Empty,
            Energy = energy as ChannelEnergyEnvelope[] ?? (energy?.ToArray()),
            EnablePerformanceMetrics = true,
        };
    }

    private static VisualizationTimeGrid MapTimeGrid(TimeGridMode grid) => grid switch
    {
        TimeGridMode.None => VisualizationTimeGrid.None,
        TimeGridMode.Authoritative => VisualizationTimeGrid.Authoritative,
        TimeGridMode.Analytical => VisualizationTimeGrid.Analytical,
        _ => VisualizationTimeGrid.Automatic,
    };

    private static EffectsMode MapEffects(VisualEffects effects) => effects switch
    {
        VisualEffects.Off => EffectsMode.None,
        VisualEffects.Cinematic => EffectsMode.Cinematic,
        _ => EffectsMode.Minimal,
    };

    private static Fmp.Core.Visualization.Rendering.NoteColorMode MapNoteColor(
        Fmp.Application.Contracts.NoteColorMode mode) => mode switch
    {
        Fmp.Application.Contracts.NoteColorMode.Channel =>
            Fmp.Core.Visualization.Rendering.NoteColorMode.Channel,
        Fmp.Application.Contracts.NoteColorMode.PitchClass =>
            Fmp.Core.Visualization.Rendering.NoteColorMode.Pitch,
        _ => Fmp.Core.Visualization.Rendering.NoteColorMode.Instrument,
    };

    private static VisualizationPalette MapPalette(PaletteKind palette) => palette switch
    {
        PaletteKind.Accessible => VisualizationPalette.Accessible,
        _ => VisualizationPalette.Default,
    };
}
