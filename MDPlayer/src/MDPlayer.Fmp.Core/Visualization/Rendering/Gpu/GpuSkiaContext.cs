using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Owns the GPU rendering context for the visualization renderer: a hidden
/// OpenTK <see cref="NativeWindow"/> with a 3.3 core OpenGL context, the
/// SkiaSharp Ganesh/OpenGL backend (<see cref="GRContext"/>), and a GPU-backed
/// <see cref="SKSurface"/> sized to the output frame.
///
/// Design contracts (GPU-first plan):
///  - The window is never shown and never focused; it exists only to own a GL
///    context that Skia can rasterize into.
///  - <see cref="BeginFrame"/> makes the context current, <see cref="Flush"/>
///    submits the frame's draw commands, and <see cref="ReadPixels"/> performs
///    the single per-frame readback into the caller's RGBA frame slot.
///  - Initialization is strict: any failure to create the GL interface, the
///    Ganesh context or the GPU surface throws — there is no silent fallback
///    to a raster surface.
/// </summary>
internal sealed class GpuSkiaContext : IDisposable
{
    private readonly NativeWindow _window;
    private readonly GRGlInterface _glInterface;
    private readonly GRContext _grContext;
    private readonly SKSurface _surface;
    private readonly SKImageInfo _frameInfo;

    public GpuSkiaContext(int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(width), "GPU frame dimensions must be positive.");

        // Opaque output: the GPU composites translucent layers onto an opaque
        // framebuffer, so alpha is always 255 and premultiplied RGB equals
        // straight RGB. Consumers then skip the full-frame unpremultiply pass.
        _frameInfo = new SKImageInfo(
            width, height, SKColorType.Rgba8888, SKAlphaType.Opaque);

