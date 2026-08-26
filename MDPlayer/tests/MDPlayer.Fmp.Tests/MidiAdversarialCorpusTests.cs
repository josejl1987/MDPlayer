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
            ["positive-control-100-3-4.json"] = "midi/positive-control-100-3-4.json",
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

    [Fact]
    public void RealAudioPositiveControls_DocumentObservedTempoEvidence()
    {
        // Every real-track abstention must be backed by the tracker's actual
        // observed output, so "unresolved" is a documented verdict, not a lazy
        // escape hatch. The reviewNotes must cite the observed tempo and its
        // alternative (when one exists); if the tracker ever starts resolving
        // these, the reviewNotes stop matching and the human must re-review.
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        foreach (JsonElement entry in document.RootElement.GetProperty("entries").EnumerateArray())
        {
            string source = entry.GetProperty("source").GetString()!;
            if (source.StartsWith("positive-control-", StringComparison.Ordinal))
                continue;

            string linked = FixtureLinks[source];
            string fixture = Path.Combine(
                AppContext.BaseDirectory, "testfixtures", linked.Replace('/', Path.DirectorySeparatorChar));
            CorpusExecution execution = Execute(fixture, entry);
            double? observed = execution.Timing.Diagnostics.SelectedBpm;
            double? alternative = execution.Timing.Diagnostics.AlternativeBpm;
            string notes = entry.GetProperty("reviewNotes").GetString()!;

            if (observed is double tempo)
                Assert.True(notes.Contains(FormatBpm(tempo), StringComparison.Ordinal),
                    $"{source}: reviewNotes must cite the observed tempo {tempo:0.###}.");
            if (alternative is double alt)
                Assert.True(notes.Contains(FormatBpm(alt), StringComparison.Ordinal),
                    $"{source}: reviewNotes must cite the observed alternative {alt:0.###}.");
            // Abstention is only legal when the manifest explicitly allows it:
            // either the entry is still unresolved, or it declares the reviewed
            // abstention expectation (e.g. the sparse Stranger control). Reviewed
            // exact controls (U.S.A., Smash Up) must NOT allow unresolved fields.
            bool abstaining = entry.GetProperty("reviewStatus").GetString() == "unresolved"
                || (entry.TryGetProperty("expectedAbstention", out JsonElement expectedAbstention)
                    && expectedAbstention.GetBoolean());
            if (abstaining)
            {
                Assert.True(
                    entry.GetProperty("allowUnresolvedTempo").GetBoolean()
                    && entry.GetProperty("allowUnresolvedMeter").GetBoolean()
                    && entry.GetProperty("allowUnresolvedDownbeat").GetBoolean(),
                    $"{source}: real-track abstention must allow unresolved tempo/meter/downbeat.");
            }
        }
    }

    private static string FormatBpm(double bpm) =>
        bpm == Math.Round(bpm) ? bpm.ToString("0") : bpm.ToString("0.###");

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
            // The export uses the fixed transport only; the frozen musical map
            // stays on the timing-contract side (G3 gate) and never feeds MIDI.
            MidiTranscriptionResult export = new MidiTranscriber().Transcribe(timeline);
            IndependentMidiPitchValidator.Validate(timeline, export);
            IndependentMidiPitchValidator.ValidateAbsoluteTiming(timeline, export);
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
        string source = entry.GetProperty("source").GetString()!;
        long quarterSamples = (long)Math.Round(sampleRate * 60.0 / bpm);
        const int tatumsPerQuarter = 4;
        int tatumsPerBar = meter.Denominator == 8
            ? meter.Numerator * 2
            : meter.Numerator * tatumsPerQuarter;
        int tatumCount = tatumsPerBar * 8;
        long tatumSamples = (long)Math.Round(quarterSamples / (double)tatumsPerQuarter);

        // Evidence designs reviewed per control: each pattern must drive the
        // DBN to a single resolved reading (asymmetric beats kill the half/double
        // alternatives; bar-periodic accents with no 4/4 backbeat symmetry pin the
        // downbeat). The tempo/meter/downbeat expectations live in the manifest.
        (int Tatum, float Strength, string Kind)[] rhythm = source switch
        {
            "positive-control-120-4-4.json" => new[]
            {
                (0, 2.0f, "kick"), (4, 1.0f, "snare"), (12, 0.25f, "snare"),
                (0, 0.35f, "hi-hat"), (2, 0.35f, "hi-hat"), (4, 0.35f, "hi-hat"),
                (6, 0.35f, "hi-hat"), (8, 0.35f, "hi-hat"), (10, 0.35f, "hi-hat"),
                (12, 0.35f, "hi-hat"), (14, 0.35f, "hi-hat"),
            },
            "positive-control-90-6-8.json" => new[]
            {
                (0, 1.5f, "kick"), (6, 0.8f, "kick"),
                (0, 0.5f, "hi-hat"), (2, 0.5f, "hi-hat"), (4, 0.5f, "hi-hat"),
                (6, 0.5f, "hi-hat"), (8, 0.5f, "hi-hat"), (10, 0.5f, "hi-hat"),
            },
            "positive-control-100-3-4.json" => new[]
            {
                (0, 2.0f, "kick"), (4, 1.0f, "snare"), (8, 1.0f, "snare"),
                (0, 0.6f, "hi-hat"), (2, 0.35f, "hi-hat"), (4, 0.6f, "hi-hat"),
                (6, 0.35f, "hi-hat"), (8, 0.6f, "hi-hat"), (10, 0.35f, "hi-hat"),
            },
            _ => throw new InvalidOperationException($"Unhandled positive control {source}"),
        };
        int[] bassTatums = source switch
        {
            "positive-control-120-4-4.json" => new[] { 0 },
            "positive-control-90-6-8.json" => new[] { 0, 6 },
            "positive-control-100-3-4.json" => new[] { 0 },
            _ => Array.Empty<int>(),
        };
        int[] noteTatums = source switch
        {
            "positive-control-120-4-4.json" => new[] { 0 },
            "positive-control-90-6-8.json" => new[] { 0, 6 },
            "positive-control-100-3-4.json" => new[] { 0 },
            _ => Array.Empty<int>(),
        };
        var notes = noteTatums
            .Select(tatum => new NoteEvent(
                "positive-control.voice",
                tatum * tatumSamples,
                tatum * tatumSamples + Math.Max(1, tatumSamples / 2),
                440.0 * Math.Pow(2.0, ((bassTatums.Contains(tatum) ? 36 : 60 + (tatum % 4)) - 69) / 12.0),
                bassTatums.Contains(tatum) ? 36 : 60 + (tatum % 4),
                "positive-control",
                VisualizationNoteMode.Fm,
                false,
                Array.Empty<PitchChange>()))
            .ToArray();
        var rhythmEvents = new List<RhythmEvent>(tatumCount + tatumCount / 4);
        foreach ((int tatum, float strength, string kind) in rhythm)
        {
            for (int index = tatum; index < tatumCount; index += tatumsPerBar)
            {
                rhythmEvents.Add(new RhythmEvent(
                    kind, kind, index * tatumSamples, strength, 0));
            }
        }
        long barSamples = checked(tatumSamples * tatumsPerBar);
        var timeline = new VisualizationTimeline
        {
            SampleRate = sampleRate,
            StartSample = 0,
            EndSample = checked(tatumCount * tatumSamples),
            Notes = notes,
            Rhythm = rhythmEvents.ToArray(),
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
        MidiTranscriptionResult export = new MidiTranscriber().Transcribe(timeline);
        IndependentMidiPitchValidator.Validate(timeline, export);
        IndependentMidiPitchValidator.ValidateAbsoluteTiming(timeline, export);
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
    /// The FOUR human-confirmed REAL-music controls this corpus requires
    /// (reviewed audio-derived fixtures, never synthesized stand-ins). Each is
    /// wired into <see cref="RealAudioPositiveControls_ArePresentReviewedAndExact"/>
    /// as a MANDATORY gate member — no skip, ever. The ternary 3/4-or-6/8 slot
    /// is NOT here: it is still OPEN and is tracked separately by
    /// <see cref="RealCompoundControl_TernarySlot_RequiresRealAsset"/> so it
    /// stays visible without being silently merged into this gate.
    /// </summary>
    internal enum RealControlKind { ExactTiming, FamilyAmbiguous, ExpectedAbstention }

    internal static readonly IReadOnlyList<(string Id, string Source, RealControlKind Kind)>
        ConfirmedRealAudioControls = new[]
        {
            ("real-4-4-a",
                "18 U.S.A. (Ken) I.vgz",
                RealControlKind.ExactTiming),
            ("real-4-4-b",
                "120 Smash Up.spc",
                RealControlKind.ExactTiming),
            ("real-half-double-ambiguous",
                "20 Ninja Yashiki ~ Their Secrets Die With Them.vgz",
                RealControlKind.FamilyAmbiguous),
            ("real-sparse-unresolved",
                "02 Stranger ~ Wandering Swordsman.vgz",
                RealControlKind.ExpectedAbstention),
        };

    /// <summary>
    /// The OPEN ternary (3/4 or 6/8) real-control slot. Kept SEPARATE from the
    /// four-control gate: a real asset is still required, and fabricating or
    /// substituting a synthetic control for it is forbidden. The requirement
    /// stays visible as its own test that skips with this message until a real
    /// reviewed asset lands.
    /// </summary>
    internal const string OpenTernaryControlRequirement =
        "real-compound: resolved 3/4 or 6/8 real track with reviewed downbeat "
        + "phase. OPEN — a real asset is still required; do NOT fabricate or "
        + "substitute a synthetic control for this slot.";

    /// <summary>
    /// MANDATORY gate for the four human-confirmed real controls. It asserts
    /// each control is present in the manifest, is human-reviewed, and meets
    /// its EXACT documented timing expectations — by running the real capture
    /// pipeline, not by trusting the sidecar. This gate does NOT skip.
    /// U.S.A. and Smash Up are currently RED because the tracker abstains on
    /// all real tracks (TempoResolved/MeterKnown/DownbeatKnown false); that red
    /// is the honest driver for the next algorithm-fix stream — the assertions
    /// must not be tuned to paper over it.
    /// </summary>
    [Fact]
    public void RealAudioPositiveControls_ArePresentReviewedAndExact()
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        Dictionary<string, JsonElement> bySource = document.RootElement
            .GetProperty("entries").EnumerateArray()
            .ToDictionary(entry => entry.GetProperty("source").GetString()!);

        foreach ((string id, string source, RealControlKind kind) in ConfirmedRealAudioControls)
        {
            Assert.True(bySource.TryGetValue(source, out JsonElement entry),
                $"{id}: confirmed real control '{source}' is missing from the manifest.");
            Assert.Equal(id, entry.GetProperty("controlId").GetString());
            Assert.Equal("reviewed", entry.GetProperty("reviewStatus").GetString());
            Assert.True(
                entry.TryGetProperty("reviewNotes", out JsonElement notes)
                && notes.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(notes.GetString()),
                $"{source}: reviewed control must carry reviewNotes documenting the human confirmation.");

            string linked = FixtureLinks[source];
            string fixture = Path.Combine(
                AppContext.BaseDirectory, "testfixtures", linked.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fixture),
                $"{id}: reviewed real control is present in the manifest but its audio asset is missing: {fixture}");

            AssertReviewedControlShape(entry, kind, source);

            CorpusExecution execution = Execute(fixture, entry);
            switch (kind)
            {
                case RealControlKind.ExactTiming:
                case RealControlKind.FamilyAmbiguous:
                    AssertTimingContract(entry, execution.Timing, source);
                    break;
                case RealControlKind.ExpectedAbstention:
                    AssertExpectedAbstention(execution.Timing, source);
                    break;
                default:
                    throw new InvalidOperationException($"Unhandled control kind {kind}");
            }
        }
    }

    /// <summary>
    /// SEPARATE, clearly-named requirement for the still-OPEN ternary slot. It
    /// must not be merged into the four-control gate: until a real reviewed
    /// 3/4 or 6/8 asset exists this test skips with the requirement message so
    /// the gap stays visible in CI. When a real asset lands, the slot entry
    /// must satisfy the same reviewed + exact-timing contract as the four.
    /// </summary>
    [SkippableFact]
    public void RealCompoundControl_TernarySlot_RequiresRealAsset()
    {
        string manifestPath = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        JsonElement open = document.RootElement.GetProperty("entries").EnumerateArray()
            .FirstOrDefault(entry => entry.TryGetProperty("controlId", out JsonElement controlId)
                && controlId.GetString() == "real-compound");

        if (open.ValueKind == JsonValueKind.Undefined)
            Skip.If(true, "OPEN REQUIREMENT: " + OpenTernaryControlRequirement);

        // A real asset landed: enforce the same mandatory contract as the four.
        string source = open.GetProperty("source").GetString()!;
        Assert.Equal("reviewed", open.GetProperty("reviewStatus").GetString());
        Assert.True(FixtureLinks.ContainsKey(source),
            $"{source}: ternary control must be registered in FixtureLinks.");
        string linked = FixtureLinks[source];
        string fixture = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", linked.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fixture),
            $"{source}: ternary control is present in the manifest but its audio asset is missing: {fixture}");
        CorpusExecution execution = Execute(fixture, open);
        AssertTimingContract(open, execution.Timing, source);
    }

    /// <summary>
    /// Pins the manifest's DOCUMENTED expectation shape per control kind, so a
    /// sidecar edit cannot silently downgrade a confirmed control (e.g. turn an
    /// exact-tempo control into a family-level one) without this gate failing.
    /// </summary>
    private static void AssertReviewedControlShape(
        JsonElement entry,
        RealControlKind kind,
        string source)
    {
        JsonElement expected = entry.GetProperty("expected");
        switch (kind)
        {
            case RealControlKind.ExactTiming:
            {
                JsonElement tempo = expected.GetProperty("tempo");
                Assert.True(tempo.TryGetProperty("bpm", out _),
                    $"{source}: exact control must declare an exact tempo BPM.");
                Assert.True(tempo.GetProperty("mustBeResolved").GetBoolean(),
                    $"{source}: exact control must require tempo resolution.");
                Assert.False(tempo.GetProperty("mustBeAmbiguous").GetBoolean(),
                    $"{source}: exact control must not be tempo-ambiguous.");
                JsonElement meter = expected.GetProperty("meter");
                Assert.True(meter.TryGetProperty("numerator", out _)
                        && meter.TryGetProperty("denominator", out _),
                    $"{source}: exact control must declare an exact meter grid.");
                Assert.True(meter.GetProperty("mustBeResolved").GetBoolean(),
                    $"{source}: exact control must require meter resolution.");
                Assert.True(expected.GetProperty("downbeat").GetProperty("mustBeResolved").GetBoolean(),
                    $"{source}: exact control must require a resolved downbeat (phase "
                    + "may be resolved-only when the capture cannot establish it).");
                Assert.False(entry.GetProperty("allowUnresolvedTempo").GetBoolean()
                        || entry.GetProperty("allowUnresolvedMeter").GetBoolean()
                        || entry.GetProperty("allowUnresolvedDownbeat").GetBoolean(),
                    $"{source}: exact control must not allow unresolved tempo/meter/downbeat.");
                break;
            }
            case RealControlKind.FamilyAmbiguous:
            {
                JsonElement tempo = expected.GetProperty("tempo");
                Assert.True(tempo.TryGetProperty("acceptedFamily", out JsonElement family)
                        && family.GetArrayLength() >= 2,
                    $"{source}: family-ambiguous control must declare an acceptedFamily of at least two members.");
                Assert.True(tempo.GetProperty("mustBeAmbiguous").GetBoolean(),
                    $"{source}: family-ambiguous control must be mustBeAmbiguous=true.");
                Assert.False(tempo.GetProperty("mustBeResolved").GetBoolean(),
                    $"{source}: family-ambiguous control must not require resolution.");
                Assert.True(tempo.GetProperty("preferred").ValueKind == JsonValueKind.Number,
                    $"{source}: family-ambiguous control must document a preferred member.");
                break;
            }
            case RealControlKind.ExpectedAbstention:
                Assert.True(
                    entry.TryGetProperty("expectedAbstention", out JsonElement abstention)
                    && abstention.GetBoolean(),
                    $"{source}: abstention control must declare expectedAbstention=true.");
                Assert.True(expected.GetProperty("tempo").ValueKind == JsonValueKind.Null
                        && expected.GetProperty("meter").ValueKind == JsonValueKind.Null
                        && expected.GetProperty("downbeat").ValueKind == JsonValueKind.Null,
                    $"{source}: abstention control must not assert any timing expectation.");
                break;
            default:
                throw new InvalidOperationException($"Unhandled control kind {kind}");
        }
    }

    /// <summary>
    /// The sparse/unresolved control contract: the pipeline MUST abstain —
    /// resolving tempo, meter, or downbeat on genuinely sparse evidence would
    /// be a guess, exactly what this corpus forbids.
    /// </summary>
    private static void AssertExpectedAbstention(MusicalTimeMapBuildResult timing, string source)
    {
        TimingDiagnostics diagnostics = timing.Diagnostics;
        Assert.False(diagnostics.TempoResolved,
            $"{source}: expected abstention but the tracker resolved tempo.");
        Assert.False(diagnostics.MeterKnown,
            $"{source}: expected abstention but the tracker resolved a meter.");
        Assert.False(diagnostics.DownbeatKnown,
            $"{source}: expected abstention but the tracker resolved a downbeat.");
    }
}
