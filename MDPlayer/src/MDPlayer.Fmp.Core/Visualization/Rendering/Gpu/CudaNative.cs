using System;
using System.Runtime.InteropServices;

namespace Fmp.Core.Visualization.Rendering.Gpu;

// Minimal CUDA Driver API bindings for the GL↔CUDA texture-interop proof
// (Patch 3). Deliberately tiny: only the handful of entry points needed to
// register a GL texture, map it, copy its pixels device-to-device, unmap it,
// reuse the ring, and tear down — nothing more. FFmpeg.AutoGen is NOT used
// here; this is the raw seam the encoder patch will consume.

/// <summary>Cuda driver-API result code.</summary>
internal enum CudaResult : int
{
    Success = 0,
    ErrorNotInitialized = 3,
    ErrorIncompatibleDriverContext = 17,
    ErrorInvalidValue = 1,
    ErrorInvalidDevice = 10,
    ErrorNoDevice = 100,
    ErrorNotFound = 500,
    ErrorInvalidResourceHandle = 400,
}

internal enum CudaGraphicsRegisterFlags : uint
{
    None = 0x0,
    ReadOnly = 0x1,
    WriteDiscard = 0x2,
    SurfaceLoadStore = 0x4,
    TextureGather = 0x8,
}

internal enum CudaGraphicsMapFlags : uint
{
    None = 0x0,
    ReadOnly = 0x1,
    WriteDiscard = 0x2,
}

/// <summary>CUDA device-list query flags for <c>cuGLGetDevices</c>.</summary>
internal enum CudaGraphicsDeviceListFlags : uint
{
    /// <summary>Devices associated with the current OpenGL context.</summary>
    CurrentFrame = 0x1,
    All = 0x2,
}

/// <summary>Source/destination memory kind in <see cref="CudaMemcpy2D"/>.</summary>
internal enum CudaMemoryType : int
{
    Host = 1,
    Device = 2,
    Array = 3,
    Unified = 4,
}

// Opaque CUDA handles.
internal readonly struct CudaDevice
{
    public readonly int Value;
    public CudaDevice(int value) => Value = value;
    public bool IsDefault => Value == 0;
}

internal readonly struct CudaContext
{
    public readonly IntPtr Value;
    public CudaContext(IntPtr value) => Value = value;
    public bool IsNull => Value == IntPtr.Zero;
}

internal readonly struct CudaDevicePtr
{
    public readonly ulong Value;
    public CudaDevicePtr(ulong value) => Value = value;
    public bool IsNull => Value == 0;
}

internal readonly struct CudaArray
{
    public readonly IntPtr Value;
    public CudaArray(IntPtr value) => Value = value;
    public bool IsNull => Value == IntPtr.Zero;
}

internal readonly struct CudaGraphicsResource
{
    public readonly IntPtr Value;
    public CudaGraphicsResource(IntPtr value) => Value = value;
    public bool IsNull => Value == IntPtr.Zero;
}

internal readonly struct CudaStream
{
    public readonly IntPtr Value;
    public CudaStream(IntPtr value) => Value = value;
}

/// <summary>
/// 2D memory copy descriptor. Field order and sizes match the CUDA driver-API
/// <c>CUDA_MEMCPY2D</c> struct on 64-bit Linux.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct CudaMemcpy2D
{
    public ulong SrcXInBytes;
    public ulong SrcY;
    public CudaMemoryType SrcMemoryType;
    public IntPtr SrcHost;
    public ulong SrcDevice;   // CudaDevicePtr
    public IntPtr SrcArray;   // CudaArray
    public ulong SrcPitch;

    public ulong DstXInBytes;
    public ulong DstY;
    public CudaMemoryType DstMemoryType;
    public IntPtr DstHost;
    public ulong DstDevice;   // CudaDevicePtr
    public IntPtr DstArray;   // CudaArray
    public ulong DstPitch;

    public ulong WidthInBytes;
    public ulong Height;
}

/// <summary>
/// Raw <c>libcuda.so.1</c> P/Invoke surface. Every call may throw
/// <see cref="CudaException"/> (except the availability probe). Do not call
/// these directly outside <see cref="CudaGraphicsInterop"/>.
/// </summary>
internal static unsafe partial class CudaNative
{
    private const string Lib = "libcuda.so.1";

