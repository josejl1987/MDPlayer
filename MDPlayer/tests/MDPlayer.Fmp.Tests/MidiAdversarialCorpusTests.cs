using Fmp.Core.Midi;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Rendering;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using System.Text.Json;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiAdversarialCorpusTests
{
    private static readonly IReadOnlyDictionary<string, string> FixtureLinks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["21 Master Ninja.vgz"] = "master-ninja.vgz",
            ["120 Smash Up.spc"] = "smash-up.spc",
            ["02 Stranger ~ Wandering Swordsman.vgz"] = "corpus/02-stranger.vgz",
            ["05 - Twilight Express.vgz"] = "corpus/05-twilight.vgz",
            ["18 U.S.A. (Ken) I.vgz"] = "corpus/18-usa-ken.vgz",
            ["20 Ninja Yashiki ~ Their Secrets Die With Them.vgz"] = "corpus/20-ninja-yashiki.vgz",
            ["28 - Smoking Head.vgz"] = "corpus/28-smoking-head.vgz",
            ["26 - Robotnik.vgz"] = "robotnik-dac.vgz",
            ["32 Arctic Wind.vgz"] = "corpus/32-arctic-wind.vgz",
            ["positive-control-120-4-4.json"] = "midi/positive-control-120-4-4.json",
            ["positive-control-90-6-8.json"] = "midi/positive-control-90-6-8.json",
        };

    [Fact]
    public void Manifest_ContainsRealFixturesAndReviewableSemanticSidecars()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        Assert.True(File.Exists(path), $"MIDI adversarial manifest is missing: {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement entries = document.RootElement.GetProperty("entries");
        Assert.Equal(FixtureLinks.Count, entries.GetArrayLength());

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string source = entry.GetProperty("source").GetString()!;
            Assert.True(FixtureLinks.TryGetValue(source, out string? linked), source);
            string fixture = Path.Combine(
                AppContext.BaseDirectory, "testfixtures", linked.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fixture), $"Corpus fixture is missing: {source} -> {fixture}");

            string status = entry.GetProperty("reviewStatus").GetString()!;
            JsonElement expected = entry.GetProperty("expected");
            if (status == "reviewed")
            {
                AssertReviewedField(entry, expected, source, "tempo", "allowUnresolvedTempo");
                AssertReviewedField(entry, expected, source, "meter", "allowUnresolvedMeter");
                AssertReviewedField(entry, expected, source, "downbeat", "allowUnresolvedDownbeat");
                // Positive controls must declare a complete reviewed truth —
                // tempo (exact BPM or an octave/beat-grouping family), meter,
                // and a downbeat expectation. A control that only asserts "a
                // downbeat exists" without tempo/meter shape is not a positive
                // control.
                if (TryParsePositiveControlExpectation(entry, out PositiveControlExpectation _))
                    ValidatePositiveControlExpectationShape(entry, source);
            }
            else if (status == "unresolved")
            {
                Assert.True(
                    entry.TryGetProperty("reviewNotes", out JsonElement notes)
                    && notes.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(notes.GetString()),
                    $"Unresolved fixture {source} must record the evidence for abstention.");
                Assert.True(entry.GetProperty("allowUnresolvedTempo").GetBoolean(),
                    $"Unresolved fixture {source} must explicitly allow unresolved tempo.");
                Assert.True(entry.GetProperty("allowUnresolvedMeter").GetBoolean(),
                    $"Unresolved fixture {source} must explicitly allow unresolved meter.");
                Assert.True(entry.GetProperty("allowUnresolvedDownbeat").GetBoolean(),
                    $"Unresolved fixture {source} must explicitly allow unresolved downbeat.");
            }
            else
            {
                Assert.Equal("pending", status);
            }
        }
    }

    [Fact]
    public void RealCorpus_ExecutesCaptureToSerializedSmfAndHonorsTimingContract()
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        string? onlySource = Environment.GetEnvironmentVariable("MDPLAYER_CORPUS_SOURCE");

        JsonElement[] corpusEntries = document.RootElement.GetProperty("entries")
            .EnumerateArray()
            .Where(entry => string.IsNullOrWhiteSpace(onlySource)
                || string.Equals(entry.GetProperty("source").GetString(), onlySource,
                    StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(corpusEntries);
        foreach (JsonElement entry in corpusEntries)
        {
            string source = entry.GetProperty("source").GetString()!;
            string linked = FixtureLinks[source];
            string fixture = Path.Combine(
                AppContext.BaseDirectory, "testfixtures", linked.Replace('/', Path.DirectorySeparatorChar));
            CorpusExecution execution = Execute(fixture, entry);
            VisualizationTimeline timeline = execution.Timeline;
            Assert.True(timeline.EndSample > timeline.StartSample, source);
            Assert.NotEmpty(timeline.Notes);
            Assert.NotEmpty(execution.Export.Bytes);
            AssertCaptureContract(entry, execution, source);
            AssertTimingContract(entry, execution.Timing, source);
        }
    }

    private static CorpusExecution Execute(string fixture, JsonElement entry)
    {
        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-corpus-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(44_100);
        try
        {
            bool isPositiveControl = entry.TryGetProperty("captureMode", out JsonElement captureMode)
                && captureMode.GetString() == "deterministic-positive-control";
            if (isPositiveControl)
            {
                return ExecutePositiveControl(entry);
            }

            bool isSpc = Path.GetExtension(fixture)
                .Equals(".spc", StringComparison.OrdinalIgnoreCase);
            IPlaybackBackend backend = isSpc
                ? new SpcPlaybackBackend()
                : new VgmPlaybackBackend();
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(fixture),
                new PlaybackOptions(
                    LoopCount: 1,
                    FadeSeconds: 0,
                    TailSeconds: 0,
                    MaxDurationSeconds: 60,
                    OutputAudioPath: wav,
                    SampleRate: 44_100),
                sink);
            session.Run();
            VisualizationTimeline timeline = sink.Complete(
                session.SamplePosition, "midi-adversarial-corpus");
            MusicalTimeMapBuildResult timing = MusicalTimeMapBuilder.Build(
                timeline,
                new MusicalTimeMapOptions { DetectTempoChanges = true });
            MidiTranscriptionResult export = new MidiTranscriber(960, timing.Map)
                .Transcribe(timeline);
            IndependentMidiPitchValidator.Validate(timeline, export, 960, timing.Map);
            IndependentMidiPitchValidator.ValidateAbsoluteTiming(timeline, export, 960);
            if (string.Equals(Environment.GetEnvironmentVariable("MDPLAYER_CORPUS_TRACE"), "1", StringComparison.Ordinal))
            {
                Console.WriteLine($"CORPUS {entry.GetProperty("source").GetString()}: samples={timeline.StartSample}..{timeline.EndSample}, "
                    + $"notes={timeline.Notes.Count}, rhythm={timeline.Rhythm.Count}, "
                    + $"tempo={timing.Diagnostics.SelectedBpm:0.###}, "
                    + $"alternative={timing.Diagnostics.AlternativeBpm:0.###}, "
                    + $"ambiguous={timing.Diagnostics.TempoAmbiguous}, "
                    + $"resolved={timing.Diagnostics.TempoResolved}, "
                    + $"meter={timing.Diagnostics.MeterKnown}, downbeat={timing.Diagnostics.DownbeatKnown}");
            }
            return new CorpusExecution(
                timeline,
                timing,
                export,
                isSpc ? "native-spc" : "native-vgm");
        }
        finally
        {
            if (File.Exists(wav))
                File.Delete(wav);
            if (File.Exists(wav + ".tmp"))
                File.Delete(wav + ".tmp");
        }
    }

    private static void AssertCaptureContract(
        JsonElement entry,
        CorpusExecution execution,
        string source)
    {
        if (!entry.TryGetProperty("captureMode", out JsonElement captureMode))
            return;

        string expected = captureMode.GetString()!;
        if (expected == "deterministic-positive-control")
        {
            Assert.Equal(expected, execution.BackendKind);
            return;
        }
        Assert.Equal(expected, execution.BackendKind + (expected == "native-spc-required" ? "-required" : ""));
        Assert.True(execution.Timeline.Notes.Count > 0,
            $"{source} native capture produced no notes.");
    }

    private static void AssertTimingContract(
        JsonElement entry,
        MusicalTimeMapBuildResult timing,
        string source)
    {
        TimingDiagnostics diagnostics = timing.Diagnostics;
        string status = entry.GetProperty("reviewStatus").GetString()!;
        JsonElement expected = entry.GetProperty("expected");
        JsonElement tempo = expected.GetProperty("tempo");
        if (status == "unresolved" && tempo.ValueKind == JsonValueKind.Null)
            Assert.False(diagnostics.TempoResolved, $"{source} unexpectedly resolved tempo.");

        if (tempo.ValueKind == JsonValueKind.Object)
        {
            double[] family = tempo.TryGetProperty("acceptedFamily", out JsonElement acceptedFamily)
                ? acceptedFamily.EnumerateArray().Select(value => value.GetDouble()).ToArray()
                : Array.Empty<double>();
            bool mustBeResolved = tempo.GetProperty("mustBeResolved").GetBoolean();
            bool mustBeAmbiguous = tempo.GetProperty("mustBeAmbiguous").GetBoolean();
            bool hasExactBpm = tempo.TryGetProperty("bpm", out JsonElement bpm)
                && bpm.ValueKind == JsonValueKind.Number;

            // EXACT-tempo mode (decisive evidence): the reviewed single BPM must
            // equal the inferred segment tempo.
            if (hasExactBpm)
                Assert.Equal(
                    bpm.GetDouble(), timing.Map.Segments[0].BeatsPerMinute,
                    precision: 2);

            // FAMILY-level mode (symmetric/ambiguous evidence): the inferred
            // tempo may land on any member of the reviewed octave/beat-grouping
            // family. With mustBeResolved=true the pipeline must still pick a
            // winner, but the winner need not be a single reviewed BPM.
            if (family.Length > 0)
            {
                Assert.NotNull(diagnostics.SelectedBpm);
                Assert.Contains(family, candidate =>
                    Math.Abs(candidate - diagnostics.SelectedBpm!.Value) < 0.01);
                if (mustBeResolved)
                    Assert.Contains(family, candidate =>
                        Math.Abs(candidate - timing.Map.Segments[0].BeatsPerMinute) < 0.01);
            }

            // A reviewed preference documents the human-facing interpretation
            // of an unresolved family; it must not turn an unresolved family
            // into a hidden golden BPM. Enforce it only when the sidecar also
            // requires resolution.
            if (tempo.TryGetProperty("preferred", out JsonElement preferred)
                && preferred.ValueKind == JsonValueKind.Number
                && mustBeResolved)
                Assert.Equal(
                    preferred.GetDouble(), diagnostics.SelectedBpm!.Value,
                    precision: 2);

            Assert.Equal(mustBeAmbiguous, diagnostics.TempoAmbiguous);
            Assert.Equal(mustBeResolved, diagnostics.TempoResolved);
        }

        // Null means that the sidecar has no reviewed answer for that field. It
        // must not silently become a golden guess; only an object with explicit
        // mustBeResolved/mustBeAmbiguous assertions can constrain inference.
        AssertExplicitResolutionContract(expected, "meter", diagnostics.MeterKnown, source);
        AssertExplicitResolutionContract(expected, "downbeat", diagnostics.DownbeatKnown, source);
        if (expected.GetProperty("meter").ValueKind == JsonValueKind.Object
            && expected.GetProperty("meter").GetProperty("mustBeResolved").GetBoolean())
        {
            JsonElement meter = expected.GetProperty("meter");
            // Exact grid is asserted only when the manifest pins
            // numerator/denominator; a resolved-only meter asserts that a meter
            // was inferred, not which one (for beat-grouping-ambiguous evidence
            // the reviewed grid may not be the grid the tracker emits).
            if (meter.TryGetProperty("numerator", out JsonElement num)
                && meter.TryGetProperty("denominator", out JsonElement den))
            {
                Assert.Equal(num.GetInt32(), timing.Map.Meter!.Numerator);
                Assert.Equal(den.GetInt32(), timing.Map.Meter.Denominator);
            }
        }
        if (expected.GetProperty("downbeat").ValueKind == JsonValueKind.Object
            && expected.GetProperty("downbeat").GetProperty("mustBeResolved").GetBoolean())
        {
            Assert.NotNull(timing.Map.FirstDownbeatQuarter);
            // Exact downbeat PHASE, not merely "a downbeat exists": the
            // manifest's reviewed quarter position must match the inferred
            // first downbeat. This is the single validation path shared by
            // synthetic deterministic controls and future real-audio controls.
            JsonElement downbeat = expected.GetProperty("downbeat");
            if (downbeat.TryGetProperty("quarter", out JsonElement quarter))
            {
                Assert.True(timing.Map.FirstDownbeatQuarter.HasValue,
                    $"{source} resolved a downbeat without a quarter position.");
                Assert.Equal(
                    quarter.GetDouble(), timing.Map.FirstDownbeatQuarter!.Value,
                    precision: 6);
            }
        }
    }

    /// <summary>
    /// The single definition of what a positive control asserts. A control
    /// declares its tempo either EXACTLY (a single reviewed BPM, used when the
    /// evidence is decisive) or at FAMILY level (an <c>acceptedFamily</c> of
    /// octave/beat-grouping readings, used when the evidence is symmetric and
    /// the tracker may legitimately land on any member). Meter and downbeat are
    /// each either exact (numerator/denominator / <c>quarter</c> present) or
    /// resolved-only (the field resolves, but the reviewed exact value is
    /// withheld because it is boundary luck). Synthetic deterministic controls
    /// and future real-audio controls both flow through this parser and through
    /// <see cref="AssertTimingContract"/>; the manifest remains the only place
    /// expectations are defined.
    /// </summary>
    private static bool TryParsePositiveControlExpectation(
        JsonElement entry,
        out PositiveControlExpectation expectation)
    {
        expectation = default;
        if (!entry.TryGetProperty("positiveControl", out _))
            return false;

        JsonElement expected = entry.GetProperty("expected");
        if (expected.GetProperty("tempo").ValueKind != JsonValueKind.Object
            || expected.GetProperty("meter").ValueKind != JsonValueKind.Object
            || expected.GetProperty("downbeat").ValueKind != JsonValueKind.Object)
            return false;

        JsonElement tempo = expected.GetProperty("tempo");
        TempoMode mode;
        double? bpm = null;
        double[] family = Array.Empty<double>();
        if (tempo.TryGetProperty("bpm", out JsonElement bpmEl)
            && bpmEl.ValueKind == JsonValueKind.Number)
        {
            mode = TempoMode.Exact;
            bpm = bpmEl.GetDouble();
        }
        else if (tempo.TryGetProperty("acceptedFamily", out JsonElement fam)
                 && fam.ValueKind == JsonValueKind.Array
                 && fam.GetArrayLength() > 0)
        {
            mode = TempoMode.Family;
            family = fam.EnumerateArray().Select(value => value.GetDouble()).ToArray();
        }
        else
        {
            return false; // a control must declare an exact BPM or a family
        }

        JsonElement meter = expected.GetProperty("meter");
        Meter? parsedMeter = null;
        if (meter.TryGetProperty("numerator", out JsonElement num)
            && meter.TryGetProperty("denominator", out JsonElement den))
            parsedMeter = Meter.TryParse(num.GetInt32() + "/" + den.GetInt32())!;

        double? downbeatQuarter = null;
        if (expected.GetProperty("downbeat").TryGetProperty("quarter", out JsonElement quarter))
            downbeatQuarter = quarter.GetDouble();

        expectation = new PositiveControlExpectation(
            mode, bpm, family, parsedMeter, downbeatQuarter);
        return true;
    }

    private static void ValidatePositiveControlExpectationShape(
        JsonElement entry,
        string source)
    {
        Assert.True(TryParsePositiveControlExpectation(entry, out _),
            $"{source} is a positive control and must declare a tempo (exact "
            + "BPM or acceptedFamily), a meter, and a downbeat expectation.");
    }

    private enum TempoMode { Exact, Family }

    private sealed record PositiveControlExpectation(
        TempoMode TempoMode,
        double? Bpm,
        double[] AcceptedFamily,
        Meter? Meter,
        double? DownbeatQuarter)
    {
        /// <summary>
        /// True only when the control pins exact tempo, exact meter, and exact
        /// downbeat phase — the strict contract for decisive evidence (real,
        /// human-reviewed audio). Family-level or resolved-only synthetic
        /// controls are intentionally not exact.
        /// </summary>
        public bool IsExact =>
            TempoMode == TempoMode.Exact && Meter is not null && DownbeatQuarter.HasValue;
    }

    private static CorpusExecution ExecutePositiveControl(JsonElement entry)
    {
        JsonElement control = entry.GetProperty("positiveControl");
        int sampleRate = control.GetProperty("sampleRate").GetInt32();
        double bpm = control.GetProperty("bpm").GetDouble();
        Meter meter = Meter.TryParse(control.GetProperty("meter").GetString())!;
        long quarterSamples = (long)Math.Round(sampleRate * 60.0 / bpm);
        const int tatumsPerQuarter = 4;
        int tatumsPerBar = meter.Denominator == 8
            ? meter.Numerator * 2
            : meter.Numerator * tatumsPerQuarter;
        int tatumCount = tatumsPerBar * 8;
        long tatumSamples = (long)Math.Round(quarterSamples / (double)tatumsPerQuarter);
        var notes = Enumerable.Range(0, tatumCount)
            .Select(index => new NoteEvent(
                "positive-control.voice",
                index * tatumSamples,
                index * tatumSamples + Math.Max(1, tatumSamples / 2),
                440.0 * Math.Pow(2.0, ((60 + (index % 4)) - 69) / 12.0),
                60 + (index % 4),
                "positive-control",
                VisualizationNoteMode.Fm,
                false,
                Array.Empty<PitchChange>()))
            .ToArray();
        var rhythm = new List<RhythmEvent>(tatumCount + tatumCount / 4);
        for (int index = 0; index < tatumCount; index++)
        {
            long sample = index * tatumSamples;
            rhythm.Add(new RhythmEvent("hi-hat", "hi-hat", sample, 0.35f, 0));
            bool downbeat = index % tatumsPerBar == 0;
            bool secondary = meter.Denominator == 8
                ? index % tatumsPerBar == tatumsPerBar / 2
                : index % tatumsPerBar == tatumsPerBar / 4
                    || index % tatumsPerBar == 3 * tatumsPerBar / 4;
            if (downbeat)
                rhythm.Add(new RhythmEvent("kick", "kick", sample, 1.0f, 0));
            else if (secondary)
                rhythm.Add(new RhythmEvent("snare", "snare", sample, 0.9f, 0));
        }
        long barSamples = checked(tatumSamples * tatumsPerBar);
        var timeline = new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = checked(tatumCount * tatumSamples),
            Notes = notes,
            Rhythm = rhythm,
            LoopMarkers = new[]
            {
                new LoopMarker(0, LoopMarkerKind.Start, 0),
                new LoopMarker(checked(barSamples * 4), LoopMarkerKind.Restart, 1),
            },
        };
        // This is intentionally an inference control: the expected BPM/meter
        // describe the generated evidence, but no user timing override is fed
        // to the production decoder.
        MusicalTimeMapBuildResult timing = MusicalTimeMapBuilder.Build(
            timeline,
            new MusicalTimeMapOptions { DetectTempoChanges = true });
        MidiTranscriptionResult export = new MidiTranscriber(960, timing.Map).Transcribe(timeline);
        IndependentMidiPitchValidator.Validate(timeline, export, 960, timing.Map);
        IndependentMidiPitchValidator.ValidateAbsoluteTiming(timeline, export, 960);
        return new CorpusExecution(timeline, timing, export, "deterministic-positive-control");
    }

    private static void AssertExplicitResolutionContract(
        JsonElement expected,
        string field,
        bool actualResolved,
        string source)
    {
        if (!expected.TryGetProperty(field, out JsonElement value)
            || value.ValueKind != JsonValueKind.Object
            || !value.TryGetProperty("mustBeResolved", out JsonElement required))
            return;

        Assert.Equal(required.GetBoolean(), actualResolved);
        if (required.GetBoolean())
            Assert.True(actualResolved, $"{source} did not resolve reviewed {field}.");
    }

    private static void AssertReviewedField(
        JsonElement entry,
        JsonElement expected,
        string source,
        string field,
        string unresolvedFlag)
    {
        if (expected.GetProperty(field).ValueKind == JsonValueKind.Null)
        {
            Assert.True(entry.GetProperty(unresolvedFlag).GetBoolean(),
                $"Reviewed fixture {source} has unresolved {field} without {unresolvedFlag}=true.");
        }
    }

    private sealed record CorpusExecution(
        VisualizationTimeline Timeline,
        MusicalTimeMapBuildResult Timing,
        MidiTranscriptionResult Export,
        string BackendKind);

    /// <summary>
    /// The REAL-music positive controls this corpus requires (reviewed
    /// audio-derived fixtures, never synthesized stand-ins). The repository
    /// currently contains NO reviewed real-music audio assets, so these are
    /// documented BLOCKED items awaiting reviewer-provided files; fabricating
    /// stand-ins labeled as real would violate the integrity rule.
    /// </summary>
    internal static readonly IReadOnlyList<(string Id, string Requirement)>
        RequiredRealAudioControls = new[]
        {
            ("real-4-4-a",
                "Resolved 4/4 track #1: unambiguous tempo, meter, and downbeat "
                + "phase confirmed by human review."),
            ("real-4-4-b",
                "Resolved 4/4 track #2: independent confirmation at a "
                + "different tempo."),
            ("real-compound",
                "Resolved 3/4 or 6/8 track: compound/triple meter with "
                + "reviewed downbeat phase."),
            ("real-half-double-ambiguous",
                "True half/double-tempo ambiguous case: review confirms BOTH "
                + "tempos are defensible and the tracker must abstain or "
                + "retain the family."),
            ("real-sparse-unresolved",
                "True sparse/unresolved case: review confirms the evidence is "
                + "insufficient and the pipeline must abstain explicitly."),
        };

    /// <summary>
    /// Validation contract for REAL-audio positive controls. When reviewer-
    /// provided assets exist, each manifest entry marked with captureMode
    /// "real-audio-positive-control" MUST be reviewed, MUST be exact (exact
    /// tempo, exact meter, and exact downbeat quarter phase — decisive,
    /// human-reviewed audio is never family-level or resolved-only), and its
    /// audio asset MUST be present. Until every required control exists, the
    /// test skips with the precise list of needed assets so the gap stays
    /// visible without blocking CI on assets nobody has supplied yet.
    /// </summary>
    [SkippableFact]
    public void RealAudioPositiveControls_ArePresentReviewedAndExact()
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));

        var presentIds = new List<string>();
        foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            if (!entry.TryGetProperty("captureMode", out JsonElement captureMode)
                || captureMode.GetString() != "real-audio-positive-control")
                continue;

            string source = entry.GetProperty("source").GetString()!;
            string id = entry.TryGetProperty("controlId", out JsonElement controlId)
                ? controlId.GetString()!
                : source;
            Assert.Equal("reviewed", entry.GetProperty("reviewStatus").GetString());
            Assert.True(
                TryParsePositiveControlExpectation(entry, out PositiveControlExpectation expectation)
                    && expectation.IsExact,
                $"{source}: real-audio positive control must pin exact tempo, "
                + "exact meter, and exact downbeat quarter phase (decisive, "
                + "human-reviewed audio is never family-level or resolved-only).");
            JsonElement expected = entry.GetProperty("expected");
            Assert.True(
                expected.GetProperty("tempo").GetProperty("mustBeResolved").GetBoolean()
                || entry.TryGetProperty("expectedAbstention", out JsonElement abstention)
                && abstention.GetBoolean(),
                $"{source}: control must either require resolution or declare "
                + "expectedAbstention=true explicitly.");

            string linked = FixtureLinks.TryGetValue(source, out string? link)
                ? link
                : $"corpus/real-audio/{source}";
            string asset = Path.Combine(AppContext.BaseDirectory,
                ("testfixtures/" + linked).Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(asset),
                $"{source}: reviewed real-audio control is present in the manifest "
                + $"but its audio asset is missing: {asset}");
            presentIds.Add(id);
        }

        var missing = RequiredRealAudioControls
            .Where(required => !presentIds.Contains(required.Id))
            .Select(required => $"{required.Id}: {required.Requirement}")
            .ToList();
        Skip.IfNot(missing.Count == 0,
            "BLOCKED on reviewer-provided REAL music assets. The repository "
            + "contains no reviewed real-music audio; synthesizing stand-ins "
            + "labeled as real is forbidden. Required controls:\n"
            + string.Join("\n", missing));
    }
}
