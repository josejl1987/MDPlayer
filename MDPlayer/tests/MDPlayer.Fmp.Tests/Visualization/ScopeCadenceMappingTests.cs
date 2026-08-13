using System.Security.Cryptography;
using Fmp.Core.Audio;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Change B: the output-frame→scope-frame cadence mapping (plan §5). The
/// mapping is monotonic and 1:1 at scopeFps >= outputFps; the frame renderer
/// caches one scope frame so repeated mapped indices never re-read the scope
/// source (CorrscopeFrameSource restarts its bridge process on a backward
/// read).
/// </summary>
public sealed class ScopeCadenceMappingTests
{
    [Theory]
    [InlineData(60, 30, 0, 0)]     // 60 fps output, 30 Hz scope
    [InlineData(60, 30, 1, 0)]
    [InlineData(60, 30, 2, 1)]
    [InlineData(60, 30, 3, 1)]
    [InlineData(60, 30, 4, 2)]
    [InlineData(60, 30, 59, 29)]
    [InlineData(30, 30, 0, 0)]     // 1:1 at equal rates
    [InlineData(30, 30, 29, 29)]
    [InlineData(24, 60, 0, 0)]     // scope faster than output: 1:1
    [InlineData(24, 60, 5, 5)]
    [InlineData(60000, 29970, 1, 0)] // 59.94 output, 29.97 scope (NTSC family)
    [InlineData(60000, 29970, 100, 49)]
    public void Map_FollowsFloorFormula(long outputFps, long scopeFps, long outputFrame, long expectedScopeFrame)
    {
        Assert.Equal(
            expectedScopeFrame,
            ScopeFrameMapping.Map(outputFrame, scopeFps, outputFps));
    }

    [Fact]
    public void Map_IsMonotonicNonDecreasing_AtHalfCadence()
    {
        long previous = -1;
        for (long frame = 0; frame < 240; frame++)
        {
            long mapped = ScopeFrameMapping.Map(frame, scopeFps: 30, outputFps: 60);
            Assert.True(mapped >= previous, $"mapping went backward at frame {frame}");
            previous = mapped;
        }
    }

    [Theory]
    [InlineData(null, 60, 30)]     // auto default: min(output, 30)
    [InlineData(null, 30, 30)]
    [InlineData(null, 24, 24)]     // below 30: follow output
    [InlineData(30.0, 60, 30)]     // explicit 30 at 60 fps output
    [InlineData(60.0, 30, 30)]     // explicit above output clamps to 1:1
    [InlineData(0.0, 60, 30)]      // non-positive falls back to auto
    public void Resolve_AutoDefaultAndClamping(double? scopeFps, double outputFps, double expected)
    {
        Assert.Equal(expected, ScopeFrameMapping.Resolve(scopeFps, outputFps));
    }

    /// <summary>
    /// The renderer must serve repeated mapped indices from a 1-frame cache:
    /// at 60 fps output with a 30 Hz scope, six output frames read exactly
    /// three scope frames, in order, never twice (AC-B2). The rendered frame
    /// content must show the cached scope grid.
    /// </summary>
    [Fact]
    public void Renderer_ReusesMappedScopeFrames_ThroughTheCache()
    {
        var (renderer, source, overlay) = CreateRenderer(scopeFps: 30, outputFps: 60);
        using (renderer)
        {
            int scopePixel = FindUntouchedScopePixel(overlay, frameIndex: 0);
            Assert.True(scopePixel >= 0, "expected an untouched pixel in the scope region");

            var frame = new byte[renderer.FrameByteCount];
            for (long index = 0; index < 6; index++)
            {
                renderer.RenderFrame(index, frame);
                byte mapped = (byte)(index * 30 / 60);
                // The recording source fills every pixel with the frame index;
                // the full-opacity blend must expose exactly the mapped grid.
                Assert.Equal(mapped, frame[scopePixel]);
                Assert.Equal((byte)(mapped * 3), frame[scopePixel + 1]);
                Assert.Equal((byte)(mapped * 7), frame[scopePixel + 2]);
                Assert.Equal(255, frame[scopePixel + 3]);
            }

            Assert.Equal([0, 1, 2], source.Reads);
        }
    }

    [Fact]
    public void Renderer_ReadsOneToOne_WhenScopeFpsMatchesOutput()
    {
        var (renderer, source, _) = CreateRenderer(scopeFps: 60, outputFps: 60);
        using (renderer)
        {
            var frame = new byte[renderer.FrameByteCount];
            for (long index = 0; index < 6; index++)
                renderer.RenderFrame(index, frame);

            Assert.Equal([0, 1, 2, 3, 4, 5], source.Reads);
        }
    }

    [Fact]
    public void Renderer_SequentialSession_ReadsAtScopeCadence()
    {
        var (renderer, source, _) = CreateRenderer(scopeFps: 30, outputFps: 60);
        using (renderer)
        {
            VisualizationFrameRenderer.SequentialSession session = renderer.CreateSequentialSession();
            var destination = new byte[renderer.FrameByteCount];
            session.Initialize(destination);
            for (long index = 0; index < 6; index++)
                session.RenderNext(index, destination);

            Assert.Equal([0, 1, 2], source.Reads);
        }
    }

