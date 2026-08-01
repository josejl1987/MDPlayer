using System.Text;
using Fmp.Core.Playback.Spc;
using Xunit;

// PR 3 SPC voice-state and event-capture tests (spec §30.1). They open the
// synthetic voice fixture (two voices keyed on at load), render blocks through
// the native PR 3 observer, and assert the ABI reports consistent voice state,
// emits per-block transition events, and never corrupts the master PCM.
namespace MDPlayer.Fmp.Tests.Playback.Spc;

public sealed class SpcVoiceStateTests
{
    private const string NativeLibEnvVar = SpcNativeSession.NativeLibraryEnvVar;

    [Fact]
    public void GetVoiceState_AllEightChannels_NoCrashAndValuesInRange()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using var native = SpcNativeSession.Open(VoiceFixture(), SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];

        for (int block = 0; block < 2; block++)
        {
            native.Render(stereo, SpcNativeSession.DefaultBlockFrames);
            for (int c = 0; c < SpcNativeSession.VoiceCount; c++)
            {
                SpcNativeSession.SpcVoiceState vs = native.GetVoiceState(c);
                Assert.Equal(c, vs.Channel);
                Assert.InRange(vs.Active, 0, 1);
                Assert.InRange(vs.VolumeL, 0, 255);
                Assert.InRange(vs.VolumeR, 0, 255);
                Assert.InRange(vs.Pitch, 0, 0x3FFF);
                Assert.InRange(vs.SourceNumber, 0, 255);
                Assert.InRange(vs.EnvelopeLevel, 0, 0x7FF);
                Assert.InRange(vs.BrrAddress, 0, 0xFFFF);
                Assert.InRange(vs.KonDelay, 0, 5);
                Assert.InRange(vs.EnvelopeMode,
                    (int)SpcNativeSession.SpcEnvelopeMode.Release,
                    (int)SpcNativeSession.SpcEnvelopeMode.Sustain);
            }
        }
    }

    [Fact]
    public void VoiceFlags_AgreeWithPerChannelActiveState()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using var native = SpcNativeSession.Open(VoiceFixture(), SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];
        var events = new SpcNativeSession.SpcEvent[64];

        for (int block = 0; block < 2; block++)
        {
            SpcNativeSession.SpcRenderResult r =
                native.RenderAndCapture(stereo, SpcNativeSession.DefaultBlockFrames, events);

            int recomputed = 0;
            for (int c = 0; c < SpcNativeSession.VoiceCount; c++)
                if (native.GetVoiceState(c).Active == 1)
                    recomputed |= 1 << c;

            Assert.Equal(recomputed, r.VoiceFlags);
            // The fixture key-ons voices 0 and 1 at load, so both must be
            // reported active after the first block.
            Assert.True((r.VoiceFlags & 0x03) == 0x03,
                $"expected voices 0 and 1 active, got flags 0x{r.VoiceFlags:X}");
        }
    }

    [Fact]
    public void KeyOn_EmitsVoiceActivityEvent()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using var native = SpcNativeSession.Open(VoiceFixture(), SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];
        var events = new SpcNativeSession.SpcEvent[64];

        SpcNativeSession.SpcRenderResult r =
            native.RenderAndCapture(stereo, SpcNativeSession.DefaultBlockFrames, events);

        Assert.True(r.EventsWritten >= 1, "expected at least one event after the key-on block");
        Assert.Contains(
            events.Take(r.EventsWritten),
            e => e.Type == (int)SpcNativeSession.SpcEventType.KeyOn && e.Channel == 0);
    }

    [Fact]
    public void EventOverflow_IsReportedWithoutFailure()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        using var native = SpcNativeSession.Open(VoiceFixture(), SpcNativeSession.OpenOptions.Default);
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];

        // Two voices key on in the first block -> 2 events; capacity 1 overflows.
        var one = new SpcNativeSession.SpcEvent[1];
        SpcNativeSession.SpcRenderResult r1 =
            native.RenderAndCapture(stereo, SpcNativeSession.DefaultBlockFrames, one);
        Assert.Equal(1, r1.EventsWritten);
        Assert.Equal(1, r1.EventOverflow);

        // Next block: no state changes -> no events, no overflow.
        var many = new SpcNativeSession.SpcEvent[64];
        SpcNativeSession.SpcRenderResult r2 =
            native.RenderAndCapture(stereo, SpcNativeSession.DefaultBlockFrames, many);
        Assert.Equal(0, r2.EventsWritten);
        Assert.Equal(0, r2.EventOverflow);
    }

    [Fact]
    public void Render_WithAndWithoutEventCapture_ProducesIdenticalMaster()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        byte[] fixture = VoiceFixture();

        byte[] captured;
        using (var native = SpcNativeSession.Open(fixture, SpcNativeSession.OpenOptions.Default))
            captured = RenderMaster(native, captureEvents: true);

        byte[] plain;
        using (var native = SpcNativeSession.Open(fixture, SpcNativeSession.OpenOptions.Default))
            plain = RenderMaster(native, captureEvents: false);

        Assert.Equal(plain, captured);
    }

    private static byte[] RenderMaster(SpcNativeSession native, bool captureEvents)
    {
        var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];
        var events = captureEvents ? new SpcNativeSession.SpcEvent[64] : null;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        for (int block = 0; block < 4; block++)
        {
            SpcNativeSession.SpcRenderResult r = native.RenderAndCapture(
                stereo, SpcNativeSession.DefaultBlockFrames,
                events ?? Array.Empty<SpcNativeSession.SpcEvent>());
            Assert.Equal(SpcNativeSession.DefaultBlockFrames, r.FramesRendered);
            foreach (short s in stereo)
                bw.Write(s);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Synthetic SPC whose SPC700 keys on voices 0 and 1 via $F2/$F3 at frame 0
    /// (a real KEY_ON transition from the inactive baseline), then hangs. A
    /// looping BRR square wave lives at 0x0400. The native pre-roll is disabled
    /// in open, so the baseline captures the pre-key-on state and the CPU's KON
    /// is observed as KEY_ON events during block 0 (mirrors the native parity
    /// fixture). DSP regs at file offset 0x10100, linear layout.
    /// </summary>
    private static byte[] VoiceFixture()
    {
        const int size = 0x10200;
        var spc = new byte[size];
        Encoding.ASCII.GetBytes(SpcNativeSessionSignature).CopyTo(spc, 0);
        spc[0x23] = 0x30; // format
        spc[0x24] = 1;    // version
        spc[0x25] = 0x00; // pcl -> PC = 0x0200
        spc[0x26] = 0x02; // pch
        spc[0x2B] = 0xFF; // sp

        // CPU at 0x0200: KON write via $F2/$F3, then self-branch.
        //   mov $F2,#$4C   8F 4C F2
        //   mov $F3,#$03   8F 03 F3
        // hang: bra hang   2F FE
        int pc = 0x100 + 0x0200;
        spc[pc + 0] = 0x8F; spc[pc + 1] = 0x4C; spc[pc + 2] = 0xF2;
        spc[pc + 3] = 0x8F; spc[pc + 4] = 0x03; spc[pc + 5] = 0xF3;
        spc[pc + 6] = 0x2F; spc[pc + 7] = 0xFE;

        // DIR entry for source 0 at 0x0300: start = loop = 0x0400.
        spc[0x100 + 0x0300] = 0x00;
        spc[0x100 + 0x0301] = 0x04;
        spc[0x100 + 0x0302] = 0x00;
        spc[0x100 + 0x0303] = 0x04;

        // BRR block at 0x0400: header 0xA3 (scale 10, filter 0, end+loop),
        // data 0xF0 -> alternating +max/0 samples (looping square).
        spc[0x100 + 0x0400] = 0xA3;
        for (int i = 0; i < 8; i++)
            spc[0x100 + 0x0401 + i] = 0xF0;

        // DSP registers: volumes, pitch, ADSR, DIR set; KON starts at 0 so the
        // key-on is CPU-driven (observable as a transition, spec §10.3).
        for (int v = 0; v < 2; v++)
        {
            int b = v * 0x10;
            spc[0x10100 + b + 0x00] = 0x7F; // voll
            spc[0x10100 + b + 0x01] = 0x7F; // volr
            spc[0x10100 + b + 0x02] = 0x00; // pitchl
            spc[0x10100 + b + 0x03] = 0x10; // pitchh = 0x1000
            spc[0x10100 + b + 0x04] = 0x00; // srcn
            spc[0x10100 + b + 0x05] = 0xFF; // adsr0: ADSR
            spc[0x10100 + b + 0x06] = 0xE0; // adsr1
        }
        spc[0x10100 + 0x0C] = 0x7F; // mvoll
        spc[0x10100 + 0x1C] = 0x7F; // mvolr
        spc[0x10100 + 0x4C] = 0x00; // kon: 0 -> CPU-driven key-on
        spc[0x10100 + 0x5D] = 0x03; // dir
        spc[0x10100 + 0x6C] = 0x00; // flg
        return spc;
    }

    private const string SpcNativeSessionSignature = "SNES-SPC700 Sound File Data v0.30";

    // ---- Native library helpers (mirror SpcNativeSessionTests) ----

    private static string ResolveBuiltNativeLibrary()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && !string.IsNullOrEmpty(dir); i++)
        {
            string candidate = Path.Combine(dir, "runtimes", "linux-x64", "native", SpcNativeSession.NativeLibraryFileName);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    private static string RequireNativeLibrary()
    {
        string lib = ResolveBuiltNativeLibrary();
        Assert.True(lib != null && File.Exists(lib),
            "Native SPC library not built. Run: cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build && cmake --build native/MDPlayer.SpcNative/build");
        return lib;
    }

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(NativeLibEnvVar);
        Environment.SetEnvironmentVariable(NativeLibEnvVar, path);
        return new RestoreEnv(NativeLibEnvVar, previous);
    }

    private sealed class RestoreEnv : IDisposable
    {
        private readonly string _name;
        private readonly string _previous;

        public RestoreEnv(string name, string previous)
        {
            _name = name;
            _previous = previous;
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
