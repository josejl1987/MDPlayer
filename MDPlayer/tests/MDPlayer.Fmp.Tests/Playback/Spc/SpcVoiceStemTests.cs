using System.Text;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Spc;

/// <summary>
/// PR 6 SPC voice/echo stem export. Renders the synthetic CPU-keyed SPC (the
/// managed twin of the native tap_test fixture: the SPC700 keys on voices 0
/// and 1, EON is 0 and the echo volumes are 0) through
/// <see cref="SpcPlaybackBackend"/> with <see cref="PlaybackOptions.WriteSpcStems"/>.
/// Asserts the stem layout (master.wav + voice-01..08.wav + echo.wav, all the
/// same duration), that voices 0/1 are non-silent while voices 2..7 and the
/// echo are silent, and that the master is bit-identical to a stems-disabled
/// render (§5.3/§30.1).
/// </summary>
public sealed class SpcVoiceStemTests
{
    private const string NativeLibEnvVar = SpcNativeSession.NativeLibraryEnvVar;

    [Fact]
    public void Open_WithStems_WritesMasterAndEightVoiceStemsAndEchoStem()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcTempFile.Create(BuildVoiceFixture());

        string dir = Path.Combine(Path.GetTempPath(), $"spc-stems-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string masterPath = Path.Combine(dir, "master.wav");
        try
        {
            using var session = new SpcPlaybackBackend().Open(
                new FileInfo(f.Path),
                new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 1.0,
                    OutputAudioPath: masterPath, WriteSpcStems: true),
                new Sink());
            session.Run();

            Assert.True(File.Exists(masterPath), "master.wav missing");
            int masterFrames = WavFrameCount(masterPath);
            Assert.Equal(32_000, masterFrames);

            for (int c = 0; c < 8; c++)
            {
                string voicePath = Path.Combine(dir, $"voice-{c + 1:00}.wav");
                Assert.True(File.Exists(voicePath), $"missing {Path.GetFileName(voicePath)}");
                Assert.Equal(masterFrames, WavFrameCount(voicePath));
            }

            string echoPath = Path.Combine(dir, "echo.wav");
            Assert.True(File.Exists(echoPath), "missing echo.wav");
            Assert.Equal(masterFrames, WavFrameCount(echoPath));

            Assert.True(IsNonSilent(Path.Combine(dir, "voice-01.wav")), "voice-01 should be non-silent");
            Assert.True(IsNonSilent(Path.Combine(dir, "voice-02.wav")), "voice-02 should be non-silent");
            for (int c = 2; c < 8; c++)
                Assert.True(IsSilent(Path.Combine(dir, $"voice-{c + 1:00}.wav")),
                    $"voice-{c + 1:00} should be silent (never keyed)");
            Assert.True(IsSilent(echoPath), "echo.wav should be silent (no EON, echo volumes zero)");
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    [Fact]
    public void Open_WithStems_MasterIsBitIdenticalToStemsDisabledRender()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcTempFile.Create(BuildVoiceFixture());

        string dir = Path.Combine(Path.GetTempPath(), $"spc-stems-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string plainMaster = Path.Combine(dir, "plain.wav");
            string stemMaster = Path.Combine(dir, "stems.wav");
            RenderTo(f.Path, plainMaster, writeStems: false);
            RenderTo(f.Path, stemMaster, writeStems: true);
            Assert.Equal(File.ReadAllBytes(plainMaster), File.ReadAllBytes(stemMaster));
        }
        finally
        {
            TryDeleteDirectory(dir);
        }
    }

