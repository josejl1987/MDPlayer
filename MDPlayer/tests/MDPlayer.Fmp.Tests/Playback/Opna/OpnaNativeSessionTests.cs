using Fmp.Core.Playback.Opna;
using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Opna;

/// <summary>
/// Native-session tests for the OPNA (YM2608) LLE ABI. Each test sets the
/// process-global MDPLAYER_OPNA_NATIVE override to the built library and
/// restores it afterwards (see <see cref="SetNativeLibrary"/>).
/// </summary>
public sealed class OpnaNativeSessionTests
{
    private const string NativeLibEnvVar = OpnaNativeSession.NativeLibraryEnvVar;

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Open_AcceptsAllSupportedRates(int rate)
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var session = OpnaNativeSession.Open((uint)rate);

        Assert.Equal(OpnaNativeSession.AbiVersionTarget, session.AbiVersion);
        Assert.Equal(0ul, session.MasterClock);
        Assert.False(session.GetIrq());
    }

    [Fact]
    public void Open_UnsupportedRate_ThrowsOpnaUnsupportedRateException()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        var ex = Assert.Throws<OpnaUnsupportedRateException>(() => OpnaNativeSession.Open(22050));
        Assert.Equal(MdpOpnaResult.UnsupportedRate, ex.Code);
    }

    [Fact]
    public void Open_MissingLibrary_ThrowsOpnaLibraryNotFoundException()
    {
        using var restore = SetNativeLibrary("/nonexistent/mdplayer_opna.so");

        var ex = Assert.Throws<OpnaLibraryNotFoundException>(() => OpnaNativeSession.Open(44100));
        Assert.Contains(NativeLibEnvVar, ex.Message);
        Assert.Contains("runtimes", ex.Message);
    }

    [Fact]
    public void AdvanceTo_ClockRegression_ThrowsOpnaClockRegressionException()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var session = OpnaNativeSession.Open(44100);

        session.AdvanceTo(1_000_000);
        var ex = Assert.Throws<OpnaClockRegressionException>(() => session.AdvanceTo(500_000));
        Assert.Equal(MdpOpnaResult.ClockRegression, ex.Code);
        // The failing advance must not move the clock.
        Assert.Equal(1_000_000ul, session.MasterClock);
    }

    [Fact]
    public void Trace_Advance_Writes_Reads_Drains_ProducesNonzeroStereoPcm()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var session = OpnaNativeSession.Open(44100);

        WriteFurnaceTrace(session);
        ulong target = OpnaNativeSession.MasterClockHz; // one second
        int total = StreamAdvanceAndDrain(session, target, out short[] pcm);

        Assert.Equal(44_100, total);
        Assert.Equal(target, session.MasterClock); // drain does not advance time

        int nonzero = 0;
        int maxAbs = 0;
        for (int i = 0; i < total * 2; i++)
        {
            if (pcm[i] != 0)
                nonzero++;
            int a = pcm[i] < 0 ? -pcm[i] : pcm[i];
            if (a > maxAbs)
                maxAbs = a;
        }
        Assert.True(nonzero > 0, "expected non-silent PCM from the real YM2608 trace");
        Assert.True(maxAbs > 0);

        // Status reads return without erroring on both banks.
        _ = session.ReadStatus(session.MasterClock, 0);
        _ = session.ReadStatus(session.MasterClock, 1);
    }

    [Fact]
    public void Determinism_SameTraceTwice_IsByteIdentical()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        short[] Render()
        {
            using var session = OpnaNativeSession.Open(44100);
            WriteFurnaceTrace(session);
            StreamAdvanceAndDrain(session, OpnaNativeSession.MasterClockHz, out short[] pcm);
            return pcm;
        }

        short[] a = Render();
        short[] b = Render();
        Assert.Equal(a.Length, b.Length);
        Assert.Equal(a, b);
    }

    [Fact]
    public void ResetChip_PreservesRate_ResetsTime_AndEmptiesQueue()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var session = OpnaNativeSession.Open(44100);

        WriteFurnaceTrace(session);
        session.AdvanceTo(2_000_000);

        session.ResetChip();

        Assert.Equal(0ul, session.MasterClock);
        // Queue was emptied by the reset: a drain right after returns nothing.
        int drained = session.DrainAudio(new short[1024], 512);
        Assert.Equal(0, drained);
        // The session remains usable at the original rate.
        Assert.Equal(OpnaNativeSession.AbiVersionTarget, session.AbiVersion);
    }

    [Fact]
    public void ClearAdpcmRam_Succeeds_AndDoesNotMoveTime()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var session = OpnaNativeSession.Open(44100);

        session.AdvanceTo(144);
        ulong before = session.MasterClock;
        session.ClearAdpcmRam(0xFF);
        Assert.Equal(before, session.MasterClock);
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        var session = OpnaNativeSession.Open(44100);
        session.Dispose();
        session.Dispose(); // must not throw
    }

    [Fact]
    public void GetIrq_DoesNotAdvanceTime()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var session = OpnaNativeSession.Open(44100);

        session.AdvanceTo(288);
        ulong before = session.MasterClock;
        _ = session.GetIrq();
        Assert.Equal(before, session.MasterClock);
    }

    /// <summary>Schedules the real Furnace YM2608 bus-write trace.</summary>
    private static void WriteFurnaceTrace(OpnaNativeSession session)
    {
        foreach ((ulong clock, byte bank, byte register, byte value) in OpnaFurnaceTrace.Writes)
            session.WriteRegister(clock, bank, register, value);
    }

    /// <summary>
    /// Streams time forward in bounded advance steps (the native timed-audio
    /// FIFO is finite) and drains queued PCM after each step.
    /// </summary>
    private static int StreamAdvanceAndDrain(OpnaNativeSession session, ulong targetClock, out short[] pcm)
    {
        const int CapacityFrames = 48_000;
        var buffer = new short[CapacityFrames * 2];
        int total = 0;
        ulong clock = 0;
        while (clock < targetClock)
        {
            clock += 2_000_000;
            if (clock > targetClock)
                clock = targetClock;
            session.AdvanceTo(clock);
            int drained = session.DrainAudio(buffer, CapacityFrames - total);
            total += drained;
        }
        pcm = buffer;
        return total;
    }

    /// <summary>Finds the built native library in the repo runtimes layout.</summary>
    private static string ResolveBuiltNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string RequireNativeLibrary()
    {
        string lib = ResolveBuiltNativeLibrary();
        Assert.True(lib != null && File.Exists(lib),
            "Native OPNA library not built. Run: cmake -S native/MDPlayer.OpnaNative -B native/MDPlayer.OpnaNative/build && cmake --build native/MDPlayer.OpnaNative/build");
        return lib;
    }

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(NativeLibEnvVar);
        Environment.SetEnvironmentVariable(NativeLibEnvVar, path);
        return new RestoreEnv(NativeLibEnvVar, previous);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public RestoreEnv(string name, string previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
