using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Fmp.Core.Playback.Spc;

/// <summary>
/// Managed wrapper for the MDPlayer SPC native backend (libmdplayer_spc.so).
/// ALL P/Invoke declarations for the SPC native ABI live in this file and
/// nowhere else (§6). Block-based rendering only — no per-sample callbacks
/// (§9.1). The native core outputs the unmodified S-DSP stereo result at
/// 32,000 Hz; no resampling happens on either side of the boundary.
/// </summary>
internal sealed class SpcNativeSession : IDisposable
{
    public const int SampleRate = 32_000;
    public const int DefaultBlockFrames = 1024;
    public const int MinBlockFrames = 256;
    public const int MaxBlockFrames = 4096;

    public const int RamSize = 0x1_0000;
    public const int DspRegisterSize = 0x80;

    public const string NativeLibraryEnvVar = "MDPLAYER_SPC_NATIVE";

    /// <summary>
    /// Platform-specific native library file name (PR 10): mdplayer_spc.dll on
    /// Windows, libmdplayer_spc.so elsewhere. The runtimes/ layout mirrors the
    /// .NET runtime identifier convention (win-x64 vs linux-x64).
    /// </summary>
    public static string NativeLibraryFileName =>
        OperatingSystem.IsWindows() ? "mdplayer_spc.dll" : "libmdplayer_spc.so";

    /// <summary>.NET runtime identifier for the current OS (win-x64 / linux-x64).</summary>
    private static string NativeRuntimeIdentifier =>
        OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";

    private const string LibraryName = "mdplayer_spc";
    public const int VoiceCount = 8;

    // ---- PR 3 public event/envelope types (match mdplayer_spc.h) ----
    public enum SpcEventType
    {
        KeyOn = 1,
        ReleaseStart = 2,
        VoiceEnd = 3,
        SourceChanged = 4,
        PitchChanged = 5,
        VolumeChanged = 6,
        NoiseChanged = 7,
        PitchModChanged = 8,
        EchoSendChanged = 9,
        EnvelopeModeChanged = 10,
    }

    public enum SpcEnvelopeMode
    {
        Release = 0,
        Attack = 1,
        Decay = 2,
        Sustain = 3,
    }

    private static IntPtr _nativeLibraryHandle;
    private static readonly object LibraryGate = new();

    private readonly IntPtr _session;
    private bool _disposed;

    private SpcNativeSession(IntPtr session)
    {
        _session = session;
    }

    static SpcNativeSession()
    {
        NativeLibraryResolver.Register(LibraryName, _ => _nativeLibraryHandle);
    }

    internal struct OpenOptions
    {
        public int EventCapacity;
        public int EnableVoicePcm;
        public int EnableEchoPcm;
        public int AccurateDsp;

        public static OpenOptions Default => new()
        {
            EventCapacity = 64,
            EnableVoicePcm = 0,
            EnableEchoPcm = 0,
            AccurateDsp = 1,
        };
    }

    // ---- Result codes (must match mdplayer_spc.h) ----
    private const int MdpSpcOk = 0;
    private const int MdpSpcErrInvalidArgument = -1;
    private const int MdpSpcErrUnsupportedFormat = -2;
    private const int MdpSpcErrOutOfMemory = -3;
    private const int MdpSpcErrInternal = -4;
    private const int MdpSpcErrNotImplemented = -5;
    private const int MdpSpcErrSessionClosed = -6;

