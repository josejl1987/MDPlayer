using Fmp.Core.Nise98;
using Fmp.Core.Rendering;
using MDPlayer.Fmp.Tests.Clocked;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 Pass-2 native replay tests: exact event replay order against a
/// recording fake device, zero status/IRQ reads during replay, exact
/// CPU-cycle→OPNA-cloack mapping against an independent UInt128 oracle, and the
/// unsupported-cadence failure model.
/// </summary>
public class NativeOpnaTraceRendererTests
{
    private const ulong CpuHz = 8_000_000;
    private const ulong MasterHz = 7_987_200;

    private static ulong OracleOpnaClock(ulong cpuCycle) =>
        (ulong)((UInt128)cpuCycle * MasterHz / CpuHz);

    private static long OracleOutputFrame(ulong cpuCycle, int sampleRate) =>
        (long)((UInt128)cpuCycle * (ulong)sampleRate / CpuHz);

    private static ulong OracleFrameToCpuCeiling(long frame, int sampleRate) =>
        (ulong)(((UInt128)(ulong)frame * CpuHz + (ulong)sampleRate - 1) / (ulong)sampleRate);

    [Fact]
    public void Mapper_OpnaClock_MatchesUInt128Oracle()
    {
        var mapper = new NiseOpnaClockMapper((uint)CpuHz);
        var points = new ulong[]
        {
            0, 1, CpuHz - 1, CpuHz, CpuHz + 1,
            60 * CpuHz, /* 1 minute */
            10 * 60 * CpuHz, /* 10 minutes */
            60 * 60 * CpuHz, /* 1 hour */
        };
        foreach (var c in points)
            Assert.Equal(OracleOpnaClock(c), mapper.Map(c));
    }

    [Fact]
    public void Mapper_OpnaClock_IncrementAndJump_ConsistentAndMonotonic()
    {
        var mapper = new NiseOpnaClockMapper((uint)CpuHz);
        // one-cycle increments from 0 (mapper is monotonic: only ascending)
        for (ulong c = 0; c <= 64; c++)
            Assert.Equal(OracleOpnaClock(c), mapper.Map(c));
        // one large jump from the last mapped position
        Assert.Equal(OracleOpnaClock(1_000_000_000UL), mapper.Map(1_000_000_000UL));
        // irregular deterministic increments
        ulong acc = 1_000_000_000UL;
        foreach (var step in new ulong[] { 17, 3, 4095, 1, 65536, 11 })
        {
            acc += step;
            Assert.Equal(OracleOpnaClock(acc), mapper.Map(acc));
        }
        // equal-cycle events map to equal clocks (idempotent on the same value)
        Assert.Equal(OracleOpnaClock(acc), mapper.Map(acc));
    }

    [Fact]
    public void Mapper_OutputFrame_MatchesOracle()
    {
        foreach (var sr in new[] { 44100, 48000, 96000 })
        {
            var mapper = new CpuToOutputFrameMapper(CpuHz, sr);
            foreach (var c in new ulong[]
            {
                0, 1, CpuHz - 1, CpuHz, CpuHz + 1, 60 * CpuHz, 600 * CpuHz,
            })
                Assert.Equal(OracleOutputFrame(c, sr), mapper.MapOutputFrame(c));
            foreach (var f in new long[] { 0, 1, 44100, 100000, 1_000_000 })
                Assert.Equal(OracleFrameToCpuCeiling(f, sr), mapper.MapFrameToCpuCeiling(f));
        }
    }

    private static IReadOnlyList<FmpCapturedEvent> Capture(params FmpCapturedEvent[] events)
    {
        var builder = new FmpExecutionCaptureBuilder(44100, CpuHz);
        foreach (var e in events)
        {
            switch (e)
            {
                case CapturedOpnaWrite w:
                    builder.CaptureOpnaWrite(w.CpuCycle, w.Port, w.Address, w.Data);
                    break;
                case CapturedPpz8Command c:
                    builder.CapturePpz8Command(c.CpuCycle, new Ppz8Command(c.Port, c.Address, c.Data, c.BankId));
                    break;
            }
        }
        builder.SetFinalCpuCycle(1_000_000UL);
        return builder.Finish(0, 0, 0, 0, 0, "natural_stop").Events;
    }