    /// <summary>Thrown when a CUDA driver call fails.</summary>
    public sealed class CudaException : Exception
    {
        public CudaException(CudaResult result, string message)
            : base($"{message} (cuda {GetErrorName(result)}: {GetErrorString(result)})") { }
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuInit(uint flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGLGetDevices(
        uint* pCudaDeviceCount, CudaDevice* pCudaDevices, uint cudaDeviceCountMax, CudaGraphicsDeviceListFlags flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuDeviceGetCount(int* pCount);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuDeviceGet(CudaDevice* pDevice, int ordinal);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuDevicePrimaryCtxRetain(CudaContext* pctx, CudaDevice dev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuDevicePrimaryCtxRelease(CudaDevice dev);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuCtxSetCurrent(CudaContext ctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuCtxGetCurrent(CudaContext* pctx);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGraphicsGLRegisterImage(
        CudaGraphicsResource* pCudaResource, uint glImage, uint glTarget, CudaGraphicsRegisterFlags flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGraphicsMapResources(
        uint count, CudaGraphicsResource* pResources, CudaStream hStream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGraphicsSubResourceGetMappedArray(
        CudaArray* pArray, CudaGraphicsResource resource, uint arrayIndex, uint mipLevel);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGraphicsUnmapResources(
        uint count, CudaGraphicsResource* pResources, CudaStream hStream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGraphicsUnregisterResource(CudaGraphicsResource resource);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuMemAlloc(CudaDevicePtr* dptr, nuint bytesize);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuMemFree(CudaDevicePtr dptr);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuMemcpy2DAsync(CudaMemcpy2D* pCopy, CudaStream hStream);
    // Blocking cuMemcpy2D — used ONLY for the test's tiny device→host oracle
    // (cuMemcpy2DAsync to arbitrary pageable host memory is illegal), so we
    // deliberately keep the synchronous variant for the one place that needs it.
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuMemcpy2D(CudaMemcpy2D* pCopy);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuStreamCreate(CudaStream* phStream, uint flags);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuStreamSynchronize(CudaStream hStream);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuStreamDestroy(CudaStream hStream);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGetErrorName(CudaResult error, byte** pStr);
    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    private static extern CudaResult cuGetErrorString(CudaResult error, byte** pStr);

    private static string GetErrorName(CudaResult result)
    {
        try
        {
            byte* p;
            return cuGetErrorName(result, &p) == CudaResult.Success && p != null
                ? Marshal.PtrToStringAnsi((IntPtr)p) ?? "?"
                : result.ToString();
        }
        catch { return result.ToString(); }
    }

    private static string GetErrorString(CudaResult result)
    {
        try
        {
            byte* p;
            return cuGetErrorString(result, &p) == CudaResult.Success && p != null
                ? Marshal.PtrToStringAnsi((IntPtr)p) ?? "?"
                : "";
        }
        catch { return ""; }
    }

    private static void Check(CudaResult result, string what)
    {
        if (result != CudaResult.Success)
            throw new CudaException(result, what);
    }

    // ------------------------------------------------------------------
    // High-level, checked helpers (the only surface the interop uses).
    // ------------------------------------------------------------------

    /// <summary>
    /// True when libcuda loads and initializes. Does not prove a device is
    /// linked to the current GL context (see <see cref="QueryGlDevices"/>).
    /// </summary>
    public static bool TryInitialize(out string error)
    {
        error = "";
        try
        {
            return cuInit(0) == CudaResult.Success;
        }
        catch (DllNotFoundException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (BadImageFormatException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// CUDA device associated with the CURRENT OpenGL context
    /// (<c>CU_GL_DEVICE_LIST_CURRENT_FRAME</c>). False on any failure or when
    /// no device is linked — callers treat that as "interop unavailable".
    /// </summary>
    public static bool TryQueryGlDevice(out CudaDevice device, out string error)
    {
        error = "";
        device = default;
        if (!TryInitialize(out error))
            return false;

        uint count = 0;
        CudaDevice dev = default;
        CudaResult r = cuGLGetDevices(&count, &dev, 1, CudaGraphicsDeviceListFlags.CurrentFrame);
        if (r != CudaResult.Success || count == 0)
        {
            error = r != CudaResult.Success
                ? $"{nameof(cuGLGetDevices)} failed: {GetErrorName(r)}"
                : "No CUDA device is linked to the current OpenGL context.";
            return false;
        }

        device = new CudaDevice(dev.Value);
        return true;
    }

    public static CudaContext RetainPrimaryContext(CudaDevice device)
    {
        CudaContext ctx;
        Check(cuDevicePrimaryCtxRetain(&ctx, device), nameof(cuDevicePrimaryCtxRetain));
        return ctx;
    }

    /// <summary>Number of CUDA devices visible to the driver (diagnostics).</summary>
    public static int DeviceGetCount()
    {
        int count = 0;
        if (!TryInitialize(out _))
            return 0;
        return cuDeviceGetCount(&count) == CudaResult.Success ? count : -1;
    }

    /// <summary>Devices by ordinal (diagnostics).</summary>
    public static CudaDevice DeviceGet(int ordinal)
    {
        CudaDevice dev;
        Check(cuDeviceGet(&dev, ordinal), nameof(cuDeviceGet));
        return dev;
    }

    public static void ReleasePrimaryContext(CudaDevice device)
        => _ = cuDevicePrimaryCtxRelease(device);

    public static void SetCurrentContext(CudaContext ctx)
        => Check(cuCtxSetCurrent(ctx), nameof(cuCtxSetCurrent));

    public static CudaContext GetCurrentContext()
    {
        CudaContext ctx;
        Check(cuCtxGetCurrent(&ctx), nameof(cuCtxGetCurrent));
        return ctx;
    }

    public static CudaGraphicsResource RegisterGlTexture(uint glTexture, uint glTarget)
    {
        CudaGraphicsResource res;
        Check(
            cuGraphicsGLRegisterImage(&res, glTexture, glTarget, CudaGraphicsRegisterFlags.ReadOnly),
            $"cuGraphicsGLRegisterImage (texture {glTexture})");
        return res;
    }

    public static void MapResources(CudaGraphicsResource res, CudaStream stream)
    {
        Check(cuGraphicsMapResources(1, &res, stream), nameof(cuGraphicsMapResources));
    }

    public static void UnmapResources(CudaGraphicsResource res, CudaStream stream)
    {
        Check(cuGraphicsUnmapResources(1, &res, stream), nameof(cuGraphicsUnmapResources));
    }

    public static CudaArray GetMappedArray(CudaGraphicsResource res)
    {
        CudaArray arr;
        Check(cuGraphicsSubResourceGetMappedArray(&arr, res, 0, 0), nameof(cuGraphicsSubResourceGetMappedArray));
        return arr;
    }

    public static void UnregisterResource(CudaGraphicsResource res)
        => Check(cuGraphicsUnregisterResource(res), nameof(cuGraphicsUnregisterResource));

    public static CudaDevicePtr MemAlloc(nuint bytes)
    {
        CudaDevicePtr p;
        Check(cuMemAlloc(&p, bytes), nameof(cuMemAlloc));
        return p;
    }

    public static void MemFree(CudaDevicePtr p)
        => Check(cuMemFree(p), nameof(cuMemFree));

    public static void Memcpy2DAsync(CudaMemcpy2D* copy, CudaStream stream)
        => Check(cuMemcpy2DAsync(copy, stream), nameof(cuMemcpy2DAsync));

    public static void Memcpy2D(CudaMemcpy2D* copy)
        => Check(cuMemcpy2D(copy), nameof(cuMemcpy2D));

    public static CudaStream StreamCreate()
    {
        CudaStream s;
        Check(cuStreamCreate(&s, 0), nameof(cuStreamCreate));
        return s;
    }

    public static void StreamSynchronize(CudaStream s)
        => Check(cuStreamSynchronize(s), nameof(cuStreamSynchronize));

    public static void StreamDestroy(CudaStream s)
        => _ = cuStreamDestroy(s);
}