    [StructLayout(LayoutKind.Sequential)]
    private struct MdpSpcOpenOptions
    {
        public int EventCapacity;
        public int EnableVoicePcm;
        public int EnableEchoPcm;
        public int AccurateDsp;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MdpSpcAudioBuffers
    {
        public IntPtr MasterStereo;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = VoiceCount)]
        public IntPtr[] Voices;
        public IntPtr Echo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MdpSpcEvent
    {
        public long Frame;
        public int Type;
        public int Channel;
        public int Param0;
        public int Param1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MdpSpcRenderResult
    {
        public int FramesRendered;
        public int EventsWritten;
        public int IsEnd;
        public int VoiceFlags;
        public int EventOverflow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MdpSpcVoiceState
    {
        public int Channel;
        public int Active;
        public int VolumeL;
        public int VolumeR;
        public int Pitch;
        public int SourceNumber;
        public long SamplePosition;
        public int NoiseEnabled;
        // PR 3 appended fields (ABI-compatible).
        public int EnvelopeMode;
        public int EnvelopeLevel;
        public int BrrAddress;
        public int KonDelay;
        public int PitchModEnabled;
        public int EchoSendEnabled;
        // PR 8 appended field (ABI-compatible): DSP-calculated effective pitch
        // (register pitch + PMON adjustment, spec §10.2/§13.6).
        public int EffectivePitch;
    }

    // ---- PR 3 public ABI structs ----

    public struct SpcVoiceState
    {
        public int Channel;
        public int Active;
        public int VolumeL;
        public int VolumeR;
        public int Pitch;
        public int SourceNumber;
        public long SamplePosition;
        public int NoiseEnabled;
        public int EnvelopeMode;
        public int EnvelopeLevel;
        public int BrrAddress;
        public int KonDelay;
        public int PitchModEnabled;
        public int EchoSendEnabled;
        // PR 8 appended field (ABI-compatible): DSP-calculated effective pitch
        // (register pitch + PMON adjustment, spec §10.2/§13.6); equals Pitch
        // when pitch modulation is inactive.
        public int EffectivePitch;
    }

    public struct SpcEvent
    {
        public long Frame;
        public int Type;
        public int Channel;
        public int Param0;
        public int Param1;
    }

    public struct SpcRenderResult
    {
        public int FramesRendered;
        public int EventsWritten;
        public int IsEnd;
        public int VoiceFlags;
        public int EventOverflow;
    }

    // ---- P/Invoke declarations (all in this file, per §6) ----
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_spc_open(
        [In] byte[] data,
        nuint size,
        ref MdpSpcOpenOptions options,
        out IntPtr session,
        [MarshalAs(UnmanagedType.LPStr)] StringBuilder error,
        nuint errorSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_spc_render(
        IntPtr session,
        int requestedFrames,
        ref MdpSpcAudioBuffers audioBuffers,
        [In, Out] MdpSpcEvent[] events,
        int eventCapacity,
        ref MdpSpcRenderResult result);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_spc_copy_ram(IntPtr session, byte[] outRam, nuint outSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_spc_copy_dsp_registers(IntPtr session, byte[] outDsp, nuint outSize);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern int mdp_spc_get_voice_state(IntPtr session, int channel, ref MdpSpcVoiceState state);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    private static extern void mdp_spc_close(IntPtr session);

    /// <summary>
    /// Opens a native session from an in-memory SPC snapshot (signature + size
    /// are validated natively; no file I/O). Throws <see cref="SpcFormatException"/>
    /// with an actionable message when the native library cannot be located.
    /// </summary>
    public static SpcNativeSession Open(byte[] spcData, OpenOptions options)
    {
        ArgumentNullException.ThrowIfNull(spcData);

        string libraryPath = ResolveNativeLibraryPath()
            ?? throw new SpcFormatException(MissingLibraryMessage());

        EnsureNativeLibraryLoaded(libraryPath);

        var nativeOptions = new MdpSpcOpenOptions
        {
            EventCapacity = options.EventCapacity > 0 ? options.EventCapacity : 64,
            EnableVoicePcm = options.EnableVoicePcm,
            EnableEchoPcm = options.EnableEchoPcm,
            AccurateDsp = options.AccurateDsp,
        };
        var error = new StringBuilder(512);
        int rc = mdp_spc_open(spcData, (nuint)spcData.Length, ref nativeOptions,
            out IntPtr session, error, (nuint)error.Capacity);
        if (rc != MdpSpcOk || session == IntPtr.Zero)
            throw new SpcFormatException($"SPC native open failed (code {rc}): {error}");

        return new SpcNativeSession(session);
    }

    /// <summary>
    /// Renders one block (default 1024 frames) of 32 kHz stereo into
    /// <paramref name="stereoBuffer"/> (interleaved int16, at least
    /// <paramref name="frames"/> * 2 samples). Returns frames actually written.
    /// </summary>
    public int Render(short[] stereoBuffer, int frames)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(stereoBuffer);
        if (frames < MinBlockFrames || frames > MaxBlockFrames)
            throw new ArgumentOutOfRangeException(nameof(frames), frames, $"block must be {MinBlockFrames}..{MaxBlockFrames} frames");
        if (stereoBuffer.Length < frames * 2)
            throw new ArgumentException("stereo buffer too small for the requested frame block", nameof(stereoBuffer));

        var result = new MdpSpcRenderResult();
        var audio = new MdpSpcAudioBuffers { Voices = new IntPtr[VoiceCount] };
        GCHandle pinned = GCHandle.Alloc(stereoBuffer, GCHandleType.Pinned);
        try
        {
            audio.MasterStereo = pinned.AddrOfPinnedObject();
            int rc = mdp_spc_render(_session, frames, ref audio, null, 0, ref result);
            if (rc != MdpSpcOk)
                ThrowNativeError(rc);
            return result.FramesRendered;
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// PR 3: renders one block like <see cref="Render"/> and additionally
    /// captures per-block voice-state transition events into
    /// <paramref name="events"/> (see <see cref="SpcEventType"/>). The voice
    /// observer is read-only and runs inside the native core for every block,
    /// so capturing events never changes the master PCM (§5.3).
    /// </summary>
    public SpcRenderResult RenderAndCapture(short[] stereoBuffer, int frames, SpcEvent[] events)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(stereoBuffer);
        ArgumentNullException.ThrowIfNull(events);
        if (frames < MinBlockFrames || frames > MaxBlockFrames)
            throw new ArgumentOutOfRangeException(nameof(frames), frames, $"block must be {MinBlockFrames}..{MaxBlockFrames} frames");
        if (stereoBuffer.Length < frames * 2)
            throw new ArgumentException("stereo buffer too small for the requested frame block", nameof(stereoBuffer));

        var nativeEvents = new MdpSpcEvent[events.Length];
        var result = new MdpSpcRenderResult();
        var audio = new MdpSpcAudioBuffers { Voices = new IntPtr[VoiceCount] };
        GCHandle pinned = GCHandle.Alloc(stereoBuffer, GCHandleType.Pinned);
        try
        {
            audio.MasterStereo = pinned.AddrOfPinnedObject();
            int rc = mdp_spc_render(_session, frames, ref audio, nativeEvents, nativeEvents.Length, ref result);
            if (rc != MdpSpcOk)
                ThrowNativeError(rc);

            int written = Math.Min(result.EventsWritten, events.Length);
            for (int i = 0; i < written; i++)
            {
                events[i] = new SpcEvent
                {
                    Frame = nativeEvents[i].Frame,
                    Type = nativeEvents[i].Type,
                    Channel = nativeEvents[i].Channel,
                    Param0 = nativeEvents[i].Param0,
                    Param1 = nativeEvents[i].Param1,
                };
            }
            return new SpcRenderResult
            {
                FramesRendered = result.FramesRendered,
                EventsWritten = result.EventsWritten,
                IsEnd = result.IsEnd,
                VoiceFlags = result.VoiceFlags,
                EventOverflow = result.EventOverflow,
            };
        }
        finally
        {
            pinned.Free();
        }
    }

    /// <summary>
    /// PR 6: renders one block like <see cref="Render"/> and additionally
    /// fills per-voice mono PCM (<paramref name="voiceBuffers"/>[c],
    /// <paramref name="frames"/> samples each) and the stereo echo-return PCM
    /// (<paramref name="echoBuffer"/>, <paramref name="frames"/> * 2 samples)
    /// when the session was opened with <see cref="OpenOptions.EnableVoicePcm"/>
    /// / <see cref="OpenOptions.EnableEchoPcm"/>. Buffers are zeroed first so
    /// inactive voices and unproduced tail samples are deterministic silence.
    /// The native taps are pure writes to caller memory, so the master PCM is
    /// bit-identical to a render without stems (§5.3/§30.1).
    /// </summary>
    public int RenderStems(short[] stereoBuffer, int frames, short[][] voiceBuffers, short[] echoBuffer)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(stereoBuffer);
        if (frames < MinBlockFrames || frames > MaxBlockFrames)
            throw new ArgumentOutOfRangeException(nameof(frames), frames, $"block must be {MinBlockFrames}..{MaxBlockFrames} frames");
        if (stereoBuffer.Length < frames * 2)
            throw new ArgumentException("stereo buffer too small for the requested frame block", nameof(stereoBuffer));
        if (voiceBuffers != null && voiceBuffers.Length != VoiceCount)
            throw new ArgumentException("voiceBuffers must have exactly 8 entries", nameof(voiceBuffers));

        var result = new MdpSpcRenderResult();
        var audio = new MdpSpcAudioBuffers { Voices = new IntPtr[VoiceCount] };
        var handles = new List<GCHandle>(VoiceCount + 2);
        try
        {
            handles.Add(GCHandle.Alloc(stereoBuffer, GCHandleType.Pinned));
            audio.MasterStereo = handles[0].AddrOfPinnedObject();

            if (echoBuffer != null)
            {
                if (echoBuffer.Length < frames * 2)
                    throw new ArgumentException("echo buffer too small for the requested frame block", nameof(echoBuffer));
                Array.Clear(echoBuffer, 0, frames * 2);
                handles.Add(GCHandle.Alloc(echoBuffer, GCHandleType.Pinned));
                audio.Echo = handles[^1].AddrOfPinnedObject();
            }

            if (voiceBuffers != null)
            {
                for (int c = 0; c < VoiceCount; c++)
                {
                    if (voiceBuffers[c] == null)
                        continue;
                    if (voiceBuffers[c].Length < frames)
                        throw new ArgumentException($"voice buffer {c} too small for the requested frame block", nameof(voiceBuffers));
                    Array.Clear(voiceBuffers[c], 0, frames);
                    handles.Add(GCHandle.Alloc(voiceBuffers[c], GCHandleType.Pinned));
                    audio.Voices[c] = handles[^1].AddrOfPinnedObject();
                }
            }

            int rc = mdp_spc_render(_session, frames, ref audio, null, 0, ref result);
            if (rc != MdpSpcOk)
                ThrowNativeError(rc);
            return result.FramesRendered;
        }
        finally
        {
            foreach (GCHandle handle in handles)
                handle.Free();
        }
    }

    /// <summary>
    /// Renders one block with stems AND captures per-block voice-state
    /// transition events (§9.2). Combines <see cref="RenderStems"/> and
    /// <see cref="RenderAndCapture"/> in a single native call so the backend
    /// can produce audio, stems, and the timeline in one pass.
    /// </summary>
    public SpcRenderResult RenderStemsAndCapture(
        short[] stereoBuffer, int frames,
        short[][] voiceBuffers, short[] echoBuffer,
        SpcEvent[] events)
    {
        EnsureOpen();
        ArgumentNullException.ThrowIfNull(stereoBuffer);
        ArgumentNullException.ThrowIfNull(events);
        if (frames < MinBlockFrames || frames > MaxBlockFrames)
            throw new ArgumentOutOfRangeException(nameof(frames), frames, $"block must be {MinBlockFrames}..{MaxBlockFrames} frames");
        if (stereoBuffer.Length < frames * 2)
            throw new ArgumentException("stereo buffer too small for the requested frame block", nameof(stereoBuffer));
        if (voiceBuffers != null && voiceBuffers.Length != VoiceCount)
            throw new ArgumentException("voiceBuffers must have exactly 8 entries", nameof(voiceBuffers));

        var nativeEvents = new MdpSpcEvent[events.Length];
        var result = new MdpSpcRenderResult();
        var audio = new MdpSpcAudioBuffers { Voices = new IntPtr[VoiceCount] };
        var handles = new List<GCHandle>(VoiceCount + 2);
        try
        {
            handles.Add(GCHandle.Alloc(stereoBuffer, GCHandleType.Pinned));
            audio.MasterStereo = handles[0].AddrOfPinnedObject();

            if (echoBuffer != null)
            {
                if (echoBuffer.Length < frames * 2)
                    throw new ArgumentException("echo buffer too small for the requested frame size", nameof(echoBuffer));
                Array.Clear(echoBuffer, 0, frames * 2);
                handles.Add(GCHandle.Alloc(echoBuffer, GCHandleType.Pinned));
                audio.Echo = handles[^1].AddrOfPinnedObject();
            }

            if (voiceBuffers != null)
            {
                for (int c = 0; c < VoiceCount; c++)
                {
                    if (voiceBuffers[c] == null)
                        continue;
                    if (voiceBuffers[c].Length < frames)
                        throw new ArgumentException($"voice buffer {c} too small", nameof(voiceBuffers));
                    Array.Clear(voiceBuffers[c], 0, frames);
                    handles.Add(GCHandle.Alloc(voiceBuffers[c], GCHandleType.Pinned));
                    audio.Voices[c] = handles[^1].AddrOfPinnedObject();
                }
            }

            int rc = mdp_spc_render(_session, frames, ref audio, nativeEvents, nativeEvents.Length, ref result);
            if (rc != MdpSpcOk)
                ThrowNativeError(rc);

            int written = Math.Min(result.EventsWritten, events.Length);
            for (int i = 0; i < written; i++)
            {
                events[i] = new SpcEvent
                {
                    Frame = nativeEvents[i].Frame,
                    Type = nativeEvents[i].Type,
                    Channel = nativeEvents[i].Channel,
                    Param0 = nativeEvents[i].Param0,
                    Param1 = nativeEvents[i].Param1,
                };
            }
            return new SpcRenderResult
            {
                FramesRendered = result.FramesRendered,
                EventsWritten = result.EventsWritten,
                IsEnd = result.IsEnd,
                VoiceFlags = result.VoiceFlags,
                EventOverflow = result.EventOverflow,
            };
        }
        finally
        {
            foreach (GCHandle handle in handles)
                handle.Free();
        }
    }

    /// <summary>
    /// PR 3: returns the current effective voice state (read-only; may be
    /// called between renders). Reflects the emulator as of the last render
    /// block, or the loaded SPC before the first render.
    /// </summary>
    public SpcVoiceState GetVoiceState(int channel)
    {
        EnsureOpen();
        if (channel < 0 || channel >= VoiceCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, $"channel must be 0..{VoiceCount - 1}");

        var state = new MdpSpcVoiceState();
        int rc = mdp_spc_get_voice_state(_session, channel, ref state);
        if (rc != MdpSpcOk)
            ThrowNativeError(rc);
        return new SpcVoiceState
        {
            Channel = state.Channel,
            Active = state.Active,
            VolumeL = state.VolumeL,
            VolumeR = state.VolumeR,
            Pitch = state.Pitch,
            SourceNumber = state.SourceNumber,
            SamplePosition = state.SamplePosition,
            NoiseEnabled = state.NoiseEnabled,
            EnvelopeMode = state.EnvelopeMode,
            EnvelopeLevel = state.EnvelopeLevel,
            BrrAddress = state.BrrAddress,
            KonDelay = state.KonDelay,
            PitchModEnabled = state.PitchModEnabled,
            EchoSendEnabled = state.EchoSendEnabled,
            EffectivePitch = state.EffectivePitch,
        };
    }

    /// <summary>Copies the session RAM (PR 2: validated initial snapshot state).</summary>
    public byte[] CopyRam()
    {
        EnsureOpen();
        var ram = new byte[RamSize];
        int rc = mdp_spc_copy_ram(_session, ram, (nuint)ram.Length);
        if (rc != MdpSpcOk)
            ThrowNativeError(rc);
        return ram;
    }

    /// <summary>Copies the DSP registers (PR 2: validated initial snapshot state).</summary>
    public byte[] CopyDspRegisters()
    {
        EnsureOpen();
        var dsp = new byte[DspRegisterSize];
        int rc = mdp_spc_copy_dsp_registers(_session, dsp, (nuint)dsp.Length);
        if (rc != MdpSpcOk)
            ThrowNativeError(rc);
        return dsp;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_session != IntPtr.Zero)
            mdp_spc_close(_session);
    }

    // ---- Native library location ----

    /// <summary>
    /// Locates the native library: MDPLAYER_SPC_NATIVE override first (a set
    /// override that does not exist is treated as missing), then the standard
    /// runtimes/&lt;rid&gt;/native layout under AppContext.BaseDirectory for the
    /// current OS (win-x64 → mdplayer_spc.dll, linux-x64 → libmdplayer_spc.so).
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
            $"SPC playback requires the native library '{NativeLibraryFileName}'.\n" +
            $"Expected at '{expected}'.\n" +
            "Build it with:\n" +
            "  cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build\n" +
            "  cmake --build native/MDPlayer.SpcNative/build --config Release\n" +
            $"and copy the built library to runtimes/{NativeRuntimeIdentifier}/native/{NativeLibraryFileName}\n" +
            "(the MDPlayer.SpcNative CMake post-build step does this automatically on the current OS).\n" +
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
                throw new SpcFormatException($"Failed to load the SPC native library from '{path}'.");
            _nativeLibraryHandle = handle;
        }
    }

    private void EnsureOpen()
    {
        if (_disposed || _session == IntPtr.Zero)
            throw new ObjectDisposedException(nameof(SpcNativeSession));
    }

    private static void ThrowNativeError(int code)
    {
        string name = code switch
        {
            MdpSpcErrInvalidArgument => "invalid argument",
            MdpSpcErrUnsupportedFormat => "unsupported format",
            MdpSpcErrOutOfMemory => "out of memory",
            MdpSpcErrInternal => "internal error",
            MdpSpcErrNotImplemented => "not implemented",
            MdpSpcErrSessionClosed => "session closed",
            _ => "unknown error",
        };
        throw new SpcFormatException($"SPC native error (code {code}): {name}");
    }
}
