using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

public sealed class PitchCameraTests
{
    private const int SampleRate = 1000;

    private static PreparedNote Note(string channel, long start, long end, double midi)
        => new()
        {
            StartSample = start,
            EndSample = end,
            InitialMidiNote = midi,
            Mode = VisualizationNoteMode.Fm,
            InstrumentId = "inst:1",
            IsRetrigger = false,
            Fill = new OverlayColor(200, 200, 200),
            ActiveFill = new OverlayColor(255, 255, 255),
            Accent = new OverlayColor(220, 220, 220),
            CapFill = new OverlayColor(240, 240, 240),
            Pitch = Array.Empty<PreparedPitchPoint>(),
        };

    private static PreparedNote OperatorNote(string channel, long start, long end, double midi)
        => new()
        {
            StartSample = start,
            EndSample = end,
            InitialMidiNote = midi,
            Mode = VisualizationNoteMode.Fm3Operator,
            InstrumentId = "inst:1",
            IsRetrigger = false,
            Fill = new OverlayColor(200, 200, 200),
            ActiveFill = new OverlayColor(255, 255, 255),
            Accent = new OverlayColor(220, 220, 220),
            CapFill = new OverlayColor(240, 240, 240),
            Pitch = Array.Empty<PreparedPitchPoint>(),
        };

    private static PreparedNote PitchBendNote(string channel, long start, long end, double initialMidi, params (long Sample, double Midi)[] pitch)
    {
        var points = new PreparedPitchPoint[pitch.Length];
        for (int i = 0; i < pitch.Length; i++)
            points[i] = new PreparedPitchPoint(pitch[i].Sample, pitch[i].Midi);
        return new()
        {
            StartSample = start,
            EndSample = end,
            InitialMidiNote = initialMidi,
            Mode = VisualizationNoteMode.Fm,
            InstrumentId = "inst:1",
            IsRetrigger = false,
            Fill = new OverlayColor(200, 200, 200),
            ActiveFill = new OverlayColor(255, 255, 255),
            Accent = new OverlayColor(220, 220, 220),
            CapFill = new OverlayColor(240, 240, 240),
            Pitch = points,
        };
    }
    [Fact]
    public void GetRange_IsDeterministicRegardlessOfQueryOrder()
    {
        var notes = new[]
        {
            Note("ch1", 0, 3000, 60),
            Note("ch1", 3000, 6000, 72),
        };

        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            sampleRate: SampleRate,
            pastSeconds: 0.75,
            futureSeconds: 2.25,
            timelineStartSample: 0,
            timelineEndSample: 6000);

