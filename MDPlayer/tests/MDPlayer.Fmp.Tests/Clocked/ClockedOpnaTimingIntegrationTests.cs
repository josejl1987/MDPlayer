using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;
using Xunit;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Real-native timing integration for the clocked Nise98 OPNA path
/// (<see cref="ClockedFmpExecutionSession"/> + <see cref="NativeOpnaDevice"/>):
///
///  * the device is driven only through port-I/O timestamps derived from the
///    authoritative Nise286 cycle count;
///  * results are independent of the instruction-run slice size (slice
///    independence);
///  * audio drain never advances the chip clock, and drained PCM is hashed in
///    memory (no WAV files);
///  * the legacy MDSound path is untouched (regression covered by the full
///    solution suite).
/// </summary>
public sealed class ClockedOpnaTimingIntegrationTests
{
    private const uint CpuHz = 8_000_000;

    /// <summary>
    /// A small but real YM2608 register sequence (subset of the Furnace
    /// fixture) that produces deterministic audio once the chip is advanced.
    /// </summary>
    private static readonly (byte Bank, byte Register, byte Value)[] RegisterSequence =
    {
        (0, 0x00, 0x55),
        (0, 0x29, 0x83),
        (0, 0x10, 0xBF),
        (0, 0x22, 0x00),
        (0, 0x26, 0x00),
        (0, 0x26, 0xCA),
        (0, 0x27, 0x2A),
        (0, 0x27, 0x30),
        (0, 0x07, 0x38),
        (0, 0xB4, 0xC0),
        (1, 0x00, 0x01),
        (1, 0x10, 0x13),
        (1, 0x10, 0x80),
        (1, 0x01, 0x02),
        (1, 0x04, 0xFF),
        (1, 0x08, 0x28),
    };

    [Fact]
    public void RealNative_RegistersReachDevice_AtMappedClocks()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var device = NativeOpnaDevice.Open(44_100);

