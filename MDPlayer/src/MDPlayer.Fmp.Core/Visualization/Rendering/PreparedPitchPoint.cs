namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// A single pitch point used in prepared note data. A value type so the
/// per-frame hot path reads contour points from the prepared array without
/// per-element indirection. Points are sorted by
/// <see cref="SamplePosition"/>; <see cref="MidiNote"/> stays continuous
/// (never quantized to integer MIDI notes).
/// </summary>
internal readonly record struct PreparedPitchPoint(long SamplePosition, double MidiNote);
