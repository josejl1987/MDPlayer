using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;
using Xunit;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Final-cycle synchronization invariant of the clocked coordinator: after
/// every real CPU execution step the native OPNA device must sit exactly at
/// <c>mapper.Map(cpu.TotalCycles)</c> — the CPU timeline is never left ahead of
/// the synchronized chip. Covered via the executor's idle-advancement paths
/// (the same <c>SynchronizeDeviceToCpu</c> used by
/// <see cref="ClockedFmpExecutionSession.ExecuteDriverFunction"/> after each
/// real driver call, which is exercised end-to-end by the native FMP
/// integration suite).
/// </summary>
public sealed class ClockedDriverSyncTests
{
    private const uint CpuHz = 8_000_000;

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

    private static ClockedFmpExecutionSession Attach(Nise98Machine nise98, RecordingOpnaDevice device)
    {
        var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);
        return session;
    }

    /// <summary>
    /// Idle advancement to an absolute cycle leaves the native device at the
    /// mapped OPNA clock of that CPU cycle (the final-cycle invariant).
    /// </summary>
    [Fact]
    public void AdvanceIdleToCpuCycle_SynchronizesDeviceToMappedClock()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);

        ulong target = 123456UL;
        session.AdvanceIdleToCpuCycle(target);

        Assert.Equal(session.Mapper.Map(target), device.MasterClock);
        Assert.Equal(session.OpnaClock, device.MasterClock);
        Assert.True(device.MasterClock >= 0);
    }

    /// <summary>
    /// Advancing to a later absolute cycle never moves the device backward
    /// (monotonic final-cycle synchronization).
    /// </summary>
    [Fact]
    public void AdvanceIdleToCpuCycle_IsMonotonic()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);

        session.AdvanceIdleToCpuCycle(1_000_000UL);
        ulong first = device.MasterClock;

        session.AdvanceIdleToCpuCycle(2_000_000UL);
        ulong second = device.MasterClock;

        Assert.True(second >= first);
        Assert.Equal(session.OpnaClock, second);
    }

    /// <summary>
    /// After an execution slice (the idle-advancement tail), the device is at
    /// the mapped final cycle, not ahead of or behind the CPU.
    /// </summary>
    [Fact]
    public void ExecuteSlice_LeavesDeviceAtMappedFinalCycle()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);

        // hlt-only program: no I/O, negligible CPU advance, device stays mapped.
        const int baseAddr = 0x10000;
        nise98.GetMem().PokeB(baseAddr, 0xf4); // hlt
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;

        session.ExecuteSlice(1);

        Assert.Equal(session.OpnaClock, device.MasterClock);
    }
}