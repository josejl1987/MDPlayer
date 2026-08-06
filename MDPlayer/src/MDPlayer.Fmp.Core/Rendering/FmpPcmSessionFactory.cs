namespace Fmp.Core.Rendering;

/// <summary>
/// Narrow factory at the FMP host-PCM-session boundary. Selects the explicit
/// backend: <see cref="FmpOpnaBackend.Mdsound"/> returns the existing legacy
/// MDSound implementation; <see cref="FmpOpnaBackend.NativeAudio"/> returns the
/// trace-driven native YM2608 replay session.
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
            FmpOpnaBackend.NativeAudio => new NativeAudioFmpPcmSession(context),
            _ => throw new ArgumentOutOfRangeException(nameof(backend), backend, "unknown FMP OPNA backend"),
        };
    }

    /// <summary>
    /// Backend-aware factory that injects an externally-built OPNA device. Only
    /// the native backend uses it; used to place a playback-asset observer on
    /// the ordered write stream when asset dumping is requested. Passing a
    /// non-null device for the MDSound backend is rejected.
    /// </summary>
    public static IFmpPcmSession Create(
        FmpOpnaBackend backend,
        FmpPlaybackContext context,
        global::Fmp.Core.Playback.Opna.IClockedOpnaDevice device)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (backend == FmpOpnaBackend.Mdsound)
        {
            if (device is not null)
                throw new InvalidOperationException("cannot inject a device into the MDSound backend");
            return new LegacyMdsoundFmpPcmSession(context);
        }

        return new NativeAudioFmpPcmSession(context, device, ownsDevice: false);
    }
}
