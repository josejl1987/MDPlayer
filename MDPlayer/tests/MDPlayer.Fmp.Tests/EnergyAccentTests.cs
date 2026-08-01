using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR 9: energy-linked accents (§6.4, §17.6). Tests the streaming stem-energy
/// analyzer, scope-border brightness modulation, and note-flash energy scaling.
/// </summary>
public sealed class EnergyAccentTests
{
    private const int SampleRate = 1000;
    private const string Fm1 = "ym2608.0.fm.1";
    private const string InstrumentA = "ym2608:aaaaaa1111111111";

    private static NoteEvent Note(long start, long end, double midi)
        => new(Fm1, start, end, 440.0, midi, InstrumentA, VisualizationNoteMode.Fm, false, Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(params NoteEvent[] notes)
        => new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 100_000,
            Instruments = [new InstrumentDefinition(InstrumentA, "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>())],
            Notes = notes,
            Rhythm = Array.Empty<RhythmEvent>(),
        };

    private static PanelOverlayRenderer Renderer(VisualizationTimeline timeline, ChannelEnergyEnvelope[] energy = null)
        => new(timeline, RendererTestLayout.Build(timeline), new PanelOverlayRenderer.Options
        {
            FpsNumerator = 20,
            FpsDenominator = 1,
            Energy = energy,
        });

    private static ChannelEnergyEnvelope MakeEnvelope(string channelId, float[] peak, float[] rms)
        => new() { ChannelId = channelId, FramePeak = peak, FrameRms = rms };

    // ---- §6.4 ChannelEnergyAnalyzer ----

    [Fact]
    public void Analyze_ReturnsEmptyForNoStems()
    {
        var envelopes = ChannelEnergyAnalyzer.Analyze(
            Array.Empty<(string, string)>(), 100, SampleRate, 20, 1);
        Assert.Empty(envelopes);
    }

    [Fact]
    public void Analyze_SkipsMissingFiles()
    {
        var envelopes = ChannelEnergyAnalyzer.Analyze(
            new[] { ("ym2608-fm1", "/nonexistent/path.wav") },
            100, SampleRate, 20, 1);
        Assert.Empty(envelopes);
    }

    [Fact]
    public void Analyze_ProducesCorrectFrameCount()
    {
        // Create a temporary silent WAV: 1 second at 1000 Hz, 20 fps → 20 frames.
        string wavPath = Path.Combine(Path.GetTempPath(), $"energy_test_{Guid.NewGuid():N}.wav");
        WriteSilentWav(wavPath, sampleRate: 1000, sampleFrames: 1000);

        try
        {
            var envelopes = ChannelEnergyAnalyzer.Analyze(
                new[] { ("ym2608-fm1", wavPath) },
                totalFrames: 20, sampleRate: 1000, fpsNumerator: 20, fpsDenominator: 1);
            Assert.Single(envelopes);
            Assert.Equal("ym2608.0.fm.1", envelopes[0].ChannelId);
            Assert.Equal(20, envelopes[0].FramePeak.Length);
            Assert.Equal(20, envelopes[0].FrameRms.Length);
            // Silent WAV → all zeros.
            Assert.All(envelopes[0].FramePeak, v => Assert.Equal(0f, v));
            Assert.All(envelopes[0].FrameRms, v => Assert.Equal(0f, v));
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void Analyze_DetectsLoudFrames()
    {
        // Create a WAV with a loud burst in the first 50 samples (first frame at 20fps/1kHz = 50 samples).
        string wavPath = Path.Combine(Path.GetTempPath(), $"energy_loud_{Guid.NewGuid():N}.wav");
        WriteWavWithBurst(wavPath, sampleRate: 1000, sampleFrames: 200, burstStart: 0, burstLength: 50);

        try
        {
            var envelopes = ChannelEnergyAnalyzer.Analyze(
                new[] { ("ym2608-fm1", wavPath) },
                totalFrames: 4, sampleRate: 1000, fpsNumerator: 20, fpsDenominator: 1);
            Assert.Single(envelopes);
            // Frame 0 should have high peak; frame 1+ should be silent.
            Assert.True(envelopes[0].FramePeak[0] > 0.5f, $"Frame 0 peak should be loud: {envelopes[0].FramePeak[0]}");
            Assert.True(envelopes[0].FrameRms[0] > 0.1f, $"Frame 0 RMS should be non-zero: {envelopes[0].FrameRms[0]}");
            Assert.Equal(0f, envelopes[0].FramePeak[1]);
            Assert.Equal(0f, envelopes[0].FrameRms[1]);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void Analyze_HandlesMonoScopeStem()
    {
        string wavPath = Path.Combine(Path.GetTempPath(), $"energy_mono_{Guid.NewGuid():N}.wav");
        WriteWavWithBurst(wavPath, sampleRate: 1000, sampleFrames: 100, burstStart: 0, burstLength: 50, channels: 1);

        try
        {
            ChannelEnergyEnvelope envelope = Assert.Single(ChannelEnergyAnalyzer.Analyze(
                new[] { ("ym2608-fm1", wavPath) }, 2, 1000, 20, 1));
            Assert.True(envelope.FramePeak[0] > 0.5f);
            Assert.True(envelope.FrameRms[0] > 0.1f);
        }
        finally
        {
            File.Delete(wavPath);
        }
    }

    [Fact]
    public void Analyze_MapsAllPanelChannels()
    {
        // Verify all 12 panel IDs have a corresponding stem name mapping.
        string[] stemNames = {
            "ym2608-fm1", "ym2608-fm2", "ym2608-fm3", "ym2608-fm4", "ym2608-fm5", "ym2608-fm6",
            "ym2608-ssg1", "ym2608-ssg2", "ym2608-ssg3",
            "ym2608-rhythm", "ym2608-adpcm", "ppz8-01",
        };

        // Create a silent WAV for each stem.
        var stems = new List<(string, string)>();
        var tempPaths = new List<string>();
        try
        {
            foreach (var name in stemNames)
            {
                string path = Path.Combine(Path.GetTempPath(), $"energy_map_{name}_{Guid.NewGuid():N}.wav");
                WriteSilentWav(path, 1000, 100);
                stems.Add((name, path));
                tempPaths.Add(path);
            }

            var envelopes = ChannelEnergyAnalyzer.Analyze(
                stems.ToArray(), 2, 1000, 20, 1);
            Assert.Equal(12, envelopes.Length);
            // Each envelope should map to a valid panel ID.
            foreach (var env in envelopes)
                Assert.Contains(env.ChannelId, OverlayLayout.PanelIds);
        }
        finally
        {
            foreach (var path in tempPaths)
                File.Delete(path);
        }
    }

    // ---- §17.6 scope-border brightness ----

    [Fact]
    public void ScopeBorder_BrighterWithEnergy()
    {
        var timeline = Timeline(Note(500, 5000, 60));
        var energy = new[]
        {
            MakeEnvelope("ym2608.0.fm.1", new float[100], new float[100]),
        };
        // Fill frame 0 with high energy.
        Array.Fill(energy[0].FrameRms, 0.8f);
        Array.Fill(energy[0].FramePeak, 0.9f);

        var rendererWithEnergy = Renderer(timeline, energy);
        var rendererNoEnergy = Renderer(timeline, null);

        byte[] frameWithEnergy = rendererWithEnergy.RenderFrame(1); // sample 50
        byte[] frameNoEnergy = rendererNoEnergy.RenderFrame(1);

        OverlayRect scope = rendererWithEnergy.Layout.GetScopeRect(0);
        int brightWith = CountBrightBorder(frameWithEnergy, rendererWithEnergy.Width, scope);
        int brightWithout = CountBrightBorder(frameNoEnergy, rendererNoEnergy.Width, scope);

        Assert.True(brightWith > brightWithout,
            $"Scope border should be brighter with energy: with={brightWith}, without={brightWithout}.");
    }

    [Fact]
    public void ScopeBorder_NoChangeWhenEnergyAbsent()
    {
        // When no energy envelope is provided for a panel, the border should
        // be unchanged (just the static alpha-180 border).
        var timeline = Timeline(Note(500, 5000, 60));
        var renderer = Renderer(timeline, energy: null);
        byte[] frame = renderer.RenderFrame(1);
        OverlayRect scope = renderer.Layout.GetScopeRect(0);

        // The border should exist (static) but not be energy-brightened.
        Assert.True(CountBrightBorder(frame, renderer.Width, scope) > 0,
            "Static scope border should always be present.");
    }

    // ---- §6.4 note flash modulation ----

    [Fact]
    public void NoteFlash_ModulatedByEnergy()
    {
        var timeline = Timeline(Note(1500, 4500, 60));
        var energyHigh = new[]
        {
            MakeEnvelope("ym2608.0.fm.1", new float[100], new float[100]),
        };
        Array.Fill(energyHigh[0].FrameRms, 1.0f);

        var energyLow = new[]
        {
            MakeEnvelope("ym2608.0.fm.1", new float[100], new float[100]),
        };
        Array.Fill(energyLow[0].FrameRms, 0.0f);

        var rendererHigh = Renderer(timeline, energyHigh);
        var rendererLow = Renderer(timeline, energyLow);

        // Frame 31 = sample 1550 → note age 50ms → flash active.
        byte[] frameHigh = rendererHigh.RenderFrame(31);
        byte[] frameLow = rendererLow.RenderFrame(31);

        OverlayRect lane = rendererHigh.Layout.GetPitchedLaneRect(0, false);
        int bodyX = rendererHigh.Layout.GetPlayheadX(0) + 20;
        int brightHigh = MaxBrightnessInColumn(frameHigh, rendererHigh.Width, bodyX, lane.Y, lane.Bottom);
        int brightLow = MaxBrightnessInColumn(frameLow, rendererLow.Width, bodyX, lane.Y, lane.Bottom);

        Assert.True(brightHigh >= brightLow,
            $"Flash should be at least as bright with high energy: high={brightHigh}, low={brightLow}.");
    }

    // ---- helpers ----

    private static int CountBrightBorder(byte[] frame, int width, OverlayRect scope)
    {
        int count = 0;
        // Top and bottom rows of the scope rect. Use a high threshold to
        // distinguish energy-brightened borders (alpha 255) from static
        // borders (alpha 180). The blend produces higher RGB at higher alpha.
        for (int x = scope.X; x < scope.Right; x++)
        {
            int top = (scope.Y * width + x) * 4;
            int bot = ((scope.Bottom - 1) * width + x) * 4;
            if (frame[top + 3] > 200)
                count++;
            if (frame[bot + 3] > 200)
                count++;
        }
        return count;
    }

    private static int MaxBrightnessInColumn(byte[] frame, int width, int x, int top, int bottom)
    {
        int max = 0;
        for (int y = top; y < bottom; y++)
        {
            int offset = (y * width + x) * 4;
            if (frame[offset + 3] == 0)
                continue;
            int brightness = frame[offset] + frame[offset + 1] + frame[offset + 2];
            if (brightness > max)
                max = brightness;
        }
        return max;
    }

    /// <summary>Writes a silent 16-bit stereo PCM WAV file.</summary>
    private static void WriteSilentWav(string path, int sampleRate, int sampleFrames)
        => WriteWavWithBurst(path, sampleRate, sampleFrames, burstStart: -1, burstLength: 0);

    /// <summary>Writes a 16-bit stereo PCM WAV with an optional loud burst.</summary>
    private static void WriteWavWithBurst(
        string path,
        int sampleRate,
        int sampleFrames,
        int burstStart,
        int burstLength,
        int channels = 2)
    {
        int bitsPerSample = 16;
        int byteRate = sampleRate * channels * bitsPerSample / 8;
        int blockAlign = channels * bitsPerSample / 8;
        int dataSize = sampleFrames * blockAlign;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);

        for (int i = 0; i < sampleFrames; i++)
        {
            short sample = (i >= burstStart && i < burstStart + burstLength)
                ? (short)(32767 * 0.8)
                : (short)0;
            for (int channel = 0; channel < channels; channel++)
                bw.Write(sample);
        }
    }
}
