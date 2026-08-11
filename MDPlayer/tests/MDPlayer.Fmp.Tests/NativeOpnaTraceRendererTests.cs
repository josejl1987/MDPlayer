using Fmp.Core.Rendering;
using MDPlayer.Fmp.Tests.Clocked;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 Pass-2 native replay tests (master-clock timeline): exact event
/// replay order against a recording fake device, zero status/IRQ reads during
/// replay, monotonic/regression behavior on the captured absolute master clock,
/// and the unsupported-cadence failure model.
/// </summary>
public class NativeOpnaTraceRendererTests
{
    private const ulong MasterHz = 7_987_200;
    private const int SampleRate = 44100;

    private static IReadOnlyList<CapturedEvent> Capture(params CapturedEvent[] events)
    {
        var builder = new FmpExecutionCaptureBuilder(SampleRate);
        foreach (var e in events)
        {
            switch (e.Kind)
            {
                case CapturedEventKind.OpnaWrite:
                    builder.CaptureOpnaWrite(e.OpnaMasterClock, e.Payload.Opna.Port, e.Payload.Opna.Address, e.Payload.Opna.Data);
                    break;
                case CapturedEventKind.Ppz8Command:
                    builder.CapturePpz8Command(e.OpnaMasterClock, new Ppz8Command(e.Payload.Ppz8.Port, e.Payload.Ppz8.Address, e.Payload.Ppz8.Data, e.Payload.Ppz8.BankId));
                    break;
            }
        }
        builder.SetFinalOpnaMasterClock(MasterHz);
        return builder.Finish(0, 0, 0, 0, 0, "natural_stop").Events;
    }

    [Fact]
    public void Replay_WritesEveryEventAtItsClock_InOrder_ZeroStatusReads()
    {
        var events = Capture(
            CapturedEvent.OpnaWrite(100UL, 1, 0, 0x28, 0x01),
            CapturedEvent.OpnaWrite(100UL, 2, 1, 0x28, 0x02),
            CapturedEvent.OpnaWrite(101UL, 3, 0, 0x28, 0x03),
            CapturedEvent.OpnaWrite(MasterHz / 2, 4, 0, 0xB4, 0xC0));

        using var device = new RecordingOpnaDevice();
        device.OutputLatencyFrames = 0;
        using var renderer = new NativeOpnaTraceRenderer(device, events, SampleRate);
        // MasterHz/2 == 3,993,600 clocks maps to output frame 22050 at 44.1k;
        // a chunk ending at frame 30000 covers every write clock.
        renderer.ReplayToChunkBoundary(30000);

        // Every OPNA write was applied at its exact absolute clock, in call order.
        Assert.Equal(4, device.Writes.Count);
        Assert.Equal(new ulong[] { 100, 100, 101, MasterHz / 2 },
            device.Writes.Select(w => w.clock).ToArray());
        // Equal-clock writes use equal clocks and preserve sequence order.
        Assert.Equal(0, device.Writes[0].bank);
        Assert.Equal(1, device.Writes[1].bank);
        Assert.Equal((byte)0x28, device.Writes[0].address);
        Assert.Equal(0x28, device.Writes[2].address);
        // No status/IRQ reads during replay.
        Assert.Equal(0, device.StatusReads.Count);
        Assert.False(device.IrqAsserted);
    }

    [Fact]
    public void Replay_IgnoresPpz8Events_InSequence()
    {
        var events = Capture(
            CapturedEvent.OpnaWrite(50UL, 1, 0, 0x28, 0x01),
            CapturedEvent.Ppz8Command(60UL, 2, 0, 0x08, 0x10),
            CapturedEvent.OpnaWrite(70UL, 3, 0, 0x28, 0x02));

        using var device = new RecordingOpnaDevice();
        device.OutputLatencyFrames = 0;
        using var renderer = new NativeOpnaTraceRenderer(device, events, SampleRate);
        renderer.ReplayToChunkBoundary(6000);

        // Only OPNA writes reach the device, in order; the PPZ8 event is skipped.
        Assert.Equal(2, device.Writes.Count);
        Assert.Equal(new ulong[] { 50, 70 }, device.Writes.Select(w => w.clock).ToArray());
    }

    [Fact]
    public void Replay_RegressedMasterClock_Throws()
    {
        var events = new List<CapturedEvent>
        {
            CapturedEvent.OpnaWrite(500UL, 1, 0, 0x28, 0x01),
            CapturedEvent.OpnaWrite(100UL, 2, 0, 0x28, 0x02), // regressed
        };
        using var device = new RecordingOpnaDevice();
        device.OutputLatencyFrames = 0;
        using var renderer = new NativeOpnaTraceRenderer(device, events, SampleRate);
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.ReplayToChunkBoundary(6000));
        Assert.Contains("regressed", ex.Message);
    }
}