    private static void RenderTo(string spcPath, string wavPath, bool writeStems)
    {
        using var session = new SpcPlaybackBackend().Open(
            new FileInfo(spcPath),
            new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 1.0,
                OutputAudioPath: wavPath, WriteSpcStems: writeStems),
            new Sink());
        session.Run();
    }

    /// <summary>
    /// Synthetic SPC with a CPU-driven KON for voices 0 and 1 (identical to the
    /// native tap_test fixture): a looping BRR square at 64K RAM, per-voice
    /// volumes/ADSR, no EON and zero echo volumes. EON=0 and evoll/evolr=0 are
    /// written explicitly so the echo-return stem is deterministic silence.
    /// </summary>
    private static byte[] BuildVoiceFixture()
    {
        const int fileSize = 0x10200;
        const int ramOffset = 0x100;
        const int dspOffset = 0x10100;

        byte[] data = new byte[fileSize];
        Encoding.ASCII.GetBytes(SpcMetadata.Signature).CopyTo(data, 0);
        data[0x23] = 0x30; /* format */
        data[0x24] = 1;    /* version */
        data[0x25] = 0x00; /* pcl -> PC = 0x0200 */
        data[0x26] = 0x02; /* pch */
        data[0x2B] = 0xFF; /* sp */

        /* CPU at 0x0200: key on voices 0 and 1 via $F2/$F3, then hang.
         *   mov $F2,#$4C   8F 4C F2
         *   mov $F3,#$03   8F 03 F3
         * hang: bra hang   2F FE */
        int pc = ramOffset + 0x0200;
        data[pc + 0] = 0x8F; data[pc + 1] = 0x4C; data[pc + 2] = 0xF2;
        data[pc + 3] = 0x8F; data[pc + 4] = 0x03; data[pc + 5] = 0xF3;
        data[pc + 6] = 0x2F; data[pc + 7] = 0xFE;

        /* DIR entry for source 0 at 0x0300: start = loop = 0x0400. */
        int dir = ramOffset + 0x0300;
        data[dir + 0] = 0x00; data[dir + 1] = 0x04;
        data[dir + 2] = 0x00; data[dir + 3] = 0x04;

        /* BRR block at 0x0400: header 0xA3 (end+loop), data 0xF0. */
        int brr = ramOffset + 0x0400;
        data[brr] = 0xA3;
        for (int i = 0; i < 8; i++)
            data[brr + 1 + i] = 0xF0;

        for (int v = 0; v < 2; v++)
        {
            int b = v * 0x10;
            data[dspOffset + b + 0x00] = 0x7F; /* voll  */
            data[dspOffset + b + 0x01] = 0x7F; /* volr  */
            data[dspOffset + b + 0x02] = 0x00; /* pitchl */
            data[dspOffset + b + 0x03] = 0x10; /* pitchh = 0x1000 (1.0x) */
            data[dspOffset + b + 0x04] = 0x00; /* srcn  */
            data[dspOffset + b + 0x05] = 0xFF; /* adsr0 */
            data[dspOffset + b + 0x06] = 0xE0; /* adsr1 */
        }
        data[dspOffset + 0x0C] = 0x7F; /* mvoll */
        data[dspOffset + 0x1C] = 0x7F; /* mvolr */
        data[dspOffset + 0x2C] = 0x00; /* evoll: 0 -> echo return silent */
        data[dspOffset + 0x3C] = 0x00; /* evolr: 0 -> echo return silent */
        data[dspOffset + 0x4C] = 0x00; /* kon: 0 -> CPU-driven key-on */
        data[dspOffset + 0x4D] = 0x00; /* eon: 0 -> no voice feeds echo */
        data[dspOffset + 0x5D] = 0x03; /* dir  */
        data[dspOffset + 0x6C] = 0x00; /* flg  */
        return data;
    }

    private static int WavFrameCount(string path)
    {
        byte[] wav = File.ReadAllBytes(path);
        int channels = BitConverter.ToInt16(wav, 22);
        int bitsPerSample = BitConverter.ToInt16(wav, 34);
        int dataSize = BitConverter.ToInt32(wav, 40);
        return dataSize / (channels * bitsPerSample / 8);
    }

    private static bool IsNonSilent(string path)
    {
        byte[] wav = File.ReadAllBytes(path);
        int channels = BitConverter.ToInt16(wav, 22);
        int dataSize = BitConverter.ToInt32(wav, 40);
        for (int i = 0; i + 1 < dataSize; i += 2)
        {
            if (BitConverter.ToInt16(wav, 44 + i) != 0)
                return true;
        }
        return false;
    }

    private static bool IsSilent(string path) => !IsNonSilent(path);

    private static string RequireNativeLibrary()
    {
        string lib = ResolveBuiltNativeLibrary();
        Assert.True(lib != null && File.Exists(lib),
            "Native SPC library not built. Run: cmake -S native/MDPlayer.SpcNative -B native/MDPlayer.SpcNative/build && cmake --build native/MDPlayer.SpcNative/build");
        return lib;
    }

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

    private static IDisposable SetNativeLibrary(string path)
    {
        string previous = Environment.GetEnvironmentVariable(NativeLibEnvVar);
        Environment.SetEnvironmentVariable(NativeLibEnvVar, path);
        return new RestoreEnv(NativeLibEnvVar, previous);
    }

    private static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
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

    private sealed class Sink : IPlaybackEventSink
    {
        public void OnDevice(in DeviceDescriptor d) { }
        public void OnChipWrite(in TimedChipWrite w) { }
        public void OnMidi(in TimedMidiMessage m) { }
        public void OnSampleAsset(in TimedSampleAssetEvent a) { }
        public void OnLoopBoundary(in TimedLoopBoundary l) { }
    }
}
