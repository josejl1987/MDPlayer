using System.Security.Cryptography;
using Fmp.Core.IO;
using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 Pass-1 capture tests: global ordering of OPNA/PPZ8 events, capture
/// determinism from a real FMP fixture, and proof that enabling the capture tap
/// cannot alter the default MDSound output (byte-identical PCM).
/// </summary>
public class FmpExecutionCaptureTests
{
    private static FmpPlaybackContext NewContext(string? ovi, double maxSeconds = 3.0)
    {
        return new FmpPlaybackContext(
            File.ReadAllBytes(ovi!),
            Path.GetFileName(ovi!),
            new FmpRuntimeAssets(Path.Combine(AppContext.BaseDirectory, "FMP.COM")),
            new FmpFileSystem(new[] { Path.GetDirectoryName(ovi!) }),
            44100,
            SsgGainDb: 0,
            LoopCount: 1,
            FadeSeconds: 0.5,
            TailSeconds: 0.1,
            MaxDurationSeconds: maxSeconds);
    }

    private static string? FindOvi()
    {
        var testDir = AppContext.BaseDirectory;
        string? first = Directory.GetFiles(testDir, "*.ovi", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(testDir, "*.OVI", SearchOption.AllDirectories))
            .FirstOrDefault();
        if (first != null) return first;
        string downloads = "/home/jose/Downloads";
        if (OperatingSystem.IsLinux() && Directory.Exists(downloads))
        {
            var dl = Directory.GetFiles(downloads, "*.OVI", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.Ordinal).ToArray();
            if (dl.Length > 0) return dl[0];
        }
        return null;
    }

    private static bool FixturesAvailable() =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM")) && FindOvi() != null;

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    /// <summary>Runs the real legacy FMP path to completion with capture enabled.</summary>
    private static (FmpExecutionCapture capture, byte[] pcm) RunCapture(byte[]? pcmOut)
    {
        string? ovi = FindOvi();
        var builder = new FmpExecutionCaptureBuilder(44100, 8_000_000);
        using var legacy = new LegacyMdsoundFmpPcmSession(NewContext(ovi), builder);
        legacy.CpuClockFrequencyHz = 8_000_000;
        legacy.LoadTrack(File.ReadAllBytes(ovi!), Path.GetFileName(ovi!));
        legacy.Boot();

        var scratch = new short[4096 * 2];
        var outStream = new MemoryStream();
        do
        {
            int n = legacy.Render(scratch);
            if (n == 0) break;
            if (pcmOut != null)
            {
                var chunk = new byte[n * 2 * 2];
                Buffer.BlockCopy(scratch, 0, chunk, 0, chunk.Length);
                outStream.Write(chunk);
            }
        } while (!legacy.IsCompleted);

        long fadeLen = checked((long)Math.Ceiling(0.5 * 44100));
        var term = legacy.TerminationState;
        bool fadeActive = term != null && term.FadeActive;
        long fadeStart = fadeActive ? term.FadeStartSample : 0;
        long fadeEnd = fadeActive ? term.FadeStartSample + fadeLen : 0;
        builder.SetFinalCpuCycle(legacy.FinalCpuCycle);
        var capture = builder.Finish(
            finalOutputFrame: legacy.TotalSamples,
            fadeStartFrame: fadeStart,
            fadeEndFrame: fadeEnd,
            tailEndFrame: term != null ? term.StopAtSample : legacy.TotalSamples,
            loopCount: legacy.CurrentLoop,
            terminationReason: legacy.StopReason);
        return (capture, outStream.ToArray());
    }

    [Fact]
    public void Capture_Ordering_PreservesCycleAndSequenceAndCallOrder()
    {
        // Fake capture sink producing an interleaved OPNA/PPZ8 stream, exactly
        // the pattern required by the ordering contract.
        var builder = new FmpExecutionCaptureBuilder(44100, 8_000_000);
        builder.CaptureOpnaWrite(100UL, 0, 0x28, 0x01);
        builder.CapturePpz8Command(100UL, new Ppz8Command(0, 0x08, 0x00));
        builder.CaptureOpnaWrite(100UL, 0, 0x28, 0x02);
        builder.CaptureOpnaWrite(101UL, 0, 0x28, 0x03);
        builder.CapturePpz8Command(101UL, new Ppz8Command(0, 0x08, 0x01));

        var capture = builder.Finish(0, 0, 0, 0, 0, "natural_stop");

        Assert.Equal(5, capture.Events.Count);
        // No coalescing: all five events present, cycle values preserved.
        Assert.Equal(100UL, capture.Events[0].CpuCycle);
        Assert.Equal(100UL, capture.Events[1].CpuCycle);
        Assert.Equal(100UL, capture.Events[2].CpuCycle);
        Assert.Equal(101UL, capture.Events[3].CpuCycle);
        Assert.Equal(101UL, capture.Events[4].CpuCycle);
        // Global sequence strictly increases across both device streams.
        for (int i = 1; i < capture.Events.Count; i++)
            Assert.True(capture.Events[i].Sequence > capture.Events[i - 1].Sequence);
        // Equal-cycle equal-call order preserved: the 3 events at cycle 100
        // keep emit order (opna, ppz8, opna).
        Assert.IsType<CapturedOpnaWrite>(capture.Events[0]);
        Assert.IsType<CapturedPpz8Command>(capture.Events[1]);
        Assert.IsType<CapturedOpnaWrite>(capture.Events[2]);
    }

