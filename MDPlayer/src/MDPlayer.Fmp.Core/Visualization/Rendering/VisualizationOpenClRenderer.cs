using System.Runtime.InteropServices;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Small dependency-free OpenCL semantic primitive renderer. The prepared
/// scene remains renderer-neutral: the CPU prepares stable rectangles from
/// semantic notes, while the OpenCL kernel performs the per-pixel raster and
/// alpha blend. Machines without an OpenCL GPU use the deterministic CPU path.
/// </summary>
internal sealed unsafe class VisualizationOpenClRenderer : IDisposable
{
    private const int Success = 0;
    private const ulong DeviceTypeGpu = 1UL << 2;
    private const ulong MemReadWrite = 1;
    private const ulong MemReadOnly = 4;
    private const uint Blocking = 1;
    private const int PrimitiveWidth = 9;

    private readonly int _width;
    private readonly int _height;
    private readonly int _primitiveCapacity;
    private readonly IntPtr _context;
    private readonly IntPtr _queue;
    private readonly IntPtr _program;
    private readonly IntPtr _kernel;
    private readonly IntPtr _imageBuffer;
    private readonly IntPtr _primitiveBuffer;
    private readonly object _gate = new();
    private bool _disposed;

    private static readonly string KernelSource = """
        __kernel void draw_primitives(
            __global uchar* image,
            const int width,
            const int height,
            __global const int* primitives,
            const int primitive_count)
        {
            const int x = get_global_id(0);
            const int y = get_global_id(1);
            if (x >= width || y >= height)
                return;

            const int pixel = (y * width + x) * 4;
            for (int i = 0; i < primitive_count; ++i)
            {
                const int p = i * 9;
                if (primitives[p + 8] == 0)
                {
                    if (x < primitives[p] || x >= primitives[p + 2]
                        || y < primitives[p + 1] || y >= primitives[p + 3])
                        continue;
                }
                else
                {
                    const float vx = (float)(primitives[p + 2] - primitives[p]);
                    const float vy = (float)(primitives[p + 3] - primitives[p + 1]);
                    const float length_squared = vx * vx + vy * vy;
                    const float wx = (float)x - (float)primitives[p];
                    const float wy = (float)y - (float)primitives[p + 1];
                    const float projection = length_squared > 0.0f
                        ? clamp((wx * vx + wy * vy) / length_squared, 0.0f, 1.0f)
                        : 0.0f;
                    const float nearest_x = (float)primitives[p] + projection * vx;
                    const float nearest_y = (float)primitives[p + 1] + projection * vy;
                    const float dx = (float)x - nearest_x;
                    const float dy = (float)y - nearest_y;
                    const float radius = max(1.0f, (float)primitives[p + 8]);
                    if (dx * dx + dy * dy > radius * radius)
                        continue;
                }

                const float alpha = clamp((float)primitives[p + 7] / 255.0f, 0.0f, 1.0f);
                const float inverse = 1.0f - alpha;
                image[pixel] = convert_uchar_sat_rte((float)primitives[p + 4] * alpha + (float)image[pixel] * inverse);
                image[pixel + 1] = convert_uchar_sat_rte((float)primitives[p + 5] * alpha + (float)image[pixel + 1] * inverse);
                image[pixel + 2] = convert_uchar_sat_rte((float)primitives[p + 6] * alpha + (float)image[pixel + 2] * inverse);
                image[pixel + 3] = (uchar)255;
            }
        }
        """;

    private VisualizationOpenClRenderer(
        int width,
        int height,
        int primitiveCapacity,
        IntPtr context,
        IntPtr queue,
        IntPtr program,
        IntPtr kernel,
        IntPtr imageBuffer,
        IntPtr primitiveBuffer)
    {
        _width = width;
        _height = height;
        _primitiveCapacity = primitiveCapacity;
        _context = context;
        _queue = queue;
        _program = program;
        _kernel = kernel;
        _imageBuffer = imageBuffer;
        _primitiveBuffer = primitiveBuffer;
    }

