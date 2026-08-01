using System.Text;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Visualization;
using Xunit;

// PR 2 SPC tests render through the native library and temporarily override the
// process-global MDPLAYER_SPC_NATIVE location. Serializing collections keeps
// those renders deterministic w.r.t. the rest of the suite (no
// thread-scheduling dependence).
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MDPlayer.Fmp.Tests.Playback.Spc;

public sealed class SpcNativeSessionTests
{
    private const string NativeLibEnvVar = SpcNativeSession.NativeLibraryEnvVar;

    [Fact]
    public void Render_ProducesMasterWavAt32kHz()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcTempFile.Create(SpcFixture.Build());

        string wavPath = Path.Combine(Path.GetTempPath(), $"spc-master-{Guid.NewGuid():N}.wav");
        try
        {
            using var session = new SpcPlaybackBackend().Open(
                new FileInfo(f.Path),
                new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 3.0, OutputAudioPath: wavPath),
                new Sink());
            session.Run();

            byte[] wav = File.ReadAllBytes(wavPath);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal(32_000, BitConverter.ToInt32(wav, 24));
            Assert.Equal(2, BitConverter.ToInt16(wav, 22));
            Assert.Equal(16, BitConverter.ToInt16(wav, 34));
            int dataSize = BitConverter.ToInt32(wav, 40);
            Assert.Equal(3 * 32_000 * 2 * 2, dataSize);
            Assert.Equal(3 * 32_000, session.SamplePosition);
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    [Fact]
    public void Render_SameInputTwice_IsByteIdentical()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcTempFile.Create(SpcFixture.Build());

        string wav1 = Path.Combine(Path.GetTempPath(), $"spc-a-{Guid.NewGuid():N}.wav");
        string wav2 = Path.Combine(Path.GetTempPath(), $"spc-b-{Guid.NewGuid():N}.wav");
        try
        {
            RenderToFile(f.Path, wav1);
            RenderToFile(f.Path, wav2);
            Assert.Equal(File.ReadAllBytes(wav1), File.ReadAllBytes(wav2));
        }
        finally
        {
            TryDelete(wav1);
            TryDelete(wav2);
        }
    }

    [Fact]
    public void Open_RendersToFileWithoutOpeningAudioDevice()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcTempFile.Create(SpcFixture.Build());

        string wavPath = Path.Combine(Path.GetTempPath(), $"spc-file-{Guid.NewGuid():N}.wav");
        try
        {
            // The generic pipeline renders to a WAV file; no audio output device
            // is opened (no wave-out/ALSA/NAudio calls anywhere in the path).
            using var session = new SpcPlaybackBackend().Open(
                new FileInfo(f.Path),
                new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 1.0, OutputAudioPath: wavPath),
                new Sink());
            session.Run();

            Assert.Equal(32_000, session.Timing.SampleRate);
            Assert.Single(session.Devices);
            Assert.Equal(ChipType.SnesDsp, session.Devices[0].Type);
            Assert.True(File.Exists(wavPath));
            Assert.True(new FileInfo(wavPath).Length > 44);
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    [Fact]
    public void Open_RespectsDefaultDurationAndFadesToSilence()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);
        using var f = SpcTempFile.Create(SpcFixture.Build());

        string wavPath = Path.Combine(Path.GetTempPath(), $"spc-default-{Guid.NewGuid():N}.wav");
        try
        {
            // No explicit duration: SpcDurationResolver falls back to the 150 s
            // default (fixture carries no ID666 play length). A fade is applied
            // to the master, so the final frame must be exactly silent.
            using var session = new SpcPlaybackBackend().Open(
                new FileInfo(f.Path),
                new PlaybackOptions(FadeSeconds: 8, OutputAudioPath: wavPath),
                new Sink());
            session.Run();

            byte[] wav = File.ReadAllBytes(wavPath);
            int dataSize = BitConverter.ToInt32(wav, 40);
            Assert.Equal(150 * 32_000 * 2 * 2, dataSize);
            Assert.True(ContainsNonZero(wav, 44, 32_000 * 4),
                "The synthetic SPC must produce non-silent PCM before the fade.");
            Assert.Equal(0, BitConverter.ToInt16(wav, 44 + dataSize - 2));
            Assert.Equal(0, BitConverter.ToInt16(wav, 44 + dataSize - 4));
        }
        finally
        {
            TryDelete(wavPath);
        }
    }

    [Fact]
    public void NativeSession_CopyRamAndDspRegisters_ReturnInitialSnapshot()
    {
        string lib = RequireNativeLibrary();
        using var restore = SetNativeLibrary(lib);

        byte[] spc = SpcFixture.Build();
        using var native = SpcNativeSession.Open(spc, SpcNativeSession.OpenOptions.Default);
        byte[] ram = native.CopyRam();
        byte[] dsp = native.CopyDspRegisters();

        Assert.Equal(SpcNativeSession.RamSize, ram.Length);
        Assert.Equal(SpcNativeSession.DspRegisterSize, dsp.Length);
        Assert.Equal(0x8F, ram[0x0200]);
        Assert.Equal(0x4C, ram[0x0201]);
        Assert.Equal(0x7F, dsp[0x00]);
        Assert.Equal(0x7F, dsp[0x01]);
    }

    [Fact]
    public void Open_NativeLibraryMissing_ThrowsActionableError()
    {
        // Force the missing-library path: an env override pointing at a
        // nonexistent file is treated as missing even when the packaged .so
        // exists in the test bin.
        string missing = Path.Combine(Path.GetTempPath(), "does-not-exist", "libmdplayer_spc.so");
        using var restore = SetNativeLibrary(missing);
        using var f = SpcTempFile.Create(SpcFixture.Build());

        var ex = Assert.Throws<SpcFormatException>(() =>
            new SpcPlaybackBackend().Open(new FileInfo(f.Path), new PlaybackOptions(), new Sink()));

        Assert.Contains("libmdplayer_spc.so", ex.Message, StringComparison.Ordinal);
        Assert.Contains("runtimes/linux-x64/native", ex.Message, StringComparison.Ordinal);
        Assert.Contains("MDPLAYER_SPC_NATIVE", ex.Message, StringComparison.Ordinal);
    }

    private static void RenderToFile(string spcPath, string wavPath)
    {
        using var session = new SpcPlaybackBackend().Open(
            new FileInfo(spcPath),
            new PlaybackOptions(FadeSeconds: 0, MaxDurationSeconds: 2.0, OutputAudioPath: wavPath),
            new Sink());
        session.Run();
    }

    /// <summary>Finds the built native library in the repo runtimes layout.</summary>
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


    private static bool ContainsNonZero(byte[] wav, int offset, int byteCount)
    {
        int end = Math.Min(wav.Length, offset + byteCount);
        for (int i = offset; i < end; i++)
        {
            if (wav[i] != 0)
                return true;
        }

        return false;
    }
    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
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
