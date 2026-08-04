namespace Fmp.Core.Rendering;

/// <summary>
/// OPNA audio backend for the FMP host-PCM path. <see cref="Mdsound"/> is the
/// existing byte-identical default; <see cref="NativeAudio"/> replays a
/// deterministic control capture through the native YM2608 device and the
/// shared PPZ8 renderer. There is deliberately no fallback and no "auto"
/// selection. The default MDSound value (0) is unchanged so old serialized
/// configs keep selecting MDSound.
/// </summary>
internal enum FmpOpnaBackend
{
    /// <summary>Existing MDSound host-PCM path (default, unchanged).</summary>
    Mdsound = 0,

    /// <summary>Trace-driven native YM2608 audio replay; never falls back.</summary>
    NativeAudio = 1,
}
