namespace Fmp.Core.PlaybackAssets;

/// <summary>
/// Narrow observer of the normalized ordered chip-write stream. A single
/// authoritative instance is attached at the one observation point shared by
/// every playback path feeding a chip.
/// </summary>
internal interface IPlaybackAssetCollector
{
    /// <summary>Observes one ordered chip register write.</summary>
    void Observe(in ChipWriteEvent write);

    /// <summary>Finalizes the collection and returns the export-ready snapshot.</summary>
    PlaybackAssetSnapshot Complete();
}
