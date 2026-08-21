using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization;

/// <summary>
/// Patch-1 synchronization contract: one canonical frame↔sample clock.
///
/// Every semantic event family (notes, rhythm, PCM/DAC, loop markers) must
/// contact the playhead at the frame whose canonical sample is within half a
/// frame of the event's source sample. Fractional frame rates (60000/1001) are
/// part of the contract because sloppy double conversions expose themselves
/// exactly there.
/// </summary>
public sealed class FrameSampleClockTests
{
    private const int Rate = 48_000;

    public static TheoryData<int, int> FrameRates => new()
    {
        { 60, 1 },
        { 30, 1 },
        { 25, 1 },
        { 60000, 1001 }, // ≈ 59.94 — the fractional case
    };

    [Fact]
    public void SixtyFps_MapsFramesToExactSamples()
    {
        // 48000 / 60 = 800 samples per frame.
        Assert.Equal(0, FrameSampleClock.SampleAtFrame(0, Rate, 60, 1));
        Assert.Equal(735, FrameSampleClock.SampleAtFrame(1, 44_100, 60, 1));
        Assert.Equal(800, FrameSampleClock.SampleAtFrame(1, Rate, 60, 1));
        Assert.Equal(1600, FrameSampleClock.SampleAtFrame(2, Rate, 60, 1));
    }

    [Fact]
    public void NoteAt24000_ContactsPlayheadAtFrame30_OnSixtyFps()
    {
        // The core acceptance example: sampleRate=48000, fps=60,
        // note.StartSample=24000 → contact at frame 30 (exactly 0.5 s).
        long contactFrame = ContactFrame(24_000, Rate, 60, 1);
        Assert.Equal(30, contactFrame);
        Assert.Equal(24_000, FrameSampleClock.SampleAtFrame(contactFrame, Rate, 60, 1));
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void ContactInvariant_HoldsForEveryEventFamily(int fpsNumerator, int fpsDenominator)
    {
        const string voiceId = "ym2608.0.fm.1";
        var notes = new NoteEvent[]
        {
            Note(voiceId, 0, 5_000),
            Note(voiceId, 24_000, 30_000),
            Note(voiceId, 61_234, 70_000),
            Note(voiceId, 123_456, 130_000),
        };
        var rhythm = new RhythmEvent[]
        {
            Rhythm(voiceId, 12_000),
            Rhythm(voiceId, 36_001),
            Rhythm(voiceId, 90_002),
        };
        var pcm = new Ppz8Event[]
        {
            Pcm(voiceId, 8_000, 12_000),
            Pcm(voiceId, 55_555, 60_000),
        };
        var loops = new LoopMarker[]
        {
            new(48_000, LoopMarkerKind.Restart, 1),
            new(144_000, LoopMarkerKind.Restart, 2),
        };

        foreach (long sample in notes.Select(n => n.StartSample)
                     .Concat(rhythm.Select(r => r.SamplePosition))
                     .Concat(pcm.Select(p => p.StartSample))
                     .Concat(loops.Select(l => l.SamplePosition)))
        {
            AssertContactsWithinHalfFrame(sample, Rate, fpsNumerator, fpsDenominator);
        }
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void SampleAtFrame_IsStrictlyMonotonic(int fpsNumerator, int fpsDenominator)
    {
        long previous = FrameSampleClock.SampleAtFrame(0, Rate, fpsNumerator, fpsDenominator);
        for (long frame = 1; frame <= 5_000; frame++)
        {
            long current = FrameSampleClock.SampleAtFrame(frame, Rate, fpsNumerator, fpsDenominator);
            Assert.True(current > previous, $"frame {frame} must map past frame {frame - 1}");
            previous = current;
        }
    }

    [Fact]
    public void FractionalFrameRate_DoesNotDriftFromDecimalReference()
    {
        // Independent decimal reference computation; a double-based
        // implementation accumulates error over long renders at 59.94 fps.
        for (long frame = 0; frame <= 20_000; frame += 7)
        {
            decimal expected = Math.Round(
                (decimal)frame * Rate * 1001m / 60000m,
                0,
                MidpointRounding.AwayFromZero);
            Assert.Equal((long)expected, FrameSampleClock.SampleAtFrame(frame, Rate, 60000, 1001));
        }
    }

    [Fact]
    public void FrameCount_CoversAudioExactly_AndMatchesRendererArithmetic()
    {
        // 10 s @ 44.1 kHz, 60 fps → exactly 600 frames.
        Assert.Equal(600, FrameSampleClock.FrameCount(44_100 * 10, 44_100, 60, 1));
        // 10 s @ 48 kHz, 59.94 fps → 599.4… → 600 frames (ceiling).
        Assert.Equal(600, FrameSampleClock.FrameCount(Rate * 10, Rate, 60000, 1001));
        Assert.Equal(0, FrameSampleClock.FrameCount(0, Rate, 60, 1));
    }

    [Theory]
    [MemberData(nameof(FrameRates))]
    public void LastFrame_NeverOvershootsAudioDuration(int fpsNumerator, int fpsDenominator)
    {
        long totalSamples = Rate * 37 + 123; // deliberately not frame-aligned
        long frames = FrameSampleClock.FrameCount(totalSamples, Rate, fpsNumerator, fpsDenominator);

        Assert.True(
            FrameSampleClock.SampleAtFrame(frames - 1, Rate, fpsNumerator, fpsDenominator)
            < totalSamples + Rate * fpsDenominator / fpsNumerator,
            "last rendered frame must still play audio inside the render duration");
    }

    private static void AssertContactsWithinHalfFrame(long sample, int rate, int fpsNum, int fpsDen)
    {
        long contactFrame = ContactFrame(sample, rate, fpsNum, fpsDen);
        long mapped = FrameSampleClock.SampleAtFrame(contactFrame, rate, fpsNum, fpsDen);
        double samplesPerFrame = (double)rate * fpsDen / fpsNum;
        double tolerance = Math.Ceiling(samplesPerFrame / 2);
        Assert.True(
            Math.Abs(mapped - sample) <= tolerance,
            $"sample {sample} contacted at frame {contactFrame} maps to {mapped} " +
            $"(tolerance {tolerance})");
    }

    /// <summary>Nearest frame to the instant a given sample plays.</summary>
    private static long ContactFrame(long sample, int rate, int fpsNum, int fpsDen)
        => (long)Math.Round((decimal)sample * fpsNum / (rate * fpsDen), 0, MidpointRounding.AwayFromZero);

    private static NoteEvent Note(string voiceId, long start, long end) => new(
        voiceId, start, end, 440.0, 69.0, "instrument", VisualizationNoteMode.Fm, false, []);

    private static RhythmEvent Rhythm(string voiceId, long sample) =>
        new("bd", voiceId, sample, 0.8f, 0f);

    private static Ppz8Event Pcm(string voiceId, long start, long end) => new(
        0, start, end, null, null, null, 1.0, null, null, 1f, 0f, false);
}
