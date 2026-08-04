using Fmp.Core.Playback.Opna;

namespace Fmp.Core.Rendering;

using Fmp.Core.Nise98;

/// <summary>
/// Pass-2 native OPNA replay. Owns the clocked native YM2608 device, the exact
/// CPU-cycle→OPNA-master-clock mapper, the capture event cursor and the native
/// OPNA scratch buffer. It applies every captured YM2608 register write at its
/// authoritative master clock and advances the chip to each chunk-boundary
/// clock; audio produced by the device is drained out by the caller. It never
/// reads status or IRQ, never executes a CPU instruction, and never touches
/// PPZ8.
/// </summary>
internal sealed class NativeOpnaTraceRenderer : IDisposable
{
    public const int DefaultChunkFrames = 4096;

    private readonly IClockedOpnaDevice _device;
    private readonly NiseOpnaClockMapper _mapper;
    private readonly CpuToOutputFrameMapper _cpuFrame;
    private readonly IReadOnlyList<FmpCapturedEvent> _events;

    private int _cursor;
    private ulong _lastCycle;

    public NativeOpnaTraceRenderer(
        IClockedOpnaDevice device,
        IReadOnlyList<FmpCapturedEvent> events,
        ulong cpuClockHz,
        int sampleRate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _events = events;
        _mapper = new NiseOpnaClockMapper((uint)cpuClockHz);
        _cpuFrame = new CpuToOutputFrameMapper(cpuClockHz, sampleRate);
    }

    /// <summary>Current absolute native master clock.</summary>
    public ulong MasterClock => _device.MasterClock;

    /// <summary>Approximate count of OPNA frames already drained.</summary>
    public int OutputLatencyFrames => _device.OutputLatencyFrames;

    /// <summary>Index of the next unconsumed capture event (diagnostics).</summary>
    public int EventCursor => _cursor;

    /// <summary>
    /// Replays every captured OPNA write whose CPU cycle is at or below the
    /// ceiling for <paramref name="chunkEndFrame"/>, in exact order. Each write
    /// maps its own cycle to an OPNA clock, advances the device to it and
    /// writes the register at that same clock (equal-clock order preserved; no
    /// extra advance merely to perform a write). Then advances the device to
    /// the mapped clock of the chunk-boundary ceiling.
    /// </summary>
    public void ReplayToChunkBoundary(long chunkEndFrame)
    {
        // The native resampler holds a fixed output latency in flight: to make
        // output frames [0, chunkEndFrame) fully drainable, the chip clock must
        // be advanced past offset=chunkEndFrame output periods PLUS the latency
        // (the pre-roll flushes latency frames before the first real one).
        long framesClock = chunkEndFrame + _device.OutputLatencyFrames;
        ulong ceiling = _cpuFrame.MapFrameToCpuCeiling(framesClock);
        for (; _cursor < _events.Count; _cursor++)
        {
            var e = _events[_cursor];
            if (e is not CapturedOpnaWrite write)
                continue; // PPZ8 events are not consumed by the OPNA cursor
            if (write.CpuCycle > ceiling)
                break;
            if (write.CpuCycle < _lastCycle)
                throw new InvalidOperationException($"OPNA capture regressed at sequence {write.Sequence}");
            ulong clock = _mapper.Map(write.CpuCycle);
            EnsureCadenceSupported(write, clock);
            _device.WriteRegister(clock, write.Port, write.Address, write.Data);
            _lastCycle = write.CpuCycle;
        }
        ulong boundaryClock = _mapper.Map(ceiling);
        _device.AdvanceTo(boundaryClock);
    }

    /// <summary>Advances the device to the mapped clock of <paramref name="cpuCycle"/>.</summary>
    public void AdvanceToCpuCycle(ulong cpuCycle)
    {
        _device.AdvanceTo(_mapper.Map(cpuCycle));
    }

    /// <summary>Drains available native frames (never advances the clock).</summary>
    public int DrainAudio(short[] interleaved, int requestedFrames) =>
        _device.DrainAudio(interleaved, requestedFrames);

    /// <summary>
    /// Failure model for cadence writes the native ABI does not support: the
    /// ABI supports the fixed 144-master-clock cadre written via 0x2D; 0x2E
    /// and 0x2F request other cadences and must fail — never fall back, never
    /// continue with incorrect audio.
    /// </summary>
    private void EnsureCadenceSupported(CapturedOpnaWrite write, ulong clock)
    {
        if ((write.Port == 0 && write.Address == 0x2E)
            || (write.Port == 0 && write.Address == 0x2F)
            || (write.Port == 1 && write.Address == 0x2E)
            || (write.Port == 1 && write.Address == 0x2F))
        {
            throw new NotSupportedException(
                $"Captured YM2608 write requests an unsupported cadence: " +
                $"cpu={write.CpuCycle} opnaClock={clock} port={write.Port} " +
                $"address=0x{write.Address:X2} data=0x{write.Data:X2}. " +
                $"Only the fixed 144-master-clock cadence (register 0x2D) is supported.");
        }
    }

    public void Dispose() => _device.Dispose();
}
