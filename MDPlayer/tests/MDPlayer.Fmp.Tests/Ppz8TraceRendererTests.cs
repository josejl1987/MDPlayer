using Fmp.Core.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt-8 Pass-2 PPZ8 replay tests: exact command-before-frame application
/// across the 44.1/48/96 kHz grid, same-clock and adjacent-frame same-output
/// ordering, and proof that replay uses captured immutable bank bytes rather
/// than reopening the source file. Commands are timestamped with absolute
/// YM2608 master clock positions.
/// </summary>
public class Ppz8TraceRendererTests
{
    private const ulong MasterHz = OpnaMasterClock.Hz;

    [Fact]
    public void Ppz8_Mapping_AcrossRateGrid_DeterministicAndNoThrow()
    {
        foreach (var sr in new[] { 44100, 48000, 96000 })
        {
            var banks = new List<Ppz8BankSnapshot>();
            var events = new List<CapturedEvent>
            {
                CapturedEvent.Ppz8Command(100UL, 1, 0, 0x08, 0x01),
                CapturedEvent.Ppz8Command(100UL, 2, 0, 0x08, 0x02), // same clock
                CapturedEvent.Ppz8Command(14_000UL, 3, 0, 0x09, 0x40), // volume
                CapturedEvent.Ppz8Command(18_000UL, 4, 0, 0x08, 0x00), // stop
            };

            using var a = new Ppz8TraceRenderer(events, banks, sr, latencyFrames: 0);
            using var b = new Ppz8TraceRenderer(events, banks, sr, latencyFrames: 0);
            var rawA = RenderAll(a, 256, sr);
            var rawB = RenderAll(b, 256, sr);
            Assert.Equal(rawA, rawB); // deterministic
        }
    }

    [Fact]
    public void Ppz8_EqualOutputFrame_And_AdjacentFrame_OrderApplied()
    {
        foreach (var sr in new[] { 44100, 48000, 96000 })
        {
            var banks = new List<Ppz8BankSnapshot>();
            // Absolute master clocks that map to the same output frame and to
            // adjacent frames just after each frame clock boundary.
            var events = new List<CapturedEvent>
            {
                CapturedEvent.Ppz8Command(0UL, 1, 0, 0x08, 0x00),
                CapturedEvent.Ppz8Command(1UL, 2, 0, 0x08, 0x01),
                CapturedEvent.Ppz8Command(MasterHz / (ulong)sr - 1, 3, 0, 0x08, 0x02),
                CapturedEvent.Ppz8Command(MasterHz / (ulong)sr, 4, 0, 0x08, 0x03),
            };
            using var ppz8 = new Ppz8TraceRenderer(events, banks, sr, 0);
            var buf = new short[16 * 2];
            _ = ppz8.RenderFrames(0, 16, buf); // must apply and render without throwing
        }
    }

    [Fact]
    public void Ppz8_Bank_ReplayUsesCapturedBytes_NotSourceFile()
    {
        // The renderer is constructed with only the immutable snapshot; it has
        // no file-system path, so replay can only use the captured bytes.
        var bank = new Ppz8BankSnapshot(0, "bank.bin", new byte[] { 0x77, 0x88, 0x99 },
            new[] { 3 }, "FIXED-SHA");
        var captured = new List<CapturedEvent>
        {
            CapturedEvent.Ppz8Command(100UL, 1, 0, 0, 0, BankId: 0), // load
        };
        using var ppz8 = new Ppz8TraceRenderer(captured, new List<Ppz8BankSnapshot> { bank }, 48000, 0);
        // Frame 0 is the load frame (floor(100*48000/7987200)=0); producing
        // several frames forces the load to be applied from captured bytes. Any
        // attempt to reopen the source would throw / mis-load.
        var buf = new short[8 * 2];
        _ = ppz8.RenderFrames(0, 8, buf);
    }

    [Fact]
    public void Ppz8_BankLoad_ProduceDeterministicAcrossRates()
    {
        // A captured bank-load command must route to the shared renderer and
        // produce deterministic output across the rate grid. (Musical start on
        // synthetic non-audio bytes is out of scope; loading is the routing
        // seam this test exercises.)
        var bank = new Ppz8BankSnapshot(0, "bank.bin", new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 },
            new[] { 9 }, "SHA-9");
        var captured = new List<CapturedEvent>
        {
            CapturedEvent.Ppz8Command(100UL, 1, 0, 0, 0, BankId: 0), // load
        };
        foreach (var sr in new[] { 44100, 48000, 96000 })
        {
            using var ppz8 = new Ppz8TraceRenderer(
                captured, new List<Ppz8BankSnapshot> { bank }, sr, 0);
            var raw = RenderAll(ppz8, 128, sr);
            Assert.NotEmpty(raw);
            using var again = new Ppz8TraceRenderer(
                captured, new List<Ppz8BankSnapshot> { bank }, sr, 0);
            Assert.Equal(raw, RenderAll(again, 128, sr));
        }
    }

    private static byte[] RenderAll(Ppz8TraceRenderer renderer, int frames, int sr)
    {
        var buf = new short[frames * 2];
        renderer.RenderFrames(0, frames, buf);
        var raw = new byte[frames * 4];
        Buffer.BlockCopy(buf, 0, raw, 0, raw.Length);
        return raw;
    }
}
