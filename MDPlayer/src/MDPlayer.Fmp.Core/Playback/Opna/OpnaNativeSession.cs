using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Fmp.Core.Playback.Opna;

/// <summary>
/// Managed wrapper for the MDPlayer OPNA native backend (libmdplayer_opna.so).
/// ALL P/Invoke declarations for the OPNA session ABI live in this file and
/// nowhere else. The contract is the absolute YM2608 master clock: the caller
/// advances time explicitly (<see cref="AdvanceTo"/>) and the chip never
/// advances time on its own. Draining audio is a pure FIFO read that never
/// moves the clock.
/// </summary>
internal sealed class OpnaNativeSession : IDisposable
{
    /// <summary>Fixed YM2608 master clock, in Hz (ABI version 1).</summary>
    public const ulong MasterClockHz = 7_987_200;

    /// <summary>External ADPCM-B DRAM capacity, in bytes.</summary>
    public const int AdpcmRamBytes = 262_144;

    /// <summary>ABI version this wrapper targets (mdplayer_opna.h).</summary>
    public const uint AbiVersionTarget = 1;

    /// <summary>Environment variable override for the native library location.</summary>
    public const string NativeLibraryEnvVar = "MDPLAYER_OPNA_NATIVE";

    /// <summary>
    /// Platform-specific native library file name: mdplayer_opna.dll on
    /// Windows, libmdplayer_opna.so elsewhere. Matches the runtimes/ layout.
    /// </summary>
    public static string NativeLibraryFileName =>
        OperatingSystem.IsWindows() ? "mdplayer_opna.dll" : "libmdplayer_opna.so";

    /// <summary>.NET runtime identifier for the current OS (win-x64 / linux-x64).</summary>
    private static string NativeRuntimeIdentifier =>
        OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    private const string LibraryName = "mdplayer_opna";

    private static IntPtr _nativeLibraryHandle;
    private static readonly object LibraryGate = new();

    private readonly OpnaSessionHandle _session;

    private OpnaNativeSession(IntPtr session)
    {
        _session = new OpnaSessionHandle(session);
    }