    [Fact]
    public void Replay_WritesEveryEventAtMappedClock_InOrder_ZeroStatusReads()
    {
        var events = Capture(
            new CapturedOpnaWrite(100UL, 1, 0, 0x28, 0x01),
            new CapturedOpnaWrite(100UL, 2, 1, 0x28, 0x02),
            new CapturedOpnaWrite(101UL, 3, 0, 0x28, 0x03),
            new CapturedOpnaWrite(1_000_000UL, 4, 0, 0xB4, 0xC0));

        using var device = new RecordingOpnaDevice();
        using var renderer = new NativeOpnaTraceRenderer(device, events, CpuHz, 44100);
        renderer.ReplayToChunkBoundary(6000); // covers cycle 1,000,000

        // Every OPNA write was applied at its exact mapped clock, in call order.
        Assert.Equal(4, device.Writes.Count);
        Assert.Equal(new ulong[] { OracleOpnaClock(100), OracleOpnaClock(100), OracleOpnaClock(101), OracleOpnaClock(1_000_000) },
            device.Writes.Select(w => w.clock).ToArray());
        // Equal-cycle writes use equal clocks and preserve sequence order.
        Assert.Equal(OracleOpnaClock(100), device.Writes[0].clock);
        Assert.Equal(OracleOpnaClock(100), device.Writes[1].clock);
        Assert.Equal(0, device.Writes[0].bank);
        Assert.Equal(1, device.Writes[1].bank);
        Assert.Equal((byte)0x28, device.Writes[0].address);
        Assert.Equal((byte)0x01, device.Writes[0].value);

        // No control-plane calls during replay.
        Assert.Empty(device.StatusReads);
        Assert.Empty(device.ClearAdpcmCalls);
    }

    [Fact]
    public void Replay_UnsupportedCadenceWrite_ThrowsNotSupported()
    {
        foreach (var addr in new byte[] { 0x2E, 0x2F })
        {
            var events = Capture(new CapturedOpnaWrite(200UL, 1, 0, addr, 0x00));
            using var device = new RecordingOpnaDevice();
            using var renderer = new NativeOpnaTraceRenderer(device, events, CpuHz, 44100);
            var ex = Assert.Throws<NotSupportedException>(() => renderer.ReplayToChunkBoundary(8));
            Assert.Contains("0x" + addr.ToString("X2"), ex.Message);
            Assert.Contains(OracleOpnaClock(200).ToString(), ex.Message);
            // Nothing after the failure: no fallback, no silent event removal.
            Assert.Empty(device.Writes);
        }
    }

    [Fact]
    public void Replay_SupportedCadenceWrite2D_DoesNotThrow()
    {
        var events = Capture(new CapturedOpnaWrite(200UL, 1, 0, 0x2D, 0x00));
        using var device = new RecordingOpnaDevice();
        using var renderer = new NativeOpnaTraceRenderer(device, events, CpuHz, 44100);
        renderer.ReplayToChunkBoundary(8); // the fixed 144-clock cadence is supported
        Assert.Single(device.Writes);
    }

    [Fact]
    public void Replay_RegressedCapture_Throws()
    {
        // Build an out-of-order event list directly (bypassing the builder's own
        // Finish validation) to prove the renderer independently rejects it.
        var events = new List<FmpCapturedEvent>
        {
            new CapturedOpnaWrite(500UL, 1, 0, 0x28, 0x01),
            new CapturedOpnaWrite(100UL, 2, 0, 0x28, 0x02), // regressed
        };
        using var device = new RecordingOpnaDevice();
        using var renderer = new NativeOpnaTraceRenderer(device, events, CpuHz, 44100);
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.ReplayToChunkBoundary(6000));
        Assert.Contains("regressed", ex.Message);
    }
}
