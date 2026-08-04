using Fmp.Core.Playback.Opna;

namespace Fmp.Core.Nise98;

/// <summary>
/// Clocked Nise98 OPNA execution coordinator (the internal machinery only;
/// the default FMP renderer must not instantiate this — Prompt 8 wires the
/// real backend selection).
///
/// Owns the authoritative Nise286 cycle clock, the exact rational CPU→OPNA
/// clock mapper, the port bridge and the clocked OPNA device, and drives
/// deterministic execution slices:
///
///  * I/O: every YM2608 access is routed through
///    <see cref="ClockedNise98OpnaBridge"/> at the existing Nise98 port-I/O
///    boundary, timestamped with the authoritative cycle count at access time.
///  * Idle advancement: at the end of each execution slice the device is
///    advanced to <c>mapper.Map(TotalCycles)</c> — the YM2608 keeps running
///    during CPU time with no OPNA I/O. Advancement is always based on cycles
///    actually executed, never on a slice budget or an audio drain.
///  * IRQ: sampled at the existing Nise98 interrupt-poll boundaries (each
///    instruction, matching where Nise286.Interrupt() runs). No separate
///    timer, no sample-based polling, no legacy OpnaTimer on this path.
///
/// Reset order: reset Nise98/Nise286, reset port latches, reset mapper,
/// reset native OPNA chip, verify CPU origin zero, verify OPNA clock origin
/// zero. External ADPCM RAM is never cleared by an ordinary chip reset.
/// </summary>
public sealed class ClockedFmpExecutionSession : IDisposable
{
    /// <summary>
    /// interruptTrigger index for the OPNA IRQ line (IRQ12, slave IRQ4 in the
    /// Nise286 interrupt model — the PC-9801-86 factory default in fmStatus.Int).
    /// </summary>
    public const int OpnaIrqTriggerIndex = 14;

    private readonly Nise98 _nise98;
    private readonly Nise286 _cpu;
    private readonly IClockedOpnaDevice _device;
    private readonly NiseOpnaClockMapper _mapper;
    private readonly ClockedNise98OpnaBridge _bridge;

    /// <summary>
    /// Creates a clocked execution coordinator over the given machine, its
    /// authoritative Nise286 CPU and the OPNA device.
    ///
    /// Explicit construction order (the renderer/boot path must follow it):
    ///   1. construct <see cref="Nise98"/>;
    ///   2. install the clocked OPNA port bridge (done here, below) so that
    ///      any port I/O during the subsequent boot crosses the clocked path;
    ///   3. call <see cref="Nise98.Init"/>, which creates the Nise286 CPU;
    ///   4. obtain the authoritative <see cref="Nise286"/> from
    ///      <see cref="Nise98.GetCPU"/>;
    ///   5. construct this coordinator with that non-null CPU;
    ///   6. boot the FMP driver (port I/O is now timestamped).
    ///
    /// The CPU is captured eagerly and is rejected when null — the session
    /// never constructs with a yet-to-be-created CPU, so it can never fail
    /// later from <c>ExecuteSlice()</c> with a <see cref="NullReferenceException"/>.
    /// </summary>
    public ClockedFmpExecutionSession(Nise98 nise98, Nise286 cpu, IClockedOpnaDevice device)
    {
        _nise98 = nise98 ?? throw new ArgumentNullException(nameof(nise98));
        _cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (nise98.CpuClockFrequencyHz == 0)
            throw new ArgumentException(
                "ClockedFmpExecutionSession requires Nise98.CpuClockFrequencyHz set from the active machine configuration.",
                nameof(nise98));

        _mapper = new NiseOpnaClockMapper(nise98.CpuClockFrequencyHz);
        _bridge = new ClockedNise98OpnaBridge(_mapper, device);
        nise98.OpnaPortHandler = _bridge;
    }

    public NiseOpnaClockMapper Mapper => _mapper;
    public ClockedNise98OpnaBridge Bridge => _bridge;
    public ulong TotalCpuCycles => _cpu.TotalCycles;
    public ulong OpnaClock => _mapper.Map(_cpu.TotalCycles);
    public IClockedOpnaDevice Device => _device;

