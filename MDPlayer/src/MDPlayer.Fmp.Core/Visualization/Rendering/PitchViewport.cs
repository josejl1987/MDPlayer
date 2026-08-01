namespace Fmp.Core.Visualization.Rendering;

internal readonly record struct PitchViewport(double MinimumMidi, double MaximumMidi)
{
    public double Span => MaximumMidi - MinimumMidi;
}
