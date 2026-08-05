using Fmp.Core.IO;
using Fmp.Core.Rendering;
using MDPlayer.Fmp.Tests.Clocked;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8R.2 master-clock timeline regression tests that were not covered by
/// the Pass-1/Pass-2 suites:
///
///  * Part 9  — final capture clock vs an independent duration oracle
///    (1s / 12s / 20s / 60s) using integer arithmetic only.
///  * Part 10 — driver-call boundaries cannot collapse the timeline: the
///    capture clock strictly increases across real driver invocations even
///    though Nise286 CPU cycles reset per call.
///  * Part 11 — capture is independent of the legacy render block size
///    (1 / 7 / 64 / 257 / 1024 / 4096) for both event stream and final clock.
///  * Part 12 — replay cursor progresses through the track, not consumed
///    during the opening region.
///  * Part 13 — key-on (reg 0x28) and its later key-off stay temporally
///    separated on the absolute master clock and replay at those clocks.
///
/// The time coordinate under test is always the absolute YM2608 master clock
/// accumulated by FmpRuntime at the control tick rate (7,987,200 Hz integer
/// quotient/remainder) — never Nise286 cycles.
/// </summary>
public class MasterClockTimelineTests
{
    private const int SampleRate = 44100;
    private const ulong MasterHz = 7_987_200;

    // ---------------------------------------------------------------- helpers

