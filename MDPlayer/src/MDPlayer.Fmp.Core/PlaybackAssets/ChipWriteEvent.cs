using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets;

/// <summary>
/// A single normalized, ordered chip register write as observed at the
/// boundary between the playback source and the emulator. Chip identity uses
/// the existing <see cref="ChipType"/> classification plus a per-type
/// instance index.
/// </summary>
internal readonly record struct ChipWriteEvent
{
    public required ChipType ChipType { get; init; }
    public required int ChipIndex { get; init; }
    public required int Port { get; init; }
    public required int Address { get; init; }
    public required byte Data { get; init; }

    /// <summary>Optional monotonically increasing write sequence number.</summary>
    public long? WriteIndex { get; init; }

    /// <summary>Optional playback output-sample position, when available.</summary>
    public long? PlaybackSample { get; init; }
}
