using Fmp.Core.Playback.Opna;
using Xunit;

namespace MDPlayer.Fmp.Tests.Playback.Opna;

/// <summary>
/// Exercises the sample-position MDSound contract
/// (<see cref="ILegacyFmpAudioEngine"/> through
/// <see cref="LegacyMdsoundFmpAudioEngine"/>). These tests only verify the
/// existing production MDSound backend through the new contract — no
/// production call path is changed.
/// </summary>
public sealed class LegacyFmpAudioEngineTests
{
    [Fact]
    public void Start_Render_ProducesFrames()
    {
        using var engine = new LegacyMdsoundFmpAudioEngine();
        Assert.Equal(44100, engine.SampleRate);

        engine.Start();
        var pcm = new short[1024];
        int frames = engine.Render(pcm, 512);

        Assert.Equal(512, frames);
        engine.Stop();
    }

    [Fact]
    public void Render_NotStarted_ProducesSilence()
    {
        using var engine = new LegacyMdsoundFmpAudioEngine();
        var pcm = new short[1024];
        Array.Fill(pcm, (short)0x7FFF);

        int frames = engine.Render(pcm, 512);

        Assert.Equal(512, frames);
        Assert.All(pcm, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void SameDrive_IsDeterministic()
    {
        short[] Render()
        {
            using var engine = new LegacyMdsoundFmpAudioEngine();
            engine.Start();
            // Key on FM channel 0 with a low operator level so the output is
            // not clipped to silence by the default TL=127.
            engine.WriteYm2608(0, 0, 0x30, 0x10, 0);
            engine.WriteYm2608(0, 0, 0x31, 0x10, 0);
            engine.WriteYm2608(0, 0, 0x32, 0x10, 0);
            engine.WriteYm2608(0, 0, 0x33, 0x10, 0);
            engine.WriteYm2608(0, 0, 0x34, 0x10, 0);
            engine.WriteYm2608(0, 0, 0x35, 0x10, 0);
            engine.WriteYm2608(0, 0, 0xA0, 0x2D, 0);
            engine.WriteYm2608(0, 0, 0xA4, 0x03, 0);
            engine.WriteYm2608(0, 0, 0xB0, 0x20, 0);
            engine.WriteYm2608(0, 0, 0xB4, 0xC0, 0);
            engine.WriteYm2608(0, 0, 0x28, 0xF0, 0);
            var pcm = new short[4096];
            engine.Render(pcm, 2048);
            engine.Stop();
            return pcm;
        }

        short[] a = Render();
        short[] b = Render();
        Assert.Equal(a, b);
    }

    [Fact]
    public void SsgToneKeyOn_ProducesNonzeroOutput()
    {
        // NB: this MDSound.dll build (kuma4649/MDSound be9a73f4) renders SSG
        // through fmgen but its YM2608 FM path stays silent (FM visual
        // volumes remain 0 even after key-on), so the nonzero check uses the
        // SSG tone channel. FM non-silence is covered by the native session
        // tests (furnace trace).
        using var engine = new LegacyMdsoundFmpAudioEngine();
        engine.Start();
        engine.WriteYm2608(0, 0, 0x07, 0xF8, 0); // SSG: mixer all tone, no noise
        engine.WriteYm2608(0, 0, 0x00, 0x0F, 0); // ch0 tone period low
        engine.WriteYm2608(0, 0, 0x01, 0x0F, 0); // ch0 tone period high
        engine.WriteYm2608(0, 0, 0x08, 0x0F, 0); // ch0 amplitude
        engine.WriteYm2608(0, 0, 0x0A, 0x0F, 0); // ch0 amplitude (bit4=on)
        engine.WriteYm2608(0, 0, 0x09, 0x08, 0); // ch0 key-on (bit3)

        var pcm = new short[4096];
        engine.Render(pcm, 2048);

        int nonzero = pcm.Count(s => s != 0);
        Assert.True(nonzero > 0, "expected non-silent output from MDSound YM2608 SSG tone key-on");
        engine.Stop();
    }

    [Fact]
    public void Ppz8BankAndRegisterWrites_DoNotThrow()
    {
        using var engine = new LegacyMdsoundFmpAudioEngine();
        engine.Start();

        var bank = new ReadOnlyMemory<byte>[] { new byte[16], new byte[16] };
        engine.LoadPpz8Bank(0, 0, bank, 0);
        engine.WritePpz8(0, 0, 0, 0);
        engine.WritePpz8(0, 1, 0, 0);

        var pcm = new short[512];
        int frames = engine.Render(pcm, 256);
        Assert.Equal(256, frames);

        engine.Stop();
    }

    [Fact]
    public void Reset_ReturnsToSilence()
    {
        using var engine = new LegacyMdsoundFmpAudioEngine();
        engine.Start();
        engine.WriteYm2608(0, 0, 0x28, 0xF0, 0);
        engine.Reset();

        var pcm = new short[256];
        engine.Render(pcm, 128);
        Assert.All(pcm, sample => Assert.Equal(0, sample));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var engine = new LegacyMdsoundFmpAudioEngine();
        engine.Dispose();
        engine.Dispose();
    }
}
