using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;
using Xunit;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Focused tests for the corrected CPU/session construction order and the
/// explicit idle-cycle advancement (<see cref="ClockedFmpExecutionSession.AdvanceIdleToCpuCycle"/>):
///
///  * construction must reject a null CPU and must not fail later from
///    <c>ExecuteSlice()</c> with a <see cref="NullReferenceException"/>;
///  * idle advancement moves the authoritative CPU cycles AND the mapped OPNA
///    master clock together, without mutating CPU registers or memory;
///  * the exact-output-frame→CPU-cycle mapping is monotonic, chunk-independent,
///    drift-free and stable;
///  * regression throws <see cref="ArgumentOutOfRangeException"/>, equal input
///    is a no-op.
/// </summary>
public sealed class ClockedIdleAdvancementTests
{
    private const uint CpuHz = 8_000_000;
    private const ulong MasterHz = NiseOpnaClockMapper.Ym2608MasterClockHz;

    private static Nise98Machine BuildMachine(uint cpuHz = CpuHz)
    {
        var nise98 = new Nise98Machine();
        nise98.Init(
            msgWrite: (msg, args) => { },
            opnaWrite: (dat) => { },
            fileTemp: new fileTemp(),
            ongen: Nise98Machine.enmOngenBoardType.PC9801_86B);
        nise98.CpuClockFrequencyHz = cpuHz;
        return nise98;
    }