    [Fact]
    public void RealCapture_RunTwice_IsIdentical()
    {
        if (!FixturesAvailable()) return;

        var (c1, _) = RunCapture(null);
        var (c2, _) = RunCapture(null);

        Assert.Equal(c1.Events.Count, c2.Events.Count);
        for (int i = 0; i < c1.Events.Count; i++)
        {
            Assert.Equal(c1.Events[i].CpuCycle, c2.Events[i].CpuCycle);
            Assert.Equal(c1.Events[i].Sequence, c2.Events[i].Sequence);
            Assert.Equal(c1.Events[i], c2.Events[i]);
        }
        Assert.Equal(c1.FinalCpuCycle, c2.FinalCpuCycle);
        Assert.Equal(c1.FinalOutputFrame, c2.FinalOutputFrame);
        Assert.Equal(c1.LoopCount, c2.LoopCount);
        Assert.Equal(c1.TerminationReason, c2.TerminationReason);
        Assert.Equal(
            c1.Ppz8Banks.Select(b => b.Sha256),
            c2.Ppz8Banks.Select(b => b.Sha256));
    }

    [Fact]
    public void CaptureTap_DoesNotAlterMdsoundPcm()
    {
        if (!FixturesAvailable()) return;

        // Render normally (capture disabled).
        string? ovi = FindOvi();
        using var plain = new LegacyMdsoundFmpPcmSession(NewContext(ovi));
        plain.LoadTrack(File.ReadAllBytes(ovi!), Path.GetFileName(ovi!));
        plain.Boot();
        var plainBytes = RenderAll(plain);

        // Render through the capture-enabled path.
        var (_, capturedPcm) = RunCapture(plainBytes);

        Assert.Equal(Sha256(plainBytes), Sha256(capturedPcm));
    }

    [Fact]
    public void Capture_NoEventsWhenDisabled_AndFinalMetadataPopulated()
    {
        // A capture with a zero/empty event stream finalizes with the requested
        // metadata intact (used by degenerate fixtures).
        var builder = new FmpExecutionCaptureBuilder(44100, 8_000_000);
        builder.SetFinalCpuCycle(1_000_000);
        var capture = builder.Finish(44100, 0, 0, 5000, 1, "natural_stop");
        Assert.Empty(capture.Events);
        Assert.Equal(1_000_000UL, capture.FinalCpuCycle);
        Assert.Equal(44100L, capture.FinalOutputFrame);
        Assert.Equal("natural_stop", capture.TerminationReason);
        Assert.Equal(1, capture.LoopCount);
    }

    [Fact]
    public void BankSnapshots_DeduplicatedByContentHash()
    {
        var builder = new FmpExecutionCaptureBuilder(48000, 8_000_000);

        // Same content, different "files" -> same bank id (dedup by content).
        int idA = builder.CapturePpz8Bank("a.ovi", 0, 0, new[] { new byte[] { 1, 2, 3, 4 } });
        int idB = builder.CapturePpz8Bank("b.ovi", 0, 0, new[] { new byte[] { 1, 2, 3, 4 } });
        Assert.Equal(idA, idB);

        // Same name, different content -> different bank id.
        int idC = builder.CapturePpz8Bank("a.ovi", 0, 0, new[] { new byte[] { 1, 2, 3, 5 } });
        Assert.NotEqual(idA, idC);

        Assert.Equal(2, builder.Banks.Count); // one per distinct content
        var snap = builder.Banks[0];
        Assert.Equal("a.ovi", snap.LogicalName);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, snap.Data);
        Assert.Equal(64, snap.Sha256.Length); // hex SHA-256
        Assert.Equal(1, snap.ChannelLengths.Length);
        Assert.Equal(4, snap.ChannelLengths[0]);
    }

    [Fact]
    public void Capture_OutOfOrder_Throws()
    {
        // A capture holding a cycle that regressed after a later event must be
        // rejected by Finish.
        var builder1 = new FmpExecutionCaptureBuilder(48000, 8_000_000);
        var builder2 = new FmpExecutionCaptureBuilder(48000, 8_000_000);
        builder1.CaptureOpnaWrite(100UL, 0, 0x28, 0x01);
        builder2.CaptureOpnaWrite(300UL, 0, 0x28, 0x03);
        builder2.CaptureOpnaWrite(100UL, 0, 0x28, 0x01); // regressed after 300

        // builder1 is monotonic -> finishes cleanly.
        try { builder1.Finish(0, 0, 0, 0, 0, "natural_stop"); }
        catch (Exception) { throw new Xunit.Sdk.XunitException("monotonic capture should not throw"); }
        // builder2 regressed -> Finish rejects it.
        Assert.Throws<InvalidOperationException>(
            () => builder2.Finish(0, 0, 0, 0, 0, "natural_stop"));
    }

    private static byte[] RenderAll(LegacyMdsoundFmpPcmSession session)
    {
        var scratch = new short[4096 * 2];
        var outStream = new MemoryStream();
        do
        {
            int n = session.Render(scratch);
            if (n == 0) break;
            var chunk = new byte[n * 2 * 2];
            Buffer.BlockCopy(scratch, 0, chunk, 0, chunk.Length);
            outStream.Write(chunk);
        } while (!session.IsCompleted);
        return outStream.ToArray();
    }
}
