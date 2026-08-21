using System.Diagnostics;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using Xunit;

namespace MDPlayer.Fmp.Tests.Visualization.Rendering;

/// <summary>
/// Differential parity and performance guards for the PitchCamera target
/// sweep. <see cref="LegacyPitchCamera"/> below is a verbatim copy of the
/// legacy per-frame implementation (pre-sweep) and is the reference oracle:
/// the sweep must produce bit-identical public output (GetRange,
/// GetPreciseRange, IsClipped, SampleToFrame) at every frame sample and at
/// arbitrary sample queries. Do not "fix" the legacy copy — it is the gate.
/// </summary>
public sealed class PitchCameraSweepTests
{
    private const int SampleRate = 44100;
    private const long OrnamentThresholdSamples = 3528; // (long)(0.080 * 44100) at 44100 Hz

    public enum PointsMode
    {
        None,
        Dense,
        BeforeStart,
        NegativeMidi,
    }

    public sealed record Config(
        int Seed,
        int NoteCount,
        double DurationSeconds,
        int FpsNumerator,
        int FpsDenominator,
        double PastSeconds,
        double FutureSeconds,
        int LaneHeight,
        bool AllowExtendedSpan,
        PointsMode Points,
        bool IncludeNaNNote)
    {
        public static Config C(
            int seed, int noteCount, double seconds, int fpsNumerator, int fpsDenominator,
            double pastSeconds, double futureSeconds, int laneHeight, bool allowExtendedSpan,
            PointsMode points, bool includeNanNote = false)
            => new(seed, noteCount, seconds, fpsNumerator, fpsDenominator, pastSeconds, futureSeconds,
                laneHeight, allowExtendedSpan, points, includeNanNote);
    }

    // 10 configs spanning: N ∈ {0, 1, 7, 200, 800}; Q ∈ {0, 5·N, points before
    // StartSample, points with MidiNote < 0}; negative/NaN/out-of-range
    // InitialMidiNote; SSG-noise modes; durations straddling the 80 ms
    // ornament threshold; 3–40 s timelines @ 44100 Hz; fps ∈ {60/1, 30/1,
    // 60000/1001}; past/future ∈ {0.75/2.25, 0/0, 1.5/1.5}; laneHeight ∈
    // {84, 130, 168, 200} (drives preferredSpan); allowExtendedSpan both ways.
    private static readonly Config[] ParityConfigList =
    {
        Config.C(1, 0, 3, 60, 1, 0.75, 2.25, 168, false, PointsMode.None),
        Config.C(2, 1, 5, 30, 1, 0.0, 0.0, 84, true, PointsMode.Dense),
        Config.C(3, 7, 8, 60000, 1001, 1.5, 1.5, 130, false, PointsMode.BeforeStart, includeNanNote: true),
        Config.C(4, 200, 15, 60, 1, 0.75, 2.25, 84, true, PointsMode.Dense),
        Config.C(5, 200, 10, 30, 1, 0.0, 0.0, 200, false, PointsMode.NegativeMidi),
        Config.C(6, 800, 40, 60, 1, 0.75, 2.25, 168, false, PointsMode.Dense),
        Config.C(7, 800, 20, 60000, 1001, 1.5, 1.5, 130, true, PointsMode.BeforeStart, includeNanNote: true),
        Config.C(8, 7, 4, 60, 1, 0.75, 2.25, 200, true, PointsMode.NegativeMidi),
        Config.C(9, 1, 3, 30, 1, 0.75, 2.25, 84, false, PointsMode.Dense),
        Config.C(10, 200, 40, 60, 1, 0.0, 0.0, 84, true, PointsMode.Dense),
    };

    public static IEnumerable<object[]> ParityConfigs =>
        ParityConfigList.Select(config => new object[] { config });

