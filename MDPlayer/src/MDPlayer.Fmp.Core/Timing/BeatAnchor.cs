#nullable enable

namespace Fmp.Core.Timing;

/// <summary>
/// An absolute timeline sample mapped to its corresponding quarter-note position
/// (already normalized to quarter units by the map builder). Anchors are the
/// strongest evidence of tempo AND phase: unlike a bare BPM they pin the grid to
/// absolute sample positions.
/// </summary>
internal sealed record BeatAnchor(long Sample, double QuarterPosition, double? Confidence = null);