    /// <summary>
    /// Fallback parity (AC-A5): the interactive source emits a real mask —
    /// alpha-255 waveform lines on an alpha-0 background — and the waveform
    /// visibly contributes to the composite at ScopeOpacity 1.0.
    /// </summary>
    [Fact]
    public void FallbackSource_EmitsMaskAndContributesToComposite()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        using var overlay = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options { FpsNumerator = 60, FpsDenominator = 1 });
        string wav = WriteWav(220);
        try
        {
            using var source = InteractiveWaveformFrameSource.TryCreate(
                overlay,
                new[]
                {
                    new ProjectedScopeChannel(
                        PanelIndex: 0, Name: "parity", Label: "Parity",
                        WavPath: wav, SemanticClass: ScopeSemanticClass.Mixed,
                        WindowWidth: 1, DefaultAmplification: 1.0, DefaultColor: "#7AA4FF"),
                },
                fpsNumerator: 60,
                fpsDenominator: 1);
            Assert.NotNull(source);

            var maskGrid = new byte[overlay.ScopeFrameByteCount];
            source!.ReadFrame(60, maskGrid);

            // The mask contract: waveform lines are full-alpha, background is
            // alpha 0 — identical to the Corrscope transparent-background
            // frames, so the composite treats every source the same.
            int opaque = 0;
            int transparent = 0;
            for (int offset = 0; offset < maskGrid.Length; offset += 4)
            {
                byte alpha = maskGrid[offset + 3];
                Assert.True(alpha is 0 or 255, $"mask alpha must be 0 or 255, got {alpha}");
                if (alpha == 255) opaque++;
                else transparent++;
            }
            Assert.True(transparent > 0, "the mask must have a transparent background");
            Assert.True(opaque > 0, "the mask must carry opaque waveform lines");

            // The waveform visibly contributes: with the mask grid the scope
            // region differs from the empty-grid composite at the line pixels.
            var withMask = new byte[overlay.FrameByteCount];
            var withoutMask = new byte[overlay.FrameByteCount];
            overlay.RenderCompositeFrame(60, maskGrid, withMask);
            overlay.RenderCompositeFrame(60, ReadOnlySpan<byte>.Empty, withoutMask);
            Assert.NotEqual(SHA256.HashData(withMask), SHA256.HashData(withoutMask));
        }
        finally
        {
            File.Delete(wav);
        }
    }

    // ---- helpers ----

    private static (VisualizationFrameRenderer Renderer, RecordingScopeSource Source, PanelOverlayRenderer Overlay) CreateRenderer(
        double? scopeFps, int outputFps)
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        var overlay = new PanelOverlayRenderer(
            timeline,
            RendererTestLayout.Build(timeline),
            new PanelOverlayRenderer.Options { FpsNumerator = outputFps, FpsDenominator = 1 });
        var source = new RecordingScopeSource(overlay.ScopeFrameByteCount);
        var renderer = new VisualizationFrameRenderer(overlay, source, scopeFps);
        return (renderer, source, overlay);
    }

    /// <summary>
    /// Finds a scope-region pixel the playhead and the dynamic pass leave
    /// untouched: the empty-grid composite equals the raw static frame there,
    /// so the blend is the only writer.
    /// </summary>
    private static int FindUntouchedScopePixel(PanelOverlayRenderer overlay, long frameIndex)
    {
        var reference = new byte[overlay.FrameByteCount];
        overlay.RenderCompositeFrame(frameIndex, ReadOnlySpan<byte>.Empty, reference);
        var staticFrame = new byte[overlay.FrameByteCount];
        overlay.WriteStaticFrame(new MemoryStream(staticFrame));
        OverlayLayout layout = overlay.Layout;
        for (int panel = 0; panel < layout.PanelCount; panel++)
        {
            OverlayRect scope = layout.GetScopeRect(panel);
            int playheadX = layout.GetPlayheadX(panel);
            for (int y = scope.Y + 4; y < scope.Bottom - 4; y++)
            for (int x = scope.X + 8; x < scope.Right - 8; x++)
            {
                if (x == playheadX)
                    continue;
                int offset = (y * overlay.Width + x) * 4;
                if (reference[offset] == staticFrame[offset]
                    && reference[offset + 1] == staticFrame[offset + 1]
                    && reference[offset + 2] == staticFrame[offset + 2]
                    && reference[offset + 3] == staticFrame[offset + 3]
                    && reference[offset + 3] == 0)
                {
                    return offset;
                }
            }
        }
        return -1;
    }

    private static string WriteWav(double frequency)
    {
        const int sampleRate = 44_100;
        var samples = new short[sampleRate * 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * 12000);
        string path = Path.Combine(Path.GetTempPath(), $"parity-{Guid.NewGuid():N}.wav");
        using (var wav = new WavWriter(path, sampleRate, 2))
        {
            wav.Write(samples);
            wav.Close();
        }
        return path;
    }

    /// <summary>In-process scope source that records every read and paints each
    /// frame with its own index so content reuse is verifiable.</summary>
    private sealed class RecordingScopeSource : IScopeFrameSource
    {
        private readonly int _byteCount;

        public RecordingScopeSource(int byteCount) => _byteCount = byteCount;

        public List<int> Reads { get; } = new();

        public bool FramesAreOpaque => false;

        public void ReadFrame(int frameIndex, Span<byte> destination)
        {
            Reads.Add(frameIndex);
            // Paint every grid pixel with the frame index so any scope-region
            // pixel reveals which mapped frame was composited.
            for (int offset = 0; offset < destination.Length; offset += 4)
            {
                destination[offset] = (byte)frameIndex;
                destination[offset + 1] = (byte)(frameIndex * 3);
                destination[offset + 2] = (byte)(frameIndex * 7);
                destination[offset + 3] = 255;
            }
        }

        public void Dispose() { }
    }
}
