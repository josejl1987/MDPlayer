namespace Fmp.Core.Rendering;

/// <summary>
/// OPNA execution backend for the FMP host-PCM path. <see cref="Mdsound"/> is
/// the existing default (byte-identical to the pre-backend branch);
/// <see cref="NativeLle"/> opts into the clocked native YM2608-LLE session.
/// The default serialized/CLI value must remain <see cref="Mdsound"/>.
/// </summary>
internal enum FmpOpnaBackend
{
    /// <summary>Existing MDSound host-PCM path (default, unchanged).</summary>
    Mdsound = 0,

    /// <summary>Clocked native YM2608-LLE session; never falls back to MDSound.</summary>
    NativeLle = 1,
}