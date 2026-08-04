using System.Security.Cryptography;
using System.Text;

namespace Fmp.Core.Rendering;

/// <summary>
/// Cache-key value for an <see cref="FmpExecutionCapture"/>. Every value that can
/// alter captured control events or output duration is part of the identity, so
/// a compatible completed capture is only ever reused when the request is
/// byte-for-byte equivalent on the capture-affecting axes.
///
/// Excluded (presentation-only) values: output path, video dimensions,
/// visualization layout, encoder, container format.
/// </summary>
internal readonly record struct FmpExecutionCaptureKey(
    string TrackContentSha256,
    int OutputSampleRate,
    ulong CpuClockFrequencyHz,
    int LoopCount,
    double FadeSeconds,
    double TailSeconds,
    double? MaxDurationSeconds,
    double SsgGainDb)
{
    /// <summary>
    /// Builds the key from a playback context plus the immutable track bytes.
    /// Content identity is SHA-256 over the track bytes (the existing loader
    /// already owns the in-memory bytes, so no re-reading of large files here).
    /// </summary>
    public static FmpExecutionCaptureKey From(FmpPlaybackContext context, byte[] trackData)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(trackData);
        return new(
            TrackContentSha256: TrackContentSha(trackData),
            OutputSampleRate: context.SampleRate,
            CpuClockFrequencyHz: NativeAudioFmpPcmSession.CpuClockHz,
            LoopCount: context.LoopCount,
            FadeSeconds: context.FadeSeconds,
            TailSeconds: context.TailSeconds,
            MaxDurationSeconds: context.MaxDurationSeconds,
            SsgGainDb: context.SsgGainDb);
    }

    internal static string TrackContentSha(byte[] trackData) =>
        Convert.ToHexString(SHA256.HashData(trackData));

    public override string ToString() =>
        string.Join('|',
            TrackContentSha256,
            OutputSampleRate,
            CpuClockFrequencyHz,
            LoopCount,
            FadeSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            TailSeconds.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            MaxDurationSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "null",
            SsgGainDb.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
}

/// <summary>
/// A single current-track in-memory cache for one completed immutable
/// <see cref="FmpExecutionCapture"/>. It exists to avoid repeating the control
/// capture when the user previews native audio and then exports the same track
/// (or restarts the preview) without changing capture-affecting settings.
///
/// This is deliberately NOT a general cache service: it holds at most one
/// capture for the current render session, is not disk-backed, is not an LRU,
/// and never caches failed, cancelled or partial captures. It must be owned by
/// the current loaded-track / render-session scope and disposed or released when
/// the track changes, a new session opens, the app closes, or the capture is
/// invalidated. It must not be kept in static fields.
/// </summary>
internal sealed class FmpExecutionCaptureCache : IDisposable
{
    private FmpExecutionCaptureKey _key;
    private FmpExecutionCapture? _capture;

    /// <summary>Whether a completed capture is currently held.</summary>
    public bool HasCapture { get; private set; }

    /// <summary>Number of completed captures inserted this session (drives reuse assertions).</summary>
    public int Inserts { get; private set; }

    /// <summary>Number of successful cache hits this session.</summary>
    public int Hits { get; private set; }

    /// <summary>
    /// Gets a completed capture whose key matches <paramref name="key"/>, or null
    /// when the cache is empty or keyed differently. A hit never mutates state.
    /// </summary>
    public FmpExecutionCapture? TryGet(FmpExecutionCaptureKey key)
    {
        if (!HasCapture || !Equals(key, _key))
            return null;
        Hits++;
        return _capture;
    }

    /// <summary>
    /// Stores a completed, immutable capture under <paramref name="key"/>,
    /// replacing any prior current-track entry. Failed/cancelled captures are
    /// the caller's responsibility to not pass here; this method only stores
    /// values it is handed and never stores a null.
    /// </summary>
    public void Put(FmpExecutionCaptureKey key, FmpExecutionCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        _key = key;
        _capture = capture;
        HasCapture = true;
        Inserts++;
    }

    /// <summary>Drops the held capture (track change / invalidation / close).</summary>
    public void Invalidate()
    {
        _capture = null;
        HasCapture = false;
    }

    public void Dispose()
    {
        Invalidate();
    }
}
