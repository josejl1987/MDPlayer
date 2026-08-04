using Fmp.Core.IO;

namespace Fmp.Core.Rendering;

/// <summary>
/// Everything a host-PCM FMP session needs to construct and drive a render.
/// Carried through <see cref="FmpPcmSessionFactory.Create"/>; the single
/// authoritative backend value travels via the caller's options/request, not
/// as a duplicate here.
/// </summary>
internal sealed record FmpPlaybackContext(
    byte[]? TrackData,
    string? TrackFileName,
    FmpRuntimeAssets Assets,
    IFmpFileSystem? FileSystem,
    int SampleRate,
    double SsgGainDb = 0,
    int LoopCount = 2,
    double FadeSeconds = 5.0,
    double TailSeconds = 0.5,
    double? MaxDurationSeconds = null);