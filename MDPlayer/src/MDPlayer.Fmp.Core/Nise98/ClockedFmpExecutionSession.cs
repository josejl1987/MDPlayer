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
    /// Creates a clocked session over the given machine and OPNA device. The
    /// machine must already have <see cref="Nise98.CpuClockFrequencyHz"/> set
    /// from the active machine configuration (never hard-coded here).
    /// </summary>
    public ClockedFmpExecutionSession(Nise98 nise98, IClockedOpnaDevice device)
    {
        _nise98 = nise98 ?? throw new ArgumentNullException(nameof(nise98));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (nise98.CpuClockFrequencyHz == 0)
            throw new ArgumentException(
                "ClockedFmpExecutionSession requires Nise98.CpuClockFrequencyHz set from the active machine configuration.",
                nameof(nise98));

        _cpu = nise98.GetCPU();
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

    /// <summary>Disposes the owned clocked OPNA device.</summary>
    public void Dispose() => _device.Dispose();
}
