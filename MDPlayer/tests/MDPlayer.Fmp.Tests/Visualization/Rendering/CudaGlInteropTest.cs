using System;
using System.Linq;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Fmp.Core.Visualization.Rendering.Gpu;
using MDPlayer.Fmp.Tests.Fixtures;
using OpenTK.Windowing.Desktop;
using Xunit;
using Xunit.Abstractions;

#nullable enable
namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Patch 3: proves CUDA↔OpenGL texture interop works under the real PRIME/GLX
/// configuration — the single biggest architectural risk before the encoder
/// build. Verifies the GL export-ring textures register, map, copy device-to-
/// device, unmap and reuse across many ring cycles with ZERO GL→CPU readback in
/// the production path.
///
/// Skips (does not fail) on machines without a display, without CUDA, or where
/// <c>cuGLGetDevices</c> links no device to the current GL context (e.g. when
/// the renderer ran on an Intel/Mesa context).
/// </summary>
public sealed class CudaGlInteropTest
{
    private const int Width = 1920;
    private const int Height = 1080;

    private readonly ITestOutputHelper _out;
    public CudaGlInteropTest(ITestOutputHelper output) => _out = output;

    private static GpuPanelRenderer CreateRendererOrSkip()
    {
        GLFWProvider.CheckForMainThread = false;
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        ResolvedVisualizationLayout layout = RendererTestLayout.Build(timeline, Width, Height);
        try
        {
            return new GpuPanelRenderer(timeline, layout, new PanelOverlayRenderer.Options
            {
                FpsNumerator = 60,
                FpsDenominator = 1,
            });
        }
        catch (Exception ex)
        {
            Skip.If(true, $"No display/GL context: {ex.Message}");
            throw; // unreachable (Skip.Always throws)
        }
    }

    private static byte[] MakeGrid(int byteCount, byte r, byte g, byte b)
    {
        var grid = new byte[byteCount];
        for (int i = 0; i + 3 < grid.Length; i += 4)
        {
            grid[i] = r;
            grid[i + 1] = g;
            grid[i + 2] = b;
            grid[i + 3] = 255;
        }
        return grid;
    }

    /// <summary>
    /// One full CUDA device→host oracle pass: captures the slot over CUDA, then
    /// compares the whole captured frame against the standard readback path.
    /// Returns a description when they differ so channel-order/orientation bugs
    /// are diagnosed from the failure message.
    /// </summary>
    private static string? CompareCapturedToStandard(
        CudaGraphicsInterop interop, int slot, byte[] standard, byte[] scratch)
    {
        interop.ReadRegion(0, 0, Width, Height, scratch);
        if (scratch.AsSpan().SequenceEqual(standard))
            return null;

        bool flipped = true;
        for (int y = 0; y < Height; y++)
        {
            int fwd = y * Width * 4;
            int rev = (Height - 1 - y) * Width * 4;
            if (!scratch.AsSpan(fwd, Width * 4).SequenceEqual(standard.AsSpan(rev, Width * 4)))
            {
                flipped = false;
                break;
            }
        }

        int first = -1;
        for (int i = 0; i < standard.Length; i++)
        {
            if (scratch[i] != standard[i]) { first = i; break; }
        }
        int row = first < 0 ? -1 : first / (Width * 4);
        int col = first < 0 ? -1 : (first % (Width * 4)) / 4;
        return $"slot={slot} firstDiffByte={first} row={row} col={col} " +
               $"expected=({A(standard, first)}) captured=({A(scratch, first)}) verticalFlipMatches={flipped}";

        static string A(byte[] buf, int i)
            => i < 0 || i + 3 >= buf.Length ? "-" : $"{buf[i]},{buf[i + 1]},{buf[i + 2]},{buf[i + 3]}";
    }

    /// <summary>Opens a GPU export session, warms the ring, returns its texture ids.</summary>
    private static (IGpuExportSession Session, uint[] TextureIds) MakeWarmSession(
        GpuPanelRenderer gpu, int capacity, bool scopeFramesAreOpaque)
    {
        IGpuExportSession session = gpu.CreateGpuExportSession(capacity: capacity, scopeFramesAreOpaque: scopeFramesAreOpaque);
        GpuExportFrame f = session.RenderNext(0, scopeFramesAreOpaque
            ? MakeGrid(gpu.ScopeFrameByteCount, 255, 40, 200)
            : new byte[gpu.ScopeFrameByteCount]);
        session.Release(f.Slot);
        var ids = new uint[capacity];
        for (int s = 0; s < capacity; s++)
            ids[s] = (uint)gpu.ExportTextureId(s);
        return (session, ids);
    }

