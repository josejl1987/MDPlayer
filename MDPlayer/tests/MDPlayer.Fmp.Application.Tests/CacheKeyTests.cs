using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Xunit;

namespace Fmp.Application.Tests;

public class CacheKeyTests
{
    private static VisualizationRequest Request() => TestRequests.Valid();

    private static string Key1(VisualizationRequest? request = null, long length = 100, DateTime? stamp = null)
        => PreviewCacheKey.CaptureKey(
            "/music/song.vgz",
            length,
            stamp ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            request ?? Request());

    [Fact]
    public void CaptureKey_IsStableForIdenticalInputs()
    {
        Assert.Equal(Key1(), Key1());
    }

    [Fact]
    public void CaptureKey_ChangesWithInputLength()
    {
        Assert.NotEqual(Key1(length: 100), Key1(length: 101));
    }

    [Fact]
    public void CaptureKey_ChangesWithLastWriteTime()
    {
        Assert.NotEqual(Key1(stamp: new DateTime(2026, 1, 1)), Key1(stamp: new DateTime(2026, 1, 2)));
    }

    [Fact]
    public void CaptureKey_ChangesWithCaptureAffectingOptions()
    {
        Assert.NotEqual(Key1(), Key1(request: Request() with { LoopCount = 3 }));
        Assert.NotEqual(Key1(), Key1(request: Request() with { SampleRate = 48000 }));
        Assert.NotEqual(Key1(), Key1(request: Request() with { TailSeconds = 2.0 }));
    }

    [Fact]
    public void CaptureKey_IgnoresExportOnlyOptions()
    {
        Assert.Equal(Key1(), Key1(request: Request() with { Encoder = VideoEncoder.Nvenc, Overwrite = true }));
    }

    [Fact]
    public void FrameKey_ChangesWithTimeDimensionsAndFidelity()
    {
        VisualizationRequest request = Request();
        string baseKey = PreviewCacheKey.FrameKey(request, 10.0, 960, 540, PreviewFidelity.AccurateStill);
        Assert.Equal(baseKey, PreviewCacheKey.FrameKey(request, 10.0, 960, 540, PreviewFidelity.AccurateStill));
        Assert.NotEqual(baseKey, PreviewCacheKey.FrameKey(request, 10.5, 960, 540, PreviewFidelity.AccurateStill));
        Assert.NotEqual(baseKey, PreviewCacheKey.FrameKey(request, 10.0, 1280, 720, PreviewFidelity.AccurateStill));
        Assert.NotEqual(baseKey, PreviewCacheKey.FrameKey(request, 10.0, 960, 540, PreviewFidelity.Layout));
    }

    [Fact]
    public void FrameKey_ChangesWithRequest()
    {
        Assert.NotEqual(
            PreviewCacheKey.FrameKey(Request(), 5, 960, 540, PreviewFidelity.AccurateStill),
            PreviewCacheKey.FrameKey(Request() with { Width = 1920 }, 5, 960, 540, PreviewFidelity.AccurateStill));
    }

    [Fact]
    public void RequestHash_IsStableAndDistinct()
    {
        string hash = PreviewCacheKey.RequestHash(Request());
        Assert.Equal(hash, PreviewCacheKey.RequestHash(Request()));
        Assert.NotEqual(hash, PreviewCacheKey.RequestHash(Request() with { Title = "other" }));
    }
}
