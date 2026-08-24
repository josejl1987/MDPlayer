using System;
using System.Runtime.InteropServices;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// CUDA↔OpenGL texture interop (Patch 3 proof seam). Owns one CUDA primary
/// context (retained, matched to the device backing the current GL context via
/// <c>cuGLGetDevices</c>) plus a stream, registers each export-ring GL texture
/// exactly once, and can map / copy a slot's pixels device-to-device / unmap on
/// demand. No per-frame registration, no CPU readback in the production path.
///
/// Context ownership is explicit: callers wrap each interop sequence in the GL
/// <c>MakeCurrentScope</c> (the GL context must be current to map), and every
/// method here presses the CUDA context onto the calling thread via a
/// save/restore scope (a retained primary context is NOT current until
/// <c>cuCtxSetCurrent</c>).
///
/// This is the full architectural-de-risking slice: if map→CUarray→device copy
///→unmap→GL reuse works reliably here, Patch 4 is straightforward plumbing.
/// </summary>
internal sealed class CudaGraphicsInterop : IDisposable
{
    private const uint GlTexture2D = 0x0DE1;

    private readonly CudaDevice _device;
    private readonly CudaContext _context;
    private readonly CudaStream _stream;
    private readonly uint[] _glTextures;
    private readonly int _width;
    private readonly int _height;
    private readonly CudaGraphicsResource[] _resources;
    private readonly bool[] _registered;
    private CudaDevicePtr _scratch;
    private nuint _scratchBytes;
    private int _registeredCount;
    private bool _disposed;

    public int Width => _width;
    public int Height => _height;
    public int FrameBytes => checked(_width * _height * 4);

    private CudaGraphicsInterop(
        CudaDevice device, CudaContext context, CudaStream stream, uint[] glTextures, int width, int height)
    {
        _device = device;
        _context = context;
        _stream = stream;
        _glTextures = glTextures;
        _width = width;
        _height = height;
        _resources = new CudaGraphicsResource[glTextures.Length];
        _registered = new bool[glTextures.Length];
    }