    private static FmpPlaybackContext NewContext(string? ovi, double maxSeconds)
    {
        return new FmpPlaybackContext(
            File.ReadAllBytes(ovi!),
            Path.GetFileName(ovi!),
            new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi!) }),
            SampleRate,
            SsgGainDb: 0,
            LoopCount: 1,
            FadeSeconds: 0.5,
            TailSeconds: 0.1,
            MaxDurationSeconds: maxSeconds);
    }

    private static string? FindOvi()
    {
        var testDir = AppContext.BaseDirectory;
        var all = Directory.GetFiles(testDir, "*.ovi", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(testDir, "*.OVI", SearchOption.AllDirectories))
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        if (all.Length > 0) return all[0];
        if (OperatingSystem.IsLinux() && Directory.Exists("/home/jose/Downloads"))
            return Directory.GetFiles("/home/jose/Downloads", "*.OVI", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
        return null;
    }

    private static bool FixturesAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM")) && FindOvi() != null;

    /// <summary>Runs the real legacy FMP path to completion with capture on.</summary>
    private static FmpExecutionCapture RunCaptureBlock(double maxSeconds, int blockFrames)
    {
        string? ovi = FindOvi();
        var builder = new FmpExecutionCaptureBuilder(SampleRate);
        using var legacy = new LegacyMdsoundFmpPcmSession(NewContext(ovi, maxSeconds), builder);
        legacy.CpuClockFrequencyHz = 8_000_000;
        legacy.LoadTrack(File.ReadAllBytes(ovi!), Path.GetFileName(ovi!));
        legacy.Boot();

        var scratch = new short[blockFrames * 2];
        do
        {
            int n = legacy.Render(scratch);
            if (n == 0) break;
        } while (!legacy.IsCompleted);

        long fadeLen = checked((long)Math.Ceiling(0.5 * SampleRate));
        var term = legacy.TerminationState;
        bool fadeActive = term != null && term.FadeActive;
        long fadeStart = fadeActive ? term.FadeStartSample : 0;
        long fadeEnd = fadeActive ? term.FadeStartSample + fadeLen : 0;
        builder.SetFinalOpnaMasterClock(legacy.FinalOpnaMasterClock);
        return builder.Finish(
            finalOutputFrame: legacy.TotalSamples,
            fadeStartFrame: fadeStart,
            fadeEndFrame: fadeEnd,
            tailEndFrame: term != null ? term.StopAtSample : legacy.TotalSamples,
            loopCount: legacy.CurrentLoop,
            terminationReason: legacy.StopReason);
    }

    private static string CanonicalEvents(FmpExecutionCapture c)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var e in c.Events)
        {
            sb.Append(e.OpnaMasterClock).Append(':').Append(e.Sequence).Append(':');
            switch (e)
            {
                case CapturedOpnaWrite w:
                    sb.Append('O').Append(w.Port).Append('/').Append(w.Address)
                      .Append('/').Append(w.Data);
                    break;
                case CapturedPpz8Command p:
                    sb.Append("P").Append(p.Port).Append('/').Append(p.Address)
                      .Append('/').Append(p.Data).Append('/').Append(p.BankId);
                    break;
            }
            sb.Append(';');
        }
        sb.Append("|final=").Append(c.FinalOpnaMasterClock)
          .Append("|out=").Append(c.FinalOutputFrame);
        return sb.ToString();
    }

    // --------------------------------------------------------- Part 9 oracle

    [Theory]
    [InlineData(1.0)]
    [InlineData(12.0)]
    [InlineData(20.0)]
    [InlineData(60.0)]
    public void FinalMasterClock_MatchesIndependentDurationOracle(double maxSeconds)
    {
        if (!FixturesAvailable()) return;

        var capture = RunCaptureBlock(maxSeconds, 4096);
        Assert.True(capture.FinalOutputFrame > 0, "no output frames were captured");

        // Independent integer oracle: expected = floor(F * Hz / R).
        UInt128 expected = (UInt128)((ulong)capture.FinalOutputFrame) * MasterHz
                           / (ulong)SampleRate;
        ulong expectedUlong = (ulong)expected;

        // The accumulated clock advances once per output frame; the oracle and
        // the accumulator share the same integer law, so they agree to the
        // final-advance convention (equal, or one control tick's worth where a
        // tick fires for the final frame the session does not count as output).
        ulong oneTick = MasterHz / (ulong)SampleRate;
        ulong lo = expectedUlong > oneTick ? expectedUlong - oneTick : 0;
        ulong hi = expectedUlong + oneTick;
        Assert.InRange(capture.FinalOpnaMasterClock, lo, hi);

        // When the fixture actually fills the requested duration window, the
        // final clock must land in the real-time range for that window — never
        // clustered near the opening. (A song that ends before the cap stops at
        // its true end; the oracle relation above still governs the clock.)
        long capFrames = (long)(maxSeconds * (double)SampleRate);
        if (capture.FinalOutputFrame >= capFrames - SampleRate)
        {
            Assert.True(capture.FinalOpnaMasterClock > (ulong)(maxSeconds * 0.9) * MasterHz,
                $"final master clock {capture.FinalOpnaMasterClock} is below ~0.9× the requested "
                + $"{maxSeconds}s timeline despite the fixture filling the window");
        }
    }

    // ---------------------------------------------------------- Part 10 reset

    [Fact]
    public void DriverCallBoundaries_DoNotCollapseTimeline_RealRuntime()
    {
        if (!FixturesAvailable()) return;

        // Drive the REAL runtime through many driver invocations. Each Tick()
        // runs the FMP driver (timer interrupt → oneFrameProc) whose Nise286
        // instruction-cycle counter restarts from a small per-call value; were
        // events timestamped by those per-call cycles the whole event stream
        // would collapse toward zero. They must instead land at strictly
        // increasing absolute master clocks tracking the control ticks.
        string? ovi = FindOvi();
        var builder = new FmpExecutionCaptureBuilder(SampleRate);
        var legacy = new LegacyMdsoundFmpPcmSession(NewContext(ovi, 3.0), builder);
        legacy.CpuClockFrequencyHz = 8_000_000;
        legacy.LoadTrack(File.ReadAllBytes(ovi!), Path.GetFileName(ovi!));
        legacy.Boot();

        var scratch = new short[64 * 2]; // small blocks => many Render boundaries
        do
        {
            int n = legacy.Render(scratch);
            if (n == 0) break;
        } while (!legacy.IsCompleted);

        long fadeLen = checked((long)Math.Ceiling(0.5 * SampleRate));
        var term = legacy.TerminationState;
        bool fadeActive = term != null && term.FadeActive;
        builder.SetFinalOpnaMasterClock(legacy.FinalOpnaMasterClock);
        var capture = builder.Finish(
            finalOutputFrame: legacy.TotalSamples,
            fadeStartFrame: fadeActive ? term!.FadeStartSample : 0,
            fadeEndFrame: fadeActive ? term.FadeStartSample + fadeLen : 0,
            tailEndFrame: term != null ? term.StopAtSample : legacy.TotalSamples,
            loopCount: legacy.CurrentLoop,
            terminationReason: legacy.StopReason);

        // Clock must be monotonic and must span real time (not per-call resets).
        ulong prev = 0;
        int writers = 0;
        foreach (var e in capture.Events)
        {
            Assert.True(e.OpnaMasterClock >= prev,
                $"event at sequence {e.Sequence} regressed: {e.OpnaMasterClock} < {prev}");
            if (e is CapturedOpnaWrite) writers++;
            prev = e.OpnaMasterClock;
        }
        Assert.True(writers > 0, "no OPNA writes captured across driver invocations");
        Assert.True(prev > MasterHz / 2,
            $"final event clock {prev} is below ~0.5s; the timeline collapsed under "
            + "per-driver-call cycle resets");
    }

    // ------------------------------------------------- Part 11 capture blocks

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(257)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void Capture_IsBlockSizeIndependent(int blockFrames)
    {
        if (!FixturesAvailable()) return;

        FmpExecutionCapture a = RunCaptureBlock(3.0, blockFrames);
        FmpExecutionCapture b = RunCaptureBlock(3.0, 4096);

        Assert.Equal(CanonicalEvents(a), CanonicalEvents(b));
        Assert.Equal(a.Events.Count, b.Events.Count);
        Assert.Equal(a.FinalOpnaMasterClock, b.FinalOpnaMasterClock);
        Assert.Equal(a.FinalOutputFrame, b.FinalOutputFrame);
        // PPZ8 bank snapshots (single-track corpus) are unchanged.
        Assert.Equal(a.Ppz8Banks.Count, b.Ppz8Banks.Count);
    }

    // ------------------------------------------------------ Part 12 cursor

    [Fact]
    public void ReplayCursor_ProgressesAcrossTrack_NotConsumedAtStart()
    {
        const int sr = 44100;
        // A realistic multi-second capture: clusters of writes at 0.5s, 1s,
        // 5s, 10s and the final frame (60s), each a key write + a note write.
        var events = new List<FmpCapturedEvent>();
        ulong seq = 0;
        foreach ((double seconds, byte data) in new[]
                 {
                     (0.5, (byte)0x01),
                     (1.0, (byte)0x03),
                     (5.0, (byte)0x07),
                     (10.0, (byte)0x0F),
                     (60.0, (byte)0x1F),
                 })
        {
            ulong clock = (ulong)((decimal)seconds * MasterHz);
            events.Add(new CapturedOpnaWrite(clock, ++seq, 0, 0x28, (byte)(0x01)));
            events.Add(new CapturedOpnaWrite(clock + 1, ++seq, 0, 0xA4, data));
        }

        using var device = new RecordingOpnaDevice();
        device.OutputLatencyFrames = 0;
        using var renderer = new NativeOpnaTraceRenderer(device, events, sr);

        // Progressively advance through the track; the cursor must follow the
        // frames, never jumping to the end during the opening region.
        long[] frames = { (long)(0.25 * sr), (long)(0.75 * sr), (long)(2.0 * sr),
                          (long)(7.0 * sr), (long)(30.0 * sr), 60L * sr };
        var cursors = new List<int>();
        foreach (long f in frames)
        {
            renderer.ReplayToChunkBoundary(f + 1);
            cursors.Add(renderer.EventCursor);
        }

        // Each checkpoint consumes exactly the events whose clock landed in the
        // covered range; the cursor strictly grows and does NOT reach the end
        // until the final interval.
        for (int i = 1; i < cursors.Count; i++)
            Assert.True(cursors[i] > cursors[i - 1],
                $"cursor did not progress between checkpoint {i - 1} and {i}: "
                + $"{cursors[i - 1]} → {cursors[i]}");
        Assert.True(cursors[0] < events.Count, "complete event stream consumed during the opening region");
        Assert.Equal(events.Count, cursors[^1]); // reached exactly once at the end
        Assert.Equal(events.Count, renderer.EventCursor);
    }

    // ----------------------------------------------------- Part 13 key on/off

    [Fact]
    public void KeyOn_KeyOff_StayTemporallySeparated_AndReplayAtClocks()
    {
        // YM2608 key-on (0x28 data bit4 set) then a later key-off (bit4 clear)
        // for the same slot, separated by a meaningful real-time interval.
        ulong keyOn = (ulong)(1.5 * MasterHz);          // 1.5s
        ulong keyOff = keyOn + (ulong)(0.7 * MasterHz); // +0.7s later

        var events = new List<FmpCapturedEvent>
        {
            new CapturedOpnaWrite(keyOn, 1, 0, 0x28, 0x10),   // key-on channel 0
            new CapturedOpnaWrite(keyOn + 1, 2, 1, 0x28, 0x12),
            new CapturedOpnaWrite(keyOff, 3, 0, 0x28, 0x00),  // key-off
        };

        using var device = new RecordingOpnaDevice();
        device.OutputLatencyFrames = 0;
        using var renderer = new NativeOpnaTraceRenderer(device, events, SampleRate);
        renderer.ReplayToChunkBoundary(3L * SampleRate); // past key-off at 2.2s

        // Raw register semantics: the 0x28 writes replay at their exact
        // absolute clocks, key-off strictly after key-on.
        var writes = device.Writes.Where(w => w.address == 0x28).ToArray();
        Assert.Equal(3, writes.Length);
        Assert.Equal(new ulong[] { keyOn, keyOn + 1, keyOff },
            writes.Select(w => w.clock).ToArray());
        Assert.Equal(new byte[] { 0x10, 0x12, 0x00 }, writes.Select(w => w.value).ToArray());
        Assert.True(writes[2].clock - writes[0].clock >= (ulong)(0.6 * MasterHz),
            "key-off is not meaningfully separated from key-on on the absolute master clock");
    }
}
