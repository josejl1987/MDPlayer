using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;
using Xunit;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Fake-device tests for <see cref="ClockedNise98OpnaBridge"/> and
/// <see cref="ClockedFmpExecutionSession"/>: port mapping, independent
/// bank-0/bank-1 latches, native status reads, equal-clock ordering, the
/// execution coordinator's reset order, idle advancement and IRQ sampling.
/// </summary>
public sealed class ClockedNise98OpnaBridgeTests
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

    private static void LoadProgram(Nise98Machine nise98, params byte[] program)
    {
        const int baseAddr = 0x10000;
        for (int i = 0; i < program.Length; i++)
            nise98.GetMem().PokeB(baseAddr + i, program[i]);
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;
    }

    // mov dx, 0x0188 ; mov al, 0x28 ; out dx, al ; mov dx, 0x018a ; mov al, 0x0f ; out dx, al ; hlt
    private static readonly byte[] WriteProgram =
    {
        0xBA, 0x88, 0x01, 0xB0, 0x28, 0xEE,
        0xBA, 0x8A, 0x01, 0xB0, 0x0F, 0xEE,
        0xF4,
    };

    /// <summary>
    /// Address-latch + data writes map through the exact Nise98 port mapping
    /// to bank-0 register writes with mapped-clock timestamps.
    /// </summary>
    [Fact]
    public void Write_RoutesBank0LatchAndData_WithMappedClock()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);
        LoadProgram(nise98, WriteProgram);

        // OUT to 0x188 is instruction 3, OUT to 0x18a is instruction 6.
        session.ExecuteSlice(100);

        Assert.Equal(1, device.Writes.Count);
        (ulong clock, byte bank, byte address, byte value) = device.Writes[0];
        Assert.Equal(0, bank);
        Assert.Equal(0x28, address);   // latched address
        Assert.Equal(0x0f, value);
        // Mapped clock of the 6th instruction at 8 MHz CPU vs 7,987,200 master.
        Assert.Equal(ClosedForm(6), clock);
    }

    /// <summary>Bank-0 and bank-1 address latches are independent.</summary>
    [Fact]
    public void Write_Bank0AndBank1Latches_AreIndependent()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);

        var bridge = session.Bridge;
        bridge.Write(1, 0x0188, 0x28); // bank-0 latch
        bridge.Write(2, 0x018c, 0x40); // bank-1 latch
        Assert.Equal(0x28, bridge.Bank0Latch);
        Assert.Equal(0x40, bridge.Bank1Latch);

        bridge.Write(3, 0x018a, 0x11); // bank-0 data
        bridge.Write(4, 0x018e, 0x22); // bank-1 data
        Assert.Equal(2, device.Writes.Count);

        Assert.Equal(0, device.Writes[0].bank);
        Assert.Equal(0x28, device.Writes[0].address);
        Assert.Equal(0x11, device.Writes[0].value);
        Assert.Equal(ClosedForm(3), device.Writes[0].clock);

        Assert.Equal(1, device.Writes[1].bank);
        Assert.Equal(0x40, device.Writes[1].address);
        Assert.Equal(0x22, device.Writes[1].value);
        Assert.Equal(ClosedForm(4), device.Writes[1].clock);
    }

    /// <summary>
    /// Status reads (0x88/0x8c) return the native device status directly;
    /// data reads (0x8a/0x8e) return the register read-back (last written
    /// value, or the 86-board pseudo-registers) — the semantics the FMP
    /// driver's boot code depends on.
    /// </summary>
    [Fact]
    public void Read_ReturnsNativeStatus_AndRegisterReadBack()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        device.StatusBytes[0] = 0xA1;
        device.StatusBytes[1] = 0xB2;
        using var session = Attach(nise98, device);

        // Status ports return the native device status directly.
        Assert.Equal(0xA1, session.Bridge.Read(5, 0x0188));
        Assert.Equal(0xB2, session.Bridge.Read(7, 0x018c));
        Assert.Equal(2, device.StatusReads.Count);

        // Data ports return the register read-back: 0 before any write.
        Assert.Equal(0x00, session.Bridge.Read(9, 0x018a));
        Assert.Equal(0x00, session.Bridge.Read(11, 0x018e));
        Assert.Equal(2, device.StatusReads.Count); // no status traffic for data ports

        // After writes, the read-back returns the last written value, and the
        // 86-board pseudo-registers answer like the legacy FMPortInport.
        session.Bridge.Write(13, 0x0188, 0x28);
        session.Bridge.Write(14, 0x018a, 0x0f);
        Assert.Equal(0x0f, session.Bridge.Read(15, 0x018a));
        Assert.Equal(0x0f, session.Bridge.GetBank0Register(0x28));

        session.Bridge.Write(17, 0x0188, 0x0e);
        Assert.Equal(0x00, session.Bridge.Read(19, 0x018a)); // IRQ-select byte
        session.Bridge.Write(21, 0x0188, 0xff);
        Assert.Equal(0x01, session.Bridge.Read(23, 0x018a)); // board present
    }

    /// <summary>
    /// Equal-clock ordering: consecutive CPU cycles can share one mapped
    /// master clock (fractional remainder carries exactly); the bridge keeps
    /// call order and never artificially increments the OPNA clock.
    /// </summary>
    [Fact]
    public void Write_EqualMappedClock_KeepsCallOrder()
    {
        // At 10 MHz CPU vs 7,987,200 master:
        //   floor(3 * 0.79872) = 2, floor(4 * 0.79872) = 3, floor(5 * 0.79872) = 3.
        var nise98 = BuildMachine(10_000_000);
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);

        session.Bridge.Write(3, 0x0188, 0x28); // bank-0 latch at clock 2
        session.Bridge.Write(4, 0x018a, 0x01); // bank-0 data at clock 3
        session.Bridge.Write(5, 0x018a, 0x02); // bank-0 data at clock 3 (equal clock)

        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(3ul, device.Writes[0].clock);
        Assert.Equal(3ul, device.Writes[1].clock); // equal mapped clock, no artificial increment
        Assert.Equal(0x01, device.Writes[0].value);
        Assert.Equal(0x02, device.Writes[1].value); // call order preserved
    }

    /// <summary>Unknown low-byte ports keep the legacy default behavior (throw).</summary>
    [Fact]
    public void Write_UnknownPort_Throws()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);

        Assert.Throws<NotImplementedException>(() => session.Bridge.Write(1, 0x0180, 0x00));
    }

    /// <summary>
    /// Coordinator reset order: Nise98/Nise286 → port latches → mapper → native
    /// chip, then CPU origin zero and OPNA clock origin zero; ADPCM RAM is
    /// never cleared by an ordinary chip reset.
    /// </summary>
    [Fact]
    public void Coordinator_Reset_FollowsOrderAndVerifiesOrigins()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);
        LoadProgram(nise98, WriteProgram);

        session.ExecuteSlice(100);
        Assert.True(session.TotalCpuCycles > 0);
        Assert.True(session.Device.MasterClock > 0);
        Assert.NotEqual(0, session.Bridge.Bank0Latch);

        session.Reset();

        Assert.Equal(0ul, nise98.GetCPU().TotalCycles);
        Assert.Equal(0ul, session.OpnaClock);
        Assert.Equal(0ul, device.MasterClock);
        Assert.Equal(0, session.Bridge.Bank0Latch);
        Assert.Equal(0, session.Bridge.Bank1Latch);
        Assert.Equal(0, device.ClearAdpcmCalls.Count); // ADPCM RAM never cleared on ordinary reset
        Assert.Equal(1, device.ResetChipCalls.Count);
    }

    /// <summary>
    /// Idle advancement is based on cycles actually executed: after a slice,
    /// the device sits exactly at mapper.Map(TotalCycles), and audio drain
    /// never moves the clock.
    /// </summary>
    [Fact]
    public void Coordinator_IdleAdvancement_MatchesCyclesExecuted()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);
        LoadProgram(nise98, 0x90, 0x90, 0x90, 0x90, 0xF4); // 4 NOP + HLT

        int executed = session.ExecuteSlice(10);
        Assert.Equal(5, executed);
        Assert.Equal(5ul, session.TotalCpuCycles);
        Assert.Equal((5ul * NiseOpnaClockMapper.Ym2608MasterClockHz) / CpuHz, device.MasterClock);
        Assert.Equal(session.Mapper.Map(5), device.MasterClock);

        session.Device.DrainAudio(new short[64], 32);
        Assert.Equal(session.Mapper.Map(5), device.MasterClock); // drain never advances
    }

    /// <summary>
    /// IRQ sampling: when the native device asserts IRQ after advancing, the
    /// coordinator routes it into the CPU interrupt line at the existing poll
    /// boundary. No separate timer is involved.
    /// </summary>
    [Fact]
    public void Coordinator_SamplesIrqAtPollBoundary_RoutesToCpu()
    {
        var nise98 = BuildMachine();
        var device = new RecordingOpnaDevice();
        using var session = Attach(nise98, device);
        LoadProgram(nise98, 0x90, 0x90, 0x90, 0x90, 0xF4);

        device.IrqAsserted = true;
        int executed = session.ExecuteSlice(5);
        Assert.Equal(5, executed);

        Nise286 cpu = nise98.GetCPU();
        Assert.True(cpu.interruptTrigger[ClockedFmpExecutionSession.OpnaIrqTriggerIndex]);
    }

    /// <summary>Missing machine CPU clock configuration is rejected.</summary>
    [Fact]
    public void Coordinator_RequiresMachineCpuClock()
    {
        var nise98 = new Nise98Machine();
        nise98.Init(
            msgWrite: (msg, args) => { },
            opnaWrite: (dat) => { },
            fileTemp: new fileTemp(),
            ongen: Nise98Machine.enmOngenBoardType.PC9801_86B);

        Assert.Throws<ArgumentException>(() => new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), new RecordingOpnaDevice()));
    }

    private static ulong ClosedForm(ulong cpuCycle, uint cpuHz = CpuHz)
        => (ulong)((UInt128)cpuCycle * NiseOpnaClockMapper.Ym2608MasterClockHz / cpuHz);

    private static ClockedFmpExecutionSession Attach(Nise98Machine nise98, RecordingOpnaDevice device)
    {
        var session = new ClockedFmpExecutionSession(nise98, nise98.GetCPU(), device);
        return session;
    }
}