        NativeWindow window;
        try
        {
            window = new NativeWindow(new NativeWindowSettings
            {
                StartVisible = false,
                StartFocused = false,
                // A 1x1 hidden window is all the context owner needs; the
                // render surface is created directly by Skia, not from the
                // window's framebuffer.
                Size = new Vector2i(1, 1),
                API = ContextAPI.OpenGL,
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.Default,
            });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "GPU renderer initialization failed: could not create the " +
                "hidden OpenGL context window (GLFW). Is a display server " +
                "(X11/Wayland) available?", ex);
        }
        _window = window;
        try
        {
            window.MakeCurrent();

            GRGlInterface glInterface = GRGlInterface.Create(
                name =>
                {
                    // Skia's GL interface assembly probes EGL display functions
                    // (eglQueryString / eglGetCurrentDisplay) and, when they
                    // resolve, calls eglQueryString with the GLX context's
                    // display handle — garbage on this setup — crashing in
                    // GrGLExtensions::init. The hidden context is created by
                    // GLFW (GLX or EGL); handing Skia those EGL pointers is
                    // wrong either way. Returning null keeps Skia on the plain
                    // core-profile path, which is all we need (mono/SkiaSharp#2350).
                    if (ShouldBlockGlProcName(name))
                        return IntPtr.Zero;
                    return GLFW.GetProcAddress(name);
                });
            if (glInterface is null)
                throw new InvalidOperationException(
                    "GPU renderer initialization failed: GRGlInterface.Create " +
                    "returned null (no usable OpenGL entry points).");

            GRContext grContext = GRContext.CreateGl(glInterface);
            if (grContext is null)
                throw new InvalidOperationException(
                    "GPU renderer initialization failed: GRContext.CreateGl " +
                    "returned null (Ganesh could not attach to the OpenGL " +
                    "context).");

            SKSurface surface = SKSurface.Create(
                grContext, budgeted: false, _frameInfo, 0, GRSurfaceOrigin.TopLeft);
            if (surface is null)
                throw new InvalidOperationException(
                    "GPU renderer initialization failed: SKSurface.Create " +
                    "returned null for the GPU-backed frame surface.");

            _glInterface = glInterface;
            _grContext = grContext;
            _surface = surface;
        }
        catch
        {
            DisposeCore();
            throw;
        }

        // Strict startup log (plan): backend identity and GL capabilities are
        // surfaced before the first frame so a broken GPU path fails loudly.
        string vendor = SafeGetString(StringName.Vendor);
        string renderer = SafeGetString(StringName.Renderer);
        string version = SafeGetString(StringName.Version);
        Console.Error.WriteLine("renderer=skia-gpu");
        Console.Error.WriteLine("backend=OpenGL");
        Console.Error.WriteLine($"GL_VENDOR={vendor}");
        Console.Error.WriteLine($"GL_RENDERER={renderer}");
        Console.Error.WriteLine($"GL_VERSION={version}");
        Console.Error.WriteLine("SkiaBackend=Ganesh/OpenGL");

        // GLFW requires the context to be non-current on this thread before the
        // render thread can take ownership. Detach here; per-frame acquisition
        // happens inside RenderCompositeFrame (see ReleaseCurrent).
        _window.Context.MakeNoneCurrent();
    }

    /// <summary>The GPU-backed Skia canvas (valid after <see cref="BeginFrame"/>).</summary>
    public SKCanvas Canvas => _canvasOverride ?? _surface.Canvas;

    private SKCanvas _canvasOverride;

    /// <summary>
    /// Redirects <see cref="Canvas"/> to an alternate target (e.g. the offscreen
    /// chrome cache) while the returned scope is active. Null restores normal
    /// rendering. Never used across threads.
    /// </summary>
    public IDisposable UseCanvas(SKCanvas canvas)
    {
        _canvasOverride = canvas;
        return new CanvasOverrideScope(this);
    }

    private sealed class CanvasOverrideScope : IDisposable
    {
        private readonly GpuSkiaContext _context;
        public CanvasOverrideScope(GpuSkiaContext context) => _context = context;
        public void Dispose() => _context._canvasOverride = null;
    }

    /// <summary>The Ganesh context (for creating additional GPU surfaces).</summary>
    public GRContext GrContext => _grContext;

    /// <summary>Creates an additional GPU-backed offscreen surface of the frame size.</summary>
    public SKSurface CreateOffscreenSurface()
        => SKSurface.Create(_grContext, budgeted: true, _frameInfo, 0, GRSurfaceOrigin.TopLeft);

    public SKImageInfo FrameInfo => _frameInfo;

    /// <summary>
    /// Makes the OpenGL context current and clears the frame surface. Called
    /// at the start of every frame before drawing.
    /// </summary>
    public void BeginFrame(SKColor clearColor)
    {
        _window.MakeCurrent();
        _surface.Canvas.Clear(clearColor);
    }

    /// <summary>Submits the current frame's draw commands to the GPU.</summary>
    public void Flush() => _surface.Canvas.Flush();

    /// <summary>
    /// Submits the current frame and BLOCKS until the GPU has finished all
    /// submitted work. Benchmark-only: the production path keeps the async
    /// <see cref="Flush"/> so frames pipeline, and the synchronous readback in
    /// <see cref="ReadPixels"/> is the only completion barrier.
    /// </summary>
    public void FlushSync()
    {
        _surface.Canvas.Flush();
        _grContext.Submit();
        GL.Finish();
    }

    /// <summary>
    /// Releases the GL context from the current thread so another thread can
    /// acquire it. GLFW requires the context to be non-current on the old
    /// thread before it becomes current on a new one.
    /// </summary>
    public void ReleaseCurrent() => _window.Context.MakeNoneCurrent();

    /// <summary>
    /// Reads the finished frame back directly into <paramref name="destination"/>
    /// (RGBA, alpha-baked; opaque output, so straight == premultiplied). This
    /// is the ONE readback per frame into the FFmpeg frame slot — no staging
    /// copy. The caller must submit the frame's draw commands (<see cref="Flush"/>)
    /// before reading; this method never flushes again.
    /// </summary>
    public unsafe void ReadPixels(Span<byte> destination)
    {
        if (destination.Length < _frameInfo.BytesSize)
            throw new ArgumentException(
                $"Destination requires at least {_frameInfo.BytesSize} bytes.",
                nameof(destination));

        fixed (byte* p = destination)
        {
            bool ok = _surface.ReadPixels(
                _frameInfo,
                (IntPtr)p,
                _frameInfo.RowBytes,
                0,
                0);

            if (!ok)
                throw new InvalidOperationException("GPU frame readback failed.");
        }
    }

    /// <summary>Ensures the GL context is current before texture uploads.</summary>
    public void MakeCurrent() => _window.MakeCurrent();

    private static string SafeGetString(StringName name)
    {
        try
        {
            return GL.GetString(name) ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Returns true when <paramref name="name"/> must NOT be handed to Skia's
    /// GL interface assembler. Skia probes EGL display functions
    /// (eglQueryString / eglGetCurrentDisplay) and, when they resolve, calls
    /// eglQueryString with the GLX context's display handle — garbage on this
    /// setup — crashing in GrGLExtensions::init (mono/SkiaSharp#2350). The
    /// hidden context is created by GLFW (GLX or EGL), so those EGL pointers
    /// are wrong either way; blocking them keeps Skia on the plain core-profile
    /// path. Do NOT "clean this up": removing the filter resurrects the SIGSEGV.
    /// </summary>
    internal static bool ShouldBlockGlProcName(string name)
        => name.StartsWith("egl", StringComparison.Ordinal);

    public void Dispose()
    {
        DisposeCore();
        GC.SuppressFinalize(this);
    }

    private void DisposeCore()
    {
        if (_surface is not null)
        {
            try
            {
                _window.MakeCurrent();
                _surface.Flush();
            }
            catch
            {
                // The context may already be gone; disposal is best-effort.
            }
            _surface.Dispose();
        }
        _grContext?.Dispose();
        _glInterface?.Dispose();
        try
        {
            _window.Dispose();
        }
        catch
        {
            // GLFW teardown is best-effort.
        }
    }
}