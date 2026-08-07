namespace Fmp.Core.Visualization;

/// <summary>
/// The complete semantic result of DAC analysis: the deduplicated sample
/// catalog together with the resolved per-trigger playback events and any
/// diagnostics. This is the object the analyzer produces before any rendering
/// or export begins (DAC specification §35 central invariant: the analyzer owns
/// both the catalog and the playback events).
/// </summary>
internal sealed record DacAnalysisReport(
    IReadOnlyList<DacSampleAsset> Assets,
    IReadOnlyList<DacPlaybackEvent> PlaybackEvents,
    IReadOnlyList<DacDiagnostic> Diagnostics)
{
    public static DacAnalysisReport From(DacPlaybackTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(tracker);
        var catalog = new DacSampleCatalog().Build(tracker.Candidates);
        tracker.ApplyAssetIds(catalog);
        return new DacAnalysisReport(
            catalog.Assets,
            tracker.PlaybackEvents,
            tracker.Diagnostics);
    }
}