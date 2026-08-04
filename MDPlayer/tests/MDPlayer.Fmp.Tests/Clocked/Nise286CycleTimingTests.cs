using Fmp.Core.Nise98;
using Xunit;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace MDPlayer.Fmp.Tests.Clocked;

/// <summary>
/// Focused CPU-timing tests for the authoritative Nise286 cycle clock.
///
/// Instruction-timing convention (exposed here and in one comment in
/// <see cref="Nise286"/>): the Nise286 core does not model 80286 microcycle
/// timing, so the clocked path counts every executed instruction as exactly
/// one CPU clock tick at the machine's configured clock frequency. The cycle
/// counter is a checked ulong accumulator: it never saturates, and overflow
/// surfaces as one deterministic internal exception (OverflowException).
/// </summary>
public sealed class Nise286CycleTimingTests
{
    /// <summary>
    /// The authoritative cycle counter starts at zero and advances by exactly
    /// one per executed instruction (the documented instruction-timing
    /// convention). Runs a fixed NOP stream through Nise98 and checks the
    /// counter against the number of executed instructions.
    /// </summary>
    [Fact]
    public void CycleCounter_AdvancesOnePerInstruction()
    {
        Nise98Machine nise98 = BuildMachine(8_000_000);
        const int nops = 37;
        int baseAddr = 0x10000;
        for (int i = 0; i < nops; i++)
            nise98.GetMem().PokeB(baseAddr + i, 0x90); // NOP
        nise98.GetMem().PokeB(baseAddr + nops, 0xF4); // HLT
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;

        INiseCycleClock clock = nise98.GetCPU();
        Assert.Equal(0ul, clock.TotalCycles);

        for (int i = 0; i < nops; i++)
            nise98.StepExecute();

        Assert.Equal((ulong)nops, clock.TotalCycles);

        // HLT itself executes (one tick); the following call reports the
        // halted state and does not tick.
        Assert.Equal(0, nise98.StepExecute());
        Assert.Equal((ulong)nops + 1, clock.TotalCycles);
        int rc = nise98.StepExecute();
        Assert.Equal(-1, rc);
        Assert.Equal((ulong)nops + 1, clock.TotalCycles);
    }

    /// <summary>
    /// The checked accumulator does not saturate: driving it to the top of
    /// the ulong range surfaces one deterministic internal exception
    /// (OverflowException) on the next tick.
    /// </summary>
    [Fact]
    public void CycleCounter_Overflow_ThrowsDeterministicException()
    {
        Nise98Machine nise98 = BuildMachine(8_000_000);
        nise98.GetMem().PokeB(0x10000, 0x90); // NOP
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;

        // Drive the private accumulator to the top of its range via reflection
        // (test-only seam; the production increment is a single checked add).
        var field = typeof(Nise286).GetField(
            "_totalCycles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field.SetValue(nise98.GetCPU(), ulong.MaxValue);

        var ex = Assert.Throws<OverflowException>(() => nise98.StepExecute());
        Assert.NotNull(ex);
    }

    /// <summary>
    /// The reported clock frequency comes from the active Nise98 machine
    /// configuration; the core never hard-codes a PC-98 CPU frequency.
    /// </summary>
    [Theory]
    [InlineData(8_000_000u)]
    [InlineData(10_000_000u)]
    [InlineData(7_987_200u)]
    public void ClockFrequencyHz_ComesFromMachineConfig(uint cpuHz)
    {
        Nise98Machine nise98 = BuildMachine(cpuHz);
        Assert.Equal(cpuHz, nise98.GetCPU().ClockFrequencyHz);
    }

    /// <summary>
    /// The I/O access-time callback fires at the existing Nise98 port-I/O
    /// boundary with the authoritative cycle count at access time, preserving
    /// per-access order. Two FM data writes (register writes) are timestamped
    /// with increasing cycle counts.
    /// </summary>
    [Fact]
    public void PortHandler_ReceivesCycleCountAtBoundary_InOrder()
    {
        Nise98Machine nise98 = BuildMachine(8_000_000);

        var seen = new List<(ulong cycle, ushort port, byte data)>();
        nise98.OpnaPortHandler = new RecordingPortHandler(
            read: null,
            write: (cycle, port, data) => seen.Add((cycle, port, data)));

        // mov dx, 0x0188 ; mov al, 0x28 ; out dx, al   (address latch)
        // mov dx, 0x018a ; mov al, 0x0f ; out dx, al   (data write)
        byte[] program =
        {
            0xBA, 0x88, 0x01, // mov dx, 0x0188
            0xB0, 0x28,       // mov al, 0x28
            0xEE,             // out dx, al
            0xBA, 0x8A, 0x01, // mov dx, 0x018a
            0xB0, 0x0F,       // mov al, 0x0f
            0xEE,             // out dx, al
            0xF4,             // hlt
        };
        int baseAddr = 0x10000;
        for (int i = 0; i < program.Length; i++)
            nise98.GetMem().PokeB(baseAddr + i, program[i]);
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;

        // The two OUT instructions are instructions 3 and 6 (1-based) in the
        // stream, so their authoritative tick counts are 3 and 6.
        while (seen.Count < 2)
            nise98.StepExecute();

        Assert.Equal(2, seen.Count);
        Assert.Equal((3ul, (ushort)0x0188, (byte)0x28), seen[0]);
        Assert.Equal((6ul, (ushort)0x018a, (byte)0x0f), seen[1]);
    }

    /// <summary>
    /// Status reads route through the port handler and return its result
    /// directly — the clocked path never falls back to cached MDSound status.
    /// </summary>
    [Fact]
    public void PortHandler_Read_ReturnsHandlerResult()
    {
        Nise98Machine nise98 = BuildMachine(8_000_000);
        nise98.OpnaPortHandler = new RecordingPortHandler(
            read: (cycle, port) => (byte)(port & 0xff),
            write: null);

        // mov dx, 0x0188 ; in al, dx ; hlt
        byte[] program =
        {
            0xBA, 0x88, 0x01, // mov dx, 0x0188
            0xEC,             // in al, dx
            0xF4,             // hlt
        };
        int baseAddr = 0x10000;
        for (int i = 0; i < program.Length; i++)
            nise98.GetMem().PokeB(baseAddr + i, program[i]);
        Register286 regs = nise98.GetRegisters();
        regs.CS = 0x1000;
        regs.IP = 0x0000;

        nise98.StepExecute(); // mov dx
        nise98.StepExecute(); // in al, dx
        nise98.StepExecute(); // hlt

        Assert.Equal((byte)0x88, (byte)regs.AL);
    }

    private static Nise98Machine BuildMachine(uint cpuHz)
    {
        var ft = new fileTemp();
        var nise98 = new Nise98Machine();
        nise98.Init(
            msgWrite: (msg, args) => { },
            opnaWrite: (dat) => { },
            fileTemp: ft,
            ongen: Nise98Machine.enmOngenBoardType.PC9801_86B);
        nise98.CpuClockFrequencyHz = cpuHz;
        return nise98;
    }

    private sealed class RecordingPortHandler : INise98OpnaPortHandler
    {
        private readonly Func<ulong, ushort, byte>? _read;
        private readonly Action<ulong, ushort, byte>? _write;

        public RecordingPortHandler(Func<ulong, ushort, byte>? read, Action<ulong, ushort, byte>? write)
        {
            _read = read;
            _write = write;
        }

        public byte Read(ulong cpuCycle, ushort port) => _read(cpuCycle, port);
        public void Write(ulong cpuCycle, ushort port, byte data) => _write(cpuCycle, port, data);
    }
}