    [SkippableFact]
    public void GlExportTextures_RegisterMapCopyUnmapReuse_OverManyCycles()
    {
        using GpuPanelRenderer gpu = CreateRendererOrSkip();
        const int Capacity = 4;
        (IGpuExportSession session, uint[] textureIds) = MakeWarmSession(gpu, Capacity, scopeFramesAreOpaque: true);
        using (session)
        {
            CudaGraphicsInterop? interop = null;
            string cudaError = "";
            using (gpu.MakeCurrentScope())
                _ = CudaGraphicsInterop.TryCreate(textureIds, Width, Height, out interop, out cudaError);
            if (interop is null)
            {
                Skip.If(true, $"CUDA-GL interop unavailable: {cudaError}");
                return;
            }
            using (interop)
            {
                using (gpu.MakeCurrentScope())
                    interop.RegisterAll();
                Assert.Equal(Capacity, interop.RegisteredCount);

                byte[] grid = MakeGrid(gpu.ScopeFrameByteCount, 255, 40, 200);
                byte[] standard = new byte[gpu.FrameByteCount];
                byte[] scratch = new byte[gpu.FrameByteCount];

                const int Iterations = 32;
                for (int i = 0; i < Iterations; i++)
                {
                    int frameIndex = (int)(i % gpu.TotalFrames);
                    GpuExportFrame f = session.RenderNext(frameIndex, grid);
                    using (gpu.MakeCurrentScope())
                    {
                        interop.CaptureFrame(f.Slot);
                        gpu.RenderCompositeFrame(frameIndex, grid, standard, scopeFramesAreOpaque: true);
                    }
                    string? diff = CompareCapturedToStandard(interop, f.Slot, standard, scratch);
                    Assert.True(diff is null, $"CUDA-captured frame diverged on iteration {i} ({f.Slot}): {diff}");

                    // Reuse: the slot is free again immediately after CaptureFrame/Release.
                    session.Release(f.Slot);
                }
            }
        }
    }

    [SkippableFact]
    public void CudaCapture_IsScopeSensitive()
    {
        using GpuPanelRenderer gpu = CreateRendererOrSkip();
        (IGpuExportSession session, uint[] textureIds) = MakeWarmSession(gpu, 2, scopeFramesAreOpaque: true);
        using (session)
        {
            CudaGraphicsInterop? interop = null;
            string cudaError = "";
            using (gpu.MakeCurrentScope())
                _ = CudaGraphicsInterop.TryCreate(textureIds, Width, Height, out interop, out cudaError);
            if (interop is null)
            {
                Skip.If(true, $"CUDA-GL interop unavailable: {cudaError}");
                return;
            }
            using (interop)
            {
                using (gpu.MakeCurrentScope())
                    interop.RegisterAll();

                byte[] capA = new byte[gpu.FrameByteCount];
                byte[] capB = new byte[gpu.FrameByteCount];
                GpuExportFrame fa = session.RenderNext(0, MakeGrid(gpu.ScopeFrameByteCount, 255, 40, 200));
                GpuExportFrame fb = session.RenderNext(1, MakeGrid(gpu.ScopeFrameByteCount, 0, 0, 0));

                using (gpu.MakeCurrentScope())
                {
                    interop.CaptureFrame(fa.Slot);
                    interop.ReadRegion(0, 0, Width, Height, capA);
                    interop.CaptureFrame(fb.Slot);
                    interop.ReadRegion(0, 0, Width, Height, capB);
                }

                session.Release(fa.Slot);
                session.Release(fb.Slot);

                Assert.False(
                    capA.AsSpan().SequenceEqual(capB.AsSpan()),
                    "CUDA-captured frames must differ when the scope grid differs — " +
                    "a match means the scope source is not reaching the CUDA view.");
            }
        }
    }

    [SkippableFact]
    public void PureCudaAlloc_OnOrdinalZero_IsIsolatedDiagnostic()
    {
        // Pure-CUDA probe with NO GL context: does this driver allow a plain
        // primary context + cuMemAlloc at all? Isolates whether the
        // INVALID_CONTEXT on cuMemAlloc is GL-association-specific or is a
        // broader environment/driver condition.
        if (!CudaNative.TryInitialize(out string initError))
        {
            Skip.If(true, $"CUDA initialization failed: {initError}");
            return;
        }
        int count = CudaNative.DeviceGetCount();
        _out.WriteLine($"cuda-device-count={count}");
        if (count < 1)
        {
            Skip.If(true, "No CUDA device visible to the driver.");
            return;
        }

        CudaDevice device = CudaNative.DeviceGet(0);
        CudaContext before = CudaNative.GetCurrentContext();
        CudaContext ctx = CudaNative.RetainPrimaryContext(device);
        CudaNative.SetCurrentContext(ctx);
        try
        {
            CudaDevicePtr p = CudaNative.MemAlloc(64);
            _out.WriteLine($"pure-cuda cuMemAlloc(64) OK -> {p.Value}");
            CudaNative.MemFree(p);
        }
        finally
        {
            CudaNative.SetCurrentContext(before);
            CudaNative.ReleasePrimaryContext(device);
        }
    }
[SkippableFact]
    public void FailedRegistration_UnwindsCleanly()
    {
        using GpuPanelRenderer gpu = CreateRendererOrSkip();
        // Register a real texture (slot 0) followed by a bogus one so a failure
        // occurs AFTER one successful registration — wrong device/GL association
        // (registering a non-existent GL texture id) must unwind cleanly.
        const int Capacity = 2;
        (IGpuExportSession session, uint[] textureIds) = MakeWarmSession(gpu, Capacity, scopeFramesAreOpaque: false);
        textureIds[1] = 0xFFFFFFF0u; // not a real GL texture
        using (session)
        {
            CudaGraphicsInterop? interop = null;
            string cudaError = "";
            using (gpu.MakeCurrentScope())
                _ = CudaGraphicsInterop.TryCreate(textureIds, Width, Height, out interop, out cudaError);
            if (interop is null)
            {
                Skip.If(true, $"CUDA-GL interop unavailable: {cudaError}");
                return;
            }
            using (interop)
            {
                CudaNative.CudaException ex = Assert.Throws<CudaNative.CudaException>(() =>
                {
                    using (gpu.MakeCurrentScope())
                        interop.RegisterAll();
                });
                Assert.Contains("cuGraphicsGLRegisterImage", ex.Message);
                // Unwound: the one already-registered texture is unregistered, so
                // disposing the session is safe.
                Assert.Equal(0, interop.RegisteredCount);
            }
        }
    }
}