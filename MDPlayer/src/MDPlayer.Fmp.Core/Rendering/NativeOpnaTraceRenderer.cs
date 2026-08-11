using Fmp.Core.Playback.Opna;

namespace Fmp.Core.Rendering;

/// <summary>
/// Pass-2 native OPNA replay. Owns the clocked native YM2608 device, the
/// capture event cursor and the native OPNA scratch buffer. It applies every
/// captured YM2608 register write at its absolute YM2608 master clock and
/// advances the chip to each chunk-boundary clock; audio produced by the
/// device is drained out by the caller. It never reads status or IRQ, never
/// executes a CPU instruction, and never touches PPZ8. Event clock positions
/// come directly from the capture — no CPU-cycle mapper is involved.
/// </summary>
internal sealed class NativeOpnaTraceRenderer : IDisposable
{
    public const int DefaultChunkFrames = 4096;

    private readonly IClockedOpnaDevice _device;
    private readonly OpnaMasterClockFrameMapper _frameMapper;
    private readonly IReadOnlyList<CapturedEvent> _events;

    private int _cursor;
    private ulong _lastClock;

    public NativeOpnaTraceRenderer(
        IClockedOpnaDevice device,
        IReadOnlyList<CapturedEvent> events,
        int sampleRate)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _events = events;
        _frameMapper = new OpnaMasterClockFrameMapper(sampleRate);
    }

    /// <summary>Current absolute native master clock.</summary>
    public ulong MasterClock => _device.MasterClock;

    /// <summary>Approximate count of OPNA frames already drained.</summary>
    public int OutputLatencyFrames => _device.OutputLatencyFrames;

    /// <summary>Index of the next unconsumed capture event (diagnostics).</summary>
    public int EventCursor => _cursor;

    /// <summary>
    /// Replays every captured OPNA write whose master clock is at or below the
    /// ceiling for <paramref name="chunkEndFrame"/>, in exact order. Each write
    /// advances the device to its own absolute clock and writes the register at
    /// that same clock (equal-clock order preserved; no extra advance merely to
    /// perform a write). Then advances the device to the clock of the
    /// chunk-boundary ceiling.
    /// </summary>
    public void ReplayToChunkBoundary(long chunkEndFrame)
    {
        // The native resampler holds a fixed output latency in flight: to make
        // output frames [0, chunkEndFrame) fully drainable, the chip clock must
        // be advanced past offset=chunkEndFrame output periods PLUS the latency
        // (the pre-roll flushes latency frames before the first real one).
        long framesClock = chunkEndFrame + _device.OutputLatencyFrames;
        ulong ceiling = _frameMapper.MapFrameToMasterCeiling(framesClock);
        for (; _cursor < _events.Count; _cursor++)
        {
            var e = _events[_cursor];
            if (e.Kind != CapturedEventKind.OpnaWrite)
                continue; // PPZ8 events are not consumed by the OPNA cursor
            if (e.OpnaMasterClock > ceiling)
                break;
            if (e.OpnaMasterClock < _lastClock)
                throw new InvalidOperationException($"OPNA capture regressed at sequence {e.Sequence}");
            EnsureCadenceSupported(e, e.OpnaMasterClock);
            _device.WriteRegister(e.OpnaMasterClock, e.Payload.Opna.Port, e.Payload.Opna.Address, e.Payload.Opna.Data);
            _lastClock = e.OpnaMasterClock;
        }
        _device.AdvanceTo(ceiling);
    }

    /// <summary>
    /// Failure model for cadence writes the native ABI does not support: the
    /// ABI supports the fixed 144-master-clock cadre written via 0x2D; 0x2E
    /// and 0x2F request other cadences and must fail — never fall back, never
    /// continue with incorrect audio.
    /// </summary>
    private void EnsureCadenceSupported(CapturedEvent e, ulong clock)
    {
        if ((e.Payload.Opna.Port == 0 && e.Payload.Opna.Address == 0x2E)
            || (e.Payload.Opna.Port == 0 && e.Payload.Opna.Address == 0x2F)
            || (e.Payload.Opna.Port == 1 && e.Payload.Opna.Address == 0x2E)
            || (e.Payload.Opna.Port == 1 && e.Payload.Opna.Address == 0x2F))
        {
            throw new NotSupportedException(
                $"Captured YM2608 write requests an unsupported cadence: " +
                $"opnaClock={clock} port={e.Payload.Opna.Port} " +
                $"address=0x{e.Payload.Opna.Address:X2} data=0x{e.Payload.Opna.Data:X2}. " +
                $"Only the fixed 144-master-clock cadence (register 0x2D) is supported.");
        }
    }

    public void Dispose() => _device.Dispose();

    /// <summary>Drains available native frames (never advances the clock).</summary>
    public int DrainAudio(short[] interleaved, int requestedFrames) =>
        _device.DrainAudio(interleaved, requestedFrames);
}
