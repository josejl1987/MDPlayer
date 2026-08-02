using System;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Supplies the scope grid frame (CorrscopeGridWidth x CorrscopeGridHeight
/// RGBA) for a given frame index. Implementations are either the external
/// Corrscope bridge (<see cref="CorrscopeFrameSource"/>) or the internal
/// master-waveform fallback (<see cref="MasterWaveformFrameSource"/>) that
/// draws the captured master audio directly into the grid cells. Every
/// consumer (final video, GUI preview, visual review) reads scope frames
/// through this single abstraction so preview and final stay identical even
/// when Corrscope/Python is unavailable.
/// </summary>
internal interface IScopeFrameSource : IDisposable
{
    /// <summary>
    /// Reads the scope grid frame for <paramref name="frameIndex"/> into
    /// <paramref name="destination"/>. The destination must be at least
    /// <c>CorrscopeGridWidth * CorrscopeGridHeight * 4</c> bytes.
    /// </summary>
    void ReadFrame(int frameIndex, Span<byte> destination);
}