    static OpnaNativeSession()
    {
        NativeLibraryResolver.Register(LibraryName, _ => _nativeLibraryHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MdpOpnaOpenOptions
    {
        public uint OutputRateHz;
    }

    // ---- P/Invoke declarations (all in this file) ----

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern uint mdp_opna_get_abi_version();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_open(
        ref MdpOpnaOpenOptions options,
        out IntPtr session,
        [MarshalAs(UnmanagedType.LPStr)] StringBuilder error,
        nuint errorSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_reset_chip(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_clear_adpcm_ram(IntPtr session, byte fillValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_advance_to(IntPtr session, ulong masterClock);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_write_register(
        IntPtr session, ulong requestedMasterClock, byte bank, byte address, byte value);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_read_status(
        IntPtr session, ulong requestedMasterClock, byte bank, out byte outValue);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_get_irq(IntPtr session, out int outAsserted);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_opna_drain_audio(
        IntPtr session, IntPtr interleavedStereo, int requestedFrames, out int outDrainedFrames);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong mdp_opna_get_master_clock(IntPtr session);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mdp_opna_close(IntPtr session);

    /// <summary>
    /// Owns the native OPNA session handle. Runs <see cref="mdp_opna_close"/>
    /// exactly once via <see cref="SafeHandle.ReleaseHandle"/> when the session
    /// is disposed or finalized, so the native session can never leak.
    /// </summary>
    private sealed class OpnaSessionHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal OpnaSessionHandle(IntPtr session)
            : base(ownsHandle: true)
        {
            SetHandle(session);
        }

        protected override bool ReleaseHandle()
        {
            mdp_opna_close(handle);
            return true;
        }
    }

    /// <summary>
    /// Opens a native session at the requested output rate (only 44100, 48000
    /// and 96000 are accepted). Runs the native power-on sequence. Throws
    /// <see cref="OpnaLibraryNotFoundException"/> with an actionable message
    /// when the native library cannot be located, and a typed
    /// <see cref="OpnaNativeException"/> for native failures such as
    /// <see cref="OpnaUnsupportedRateException"/>.
    /// </summary>
    public static OpnaNativeSession Open(uint outputRateHz)
    {
        string libraryPath = ResolveNativeLibraryPath()
            ?? throw new OpnaLibraryNotFoundException(MissingLibraryMessage());

        EnsureNativeLibraryLoaded(libraryPath);

        var options = new MdpOpnaOpenOptions { OutputRateHz = outputRateHz };
        var error = new StringBuilder(512);
        int rc = mdp_opna_open(ref options, out IntPtr session, error, (nuint)error.Capacity);
        if (rc != (int)MdpOpnaResult.Ok || session == IntPtr.Zero)
            throw CreateNativeException((MdpOpnaResult)rc,
                $"OPNA native open failed: {error}");

        return new OpnaNativeSession(session);
    }

    /// <summary>Current absolute YM2608 master clock. Never advances time.</summary>
    public ulong MasterClock
    {
        get
        {
            EnsureOpen();
            return mdp_opna_get_master_clock(_session.DangerousGetHandle());
        }
    }

    /// <summary>ABI version implemented by the loaded native library.</summary>
    public uint AbiVersion
    {
        get
        {
            EnsureOpen();
            return mdp_opna_get_abi_version();
        }
    }

    /// <summary>
    /// Advances the chip so the given value becomes the current absolute master
    /// clock. Rejects regression (<see cref="OpnaClockRegressionException"/>).
    /// Queue capacity is bounded: drain before advancing too far, or expect
    /// <see cref="OpnaFifoOverflowException"/>.
    /// </summary>
    public void AdvanceTo(ulong masterClock)
    {
        EnsureOpen();
        int rc = mdp_opna_advance_to(_session.DangerousGetHandle(), masterClock);
        if (rc != (int)MdpOpnaResult.Ok)
            ThrowNativeError((MdpOpnaResult)rc);
    }

    /// <summary>
    /// Schedules one register write at the requested clock through the native
    /// bus scheduler. Preserves call order for equal clocks.
    /// </summary>
    public void WriteRegister(ulong requestedMasterClock, byte bank, byte address, byte value)
    {
        EnsureOpen();
        int rc = mdp_opna_write_register(_session.DangerousGetHandle(), requestedMasterClock, bank, address, value);
        if (rc != (int)MdpOpnaResult.Ok)
            ThrowNativeError((MdpOpnaResult)rc);
    }

    /// <summary>Reads the live LLE status byte at the requested clock.</summary>
    public byte ReadStatus(ulong requestedMasterClock, byte bank)
    {
        EnsureOpen();
        int rc = mdp_opna_read_status(_session.DangerousGetHandle(), requestedMasterClock, bank, out byte value);
        if (rc != (int)MdpOpnaResult.Ok)
            ThrowNativeError((MdpOpnaResult)rc);
        return value;
    }

    /// <summary>Current chip IRQ level. Never advances or clears anything.</summary>
    public bool GetIrq()
    {
        EnsureOpen();
        int rc = mdp_opna_get_irq(_session.DangerousGetHandle(), out int asserted);
        if (rc != (int)MdpOpnaResult.Ok)
            ThrowNativeError((MdpOpnaResult)rc);
        return asserted != 0;
    }

    /// <summary>
    /// Drains already-queued resampled stereo PCM into
    /// <paramref name="interleavedStereo"/> (interleaved int16, at least
    /// <paramref name="requestedFrames"/> * 2 samples). Returns the number of
    /// frames written, which may be smaller than requested when the queue has
    /// less queued audio. Draining never advances the master clock.
    /// </summary>
    public int DrainAudio(short[] interleavedStereo, int requestedFrames)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(interleavedStereo);
        if (requestedFrames < 0)
            throw new ArgumentOutOfRangeException(nameof(requestedFrames), requestedFrames, "frame count must be non-negative");
        if (interleavedStereo.Length < requestedFrames * 2)
            throw new ArgumentException("interleaved stereo buffer too small for the requested frame count", nameof(interleavedStereo));

        GCHandle pinned = GCHandle.Alloc(interleavedStereo, GCHandleType.Pinned);
        try
        {
            int rc = mdp_opna_drain_audio(_session.DangerousGetHandle(), pinned.AddrOfPinnedObject(),
                requestedFrames, out int drained);
            if (rc != (int)MdpOpnaResult.Ok)
                ThrowNativeError((MdpOpnaResult)rc);
            return drained;
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// Runs the native chip-reset helper: preserves the external ADPCM RAM and
    /// the output rate, empties queued audio, resets time to zero.
    /// </summary>
    public void ResetChip()
    {
        EnsureOpen();
        int rc = mdp_opna_reset_chip(_session.DangerousGetHandle());
        if (rc != (int)MdpOpnaResult.Ok)
            ThrowNativeError((MdpOpnaResult)rc);
    }

    /// <summary>
    /// Fills all 256 KiB of external ADPCM RAM with <paramref name="fillValue"/>.
    /// Does not reset the chip, alter time, clear queued audio or resampler history.
    /// </summary>
    public void ClearAdpcmRam(byte fillValue)
    {
        EnsureOpen();
        int rc = mdp_opna_clear_adpcm_ram(_session.DangerousGetHandle(), fillValue);
        if (rc != (int)MdpOpnaResult.Ok)
            ThrowNativeError((MdpOpnaResult)rc);
    }

    public void Dispose()
    {
        _session.Dispose();
    }

    // ---- Native library location ----

    /// <summary>
    /// Locates the native library: MDPLAYER_OPNA_NATIVE override first (a set
    /// override that does not exist is treated as missing), then the standard
    /// runtimes/&lt;rid&gt;/native layout under AppContext.BaseDirectory.
    /// </summary>
    internal static string ResolveNativeLibraryPath()
    {
        string env = Environment.GetEnvironmentVariable(NativeLibraryEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return File.Exists(env) ? env : null;

        string baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "runtimes", NativeRuntimeIdentifier, "native", NativeLibraryFileName),
            Path.Combine(baseDir, NativeLibraryFileName),
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }
        return null;
    }

    internal static string MissingLibraryMessage()
    {
        string expected = Path.Combine(
            AppContext.BaseDirectory, "runtimes", NativeRuntimeIdentifier, "native", NativeLibraryFileName);
        return
            $"OPNA playback requires the native library '{NativeLibraryFileName}'.\n" +
            $"Expected at '{expected}'.\n" +
            "Build it with:\n" +
            "  cmake -S native/MDPlayer.OpnaNative -B native/MDPlayer.OpnaNative/build\n" +
            "  cmake --build native/MDPlayer.OpnaNative/build --config Release\n" +
            $"and copy the built library to runtimes/{NativeRuntimeIdentifier}/native/{NativeLibraryFileName}\n" +
            "(the MDPlayer.OpnaNative CMake post-build step does this automatically on the current OS).\n" +
            $"Override the location with the {NativeLibraryEnvVar} environment variable.";
    }

    private static void EnsureNativeLibraryLoaded(string path)
    {
        if (_nativeLibraryHandle != IntPtr.Zero)
            return;
        lock (LibraryGate)
        {
            if (_nativeLibraryHandle != IntPtr.Zero)
                return;
            if (!NativeLibrary.TryLoad(path, out IntPtr handle))
                throw new OpnaLibraryNotFoundException($"Failed to load the OPNA native library from '{path}'.");
            _nativeLibraryHandle = handle;
        }
    }

    private void EnsureOpen()
    {
        if (_session.IsClosed || _session.IsInvalid)
            throw new ObjectDisposedException(nameof(OpnaNativeSession));
    }

    /// <summary>Maps a native result code to a typed managed exception.</summary>
    private static void ThrowNativeError(MdpOpnaResult code) => throw CreateNativeException(code, null);

    private static OpnaNativeException CreateNativeException(MdpOpnaResult code, string? detail)
    {
        string text = detail ?? $"OPNA native error (code {(int)code}: {ResultName(code)})";
        return code switch
        {
            MdpOpnaResult.ClockRegression => new OpnaClockRegressionException(text),
            MdpOpnaResult.FifoOverflow => new OpnaFifoOverflowException(text),
            MdpOpnaResult.UnsupportedRate => new OpnaUnsupportedRateException(text),
            MdpOpnaResult.UnsupportedCadence => new OpnaUnsupportedCadenceException(text),
            MdpOpnaResult.InvalidArgument => new OpnaInvalidArgumentException(text),
            _ => new OpnaNativeException(code, text),
        };
    }

    private static string ResultName(MdpOpnaResult code) => code switch
    {
        MdpOpnaResult.InvalidArgument => "invalid argument",
        MdpOpnaResult.OutOfMemory => "out of memory",
        MdpOpnaResult.ClockRegression => "clock regression",
        MdpOpnaResult.FifoOverflow => "timed-audio FIFO overflow",
        MdpOpnaResult.UnsupportedRate => "unsupported rate",
        MdpOpnaResult.UnsupportedCadence => "unsupported cadence",
        MdpOpnaResult.Internal => "internal error",
        _ => "unknown error",
    };
}