    /// <summary>
    /// Creates the interop session by resolving the CUDA device behind the
    /// CURRENT OpenGL context. Returns false (with <paramref name="error"/>) when
    /// CUDA is absent or no device is linked to the GL context — callers skip
    /// the GPU-export path and keep using readback.
    /// </summary>
    public static bool TryCreate(
        uint[] glTextureIds, int width, int height, out CudaGraphicsInterop interop, out string error)
    {
        interop = null;
        error = "";
        try
        {
            if (!CudaNative.TryQueryGlDevice(out CudaDevice device, out error))
                return false;

            CudaContext context = CudaNative.RetainPrimaryContext(device);
            try
            {
                // A retained primary context is NOT current (per NVIDIA docs);
                // cuStreamCreate demands a current context, so press it on for
                // the thread and restore afterwards.
                CudaContext prior = CudaNative.GetCurrentContext();
                CudaNative.SetCurrentContext(context);
                CudaStream stream;
                try
                {
                    stream = CudaNative.StreamCreate();
                }
                finally
                {
                    CudaNative.SetCurrentContext(prior);
                }
                try
                {
                    interop = new CudaGraphicsInterop(device, context, stream, glTextureIds, width, height);
                    return true;
                }
                catch
                {
                    CudaNative.StreamDestroy(stream);
                    throw;
                }
            }
            catch
            {
                CudaNative.ReleasePrimaryContext(device);
                throw;
            }
        }
        catch (CudaNative.CudaException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Number of registered GL textures (0 until <see cref="RegisterAll"/>).</summary>
    public int RegisteredCount => _registeredCount;

    /// <summary>
    /// Registers every GL texture with CUDA exactly once. Must run with the GL
    /// context current (caller wraps in an <c>IDisposable</c> GL scope). On any
    /// registration failure it unregisters everything already registered and
    /// rethrows, so the session is left clean to dispose.
    /// </summary>
    public void RegisterAll()
    {
        if (_registeredCount == _glTextures.Length)
            return;
        ThrowIfDisposed();
        using (new CudaCurrentScope(this))
        {
            for (int slot = 0; slot < _glTextures.Length; slot++)
            {
                if (_registered[slot])
                    continue;
                try
                {
                    _resources[slot] = CudaNative.RegisterGlTexture(_glTextures[slot], GlTexture2D);
                    _registered[slot] = true;
                    _registeredCount++;
                }
                catch
                {
                    UnregisterRegistered(silent: true);
                    throw;
                }
            }
        }
    }

    /// <summary>
    /// Maps a slot's GL texture, copies its full RGBA frame into the internal
    /// CUDA device scratch buffer (VRAM→VRAM via <c>cuMemcpy2DAsync</c>), unmaps
    /// and waits on the stream. The GL resource mapping provides the
    /// CUDA/GL synchronization, so no <c>glFinish</c> is needed. Caller holds the
    /// GL context current. The captured pixels sit in scratch for
    /// <see cref="ReadRegion"/> (test oracle) or the future NVENC frame copy.
    /// </summary>
    public unsafe void CaptureFrame(int slot)
    {
        ThrowIfDisposed();
        if ((uint)slot >= (uint)_resources.Length || !_registered[slot])
            throw new InvalidOperationException($"Slot {slot} is not registered.");

        using (new CudaCurrentScope(this))
        {
            CudaGraphicsResource resource = _resources[slot];
            CudaNative.MapResources(resource, _stream);
            try
            {
                CudaArray array = CudaNative.GetMappedArray(resource);
                EnsureScratch((nuint)FrameBytes);

                var copy = new CudaMemcpy2D
                {
                    SrcMemoryType = CudaMemoryType.Array,
                    SrcArray = array.Value,
                    DstMemoryType = CudaMemoryType.Device,
                    DstDevice = _scratch.Value,
                    DstPitch = (ulong)(_width * 4),
                    WidthInBytes = (ulong)(_width * 4),
                    Height = (ulong)_height,
                };
                CudaNative.Memcpy2DAsync(&copy, _stream);
            }
            finally
            {
                CudaNative.UnmapResources(resource, _stream);
            }
            CudaNative.StreamSynchronize(_stream);
        }
    }

    /// <summary>
    /// Copies a <paramref name="w"/>×<paramref name="h"/> region at
    /// (<paramref name="x"/>, <paramref name="y"/>) out of the last captured
    /// scratch frame into <paramref name="buffer"/> (RGBA). TEST/ORACLE ONLY —
    /// proves the CUDA-side pixels are correct without benchmarking the shipping
    /// transport. Needs no GL context.
    /// </summary>
    public unsafe void ReadRegion(int x, int y, int w, int h, Span<byte> buffer)
    {
        ThrowIfDisposed();
        if (x < 0 || y < 0 || w <= 0 || h <= 0 || buffer.Length < checked(w * h * 4))
            throw new ArgumentException("Invalid region or buffer size.");
        if (_scratch.IsNull)
            throw new InvalidOperationException("No captured frame; call CaptureFrame first.");

        fixed (byte* dst = buffer)
        {
            var copy = new CudaMemcpy2D
            {
                SrcMemoryType = CudaMemoryType.Device,
                SrcDevice = _scratch.Value,
                SrcXInBytes = (ulong)(x * 4),
                SrcY = (ulong)y,
                SrcPitch = (ulong)(_width * 4),
                DstMemoryType = CudaMemoryType.Host,
                DstHost = (IntPtr)dst,
                DstPitch = (ulong)(w * 4),
                WidthInBytes = (ulong)(w * 4),
                Height = (ulong)h,
            };
            // Blocking host copy (pageable host memory is illegal for the async
            // variant); oracle-only, never on the shipping path.
            using (new CudaCurrentScope(this))
            {
                CudaNative.Memcpy2D(&copy);
            }
        }
    }

    private void EnsureScratch(nuint bytes)
    {
        if (!_scratch.IsNull && _scratchBytes >= bytes)
            return;
        if (!_scratch.IsNull)
            CudaNative.MemFree(_scratch);
        using (new CudaCurrentScope(this))
        {
            _scratch = CudaNative.MemAlloc(bytes);
            _scratchBytes = bytes;
        }
    }

    private void UnregisterRegistered(bool silent)
    {
        using (new CudaCurrentScope(this))
        {
            for (int slot = 0; slot < _resources.Length; slot++)
            {
                if (!_registered[slot])
                    continue;
                try
                {
                    CudaNative.UnregisterResource(_resources[slot]);
                }
                catch when (silent) { }
                _registered[slot] = false;
                _registeredCount--;
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(CudaGraphicsInterop));
    }

    /// <summary>
    /// Unregisters every CUDA resource BEFORE any GL texture is deleted, then
    /// frees the scratch buffer, stream and primary context. Caller holds the GL
    /// context current (registration created GL-context links).
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_registeredCount > 0)
        {
            try { UnregisterRegistered(silent: true); } catch { }
        }
        if (!_scratch.IsNull)
        {
            try
            {
                using (new CudaCurrentScope(this))
                    CudaNative.MemFree(_scratch);
            }
            catch { }
            _scratch = default;
        }
        try { CudaNative.StreamDestroy(_stream); } catch { }
        try { CudaNative.ReleasePrimaryContext(_device); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Presses this session's CUDA context onto the calling thread and restores
    /// the prior context on dispose (explicit ownership — a retained primary
    /// context is not current until set, per NVIDIA docs).
    /// </summary>
    private sealed class CudaCurrentScope : IDisposable
    {
        private readonly CudaGraphicsInterop _owner;
        private readonly CudaContext _prior;

        public CudaCurrentScope(CudaGraphicsInterop owner)
        {
            _owner = owner;
            _prior = SafeGetCurrent();
            CudaNative.SetCurrentContext(owner._context);
        }

        public void Dispose()
        {
            try { CudaNative.SetCurrentContext(_prior); } catch { }
        }

        private static CudaContext SafeGetCurrent()
        {
            try { return CudaNative.GetCurrentContext(); }
            catch { return default; }
        }
    }
}