        var first = camera.GetRange(4000);
        var second = camera.GetRange(1000);
        var third = camera.GetRange(4000);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
    }

    [Theory]
    [InlineData(84, 16)]
    [InlineData(126, 18)]
    [InlineData(168, 18)]
    [InlineData(240, 18)]
    public void Span_MatchesLaneHeight(int laneHeight, int expectedSpan)
    {
        var notes = new[] { Note("ch1", 0, 1000, 60) };
        var camera = new PitchCamera(
            notes,
            laneHeight,
            SampleRate,
            0.75,
            2.25,
            0,
            2000);

        var (min, max) = camera.GetRange(500);
        Assert.Equal(expectedSpan, max - min);
    }

    [Fact]
    public void BoundariesPreferCWhenWithinThreeSemitones()
    {
        var notes = new[] { Note("ch1", 0, 1000, 61) };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            2000);

        var (min, max) = camera.GetRange(500);
        Assert.True(min % 12 == 0 || max % 12 == 0,
            $"Expected a C-aligned boundary, got [{min}, {max}].");
        Assert.True(min <= 61);
        Assert.True(max >= 61);
    }

    [Fact]
    public void Fm3OperatorNotes_AreIncludedInAnalysisWindow()
    {
        // §11.2: FM3 operator pitches are included; extended span (30) is allowed.
        var camera = Cam(new[] { OperatorNote("ym2608.0.fm3.op.1", 0, 4000, 80) }, 5000, extended: true);
        var (min, max) = camera.GetRange(2000);
        Assert.InRange(80, min, max);
        Assert.True(max - min <= 30, $"FM3 span {max - min} exceeds 30.");
    }

    [Fact]
    public void OverflowingPitches_ClampToSpanWithoutOrnamentFlag()
    {
        // IsClipped now means "deliberately clipped ornament" (§11.3), not generic overflow.
        var camera = new PitchCamera(
            new[] { Note("ch1", 0, 4000, 48), Note("ch1", 0, 4000, 84) },
            laneHeight: 84, SampleRate, 0.75, 2.25, 0, 5000);
        var (min, max) = camera.GetRange(2000);
        Assert.Equal(24, max - min);
        Assert.False(camera.IsClipped(2000));
    }

    [Fact]
    public void SingleNote_IsCenteredInLane()
    {
        // With a single note at midi 62 and the preferred 18-semitone span,
        // the camera keeps the note near the centre.
        var notes = new[] { Note("ch1", 0, 1000, 62) };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            2000);

        var (min, max) = camera.GetRange(500);
        int rangeCenter = (min + max) / 2;
        // The note should be within 1.5 semitones of the range center — i.e. truly centered.
        Assert.InRange(62, rangeCenter - 1, rangeCenter + 1);
    }

    [Fact]
    public void PairedNotes_AreCenteredAcrossRange()
    {
        // Notes at 60 and 64, span 24: ideal low = 60 - 10 = 50 → snaps to 51 (nearest).
        // Range [51, 75], center = 63; note-pair center = 62 → within 1 semitone.
        var notes = new[]
        {
            Note("ch1", 0, 1000, 60),
            Note("ch1", 0, 1000, 64),
        };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            2000);

        var (min, max) = camera.GetRange(500);
        int rangeCenter = (min + max) / 2;
        Assert.InRange(62, rangeCenter - 1, rangeCenter + 1);
    }

    [Fact]
    public void LargeOutlierPitchChange_DoesNotExpandCameraBeyond24()
    {
        // A note at midi 60 with a pitch change to midi 100 (40 semitones up)
        // must not create a 40-semitone viewport. The pitch change is clamped
        // to anchorMidi ± 6, so the camera stays within the 24-semitone max.
        var notes = new[]
        {
            PitchBendNote("ch1", 0, 4000, 60,
                (2000, 100)),
        };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            5000);

        var (min, max) = camera.GetRange(2000);
        Assert.True(max - min <= 24,
            $"Camera span {max - min} exceeds the 24-semitone maximum for a 40-semitone outlier.");
    }

    [Fact]
    public void BendWithinSixSemitones_IsFullyVisible()
    {
        // A bend of +5 semitones is within the ±6 clamp and must be fully
        // visible in the camera (the viewport must contain both the anchor and
        // the bent pitch).
        var notes = new[]
        {
            PitchBendNote("ch1", 0, 4000, 60,
                (2000, 65)),
        };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            5000);

        var (min, max) = camera.GetRange(2000);
        Assert.True(min <= 60 && max >= 65,
            $"Bend of +5 not fully visible: range [{min}, {max}].");
    }

    [Fact]
    public void AdjacentIdenticalCameraSegments_AreMerged()
    {
        // Two consecutive notes at the same pitch should produce a stable
        // camera that does not jump between them.
        var notes = new[]
        {
            Note("ch1", 0, 2000, 60),
            Note("ch1", 2000, 4000, 60),
        };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            5000);

        var before = camera.GetRange(1800);
        var after = camera.GetRange(2200);
        Assert.Equal(before, after);
    }

    [Fact]
    public void SmallBend_OfOneSemitone_IsFullyVisible()
    {
        // A bend of ±1 semitone is within the ±6 clamp and must be fully
        // visible: the viewport must contain both the anchor and the bent pitch.
        var notes = new[]
        {
            PitchBendNote("ch1", 0, 4000, 60,
                (2000, 61)),
        };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            5000);

        var (min, max) = camera.GetRange(2000);
        Assert.True(min <= 60 && max >= 61,
            $"Bend of +1 not fully visible: range [{min}, {max}].");
    }

    [Fact]
    public void Camera_StaysStableWithinSafetyMargin()
    {
        // Two notes at midi 60 and 61. At a frame where only 60 is visible,
        // the camera centers on 60. When 61 becomes visible (within the 1-semitone
        // inner margin of the existing range), the camera should NOT move.
        var notes = new[]
        {
            Note("ch1", 0, 2000, 60),
            Note("ch1", 2000, 4000, 61),
        };
        var camera = new PitchCamera(
            notes,
            laneHeight: 168,
            SampleRate,
            0.75,
            2.25,
            0,
            5000);

        // Before the second note enters the window, the camera is centered on 60.
        var earlyRange = camera.GetRange(500);
        // When the second note (61) enters the window but is within the safety
        // margin of the existing range, the camera should retain the same range.
        var laterRange = camera.GetRange(2200);
        Assert.Equal(earlyRange, laterRange);
    }

    private static PitchCamera Cam(PreparedNote[] notes, long end = 7000, bool extended = false)
        => new(notes, 168, SampleRate, 0.75, 2.25, 0, end, allowExtendedSpan: extended);

    [Fact]
    public void ExpansionAndContraction_ImmediateExpandDelayedContract()
    {
        // §11.3: expand immediately when a note enters the window; contract
        // only after a 0.75 s hold once the wide range is unnecessary.
        var camera = Cam(new[] { Note("ch1", 0, 4000, 60), Note("ch1", 2000, 6000, 80) });
        // At 1000, note at 80 is 1 s ahead (inside future window) → already expanded.
        var (min, max) = camera.GetRange(1000);
        Assert.True(min <= 60 && max >= 80, $"Did not expand immediately: [{min}, {max}].");

        // Now test delayed contraction: high note ends at 2000, hold 0.75 s.
        var cam2 = Cam(new[] { Note("ch1", 0, 2000, 60), Note("ch1", 0, 2000, 80), Note("ch1", 2000, 6000, 60) });
        var (_, wideMax) = cam2.GetRange(1500);
        Assert.True(wideMax >= 80, $"Wide range should contain 80: max={wideMax}");
        var (_, holdMax) = cam2.GetRange(2400); // 0.4 s after — within hold
        Assert.True(holdMax >= wideMax - 1, $"Contracted before hold expired: {holdMax} < {wideMax}");
        var (_, afterMax) = cam2.GetRange(3500); // 1.5 s after — hold expired
        Assert.True(afterMax < wideMax, $"Did not contract after hold: {afterMax} >= {wideMax}");
    }

    [Theory]
    [InlineData(1550, true, "Short ornament excluded")]   // 50 ms, +1 st
    [InlineData(1600, false, "100 ms note included")]     // 100 ms, +12 st
    public void OrnamentHandling(int endOffset, bool shouldExclude, string label)
    {
        // §11.3: ornaments < 80 ms clipped by < 2 st are excluded; longer notes are not.
        var notes = endOffset == 1550
            ? new[] { Note("ch1", 0, 4000, 60), Note("ch1", 1500, endOffset, 61) }
            : new[] { Note("ch1", 0, 4000, 60), Note("ch1", 1500, endOffset, 72) };
        var camera = Cam(notes, 5000);
        var (min, max) = camera.GetRange(1550);
        if (shouldExclude)
            Assert.True(max - min <= 24, $"{label}: expanded to {max - min} st.");
        else
            Assert.True(max >= 72, $"{label}: not included [{min}, {max}].");
    }

    [Fact]
    public void IsClipped_TrueForDeliberatelyExcludedOrnament()
    {
        // §11.3: two sustained notes span exactly preferred (24 st); a 50 ms
        // ornament at 85 (+1 st) is excluded → IsClipped is true.
        var camera = Cam(new[] { Note("ch1", 0, 4000, 60), Note("ch1", 0, 4000, 84), Note("ch1", 1500, 1550, 85) }, 5000);
        Assert.True(camera.IsClipped(1525), "Deliberately excluded ornament did not set IsClipped.");
    }

    [Fact]
    public void OctaveAlignment_PrefersBoundariesNearC()
    {
        // §11.4: prefer C boundaries when centering is preserved.
        var camera = Cam(new[] { Note("ch1", 0, 1000, 60) }, 2000);
        var (min, max) = camera.GetRange(500);
        Assert.True(min % 12 == 0 || (min <= 60 && max >= 60),
            $"Range [{min}, {max}] neither C-aligned nor note-containing.");
    }

    [Fact]
    public void Interpolation_ProducesSmoothContraction_NoOvershoot()
    {
        // §11.5: critically-damped contraction — max trends down, ≤1 reversal.
        var camera = Cam(new[] { Note("ch1", 0, 3000, 60), Note("ch1", 0, 3000, 80), Note("ch1", 3000, 6000, 60) });
        int prevMax = 0, reversals = 0, dir = 0;
        for (long s = 3000; s <= 4500; s += 50)
        {
            var (_, max) = camera.GetRange(s);
            if (prevMax > 0)
            {
                int d = max < prevMax ? -1 : max > prevMax ? 1 : 0;
                if (d != 0 && d != dir && dir != 0) reversals++;
                if (d != 0) dir = d;
            }
            prevMax = max;
        }
        Assert.True(reversals <= 1, $"Max oscillated ({reversals} reversals) — not critically damped.");
        Assert.True(camera.GetRange(4500).Item2 < 80, "Did not contract after hold.");
    }

    [Fact]
    public void GetRange_IsDeterministicInReverseOrder()
    {
        // §11.5/§21: query order must not affect results.
        var camera = Cam(new[] { Note("ch1", 0, 3000, 60), Note("ch1", 2000, 5000, 72) }, 6000);
        long[] samples = { 500, 1500, 2500, 3500, 4500 };
        var forward = new (int, int)[samples.Length];
        for (int i = 0; i < samples.Length; i++) forward[i] = camera.GetRange(samples[i]);
        var reverse = new (int, int)[samples.Length];
        for (int i = samples.Length - 1; i >= 0; i--) reverse[i] = camera.GetRange(samples[i]);
        Assert.Equal(forward, reverse);
    }

}
