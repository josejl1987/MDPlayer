using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Opt-in production-render benchmark for the V3 §25 thresholds. It is
/// skipped in ordinary test runs because frame-time limits are machine
/// dependent; performance CI enables it with MDPLAYER_RUN_PERF=1.
/// </summary>
public sealed class VisualizationPerformanceTests
{
    private const int SampleRate = 1_000;
    private const int WarmupFrames = 120;
    private const int BenchmarkFrames = 120;

    [SkippableFact]
    public void DenseUnifiedOverlayMeetsConfiguredP95Budget()
    {
        Skip.If(
            Environment.GetEnvironmentVariable("MDPLAYER_RUN_PERF") != "1",
            "Set MDPLAYER_RUN_PERF=1 to run the machine-dependent renderer benchmark.");

        foreach ((int width, int height, double p95BudgetMs) in new[]
        {
            (1280, 720, 3.0),
            (1920, 1080, 6.0),
        })
        {
            VisualizationTimeline timeline = CreateDenseTimeline();
            Stopwatch preparation = Stopwatch.StartNew();
            var renderer = new PanelOverlayRenderer(
                timeline,
                new PanelOverlayRenderer.Options
                {
                    Width = width,
                    Height = height,
                    FpsNumerator = 60,
                    LayoutMode = VisualizationLayoutMode.UnifiedRoll,
                    Channels = VisualizationChannelFilter.Active,
                    Effects = EffectsMode.Minimal,
                });
            preparation.Stop();

            byte[] destination = new byte[renderer.FrameByteCount];
            SequentialCompositeSession session = renderer.CreateSequentialSession();
            session.Initialize(destination);
            for (int frame = 0; frame < WarmupFrames; frame++)
                session.RenderNext(frame, ReadOnlySpan<byte>.Empty, destination);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int frame = 0; frame < 30; frame++)
                session.RenderNext(frame + WarmupFrames, ReadOnlySpan<byte>.Empty, destination);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            Assert.True(
                allocated == 0,
                $"{width}x{height} allocated {allocated} managed bytes after warm-up.");

            var samples = new double[BenchmarkFrames];
            for (int frame = 0; frame < BenchmarkFrames; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                session.RenderNext(frame + WarmupFrames, ReadOnlySpan<byte>.Empty, destination);
                samples[frame] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            }

            Array.Sort(samples);
            double p95 = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
            Assert.True(
                preparation.Elapsed <= TimeSpan.FromSeconds(5),
                $"{width}x{height} preparation took {preparation.Elapsed.TotalSeconds:F3}s.");
            Assert.True(
                p95 <= p95BudgetMs,
                $"{width}x{height} dynamic overlay p95 was {p95:F3}ms; budget is {p95BudgetMs:F3}ms.");
        }
    }

    private static VisualizationTimeline CreateDenseTimeline()
    {
        DeviceId device = new(ChipType.Unknown, 0);
        VoiceDescriptor[] voices = Enumerable.Range(0, 12)
            .Select(index => new VoiceDescriptor(
                new VoiceId(device, VoiceKind.Fm, index),
                $"VOICE {index + 1}",
                VoicePresentationKind.Fm,
                index,
                false,
                false,
                true))
            .ToArray();

        const int notesPerVoice = 24;
        var notes = new List<NoteEvent>(12 * notesPerVoice);
        for (int voice = 0; voice < voices.Length; voice++)
        {
            string channel = voices[voice].Id.ToString();
            for (int index = 0; index < notesPerVoice; index++)
            {
                long start = 250L + index * 120L + voice * 7L;
                long end = start + 96;
                double midi = 48 + (voice % 4) * 7 + (index % 8);
                notes.Add(new NoteEvent(
                    channel,
                    start,
                    end,
                    440 * Math.Pow(2, (midi - 69) / 12),
                    midi,
                    "benchmark-instrument",
                    VisualizationNoteMode.Fm,
                    index > 0,
                    Array.Empty<PitchChange>()));
            }
        }

        return new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = 4_000,
            Voices = voices,
            Notes = notes,
            Instruments =
            [
                new InstrumentDefinition(
                    "benchmark-instrument",
                    "fm",
                    4,
                    3,
                    0,
                    2,
                    Array.Empty<FmOperatorDefinition>()),
            ],
        };
    }
}
