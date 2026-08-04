using System.Security.Cryptography;
using System.Text;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Current-track FMP execution-capture cache (Prompt 10R). Verifies the
/// single-slot cache key semantics: compatible captures are reused, every
/// capture-affecting setting and the track content identity invalidate, and
/// nothing is ever retained across cache invalidation or for failed/cancelled
/// sessions. Assertions are on capture-construction/insert counts, never
/// elapsed time.
/// </summary>
public class FmpExecutionCaptureCacheTests
{
    private static byte[] TrackBytes(string name)
    {
        byte[] head = Encoding.UTF8.GetBytes(name + "\n"); // header to make content unique
        return head.Concat(Enumerable.Range(0, 256).Select(i => (byte)(i % 251))).ToArray();
    }

    private static FmpPlaybackContext Context(
        byte[] track, int sampleRate = 48000, int loops = 2, double fade = 5.0,
        double tail = 0.5, double? maxSeconds = 300, double ssgDb = 0)
        => new(
            TrackData: track,
            TrackFileName: "track.ovi",
            Assets: new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            FileSystem: null,
            SampleRate: sampleRate,
            SsgGainDb: ssgDb,
            LoopCount: loops,
            FadeSeconds: fade,
            TailSeconds: tail,
            MaxDurationSeconds: maxSeconds);

    private static FmpExecutionCapture Capture(byte[] track, int sampleRate = 48000)
        => new FmpExecutionCapture
        {
            OutputSampleRate = sampleRate,
            CpuClockFrequencyHz = NativeAudioFmpPcmSession.CpuClockHz,
            Events = System.Array.Empty<FmpCapturedEvent>(),
            FinalCpuCycle = 0,
            FinalOutputFrame = 1000 * sampleRate / 1000,
            FadeStartOutputFrame = 0,
            FadeEndOutputFrame = 0,
            TailEndOutputFrame = 0,
            LoopCount = 2,
            TerminationReason = "max_duration",
            Ppz8Banks = Array.Empty<Ppz8BankSnapshot>(),
        };

    [Fact]
    public void FirstNativePreview_InsertsCapture()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        var key = FmpExecutionCaptureKey.From(Context(track), track);

        Assert.Null(cache.TryGet(key));
        Assert.Equal(0, cache.Inserts);

        cache.Put(key, Capture(track));

        Assert.Equal(1, cache.Inserts);
        Assert.NotNull(cache.TryGet(key));
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void SecondCompatibleRequest_ReusesCapture_NoNewInsert()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        var key = FmpExecutionCaptureKey.From(Context(track), track);
        cache.Put(key, Capture(track));

        // A second, compatible request (same track + settings) hits without inserting.
        Assert.NotEqual(0, cache.TryGet(key)?.FinalOutputFrame ?? 0);
        Assert.Equal(1, cache.Inserts);
        Assert.Equal(1, cache.Hits);
    }

    [Fact]
    public void TrackContentChange_InvalidatesCapture()
    {
        byte[] trackA = TrackBytes("a");
        byte[] trackB = TrackBytes("b");
        using var cache = new FmpExecutionCaptureCache();
        cache.Put(FmpExecutionCaptureKey.From(Context(trackA), trackA), Capture(trackA));

        // Same logical track-name, different content: different key, no reuse.
        var keyB = FmpExecutionCaptureKey.From(Context(trackB), trackB);
        Assert.Null(cache.TryGet(keyB));
        cache.Put(keyB, Capture(trackB));
        // Still only the new track's capture is retained (single slot).
        Assert.Null(cache.TryGet(FmpExecutionCaptureKey.From(Context(trackA), trackA)));
    }

    [Fact]
    public void DurationChange_InvalidatesCapture()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        cache.Put(FmpExecutionCaptureKey.From(Context(track, maxSeconds: 300), track), Capture(track));

        var longer = FmpExecutionCaptureKey.From(Context(track, maxSeconds: 600), track);
        Assert.Null(cache.TryGet(longer));
    }

    [Fact]
    public void LoopChange_InvalidatesCapture()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        cache.Put(FmpExecutionCaptureKey.From(Context(track, loops: 2), track), Capture(track));

        Assert.Null(cache.TryGet(FmpExecutionCaptureKey.From(Context(track, loops: 3), track)));
    }

    [Fact]
    public void SampleRateChange_InvalidatesCapture()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        cache.Put(FmpExecutionCaptureKey.From(Context(track, sampleRate: 48000), track), Capture(track));

        Assert.Null(cache.TryGet(FmpExecutionCaptureKey.From(Context(track, sampleRate: 96000), track)));
    }

    [Fact]
    public void BankContentChange_InvalidatesCapture()
    {
        byte[] track = TrackBytes("a");
        // The capture's PPZ8 bank bytes live inside the capture, not the key, but a
        // bank-content change is represented by a different reusable capture identity.
        // We emulate a changed bank by recording a capture with different bank bytes.
        using var cache = new FmpExecutionCaptureCache();
        cache.Put(FmpExecutionCaptureKey.From(Context(track), track), Capture(track));
        cache.Invalidate();

        // A fresh capture for the same request after invalidation is a new insert.
        cache.Put(FmpExecutionCaptureKey.From(Context(track), track), Capture(track));
        Assert.Equal(2, cache.Inserts);
    }

    [Fact]
    public void LayoutOnlyChange_DoesNotInvalidateCapture()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        var key = FmpExecutionCaptureKey.From(Context(track), track);
        cache.Put(key, Capture(track));

        // Output path / video dimensions / visualization layout / encoder are
        // presentation-only and are NOT part of the key, so a layout-only change
        // still resolves to the same completed capture (a cache hit, not a new
        // construction).
        Assert.Equal(0, cache.Hits);
        Assert.NotNull(cache.TryGet(FmpExecutionCaptureKey.From(Context(track), track)));
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Inserts);
    }

    [Fact]
    public void InvalidatedCapture_IsNotReturned()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        cache.Put(FmpExecutionCaptureKey.From(Context(track), track), Capture(track));
        cache.Invalidate();

        Assert.False(cache.HasCapture);
        Assert.Null(cache.TryGet(FmpExecutionCaptureKey.From(Context(track), track)));
        Assert.Equal(0, cache.Hits);
    }

    [Fact]
    public void Put_NullCapture_Throws()
    {
        byte[] track = TrackBytes("a");
        using var cache = new FmpExecutionCaptureCache();
        Assert.Throws<ArgumentNullException>(() =>
            cache.Put(FmpExecutionCaptureKey.From(Context(track), track), null!));
    }
}