    /// <summary>
    /// Construction ordering (Commit 2): the coordinator rejects a null CPU
    /// with an immediate <see cref="ArgumentNullException"/>; and a session
    /// constructed with a valid, initialized CPU never fails later from
    /// <c>ExecuteSlice()</c> with a <see cref="NullReferenceException"/>.
    /// </summary>
    [Fact]
    public void Constructor_RejectsNullCpu()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        Assert.Throws<ArgumentNullException>(() => new ClockedFmpExecutionSession(nise98, null, device));
    }

    [Fact]
    public void Constructor_WithInitializedCpu_CanExecuteWithoutNullRef()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);
        LoadProgram(nise98, 0x90, 0x90, 0xF4); // NOP, NOP, HLT
        // Must not throw NullReferenceException.
        int executed = session.ExecuteSlice(10);
        Assert.Equal(3, executed);
        Assert.Equal(3ul, session.TotalCpuCycles);
    }

    /// <summary>
    /// Exact advancement matches independent UInt128 closed-form calculations
    /// for several output-frame positions at several output rates.
    /// </summary>
    [Theory]
    [InlineData(44100, 1)]
    [InlineData(44100, 64)]
    [InlineData(44100, 257)]
    [InlineData(48000, 1)]
    [InlineData(48000, 7)]
    [InlineData(48000, 64)]
    [InlineData(48000, 257)]
    [InlineData(96000, 1)]
    [InlineData(96000, 7)]
    [InlineData(96000, 64)]
    [InlineData(96000, 257)]
    public void IdleAdvancement_ExactFrameToCpuCycleMapping(int rate, long frames)
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);

        // targetCpuCycle = ceil(frame * cpuHz / rate)
        ulong targetCpu = (ulong)(((UInt128)(ulong)frames * CpuHz + (ulong)rate - 1) / (ulong)rate);
        // OPNA clock = floor(cpuCycle * masterHz / cpuHz)
        ulong expectedOpna = (ulong)((UInt128)targetCpu * MasterHz / CpuHz);

        session.AdvanceIdleToCpuCycle(targetCpu);

        Assert.Equal(targetCpu, session.TotalCpuCycles);
        Assert.Equal(expectedOpna, session.OpnaClock);
        Assert.Equal(expectedOpna, device.MasterClock);
    }

    /// <summary>
    /// Idle advancement must not modify CPU registers, memory, or perform any
    /// port I/O / status reads / register writes. Only total machine cycles,
    /// the mapped OPNA clock and the IRQ level may change.
    /// </summary>
    [Fact]
    public void IdleAdvancement_DoesNotMutateCpuOrMemory()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);

        var regs = nise98.GetRegisters();
        var mem = nise98.GetMem();

        // Seed a bounded region of memory and some register values.
        const int regStart = 0x1000;
        byte[] memBefore = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            byte v = (byte)(i * 3 + 1);
            mem.PokeB(regStart + i, v);
            memBefore[i] = v;
        }
        var regSnapshot = CaptureRegisters(regs);

        device.IrqAsserted = true;
        ulong targetCpu = 1_000_000;
        session.AdvanceIdleToCpuCycle(targetCpu);

        // Registers unchanged.
        var regAfter = CaptureRegisters(regs);
        Assert.Equal(regSnapshot, regAfter);

        // Memory unchanged.
        for (int i = 0; i < 256; i++)
            Assert.Equal(memBefore[i], mem.PeekB(regStart + i));

        // No port accesses, no register writes, no status reads.
        Assert.Empty(device.Writes);
        Assert.Empty(device.StatusReads);

        // Machine cycles and mapped OPNA clock advanced.
        Assert.Equal(targetCpu, session.TotalCpuCycles);
        ulong expectedOpna = (ulong)((UInt128)targetCpu * MasterHz / CpuHz);
        Assert.Equal(expectedOpna, device.MasterClock);
        // IRQ level routed into the CPU interrupt line (pending for the next
        // real execution boundary).
        Nise286 cpu = nise98.GetCPU();
        Assert.True(cpu.interruptTrigger[ClockedFmpExecutionSession.OpnaIrqTriggerIndex]);
    }

    /// <summary>Chunk-independence: the final state is identical regardless of how the idle advance is split.</summary>
    [Fact]
    public void IdleAdvancement_IsChunkIndependent()
    {
        ulong target = 1_000_000;

        var finalOneShot = RunIdleSplit(new ulong[] { target });
        var finalBig = RunIdleSplit(new ulong[] { 400_000, target });
        var finalPerFrame = RunIdleSplit(new ulong[] { 50_000, 100_000, 150_000, 200_000, target });
        var finalIrrReg = RunIdleSplit(new ulong[] { 3, 114, 55_569, target }); // irregular

        Assert.Equal(finalOneShot, finalBig);
        Assert.Equal(finalOneShot, finalPerFrame);
        Assert.Equal(finalOneShot, finalIrrReg);
    }

    private static (ulong Cpu, ulong Opna, ulong Master) RunIdleSplit(params ulong[] absoluteStopsAtEach)
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);
        foreach (ulong stop in absoluteStopsAtEach)
            session.AdvanceIdleToCpuCycle(stop);
        return (session.TotalCpuCycles, session.OpnaClock, device.MasterClock);
    }

    /// <summary>Regression throws; equal input is a no-op.</summary>
    [Fact]
    public void IdleAdvancement_RegressionThrows_EqualIsNoop()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);

        session.AdvanceIdleToCpuCycle(500);
        ulong cpu = session.TotalCpuCycles;
        ulong opna = session.OpnaClock;

        // Equal target: no-op.
        session.AdvanceIdleToCpuCycle(500);
        Assert.Equal(cpu, session.TotalCpuCycles);
        Assert.Equal(opna, session.OpnaClock);
        Assert.Equal(1, device.AdvanceToCalls.Count); // only the first call advanced

        // Regression (an earlier output frame / cycle) throws.
        Assert.Throws<ArgumentOutOfRangeException>(() => session.AdvanceIdleToCpuCycle(499));
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private static void LoadProgram(Nise98Machine nise98, params byte[] program)
    {
        const int baseAddr = 0x10000;
        for (int i = 0; i < program.Length; i++)
            nise98.GetMem().PokeB(baseAddr + i, program[i]);
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
    }

    private static string CaptureRegisters(Register286 regs)
    {
        // Capture the 16-bit registers into a stable, comparable string.
        var parts = new List<string>
        {
            regs.AX.ToString("X4"), regs.BX.ToString("X4"), regs.CX.ToString("X4"),
            regs.DX.ToString("X4"), regs.SI.ToString("X4"), regs.DI.ToString("X4"),
            regs.BP.ToString("X4"), regs.SP.ToString("X4"), regs.CS.ToString("X4"),
            regs.IP.ToString("X4"), regs.SS.ToString("X4"), regs.DS.ToString("X4"),
            regs.ES.ToString("X4"), regs.FLAG.ToString("X4"),
        };
        return string.Join("|", parts);
    }
}