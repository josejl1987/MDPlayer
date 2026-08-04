namespace Fmp.Core.Rendering;

/// <summary>
/// Narrow factory at the FMP host-PCM-session boundary. Selects the explicit
/// execution path: <see cref="FmpOpnaBackend.Mdsound"/> returns the existing
/// legacy MDSound implementation; <see cref="FmpOpnaBackend.NativeLle"/> returns
/// the native clocked-YM2608 session.
///
/// The factory only ever constructs the requested backend: it never probes or
/// loads the native library for MDSound, and it never catches a native
/// construction failure to fall back to MDSound.
/// </summary>
internal static class FmpPcmSessionFactory
{
    public static IFmpPcmSession Create(FmpOpnaBackend backend, FmpPlaybackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return backend switch
        {
            FmpOpnaBackend.Mdsound => new LegacyMdsoundFmpPcmSession(context),
            FmpOpnaBackend.NativeLle => new NativeLleFmpPcmSession(context),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "unknown FMP OPNA backend"),
        };
    }
}