    [Theory]
    [MemberData(nameof(ParityConfigs))]
    public void Sweep_Parity_MatchesLegacyAtEveryFrameAndRandomSamples(Config config)
    {
        var notes = GenerateNotes(config, out long timelineEnd);
        var legacy = BuildLegacy(notes, config, timelineEnd);
        var camera = BuildCamera(notes, config, timelineEnd);

        long totalFrames = FrameSampleClock.FrameCount(
            timelineEnd, SampleRate, config.FpsNumerator, config.FpsDenominator);
        for (long frame = 0; frame < totalFrames; frame++)
        {
            long sample = FrameSampleClock.SampleAtFrame(
                frame, SampleRate, config.FpsNumerator, config.FpsDenominator);
            AssertCameraMatchesLegacy(camera, legacy, sample, config);
        }

        var rng = new Random(config.Seed + 77_777);
        for (int i = 0; i < 200; i++)
        {
            long sample = rng.Next((int)(timelineEnd + 4000)) - 2000;
            AssertCameraMatchesLegacy(camera, legacy, sample, config);
        }
    }

    [Theory]
    [MemberData(nameof(ParityConfigs))]
    public void SampleToFrame_MatchesLegacyBinarySearch(Config config)
    {
        var notes = GenerateNotes(config, out long timelineEnd);
        var legacy = BuildLegacy(notes, config, timelineEnd);
        var camera = BuildCamera(notes, config, timelineEnd);

        Check(-5);
        Check(-1);
        Check(0);
        Check(1);
        Check(timelineEnd - 1);
        Check(timelineEnd);
        Check(timelineEnd + 1);
        Check(timelineEnd + 5);

        for (long sample = -3; sample <= timelineEnd + 3; sample += 3)
            Check(sample);

        long totalFrames = FrameSampleClock.FrameCount(
            timelineEnd, SampleRate, config.FpsNumerator, config.FpsDenominator);
        for (long frame = 0; frame < totalFrames; frame++)
        {
            long boundary = FrameSampleClock.SampleAtFrame(
                frame, SampleRate, config.FpsNumerator, config.FpsDenominator);
            Check(boundary - 1);
            Check(boundary);
            Check(boundary + 1);
        }

        void Check(long sample)
        {
            AssertEqual(legacy.SampleToFrame(sample), camera.SampleToFrame(sample),
                $"SampleToFrame mismatch at sample {sample}.");
        }
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void Construction_IsFast_OnLongRealisticTimeline()
    {
        // 33.09 s @ 44100 Hz, 2062 notes with realistic 60–120 ms durations
        // over a ~3.5 s active window (0.75 s past + 2.25 s future + 0.5 s
        // additional lookahead), 1520 pitch points, 60 fps.
        var rng = new Random(0xC0FFEE);
        long timelineEnd = 1_459_269; // 33.09 s @ 44100 Hz
        const int noteCount = 2062;
        const int pitchPointCount = 1520;
        var notes = new PreparedNote[noteCount];
        for (int i = 0; i < noteCount; i++)
        {
            int anchor = rng.Next(36, 96);
            long start = (long)i * timelineEnd / noteCount + rng.Next(200);
            long duration = 2646 + rng.Next(2647); // 60–120 ms
            var pitch = Array.Empty<PreparedPitchPoint>();
            if (i < pitchPointCount)
            {
                pitch = new[]
                {
                    new PreparedPitchPoint(start + duration / 2, anchor + (rng.Next(7) - 3)),
                };
            }
            notes[i] = CreateNote(start, start + duration, anchor, VisualizationNoteMode.Fm, pitch);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        _ = new PitchCamera(
            notes,
            laneHeight: 168,
            sampleRate: SampleRate,
            pastSeconds: 0.75,
            futureSeconds: 2.25,
            timelineStartSample: 0,
            timelineEndSample: timelineEnd);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Construction took {stopwatch.Elapsed.TotalMilliseconds:F0} ms (budget 2000 ms).");
    }

    [Fact]
    [Trait("Category", "Heavy")]
    public void Construction_SweepBeatsLegacyScan_OnLongRealisticTimeline()
    {
        // Same workload as Construction_IsFast: the quadratic legacy oracle
        // (per-frame full note scan) versus the monotonic sweep. Documents the
        // before/after ratio; the margin only grows with timeline length.
        var rng = new Random(0xC0FFEE);
        long timelineEnd = 1_459_269;
        const int noteCount = 2062;
        const int pitchPointCount = 1520;
        var notes = new PreparedNote[noteCount];
        for (int i = 0; i < noteCount; i++)
        {
            int anchor = rng.Next(36, 96);
            long start = (long)i * timelineEnd / noteCount + rng.Next(200);
            long duration = 2646 + rng.Next(2647);
            var pitch = Array.Empty<PreparedPitchPoint>();
            if (i < pitchPointCount)
                pitch = new[] { new PreparedPitchPoint(start + duration / 2, anchor + (rng.Next(7) - 3)) };
            notes[i] = CreateNote(start, start + duration, anchor, VisualizationNoteMode.Fm, pitch);
        }

        var legacyStopwatch = Stopwatch.StartNew();
        _ = BuildLegacy(notes, Config.C(1, noteCount, 33.09, 60, 1, 0.75, 2.25, 168, false, PointsMode.Dense), timelineEnd);
        legacyStopwatch.Stop();

        var sweepStopwatch = Stopwatch.StartNew();
        _ = BuildCamera(notes, Config.C(1, noteCount, 33.09, 60, 1, 0.75, 2.25, 168, false, PointsMode.Dense), timelineEnd);
        sweepStopwatch.Stop();

        double ratio = legacyStopwatch.Elapsed.TotalMilliseconds
            / Math.Max(0.01, sweepStopwatch.Elapsed.TotalMilliseconds);
        Assert.True(ratio > 20,
            $"expected >=20x speedup, got {ratio:F1}x (legacy {legacyStopwatch.Elapsed.TotalMilliseconds:F0} ms, sweep {sweepStopwatch.Elapsed.TotalMilliseconds:F0} ms)");
    }

    private static void AssertCameraMatchesLegacy(
        PitchCamera camera, LegacyPitchCamera legacy, long sample, Config config)
    {
        AssertEqual(legacy.SampleToFrame(sample), camera.SampleToFrame(sample),
            $"SampleToFrame mismatch at sample {sample}.");
        AssertEqual(legacy.GetRange(sample), camera.GetRange(sample),
            $"GetRange mismatch at sample {sample}.");
        AssertEqual(legacy.GetPreciseRange(sample), camera.GetPreciseRange(sample),
            $"GetPreciseRange mismatch at sample {sample}.");
        AssertEqual(legacy.IsClipped(sample), camera.IsClipped(sample),
            $"IsClipped mismatch at sample {sample}.");
    }

    /// <summary>Exact equality with a diagnostic message (the gate is bit-exact parity).</summary>
    private static void AssertEqual<T>(T expected, T actual, string because)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Xunit.Sdk.XunitException(
                $"{because} expected {expected}, actual {actual}.");
    }

    private static PitchCamera BuildCamera(PreparedNote[] notes, Config config, long timelineEnd)
        => new(
            notes,
            config.LaneHeight,
            SampleRate,
            config.PastSeconds,
            config.FutureSeconds,
            0,
            timelineEnd,
            config.FpsNumerator,
            config.FpsDenominator,
            config.AllowExtendedSpan,
            1.0);

    private static LegacyPitchCamera BuildLegacy(PreparedNote[] notes, Config config, long timelineEnd)
        => new(
            notes,
            config.LaneHeight,
            SampleRate,
            config.PastSeconds,
            config.FutureSeconds,
            0,
            timelineEnd,
            config.FpsNumerator,
            config.FpsDenominator,
            config.AllowExtendedSpan,
            1.0);

    private static PreparedNote[] GenerateNotes(Config config, out long timelineEnd)
    {
        var rng = new Random(config.Seed);
        timelineEnd = (long)(config.DurationSeconds * SampleRate);
        var notes = new PreparedNote[config.NoteCount];
        for (int i = 0; i < config.NoteCount; i++)
        {
            double initialMidi = PickInitialMidi(rng, config.IncludeNaNNote && i == 0);
            var mode = PickMode(rng);
            long start = (long)(rng.NextDouble() * timelineEnd);
            long duration = PickDuration(rng);
            long end = Math.Min(timelineEnd + 5000, start + duration);
            notes[i] = CreateNote(
                start, end, initialMidi, mode, GeneratePoints(rng, config.Points, start, end));
        }
        return notes;
    }

    private static PreparedNote CreateNote(
        long start, long end, double initialMidi, VisualizationNoteMode mode, PreparedPitchPoint[] pitch)
        => new()
        {
            StartSample = start,
            EndSample = end,
            InitialMidiNote = initialMidi,
            Mode = mode,
            InstrumentId = "inst:1",
            IsRetrigger = false,
            Fill = new OverlayColor(200, 200, 200),
            ActiveFill = new OverlayColor(255, 255, 255),
            Accent = new OverlayColor(220, 220, 220),
            CapFill = new OverlayColor(240, 240, 240),
            Pitch = pitch,
        };

    private static double PickInitialMidi(Random rng, bool forceNan)
    {
        if (forceNan)
            return double.NaN;
        switch (rng.Next(10))
        {
            case 0:
                return -rng.Next(1, 20) - rng.NextDouble(); // negative → excluded from the camera
            case 1:
                return 128 + rng.Next(0, 80); // out-of-range high anchor
            case 2:
                return rng.Next(0, 128) + 0.5; // banker's-rounding tie
            case 3:
                return rng.Next(0, 128) + rng.NextDouble(); // fractional
            default:
                return rng.Next(0, 128);
        }
    }

    private static VisualizationNoteMode PickMode(Random rng)
    {
        switch (rng.Next(100))
        {
            case < 16: return VisualizationNoteMode.SsgNoise;
            case < 32: return VisualizationNoteMode.SsgEnvelopeNoise;
            case < 47: return VisualizationNoteMode.Fm3Operator;
            case < 57: return VisualizationNoteMode.SsgTone;
            case < 67: return VisualizationNoteMode.SsgEnvelopeTone;
            case < 77: return VisualizationNoteMode.Pcm;
            case < 87: return VisualizationNoteMode.Midi;
            default: return VisualizationNoteMode.Fm;
        }
    }

    private static long PickDuration(Random rng)
    {
        switch (rng.Next(10))
        {
            case 0:
            case 1:
            case 2:
                return 50 + (long)(rng.NextDouble() * 300); // short ornament, well under 80 ms
            case 3:
            case 4:
                return OrnamentThresholdSamples - 28 + rng.Next(60); // straddles the 80 ms threshold
            default:
                return 2646 + (long)(rng.NextDouble() * 41454); // 60 ms – 1 s
        }
    }

    private static PreparedPitchPoint[] GeneratePoints(
        Random rng, PointsMode mode, long start, long end)
    {
        switch (mode)
        {
            case PointsMode.None:
                return Array.Empty<PreparedPitchPoint>();
            case PointsMode.Dense:
            {
                // Deliberately left unsorted: the sweep must not depend on
                // input point order (the legacy per-frame scan does not).
                var points = new PreparedPitchPoint[5];
                for (int k = 0; k < 5; k++)
                {
                    long position = start - 1000 + rng.Next((int)(end - start + 2000) + 1);
                    points[k] = new PreparedPitchPoint(position, PickPointMidi(rng));
                }
                return points;
            }
            case PointsMode.BeforeStart:
            {
                // Edge case: a point strictly BEFORE the note's StartSample
                // must be included once the lookahead passes it.
                var points = new List<PreparedPitchPoint>
                {
                    new(start - 1 - rng.Next(4999), rng.Next(0, 128)),
                };
                if (rng.Next(2) == 0)
                    points.Add(new(start + (int)(rng.NextDouble() * (end - start)), rng.Next(0, 128)));
                return points.ToArray();
            }
            case PointsMode.NegativeMidi:
            {
                // All points excluded (MidiNote < 0): the anchor alone drives
                // the note extent.
                var points = new PreparedPitchPoint[3];
                for (int k = 0; k < 3; k++)
                {
                    long position = start + (int)(rng.NextDouble() * (end - start + 3000)) - 1500;
                    points[k] = new PreparedPitchPoint(position, -(rng.Next(1, 30) + rng.NextDouble()));
                }
                return points;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    private static double PickPointMidi(Random rng)
    {
        switch (rng.Next(20))
        {
            case 0:
                return -rng.Next(1, 30) - rng.NextDouble(); // excluded (MidiNote < 0)
            case 1:
                return 128 + rng.Next(0, 80); // clamped to 127
            case 2:
                return rng.Next(0, 128) + 0.5; // banker's-rounding tie
            case 3:
                return rng.Next(0, 128) + rng.NextDouble(); // fractional
            default:
                return rng.Next(0, 128);
        }
    }

    // ---------------------------------------------------------------------
    // Legacy oracle: verbatim copy of the pre-sweep PitchCamera pipeline
    // (per-frame PrecomputeTarget + unchanged downstream stages + binary
    // search SampleToFrame). Kept for differential testing only.
    // ---------------------------------------------------------------------
    private sealed class LegacyPitchCamera
    {
        private const int MinSpan = 12;
        private const int MaxSpan = 24;
        private const int Fm3MaxSpan = 30;
        private const double ContractionHoldSeconds = 0.75;
        private const double OrnamentThresholdSeconds = 0.080;
        private const double AdditionalLookaheadSeconds = 0.50;
        private const double OrnamentClipThresholdSemitones = 2.0;
        private const double DampingTimeConstantSeconds = 0.045;
        private const int DefaultMinMidi = 48;
        private const int DefaultMaxMidi = 72;

        private readonly FrameRange[] _frames;
        private readonly long _timelineStartSample;
        private readonly long _timelineEndSample;
        private readonly int _totalFrames;

        private sealed record FrameRange(long FromSample, double MinMidi, double MaxMidi, bool IsClipped, bool IsEmpty);

        public LegacyPitchCamera(
            PreparedNote[] mainNotes,
            int laneHeight,
            int sampleRate,
            double pastSeconds,
            double futureSeconds,
            long timelineStartSample,
            long timelineEndSample,
            int fpsNumerator = 60,
            int fpsDenominator = 1,
            bool allowExtendedSpan = false,
            double rollZoom = 1.0)
        {
            if (!double.IsFinite(rollZoom) || rollZoom <= 0)
                throw new ArgumentOutOfRangeException(nameof(rollZoom));
            _timelineStartSample = timelineStartSample;
            _timelineEndSample = timelineEndSample;
            int preferredSpan = Math.Clamp(
                (int)Math.Round(laneHeight / (7.0 * rollZoom)),
                rollZoom > 1.0 ? MinSpan : 16,
                18);
            int maxSpan = allowExtendedSpan ? Fm3MaxSpan : MaxSpan;

            _totalFrames = Math.Max(1, checked((int)FrameSampleClock.FrameCount(
                Math.Max(0, timelineEndSample - timelineStartSample),
                sampleRate,
                fpsNumerator,
                fpsDenominator)));

            _frames = new FrameRange[_totalFrames];
            RawTarget[] targets = new RawTarget[_totalFrames];
            for (int frame = 0; frame < _totalFrames; frame++)
            {
                long currentSample = Math.Min(
                    timelineEndSample,
                    timelineStartSample + FrameSampleClock.SampleAtFrame(
                        frame, sampleRate, fpsNumerator, fpsDenominator));
                targets[frame] = PrecomputeTarget(
                    mainNotes, currentSample, sampleRate, pastSeconds, futureSeconds, preferredSpan);
            }

            ApplyDelayedContraction(targets, sampleRate);

            for (int i = 0; i < targets.Length; i++)
            {
                ref RawTarget t = ref targets[i];
                if (t.IsEmpty) continue;
                (int low, int high) = AlignToOctave(
                    (int)Math.Round(t.MinMidi),
                    (int)Math.Round(t.MaxMidi),
                    preferredSpan,
                    maxSpan);
                t.MinMidi = low; t.MaxMidi = high;
            }

            ApplyDampedInterpolation(targets, sampleRate);

            for (int i = 0; i < _totalFrames; i++)
            {
                RawTarget t = targets[i];
                _frames[i] = t.IsEmpty
                    ? new FrameRange(t.FromSample, 0, 0, false, true)
                    : new FrameRange(t.FromSample, t.MinMidi, t.MaxMidi, t.IsClipped, false);
            }
        }

        public (int MinMidi, int MaxMidi) GetRange(long currentSample)
        {
            var f = _frames[SampleToFrame(currentSample)];
            return f.IsEmpty
                ? (DefaultMinMidi, DefaultMaxMidi)
                : ((int)Math.Round(f.MinMidi), (int)Math.Round(f.MaxMidi));
        }

        public (double MinMidi, double MaxMidi) GetPreciseRange(long currentSample)
        {
            var f = _frames[SampleToFrame(currentSample)];
            return f.IsEmpty ? (DefaultMinMidi, DefaultMaxMidi) : (f.MinMidi, f.MaxMidi);
        }

        public bool IsClipped(long currentSample)
            => _frames[SampleToFrame(currentSample)].IsClipped;

        public int SampleToFrame(long currentSample)
        {
            if (currentSample <= _timelineStartSample) return 0;
            if (currentSample >= _timelineEndSample) return _totalFrames - 1;
            int lo = 0, hi = _totalFrames - 1;
            while (lo < hi)
            {
                int mid = lo + (hi - lo + 1) / 2;
                if (_frames[mid].FromSample <= currentSample) lo = mid;
                else hi = mid - 1;
            }
            return lo;
        }

        private struct RawTarget(long fromSample)
        {
            public long FromSample = fromSample;
            public double MinMidi, MaxMidi;
            public double? ActiveMinMidi, ActiveMaxMidi;
            public bool IsEmpty = true, IsClipped;
        }

        private readonly record struct NoteExtent(int Min, int Max, long Duration);

        /// <summary>Returns the excluded edge value if the sole edge contributor is a short ornament saving &lt; 2 st.</summary>
        private static bool TryExcludeOrnament(int edge, int without, bool onlyOrnament, int sentinel, out int result)
        {
            result = without;
            if (!onlyOrnament || without == sentinel) return false;
            int savings = Math.Abs(edge - without);
            return savings > 0 && savings < OrnamentClipThresholdSemitones;
        }

        private static RawTarget PrecomputeTarget(
            PreparedNote[] notes, long currentSample, int sampleRate,
            double pastSeconds, double futureSeconds, int preferredSpan)
        {
            long windowStart = currentSample - (long)(pastSeconds * sampleRate);
            long windowEnd = currentSample + (long)(futureSeconds * sampleRate);
            long lookaheadEnd = windowEnd + (long)(AdditionalLookaheadSeconds * sampleRate);
            long ornamentSamples = (long)(OrnamentThresholdSeconds * sampleRate);

            int noteCount = 0;
            double? activeMin = null;
            double? activeMax = null;
            Span<NoteExtent> extents = notes.Length <= 128
                ? stackalloc NoteExtent[notes.Length]
                : new NoteExtent[notes.Length];

            foreach (PreparedNote note in notes)
            {
                if (note.InitialMidiNote < 0) continue;
                // §11.2: FM3 operator pitches included; only pure-noise SSG excluded.
                if (note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise)
                    continue;
                if (note.EndSample <= windowStart || note.StartSample > lookaheadEnd)
                    continue;

                int anchorMidi = (int)Math.Round(note.InitialMidiNote);
                int noteMin = anchorMidi, noteMax = anchorMidi;
                foreach (PreparedPitchPoint pc in note.Pitch)
                {
                    if (pc.MidiNote < 0 || pc.SamplePosition > lookaheadEnd) continue;
                    // Keep the actual prepared contour in the camera extent. The
                    // §11.3 ornament exclusion below surfaces deliberate exclusions
                    // via IsClipped; important content wider than the preferred span
                    // expands the range and is not flagged as clipped.
                    int pitchMidi = Math.Clamp((int)Math.Round(pc.MidiNote), 0, 127);
                    if (pitchMidi < noteMin) noteMin = pitchMidi;
                    if (pitchMidi > noteMax) noteMax = pitchMidi;
                }
                extents[noteCount++] = new NoteExtent(noteMin, noteMax, note.EndSample - note.StartSample);
                if (note.StartSample <= currentSample && currentSample < note.EndSample)
                {
                    activeMin = activeMin is double activeMinValue
                        ? Math.Min(activeMinValue, noteMin)
                        : noteMin;
                    activeMax = activeMax is double activeMaxValue
                        ? Math.Max(activeMaxValue, noteMax)
                        : noteMax;
                }
            }

            if (noteCount == 0)
                return new RawTarget(currentSample);

            int fullMin = int.MaxValue, fullMax = int.MinValue;
            for (int i = 0; i < noteCount; i++)
            {
                if (extents[i].Min < fullMin) fullMin = extents[i].Min;
                if (extents[i].Max > fullMax) fullMax = extents[i].Max;
            }

            // §11.3 ornament rule: when the full span exceeds preferred, a short
            // ornament (< 80 ms) at an edge contributing < 2 st is excluded.
            int minMidi = fullMin, maxMidi = fullMax;
            bool clipped = false;
            if (fullMax - fullMin > preferredSpan)
            {
                int highWithout = int.MinValue, lowWithout = int.MaxValue;
                bool highOnlyOrnament = true, lowOnlyOrnament = true;
                for (int i = 0; i < noteCount; i++)
                {
                    bool isOrnament = extents[i].Duration < ornamentSamples;
                    if (extents[i].Max < fullMax) highWithout = Math.Max(highWithout, extents[i].Max);
                    else if (!isOrnament) highOnlyOrnament = false;
                    if (extents[i].Min > fullMin) lowWithout = Math.Min(lowWithout, extents[i].Min);
                    else if (!isOrnament) lowOnlyOrnament = false;
                }
                if (TryExcludeOrnament(fullMax, highWithout, highOnlyOrnament, int.MinValue, out int newMax))
                { maxMidi = newMax; clipped = true; }
                if (TryExcludeOrnament(lowWithout, fullMin, lowOnlyOrnament, int.MaxValue, out int newMin))
                { minMidi = newMin; clipped = true; }
            }

            return new RawTarget(currentSample)
            {
                MinMidi = minMidi,
                MaxMidi = maxMidi,
                ActiveMinMidi = activeMin,
                ActiveMaxMidi = activeMax,
                IsEmpty = false,
                IsClipped = clipped,
            };
        }

        /// <summary>§11.3: expand immediately; contract only after a 0.75 s hold.</summary>
        private static void ApplyDelayedContraction(RawTarget[] targets, int sampleRate)
        {
            long holdSamples = (long)(ContractionHoldSeconds * sampleRate);
            int? heldMin = null, heldMax = null;
            long lastExpansionSample = -1;

            for (int i = 0; i < targets.Length; i++)
            {
                ref RawTarget t = ref targets[i];
                if (t.IsEmpty)
                {
                    heldMin = null; heldMax = null; lastExpansionSample = -1;
                    continue;
                }

                int targetMin = (int)Math.Round(t.MinMidi), targetMax = (int)Math.Round(t.MaxMidi);
                if (!heldMin.HasValue)
                {
                    heldMin = targetMin; heldMax = targetMax;
                    lastExpansionSample = t.FromSample;
                }
                else
                {
                    bool expands = targetMin < heldMin.Value || targetMax > heldMax.Value;
                    bool withinHold = t.FromSample - lastExpansionSample < holdSamples;
                    if (expands || withinHold)
                    {
                        // Expand immediately; during hold only widen (never narrow).
                        heldMin = Math.Min(heldMin.Value, targetMin);
                        heldMax = Math.Max(heldMax.Value, targetMax);
                    }
                    else
                    {
                        heldMin = targetMin; heldMax = targetMax;
                        lastExpansionSample = t.FromSample;
                    }
                    if (expands) lastExpansionSample = t.FromSample;
                }
                t.MinMidi = heldMin.Value; t.MaxMidi = heldMax.Value;
            }
        }

        /// <summary>
        /// §11.4: prefer boundaries near C, but only inside the containment
        /// interval — alignment never pushes a note out of the half-open window.
        /// </summary>
        private static (int Low, int High) AlignToOctave(int minMidi, int maxMidi, int preferredSpan, int maxSpan)
        {
            // A half-open [low, high) window needs range+1 rows to contain both
            // endpoints; sizing from range alone dropped the top boundary pitch.
            int required = maxMidi - minMidi + 1;
            // The preferred/max spans are readability targets, not permission to
            // hide an important sounding pitch. A bend or simultaneous note that
            // exceeds the preferred range expands the viewport for this target;
            // contraction can restore the readable span after the hold interval.
            int span = Math.Max(preferredSpan, required);

            if (required > span)
            {
                // Content cannot fit under the hard maximum: anchor to the top so
                // the extreme boundary pitch itself stays visible.
                return (maxMidi - span + 1, maxMidi + 1);
            }

            // Any low inside [lowMin, lowMax] keeps every note visible. The
            // centered position is the default, with the odd spare semitone going
            // below the content so the lowest voice never hugs the bottom edge.
            // A C-aligned boundary replaces it only within three semitones.
            int lowMin = maxMidi - span + 1;
            int lowMax = minMidi;
            int idealLow = Math.Clamp(minMidi - (span - required + 1) / 2, lowMin, lowMax);

            int best = idealLow;
            int bestDistance = 4;
            for (int candidate = lowMin; candidate <= lowMax; candidate++)
            {
                bool lowIsC = Mod(candidate, 12) == 0;
                bool highIsC = Mod(candidate + span, 12) == 0;
                if (!lowIsC && !highIsC)
                    continue;
                int distance = Math.Abs(candidate - idealLow);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            return (best, best + span);
        }

        private static int Mod(int value, int modulus)
        {
            int result = value % modulus;
            return result < 0 ? result + modulus : result;
        }

        /// <summary>
        /// §11.5: analytical critically-damped interpolation in both directions.
        /// The camera keeps continuous position and velocity state; integer MIDI
        /// values are produced only by the compatibility accessor above.
        /// </summary>
        private void ApplyDampedInterpolation(RawTarget[] targets, int sampleRate)
        {
            if (targets.Length <= 1) return;
            double durationSeconds = Math.Max(1, _timelineEndSample - _timelineStartSample) / (double)sampleRate;
            double frameSeconds = durationSeconds / Math.Max(1, _totalFrames - 1);
            double angularFrequency = 1.0 / DampingTimeConstantSeconds;

            double curMin = targets[0].IsEmpty ? DefaultMinMidi : targets[0].MinMidi;
            double curMax = targets[0].IsEmpty ? DefaultMaxMidi : targets[0].MaxMidi;
            double minVelocity = 0;
            double maxVelocity = 0;
            for (int i = 1; i < targets.Length; i++)
            {
                ref RawTarget t = ref targets[i];
                double targetMin = t.IsEmpty ? DefaultMinMidi : t.MinMidi;
                double targetMax = t.IsEmpty ? DefaultMaxMidi : t.MaxMidi;

                StepCriticallyDamped(
                    ref curMin,
                    ref minVelocity,
                    targetMin,
                    frameSeconds,
                    angularFrequency);
                StepCriticallyDamped(
                    ref curMax,
                    ref maxVelocity,
                    targetMax,
                    frameSeconds,
                    angularFrequency);

                // Lookahead expansion remains damped for visual continuity. Once
                // a note is actually sounding, the active-content guard prevents
                // the camera from clipping its pitch even if damping has not yet
                // reached the wider future target.
                if (t.ActiveMinMidi is double activeMin
                    && t.ActiveMaxMidi is double activeMax)
                {
                    if (curMin > activeMin)
                        curMin = activeMin;
                    if (curMax < activeMax)
                        curMax = activeMax;
                }

                if (curMax - curMin < MinSpan)
                {
                    double center = (curMin + curMax) / 2;
                    curMin = center - MinSpan / 2;
                    curMax = curMin + MinSpan;
                }
                t.MinMidi = curMin; t.MaxMidi = curMax;
            }
        }

        private static void StepCriticallyDamped(
            ref double position,
            ref double velocity,
            double target,
            double deltaSeconds,
            double angularFrequency)
        {
            double offset = position - target;
            double decay = Math.Exp(-angularFrequency * deltaSeconds);
            double nextOffset = (offset + (velocity + angularFrequency * offset) * deltaSeconds) * decay;
            double nextVelocity = (velocity - angularFrequency
                * (velocity + angularFrequency * offset) * deltaSeconds) * decay;
            position = target + nextOffset;
            velocity = nextVelocity;
        }
    }
}