    /// <summary>
    /// Coordinator reset order: reset Nise98/Nise286, reset port latches,
    /// reset mapper, reset the native OPNA chip, then verify CPU origin zero
    /// and OPNA clock origin zero. External ADPCM RAM is never cleared here.
    /// </summary>
    public void Reset()
    {
        _nise98.Reset();            // Nise98/Nise286 → CPU origin (cycles zero, halt cleared, triggers cleared)
        _bridge.Reset();            // port latches (bank-0/bank-1)
        _mapper.Reset();            // mapper origin
        _device.ResetChip();        // native chip reset (preserves ADPCM RAM, time to zero)

        if (_cpu.TotalCycles != 0)
            throw new InvalidOperationException($"CPU origin not zero after reset: {_cpu.TotalCycles}");
        if (_device.MasterClock != 0)
            throw new InvalidOperationException($"OPNA clock origin not zero after reset: {_device.MasterClock}");
    }

    /// <summary>
    /// Executes up to <paramref name="maxInstructions"/> Nise98 instructions.
    /// At each instruction (the existing interrupt-poll boundary) the device
    /// is advanced to the mapped clock and the OPNA IRQ line is sampled;
    /// asserted IRQ is routed into <see cref="Nise286.interruptTrigger"/>.
    /// At the end of the slice the device is idle-advanced to the mapped
    /// clock of the cycles actually executed. Returns the instruction count
    /// executed (fewer when the machine halts first).
    /// </summary>
    public int ExecuteSlice(int maxInstructions)
    {
        if (maxInstructions < 0)
            throw new ArgumentOutOfRangeException(nameof(maxInstructions), maxInstructions, "must be non-negative");

        int executed = 0;
        for (int i = 0; i < maxInstructions; i++)
        {
            // Existing Nise98 interrupt-poll boundary: advance the device to
            // the current mapped clock, then sample the OPNA IRQ line.
            ulong mapped = _mapper.Map(_cpu.TotalCycles);
            _device.AdvanceTo(mapped);
            if (_device.IrqAsserted)
                _cpu.interruptTrigger[OpnaIrqTriggerIndex] = true;

            int rc = _nise98.StepExecute();
            if (rc < 0)
                break; // halted
            executed++;
        }

        // Idle advancement based on cycles actually executed.
        _device.AdvanceTo(_mapper.Map(_cpu.TotalCycles));
        return executed;
    }

    /// <summary>
    /// Advances idle machine time to the given absolute CPU cycle in one
    /// operation: the authoritative Nise286 cycle counter AND the native
    /// YM2608 master clock move together through the mapper.
    ///
    /// This models elapsed machine time during which the rendering host did
    /// NOT invoke driver code. It is NOT an executed instruction: no
    /// instruction is fetched, no register is modified, no memory is touched
    /// and no port I/O is performed. Only the authoritative machine-cycle
    /// counter, the mapped OPNA master clock and the IRQ level change.
    ///
    /// Sequence (per the explicit idle-cycle contract):
    ///   1. reject regression;
    ///   2. advance the authoritative Nise286 cycle counter by the exact
    ///      positive delta (no-op for an equal target);
    ///   3. map the resulting absolute cycle through the existing
    ///      <see cref="NiseOpnaClockMapper"/>;
    ///   4. advance the native OPNA device to that mapped clock;
    ///   5. sample the native IRQ at the resulting boundary through the
    ///      existing interrupt-arbitration path;
    ///   6. the pending IRQ stays set for the next real CPU-execution
    ///      boundary (<see cref="ExecuteSlice"/> consumes it).
    ///
    /// The cycle counter stays owned by <see cref="Nise286"/> — the renderer
    /// never carries a second counter or a renderer-side offset.
    /// </summary>
    internal void AdvanceIdleToCpuCycle(ulong absoluteCpuCycle)
    {
        // Reject regression; equal target is a no-op (both the CPU counter and
        // the mapped OPNA clock are unchanged).
        if (absoluteCpuCycle < _cpu.TotalCycles)
            throw new ArgumentOutOfRangeException(
                nameof(absoluteCpuCycle), absoluteCpuCycle,
                $"Idle target CPU cycle regressed: {absoluteCpuCycle} < {_cpu.TotalCycles}");
        if (absoluteCpuCycle == _cpu.TotalCycles)
            return;

        _cpu.AdvanceIdleToCpuCycle(absoluteCpuCycle);

        // Map the resulting absolute cycle and advance the device.
        ulong mapped = _mapper.Map(absoluteCpuCycle);
        _device.AdvanceTo(mapped);

        // Sample the native IRQ at the resulting boundary.
        if (_device.IrqAsserted)
            _cpu.interruptTrigger[OpnaIrqTriggerIndex] = true;
    }

    /// <summary>Disposes the owned clocked OPNA device.</summary>
    public void Dispose() => _device.Dispose();
}
