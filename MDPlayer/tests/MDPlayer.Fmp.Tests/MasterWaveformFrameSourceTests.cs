using Fmp.Application.Contracts;
using Fmp.Cli;
using Fmp.Core.Audio;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Tests for the internal master-waveform scope source — the shared fallback
/// that keeps final, preview and review frames identical when the Corrscope
/// bridge is unavailable.
/// </summary>
public sealed class MasterWaveformFrameSourceTests
{
    private const int SampleRate = 1_000; // matches VisualizationTimelineFixture

    [Fact]
    public void TryCreate_ReturnsSource_ForPcm16StereoWav()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string wav = WriteWav(SineWave(SampleRate * 5, SampleRate, 110));

        using MasterWaveformFrameSource? source =
            MasterWaveformFrameSource.TryCreate(renderer, wav, 60, 1);

        Assert.NotNull(source);
    }

    [Fact]
    public void TryCreate_ReturnsNull_ForMissingOrUnsupportedInput()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string missing = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.wav");

        Assert.Null(MasterWaveformFrameSource.TryCreate(renderer, missing, 60, 1));

        string text = Path.Combine(Path.GetTempPath(), $"text-{Guid.NewGuid():N}.wav");
        File.WriteAllText(text, "definitely not a wav file");
        try
        {
            Assert.Null(MasterWaveformFrameSource.TryCreate(renderer, text, 60, 1));
        }
        finally
        {
            File.Delete(text);
        }
    }

    [Fact]
    public void ReadFrame_IsDeterministic_AndRendersSameFrameTwice()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string wav = WriteWav(SineWave(SampleRate * 5, SampleRate, 220));
        using var source = MasterWaveformFrameSource.TryCreate(renderer, wav, 60, 1);
        Assert.NotNull(source);

        byte[] first = new byte[renderer.ScopeFrameByteCount];
        byte[] second = new byte[renderer.ScopeFrameByteCount];
        source!.ReadFrame(120, first);
        source.ReadFrame(120, second);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ReadFrame_DrawsMoreContentForSineThanForSilence()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string sine = WriteWav(SineWave(SampleRate * 5, SampleRate, 440));
        string silence = WriteWav(new short[SampleRate * 5]);

        using var sineSource = MasterWaveformFrameSource.TryCreate(renderer, sine, 60, 1);
        using var silenceSource = MasterWaveformFrameSource.TryCreate(renderer, silence, 60, 1);
        Assert.NotNull(sineSource);
        Assert.NotNull(silenceSource);

        byte[] sineFrame = new byte[renderer.ScopeFrameByteCount];
        byte[] silenceFrame = new byte[renderer.ScopeFrameByteCount];
        sineSource!.ReadFrame(60, sineFrame);
        silenceSource!.ReadFrame(60, silenceFrame);

        int sinePixels = CountColored(sineFrame);
        int silencePixels = CountColored(silenceFrame);

        // A loud sine fills columns with tall waveform strokes.
        Assert.True(sinePixels > silencePixels,
            $"sine frame ({sinePixels}) should draw more pixels than silence ({silencePixels})");
        // Silence still draws the center baseline in every cell column.
        Assert.True(silencePixels > 0,
            "silence should still render a visible center line");
        Assert.All(Enumerable.Range(0, sineFrame.Length / 4), pixel =>
        {
            int offset = pixel * 4;
            if (sineFrame[offset] != 0 || sineFrame[offset + 1] != 0 || sineFrame[offset + 2] != 0)
                Assert.Equal(0x70, sineFrame[offset + 3]);
        });
    }

    [Fact]
    public void ReadFrame_ClampsBeyondAudioEnd_WithoutThrowing()
    {
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        // 0.1s of audio: only ~6 frames at 60fps.
        string wav = WriteWav(SineWave(SampleRate / 10, SampleRate, 330));
        using var source = MasterWaveformFrameSource.TryCreate(renderer, wav, 60, 1);
        Assert.NotNull(source);

        byte[] frame = new byte[renderer.ScopeFrameByteCount];
        source!.ReadFrame((int)Math.Min(int.MaxValue, renderer.TotalFrames - 1), frame); // far beyond audio end
        Assert.Contains(frame, b => b != 0); // center line still drawn
    }

    [Fact]
    public void StereoWithIdenticalChannels_MatchesMonoRender()
    {
        // Regression: the stereo deinterleave used the wrong byte offset for
        // the right channel. With left == right, a correctly deinterleaved
        // stereo frame must be byte-identical to the mono frame (min/max over
        // both channels equals min/max over one).
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        short[] mono = SineWave(SampleRate * 5, SampleRate, 330);
        string monoWav = WriteMonoWav(mono);
        string stereoWav = WriteWav(InterleavedStereo(mono));

        using var monoSource = MasterWaveformFrameSource.TryCreate(renderer, monoWav, 60, 1);
        using var stereoSource = MasterWaveformFrameSource.TryCreate(renderer, stereoWav, 60, 1);
        Assert.NotNull(monoSource);
        Assert.NotNull(stereoSource);

        byte[] monoFrame = new byte[renderer.ScopeFrameByteCount];
        byte[] stereoFrame = new byte[renderer.ScopeFrameByteCount];
        monoSource!.ReadFrame(120, monoFrame);
        stereoSource!.ReadFrame(120, stereoFrame);

        Assert.Equal(monoFrame, stereoFrame);
    }

    [Fact]
    public void ReadFrame_DrawsBothPositiveAndNegativeExcursions()
    {
        // Regression: the min/max column loop iterated yBottom -> yTop, which
        // is empty for the normal case (min < 0 < max), so the stroke was
        // collapsed to the positive half and negative lobes were never drawn.
        (PanelOverlayRenderer renderer, _) = CreateRenderer();
        string wav = WriteWav(SineWave(SampleRate * 5, SampleRate, 440));
        using var source = MasterWaveformFrameSource.TryCreate(renderer, wav, 60, 1);
        Assert.NotNull(source);

        byte[] frame = new byte[renderer.ScopeFrameByteCount];
        source!.ReadFrame(60, frame);

        Assert.True(CountBelowCellCenters(renderer, frame) > 0,
            "waveform must draw content below each cell's vertical center (negative excursion)");
    }

    // ---- helpers ----

    private static (PanelOverlayRenderer Renderer, VisualizationRequest Request) CreateRenderer()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        VisualizationRequest request = new()
        {
            InputPath = "fixture.ovi",
            OutputPath = "/tmp/fixture.mp4",
            Output = new OutputSettings
            {
                Width = 960,
                Height = 540,
                FpsNumerator = 60,
                FpsDenominator = 1,
            },
            View = new ViewSettings { PastSeconds = 0.75, FutureSeconds = 2.25 },
            Playback = new PlaybackSettings { SampleRate = SampleRate },
            Tracks = new TrackSettings { Selection = TrackSelectionMode.All },
        };
        var renderer = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline, request.Output.Width, request.Output.Height),
            VisualizationRendererOptions.Build(
                request,
                new VisualizationPresentation("T", "S", "C"),
                introOutro: false,
                energy: Array.Empty<ChannelEnergyEnvelope>()));
        return (renderer, request);
    }

    private static string WriteWav(short[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), $"master-waveform-{Guid.NewGuid():N}.wav");
        using var writer = new WavWriter(path, SampleRate, channels: 2);
        writer.Write(samples);
        writer.Close();
        return path;
    }

    private static string WriteMonoWav(short[] samples)
    {
        string path = Path.Combine(Path.GetTempPath(), $"master-waveform-mono-{Guid.NewGuid():N}.wav");
        using var writer = new WavWriter(path, SampleRate, channels: 1);
        writer.Write(samples);
        writer.Close();
        return path;
    }

    private static short[] InterleavedStereo(short[] mono)
    {
        var result = new short[mono.Length * 2];
        for (int i = 0; i < mono.Length; i++)
        {
            result[2 * i] = mono[i];
            result[2 * i + 1] = mono[i];
        }
        return result;
    }

    private static short[] SineWave(int sampleCount, int sampleRate, double frequency)
    {
        var samples = new short[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double t = i / (double)sampleRate;
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * t) * short.MaxValue * 0.8);
        }
        return samples;
    }

    private static int CountColored(byte[] frame)
    {
        int count = 0;
        for (int i = 0; i < frame.Length; i += 4)
        {
            if (frame[i] != 0 || frame[i + 1] != 0 || frame[i + 2] != 0)
                count++;
        }
        return count;
    }

    private static int CountBelowCellCenters(PanelOverlayRenderer renderer, byte[] frame)
    {
        // Count colored pixels strictly below each grid cell's vertical center.
        int columnCount = renderer.Layout.ColumnCount;
        int scopeHeight = renderer.Layout.ScopeHeight;
        int gridWidth = renderer.Layout.CorrscopeGridWidth;
        int count = 0;
        for (int panelIndex = 0; panelIndex < renderer.Layout.PanelCount; panelIndex++)
        {
            int row = panelIndex / columnCount;
            int cellY = row * scopeHeight;
            int center = cellY + scopeHeight / 2;
            for (int y = center + 1; y < cellY + scopeHeight; y++)
            {
                for (int x = 0; x < gridWidth; x++)
                {
                    int offset = (y * gridWidth + x) * 4;
                    if (frame[offset] != 0 || frame[offset + 1] != 0 || frame[offset + 2] != 0)
                        count++;
                }
            }
        }
        return count;
    }
}