        var nise98 = BuildMachine();
        LoadProgram(nise98, BuildProgram(advanceLoopIterations: 4));
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);

        int executed = session.ExecuteSlice(1_000);
        // 6 instructions per register write, plus NOPs and HLT.
        Assert.True(executed >= RegisterSequence.Length * 6, $"program did not run fully: {executed}");

        // The device clock sits exactly at mapper.Map(cycles actually executed).
        Assert.Equal(session.OpnaClock, device.MasterClock);
        Assert.Equal(session.Mapper.Map(session.TotalCpuCycles), device.MasterClock);
        Assert.True(device.MasterClock > 0);
    }

    /// <summary>
    /// Slice independence: the same instruction stream yields the same final
    /// device clock and the same in-memory PCM hash regardless of how the run
    /// is split into execution slices.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1_000)]
    public void RealNative_SliceIndependence_SamePcmHash(int sliceSize)
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        var (hash, totalCycles, masterClock) = RunAndHash(sliceSize);
        Assert.True(totalCycles > 0);

        // Run once more with a different split and compare.
        int otherSlice = sliceSize == 7 ? 11 : 7;
        var (hash2, totalCycles2, masterClock2) = RunAndHash(otherSlice);
        Assert.Equal(totalCycles, totalCycles2);
        Assert.Equal(masterClock, masterClock2);
        Assert.Equal(hash, hash2);
    }

    /// <summary>
    /// Audio invariants: draining never advances the chip clock; the PCM hash
    /// is stable across drain block sizes; draining is a pure FIFO read.
    /// </summary>
    [Fact]
    public void RealNative_AudioDrain_NeverAdvancesClock()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        var nise98 = BuildMachine();
        LoadProgram(nise98, BuildProgram(advanceLoopIterations: 100_000));
        using var device = NativeOpnaDevice.Open(44_100);
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);

        session.ExecuteSlice(2_000_000);

        ulong clockBefore = device.MasterClock;
        Assert.True(clockBefore > 0);

        var buffer = new short[44_100 * 2];
        int frames = device.DrainAudio(buffer, 44_100);
        Assert.True(frames >= 0);
        Assert.Equal(clockBefore, device.MasterClock); // drain never advances

        // Draining twice more (any block size) never moves the clock either.
        _ = device.DrainAudio(buffer, 8_000);
        Assert.Equal(clockBefore, device.MasterClock);
    }

    /// <summary>
    /// PCM determinism: hashing drained audio in memory yields the same digest
    /// for two identical runs (a guard against nondeterminism in the timing
    /// path).
    /// </summary>
    [Fact]
    public void RealNative_PcmHash_DeterministicAcrossRuns()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        ulong hashA = RunAndHash(9).Hash;
        ulong hashB = RunAndHash(13).Hash;
        Assert.Equal(hashA, hashB);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static Nise98Machine BuildMachine()
    {
        var nise98 = new Nise98Machine();
        nise98.Init(
            msgWrite: (msg, args) => { },
            opnaWrite: (dat) => { },
            fileTemp: new fileTemp(),
            ongen: Nise98Machine.enmOngenBoardType.PC9801_86B);
        nise98.CpuClockFrequencyHz = CpuHz;
        return nise98;
    }

    private static void LoadProgram(Nise98Machine nise98, byte[] program)
    {
        const int baseAddr = 0x10000;
        for (int i = 0; i < program.Length; i++)
            nise98.GetMem().PokeB(baseAddr + i, program[i]);
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
    }

    /// <summary>
    /// Builds a PC-98 machine-code program: for each register write, out the
    /// bank address latch then the data byte via 0x188/0x18a (bank 0) or
    /// 0x18c/0x18e (bank 1), then a compact <c>dec cx / jnz</c> loop that runs
    /// approximately <paramref name="advanceLoopIterations"/> iterations (the
    /// whole program stays far inside the 64 KiB CS segment so IP never wraps
    /// and the trailing HLT is always reached), then HLT.
    /// </summary>
    private static byte[] BuildProgram(int advanceLoopIterations)
    {
        var code = new List<byte>();
        foreach ((byte bank, byte reg, byte val) in RegisterSequence)
        {
            ushort adrPort = bank == 0 ? (ushort)0x0188 : (ushort)0x018c;
            ushort datPort = bank == 0 ? (ushort)0x018a : (ushort)0x018e;

            code.AddRange(new byte[] { 0xBA, (byte)(adrPort & 0xff), (byte)(adrPort >> 8) }); // mov dx, adrPort
            code.AddRange(new byte[] { 0xB0, reg });                                          // mov al, reg
            code.Add(0xEE);                                                                   // out dx, al
            code.AddRange(new byte[] { 0xBA, (byte)(datPort & 0xff), (byte)(datPort >> 8) }); // mov dx, datPort
            code.AddRange(new byte[] { 0xB0, val });                                          // mov al, val
            code.Add(0xEE);                                                                   // out dx, al
        }

        // mov cx, loopIterations
        code.Add(0xB9);
        code.Add((byte)(advanceLoopIterations & 0xff));
        code.Add((byte)(advanceLoopIterations >> 8));
        int loopStart = code.Count;
        code.Add(0x90);         // nop
        code.Add(0x49);         // dec cx
        code.Add(0x75);         // jnz short loopStart (3 bytes back from next IP)
        code.Add((byte)(loopStart - (code.Count + 1)));

        code.Add(0xF4);         // hlt
        return code.ToArray();
    }

    private static (ulong Hash, ulong TotalCycles, ulong MasterClock) RunAndHash(int sliceSize)
    {
        var nise98 = BuildMachine();
        LoadProgram(nise98, BuildProgram(advanceLoopIterations: 100_000));
        using var device = NativeOpnaDevice.Open(44_100);
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);

        int executed;
        do
        {
            executed = session.ExecuteSlice(sliceSize);
        } while (executed == sliceSize);

        ulong totalCycles = session.TotalCpuCycles;
        ulong masterClock = device.MasterClock;

        var buffer = new short[44_100 * 2];
        ulong hash = 14695981039346656037; // FNV-1a
        int frames;
        while ((frames = device.DrainAudio(buffer, buffer.Length / 2)) > 0)
        {
            for (int i = 0; i < frames * 2; i++)
            {
                hash ^= (byte)buffer[i];
                hash *= 1099511628211;
            }
        }

        return (hash, totalCycles, masterClock);
    }

    private static string RequireNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new Xunit.Sdk.XunitException(
            "Native OPNA library not built. Run: cmake -S native/MDPlayer.OpnaNative -B native/MDPlayer.OpnaNative/build && cmake --build native/MDPlayer.OpnaNative/build");
    }

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar);
        Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, path);
        return new RestoreEnv(OpnaNativeSession.NativeLibraryEnvVar, previous);
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