    public static bool TryProbe(out string reason)
    {
        try
        {
            IntPtr platform = OpenClNative.GetGpuPlatform();
            reason = platform == IntPtr.Zero
                ? "no OpenCL GPU platform is available"
                : "OpenCL GPU platform is available";
            return platform != IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException)
        {
            reason = $"OpenCL runtime unavailable: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return false;
        }
    }

    public static bool TryCreate(
        int width,
        int height,
        int primitiveCapacity,
        out VisualizationOpenClRenderer renderer,
        out string reason)
    {
        renderer = null;
        reason = string.Empty;
        IntPtr context = IntPtr.Zero;
        IntPtr queue = IntPtr.Zero;
        IntPtr program = IntPtr.Zero;
        IntPtr kernel = IntPtr.Zero;
        IntPtr imageBuffer = IntPtr.Zero;
        IntPtr primitiveBuffer = IntPtr.Zero;
        try
        {
            if (width <= 0 || height <= 0 || primitiveCapacity <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));

            IntPtr platform = OpenClNative.GetGpuPlatform();
            if (platform == IntPtr.Zero)
                throw new InvalidOperationException("no OpenCL GPU platform is available");
            IntPtr device = OpenClNative.GetGpuDevice(platform);
            if (device == IntPtr.Zero)
                throw new InvalidOperationException("no OpenCL GPU device is available");

            context = OpenClNative.CreateContext(device);
            queue = OpenClNative.CreateCommandQueue(context, device);
            program = OpenClNative.CreateProgram(context, KernelSource);
            OpenClNative.BuildProgram(program, device);
            kernel = OpenClNative.CreateKernel(program, "draw_primitives");
            imageBuffer = OpenClNative.CreateBuffer(
                context,
                MemReadWrite,
                checked((nint)(width * (long)height * 4)));
            primitiveBuffer = OpenClNative.CreateBuffer(
                context,
                MemReadOnly,
                checked((nint)(primitiveCapacity * (long)PrimitiveWidth * sizeof(int))));

            renderer = new VisualizationOpenClRenderer(
                width,
                height,
                primitiveCapacity,
                context,
                queue,
                program,
                kernel,
                imageBuffer,
                primitiveBuffer);
            context = queue = program = kernel = imageBuffer = primitiveBuffer = IntPtr.Zero;
            reason = "OpenCL semantic primitive renderer initialized";
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException
            or EntryPointNotFoundException or BadImageFormatException
            or InvalidOperationException or InvalidDataException)
        {
            reason = ex.Message;
            return false;
        }
        finally
        {
            OpenClNative.ReleaseKernel(kernel);
            OpenClNative.ReleaseProgram(program);
            OpenClNative.ReleaseMemObject(primitiveBuffer);
            OpenClNative.ReleaseMemObject(imageBuffer);
            OpenClNative.ReleaseCommandQueue(queue);
            OpenClNative.ReleaseContext(context);
        }
    }

    public void Render(byte[] frame, int[] primitives, int primitiveCount)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(primitives);
        if (frame.Length < _width * (long)_height * 4)
            throw new ArgumentException("GPU frame buffer is too small.", nameof(frame));
        if (primitiveCount < 0 || primitiveCount > _primitiveCapacity
            || primitives.Length < primitiveCount * PrimitiveWidth)
            throw new ArgumentOutOfRangeException(nameof(primitiveCount));

        lock (_gate)
        {
            ThrowIfDisposed();
            OpenClNative.WriteBuffer(_queue, _imageBuffer, frame, frame.Length);
            if (primitiveCount > 0)
                OpenClNative.WriteBuffer(
                    _queue,
                    _primitiveBuffer,
                    primitives,
                    primitiveCount * PrimitiveWidth * sizeof(int));

            nint image = _imageBuffer;
            int width = _width;
            int height = _height;
            nint primitiveData = _primitiveBuffer;
            OpenClNative.SetKernelArg(_kernel, 0, ref image);
            OpenClNative.SetKernelArg(_kernel, 1, ref width);
            OpenClNative.SetKernelArg(_kernel, 2, ref height);
            OpenClNative.SetKernelArg(_kernel, 3, ref primitiveData);
            OpenClNative.SetKernelArg(_kernel, 4, ref primitiveCount);
            OpenClNative.EnqueueKernel(_queue, _kernel, _width, _height);
            OpenClNative.ReadBuffer(_queue, _imageBuffer, frame, frame.Length);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            OpenClNative.ReleaseKernel(_kernel);
            OpenClNative.ReleaseProgram(_program);
            OpenClNative.ReleaseMemObject(_primitiveBuffer);
            OpenClNative.ReleaseMemObject(_imageBuffer);
            OpenClNative.ReleaseCommandQueue(_queue);
            OpenClNative.ReleaseContext(_context);
        }
        GC.SuppressFinalize(this);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(VisualizationOpenClRenderer));
    }

    private static class OpenClNative
    {
        public static IntPtr GetGpuPlatform()
        {
            int status = clGetPlatformIDs(0, null, out uint count);
            Check(status, "clGetPlatformIDs");
            if (count == 0)
                return IntPtr.Zero;
            var platforms = new IntPtr[count];
            Check(clGetPlatformIDs(count, platforms, out _), "clGetPlatformIDs");
            foreach (IntPtr platform in platforms)
            {
                int deviceStatus = clGetDeviceIDs(platform, DeviceTypeGpu, 0, null, out uint deviceCount);
                if (deviceStatus == Success && deviceCount > 0)
                    return platform;
            }
            return IntPtr.Zero;
        }

        public static IntPtr GetGpuDevice(IntPtr platform)
        {
            int status = clGetDeviceIDs(platform, DeviceTypeGpu, 0, null, out uint count);
            Check(status, "clGetDeviceIDs");
            if (count == 0)
                return IntPtr.Zero;
            var devices = new IntPtr[count];
            Check(clGetDeviceIDs(platform, DeviceTypeGpu, count, devices, out _), "clGetDeviceIDs");
            return devices[0];
        }

        public static IntPtr CreateContext(IntPtr device)
        {
            var devices = new[] { device };
            IntPtr context = clCreateContext(null, 1, devices, IntPtr.Zero, IntPtr.Zero, out int status);
            Check(status, "clCreateContext");
            return context;
        }

        public static IntPtr CreateCommandQueue(IntPtr context, IntPtr device)
        {
            IntPtr queue = clCreateCommandQueue(context, device, 0, out int status);
            Check(status, "clCreateCommandQueue");
            return queue;
        }

        public static IntPtr CreateProgram(IntPtr context, string source)
        {
            var sources = new[] { source };
            IntPtr program = clCreateProgramWithSource(context, 1, sources, null, out int status);
            Check(status, "clCreateProgramWithSource");
            return program;
        }

        public static void BuildProgram(IntPtr program, IntPtr device)
            => Check(clBuildProgram(program, 1, new[] { device }, string.Empty, IntPtr.Zero, IntPtr.Zero), "clBuildProgram");

        public static IntPtr CreateKernel(IntPtr program, string name)
        {
            IntPtr kernel = clCreateKernel(program, name, out int status);
            Check(status, "clCreateKernel");
            return kernel;
        }

        public static IntPtr CreateBuffer(IntPtr context, ulong flags, nint size)
        {
            IntPtr buffer = clCreateBuffer(context, flags, size, IntPtr.Zero, out int status);
            Check(status, "clCreateBuffer");
            return buffer;
        }

        public static void WriteBuffer(IntPtr queue, IntPtr buffer, byte[] data, int size)
        {
            fixed (byte* pointer = data)
            {
                Check(clEnqueueWriteBuffer(queue, buffer, Blocking, IntPtr.Zero, (nint)size,
                    (IntPtr)pointer, 0, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer");
            }
        }

        public static void WriteBuffer(IntPtr queue, IntPtr buffer, int[] data, int size)
        {
            fixed (int* pointer = data)
            {
                Check(clEnqueueWriteBuffer(queue, buffer, Blocking, IntPtr.Zero, (nint)size,
                    (IntPtr)pointer, 0, IntPtr.Zero, IntPtr.Zero), "clEnqueueWriteBuffer");
            }
        }

        public static void ReadBuffer(IntPtr queue, IntPtr buffer, byte[] data, int size)
        {
            fixed (byte* pointer = data)
            {
                Check(clEnqueueReadBuffer(queue, buffer, Blocking, IntPtr.Zero, (nint)size,
                    (IntPtr)pointer, 0, IntPtr.Zero, IntPtr.Zero), "clEnqueueReadBuffer");
            }
        }

        public static void SetKernelArg(IntPtr kernel, uint index, ref nint value)
            => Check(clSetKernelArg(kernel, index, (nint)IntPtr.Size, ref value), "clSetKernelArg");

        public static void SetKernelArg(IntPtr kernel, uint index, ref int value)
            => Check(clSetKernelArg(kernel, index, sizeof(int), ref value), "clSetKernelArg");

        public static void EnqueueKernel(IntPtr queue, IntPtr kernel, int width, int height)
        {
            nint* globalSize = stackalloc nint[2];
            globalSize[0] = width;
            globalSize[1] = height;
            Check(clEnqueueNDRangeKernel(queue, kernel, 2, IntPtr.Zero, (IntPtr)globalSize, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero),
                "clEnqueueNDRangeKernel");
            Check(clFinish(queue), "clFinish");
        }

        public static void ReleaseKernel(IntPtr value) { if (value != IntPtr.Zero) clReleaseKernel(value); }
        public static void ReleaseProgram(IntPtr value) { if (value != IntPtr.Zero) clReleaseProgram(value); }
        public static void ReleaseMemObject(IntPtr value) { if (value != IntPtr.Zero) clReleaseMemObject(value); }
        public static void ReleaseCommandQueue(IntPtr value) { if (value != IntPtr.Zero) clReleaseCommandQueue(value); }
        public static void ReleaseContext(IntPtr value) { if (value != IntPtr.Zero) clReleaseContext(value); }

        private static void Check(int status, string operation)
        {
            if (status != Success)
                throw new InvalidDataException($"{operation} failed with OpenCL status {status}");
        }

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clGetPlatformIDs(uint numEntries, [Out] IntPtr[] platforms, out uint numPlatforms);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clGetDeviceIDs(IntPtr platform, ulong deviceType, uint numEntries,
            [Out] IntPtr[] devices, out uint numDevices);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr clCreateContext(IntPtr[] properties, uint numDevices, IntPtr[] devices,
            IntPtr notify, IntPtr userData, out int errorCode);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr clCreateCommandQueue(IntPtr context, IntPtr device, ulong properties,
            out int errorCode);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr clCreateProgramWithSource(IntPtr context, uint count, string[] strings,
            IntPtr[] lengths, out int errorCode);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clBuildProgram(IntPtr program, uint numDevices, IntPtr[] devices,
            string options, IntPtr notify, IntPtr userData);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr clCreateKernel(IntPtr program, string name, out int errorCode);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr clCreateBuffer(IntPtr context, ulong flags, nint size,
            IntPtr hostPointer, out int errorCode);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clEnqueueWriteBuffer(IntPtr commandQueue, IntPtr buffer, uint blockingWrite,
            IntPtr offset, nint size, IntPtr pointer, uint numEvents, IntPtr waitList, IntPtr events);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clEnqueueReadBuffer(IntPtr commandQueue, IntPtr buffer, uint blockingRead,
            IntPtr offset, nint size, IntPtr pointer, uint numEvents, IntPtr waitList, IntPtr events);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clSetKernelArg(IntPtr kernel, uint index, nint size, ref nint value);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clSetKernelArg(IntPtr kernel, uint index, nint size, ref int value);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clEnqueueNDRangeKernel(IntPtr commandQueue, IntPtr kernel, uint workDim,
            IntPtr globalWorkOffset, IntPtr globalWorkSize, IntPtr localWorkSize,
            uint numEvents, IntPtr waitList, IntPtr events);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clFinish(IntPtr commandQueue);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clReleaseKernel(IntPtr kernel);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clReleaseProgram(IntPtr program);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clReleaseMemObject(IntPtr memory);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clReleaseCommandQueue(IntPtr commandQueue);

        [DllImport("OpenCL", CallingConvention = CallingConvention.Cdecl)]
        private static extern int clReleaseContext(IntPtr context);
    }
}
