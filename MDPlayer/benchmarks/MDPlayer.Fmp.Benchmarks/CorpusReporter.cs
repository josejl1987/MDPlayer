using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Cli;
using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;

namespace Fmp.Benchmarks;

/// <summary>
/// Patch 7 — corpus validation + semantic receipts. Runs real songs through the
/// full pipeline (TimelineCaptureService capture → MusicalTimeMapBuilder →
/// MusicalStructureAnalyzer → MusicalMidiExporter) and emits one semantic JSON
/// receipt per song: grid selection (bpm, meter, downbeat + diagnostics),
/// loop periods, phrases/sections, and percussion allocation. Detected loop
/// periods are compared against the expectations documented in the SDD spec's
/// corpus acceptance (§Corpus acceptance). Percussion is listen-checked
/// headlessly: the pipeline must complete, export twice must be byte-identical
/// (determinism), every channel-9 note must lie in the legal pool (preferred
/// 60-81, overflow every-other-note 27-127, plus the semantic GM drum set), and
/// unknown identities must never receive a reserved GM note.
///
/// Expectations live ONLY in this validation harness — production code never
/// hard-codes filenames, tempos, or loops (spec DoD). Receipts are written to
/// the repo's established golden-artifacts location tests/Corpus/artifacts/
/// linux-baseline/midi-receipts/ (gitignored by design, see tests/Corpus/
/// README.md) AND printed inline; nothing is committed.
/// </summary>
internal static class CorpusReporter
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    /// <summary>Expected semantic outcomes per acceptance song (spec corpus
    /// acceptance). Null = no expectation asserted for that dimension.
    /// Patch 8A adds the per-expected-candidate trace: <c>ExpectedCandidateBpm</c> /
    /// <c>ExpectedCandidateMeter</c> name the CORRECT candidate inside the ranking
    /// (expectedCandidateRank/score, bestWrongCandidateScore,
    /// expectedMinusBestWrong), while <c>ExpectedLoopBars</c> drives the
    /// expected-loop-evidence scan (which candidate's bars produce the expected
    /// repeat period). Expectations stay ONLY in this harness (spec DoD).</summary>
    private sealed record SongExpectation(
        string Fixture,
        string SongName,
        int? ExpectedLoopBars,
        int LoopToleranceBars,
        double? ExpectedBpm,
        double BpmTolerance,
        string? ExpectedMeter,
        bool RequireMeterAndDownbeat,
        bool RequireTurnaround,
        bool NoFabrication,
        string Note,
        double? ExpectedCandidateBpm = null,
        double ExpectedCandidateBpmTolerance = 0,
        string? ExpectedCandidateMeter = null);

    private static readonly SongExpectation[] AcceptanceSongs =
    [
        new("05 - Twilight Express.vgz", "Twilight Express",
            ExpectedLoopBars: 16, LoopToleranceBars: 3,
            ExpectedBpm: null, BpmTolerance: 0, ExpectedMeter: null,
            RequireMeterAndDownbeat: true, RequireTurnaround: false, NoFabrication: false,
            "~16-bar cycle; meter + downbeat resolved; positive STRUCT_LOOP",
            ExpectedCandidateBpm: 100, ExpectedCandidateBpmTolerance: 15, ExpectedCandidateMeter: null),
        new("26 - Robotnik.vgz", "Robotnik",
            ExpectedLoopBars: 15, LoopToleranceBars: 3,
            ExpectedBpm: null, BpmTolerance: 0, ExpectedMeter: null,
            RequireMeterAndDownbeat: false, RequireTurnaround: false, NoFabrication: false,
            "~15-bar; no phrase crosses the loop boundary",
            ExpectedCandidateBpm: null, ExpectedCandidateBpmTolerance: 0, ExpectedCandidateMeter: null),
        new("28 - Smoking Head.vgz", "Smoking Head",
            ExpectedLoopBars: 32, LoopToleranceBars: 4,
            ExpectedBpm: null, BpmTolerance: 0, ExpectedMeter: null,
            RequireMeterAndDownbeat: false, RequireTurnaround: false, NoFabrication: false,
            "~32-bar cycle",
            ExpectedCandidateBpm: null, ExpectedCandidateBpmTolerance: 0, ExpectedCandidateMeter: null),
        new("XA2020.OVI", "XA2020",
            ExpectedLoopBars: 33, LoopToleranceBars: 3,
            ExpectedBpm: 149.4, BpmTolerance: 6.6, ExpectedMeter: "4/4",
            RequireMeterAndDownbeat: true, RequireTurnaround: true, NoFabrication: false,
            "~149.4 BPM 4/4; downbeat resolved; 33-bar loop + turnaround",
            ExpectedCandidateBpm: 149.4, ExpectedCandidateBpmTolerance: 6.6, ExpectedCandidateMeter: "4/4"),
        new("53 Triumphal Arch.vgz", "Triumphal Arch",
            ExpectedLoopBars: null, LoopToleranceBars: 0,
            ExpectedBpm: null, BpmTolerance: 0, ExpectedMeter: null,
            RequireMeterAndDownbeat: false, RequireTurnaround: false, NoFabrication: true,
            "low confidence OK; no fabricated meter/loop; raw restart preserved; AY noise unmapped, not bass drum",
            ExpectedCandidateBpm: null, ExpectedCandidateBpmTolerance: 0, ExpectedCandidateMeter: null),
    ];

    /// <summary>Additional tracked fixtures beyond the acceptance set — run and
    /// reported as evidence, but no hard expectations (info only).</summary>
    private static readonly string[] AdditionalFixtures =
    [
        "21 Master Ninja.vgz",
        "02 Stranger ~ Wandering Swordsman.vgz",
        "XA2021.OVI",
    ];

    public static int Run(string[] args)
    {
        string root = MidiFixtureResolver.FindRepositoryRoot(Environment.CurrentDirectory);
        string? requested = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;

        var receipts = new List<object>();
        bool allPass = true;

        if (requested is not null)
        {
            // Single-song mode: run the requested file against its own
            // expectations (or none when it is not an acceptance song).
            string? fixture = MidiFixtureResolver.Resolve(requested);
            if (fixture is null)
            {
                Console.Error.WriteLine($"corpus-receipts: fixture not found or unsupported: {requested}");
                return 2;
            }
            SongExpectation? expectation = AcceptanceSongs.FirstOrDefault(e =>
                e.Fixture.Equals(Path.GetFileName(fixture), StringComparison.OrdinalIgnoreCase));
            SongRunResult single = RunSong(root, fixture, expectation);
            receipts.Add(single.Receipt);
            allPass &= single.Pass;
        }
        else
        {
            foreach (SongExpectation expectation in AcceptanceSongs)
            {
                string? fixture = ResolveTracked(root, expectation.Fixture);
                if (fixture is null)
                {
                    Console.Error.WriteLine($"corpus-receipts: fixture not found: {expectation.Fixture}");
                    allPass = false;
                    continue;
                }
                SongRunResult result = RunSong(root, fixture, expectation);
                receipts.Add(result.Receipt);
                allPass &= result.Pass;
            }
        }

        if (requested is null)
        {
            foreach (string fixture in AdditionalFixtures)
            {
                string? path = ResolveTracked(root, fixture);
                if (path is null)
                {
                    Console.Error.WriteLine($"corpus-receipts: additional fixture not found: {fixture}");
                    continue;
                }
                // No hard expectations — the receipt itself is the evidence.
                receipts.Add(RunSong(root, path, null).Receipt);
            }
        }

        // Inline report (evidence requirement: receipts reported inline when no
        // committed location exists; the gitignored artifacts location gets a copy).
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "mdplayer.corpus-receipts-summary/v1",
            total = receipts.Count,
            passed = receipts.Count(r => r is ReceiptEnvelope e && e.Pass),
            failed = receipts.Count(r => r is ReceiptEnvelope e && !e.Pass),
            receipts,
        }, new JsonSerializerOptions { WriteIndented = true }));

        string receiptsDir = CorpusArtifactsDir(root);
        try
        {
            Directory.CreateDirectory(receiptsDir);
            foreach (ReceiptEnvelope envelope in receipts)
                File.WriteAllText(Path.Combine(receiptsDir, Sanitize(envelope.SongName) + ".receipt.json"),
                    JsonSerializer.Serialize(envelope.Receipt, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(receiptsDir, "summary.json"),
                JsonSerializer.Serialize(new { total = receipts.Count, passed = receipts.Count(r => r is ReceiptEnvelope e && e.Pass), generatedAtUtc = DateTime.UtcNow }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"corpus-receipts: receipts written to {receiptsDir} (gitignored, not committed)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"corpus-receipts: could not persist receipts to {receiptsDir}: {ex.Message}");
        }

        Console.Error.WriteLine(allPass
            ? "corpus-receipts: ALL EXPECTATIONS PASS"
            : "corpus-receipts: FAILURES PRESENT (see checks)");
        return allPass ? 0 : 1;
    }

    private static string? ResolveTracked(string root, string fileName)
    {
        string path = Path.Combine(root, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Resolves the documented golden-artifacts location
    /// (tests/Corpus/artifacts/linux-baseline per tests/Corpus/README.md),
    /// preferring the .NET solution tree over a same-named repo-root path.</summary>
    private static string CorpusArtifactsDir(string root)
    {
        string[] candidates =
        [
            Path.Combine(root, "MDPlayer", "tests", "Corpus", "artifacts", "linux-baseline", "midi-receipts"),
            Path.Combine(root, "tests", "Corpus", "artifacts", "linux-baseline", "midi-receipts"),
        ];
        foreach (string candidate in candidates)
        {
            string corpusRoot = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(candidate)))!;
            if (Directory.Exists(corpusRoot) || candidate == candidates[0])
                return candidate;
        }
        return candidates[0];
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit));

    private sealed record ReceiptEnvelope(string SongName, object Receipt, bool Pass);

    private sealed record SongRunResult(ReceiptEnvelope Receipt, bool Pass)
    {
        public static SongRunResult Fail(SongExpectation expectation, string message) =>
            new(new ReceiptEnvelope(expectation.SongName, new
            {
                schema = "mdplayer.corpus-midi-receipt/v1",
                song = expectation.SongName,
                error = message,
                pass = false,
            }, false), false);
    }

    private static SongRunResult RunSong(string root, string fixture, SongExpectation? expectation)
    {
        string songName = expectation?.SongName ?? Path.GetFileName(fixture);
        var checks = new List<object>();
        bool pass = true;
        void Check(string name, bool ok, object expected, object actual, string? detail = null)
        {
            pass &= ok;
            checks.Add(new { name, expected, actual, pass = ok, detail });
        }

        try
        {
            var settings = new BatchRenderSettings
            {
                AssetsDir = root,
                // Two passes so looped sources expose a second span: with one
                // pass the pipeline can never observe a repeated block (no
                // OnLoopBoundary, no second 33-bar span) and the true period is
                // capped out of the search (barCount/2). Real corpus loops need
                // at least two passes to be detectable at all.
                Loops = 2,
                MaxDuration = 300,
                Timeout = 120,
                SampleRate = Sr,
            };
            settings.ValidateCommon();
            var inputInfo = new FileInfo(fixture);
            VisualizationTimeline timeline = TimelineCaptureService.Capture(fixture, null, settings);

            // Full symbolic pipeline — NO meter override, per corpus acceptance.
            MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
                new MusicalTimeMapOptions { DetectTempoChanges = true });
            MusicalStructure structure = MusicalStructureAnalyzer.Analyze(build.Map, timeline);
            var exporter = new MusicalMidiExporter(build.Map, Ppq,
                new MusicalMidiExportOptions { EmitPitchBend = true, EmitMarkers = true })
            {
                Diagnostics = build.Diagnostics,
                Structure = structure,
            };
            MusicalMidiExportResult first = exporter.Export(timeline);
            MusicalMidiExportResult second = exporter.Export(timeline);
            bool deterministic = first.Bytes.SequenceEqual(second.Bytes);
            Check("export-deterministic", deterministic, "byte-identical on second export", deterministic
                ? "identical"
                : "differed", "same input + options must produce identical bytes (FR-16)");

            // ---- grid selection (spec P0-2, P1-11) ----
            GridSelectionDiagnostics? grid = build.Diagnostics.GridSelection;
            double? selectedBpm = grid?.SelectedBpm;
            Meter? selectedMeter = grid?.SelectedMeter ?? build.Map.Meter;
            double? downbeat = grid?.SelectedDownbeatQuarter ?? build.Map.FirstDownbeatQuarter;

            // ---- loops ----
            var loops = (structure.Loops ?? Array.Empty<RepeatedBlock>())
                .Select(loop => new
                {
                    startBar = loop.StartBar,
                    lengthBars = loop.LengthBars,
                    repeatCount = loop.RepeatCount,
                    similarity = Round3(loop.Similarity),
                    spanCoverage = Round3(loop.SpanCoverage),
                    materialCoverage = Round3(loop.MaterialCoverage),
                    sourceSupported = loop.SourceSupported,
                    contentValidated = loop.ContentValidated,
                    boundaryErrorBars = Round3(loop.BoundaryErrorBars),
                }).ToArray();
            RepeatedBlock? primary = structure.PrimaryLoop;

            // ---- phrases / sections + the no-crossing invariant (spec P1-12) ----
            var phrases = (structure.Phrases ?? Array.Empty<MusicalPhrase>())
                .Select(p => new { startBar = p.StartBar, endBar = p.EndBar, label = p.Label, confidence = Round3(p.Confidence) })
                .ToArray();
            var sections = (structure.Sections ?? Array.Empty<MusicalSection>())
                .Select(s => new { startBar = s.StartBar, endBar = s.EndBar, label = s.Label, confidence = Round3(s.Confidence) })
                .ToArray();
            int phraseBoundaryViolations = 0;
            foreach (MusicalPhrase phrase in structure.Phrases ?? Array.Empty<MusicalPhrase>())
            foreach (RepeatedBlock loop in structure.Loops ?? Array.Empty<RepeatedBlock>())
            {
                int loopEnd = loop.StartBar + loop.LengthBars;
                if (phrase.StartBar < loopEnd && phrase.EndBar > loop.StartBar)
                    phraseBoundaryViolations++;
            }
            Check("phrases-never-cross-loop-boundary", phraseBoundaryViolations == 0,
                "0 violations", phraseBoundaryViolations, "spec P1-12: no phrase crosses a validated loop boundary");

            // ---- percussion allocation + listen-check surrogate ----
            (int[] percussionNotes, int noteOnCount) = CollectPercussion(first);
            string[] conductorTexts = ConductorTexts(first.Bytes);
            string[] unmapped = conductorTexts
                .Where(t => t.StartsWith("UNMAPPED_PERCUSSION", StringComparison.Ordinal))
                .ToArray();
            bool allInLegalSet = percussionNotes.All(IsLegalPercussionNote);
            Check("percussion-notes-within-legal-pool", allInLegalSet,
                "all channel-9 notes in {GM drums} ∪ {60-81} ∪ {odd 27-127 minus reserved}",
                allInLegalSet ? $"all {percussionNotes.Length} distinct notes legal"
                    : $"illegal: {string.Join(",", percussionNotes.Where(n => !IsLegalPercussionNote(n)))}",
                "spec P1-13: unknown identities never receive reserved GM notes");
            bool unmappedNeverReserved = unmapped.All(line => !line.Contains("note=36", StringComparison.Ordinal)
                && !line.Contains("note=37", StringComparison.Ordinal)
                && !line.Contains("note=38", StringComparison.Ordinal)
                && !line.Contains("note=42", StringComparison.Ordinal)
                && !line.Contains("note=45", StringComparison.Ordinal)
                && !line.Contains("note=48", StringComparison.Ordinal)
                && !line.Contains("note=49", StringComparison.Ordinal)
                && !line.Contains("note=50", StringComparison.Ordinal)
                && !line.Contains("note=57", StringComparison.Ordinal));
            Check("unknown-identities-never-bass-drum", unmappedNeverReserved,
                "no UNMAPPED_PERCUSSION entry with a reserved GM note (incl. 36 bass drum)",
                unmappedNeverReserved ? $"0 reserved-note unmapped entries ({unmapped.Length} unmapped identities)"
                    : "reserved note assigned to an unknown identity", "AY noise and any other non-rhythm source stay unmapped, never bass drum");

            // ---- Patch 8A: per-expected-candidate trace + structural evidence audit ----
            CandidateTraceResult trace = TraceExpectedCandidates(
                build, timeline, grid?.TopCandidates, expectation);

            // XA2020 evidence invariants (Patch 8A): the ~149.4 BPM 4/4 candidate
            // MUST show strong repeat evidence in ITS OWN bar grid — 33-bar period,
            // repeat count >= 2, similarity >= 0.90, span coverage >= 0.80. Each
            // check fails with a specific per-field message; a bare "rejected" is
            // never the answer. Acceptance by the selector is NOT required yet —
            // first prove the evidence EXISTS.
            if (expectation is not null && expectation.ExpectedCandidateBpm is not null)
            {
                bool found = trace.ExpectedCandidateEvidence is not null;
                Check("expected-candidate-in-ranking", found,
                    $"~{expectation.ExpectedCandidateBpm} BPM candidate in top-10 receipts",
                    found ? $"rank {trace.ExpectedCandidateRank} (score {Round3(trace.ExpectedCandidateScore ?? 0)})"
                        : "not in top-10 ranking",
                    "Patch 8A: the expected candidate must be locatable before its evidence is judged");
                if (trace.ExpectedCandidateEvidence is { } evidence)
                {
                    RepeatedBlock? loop = evidence.BestRepeatedBlock;
                    bool periodOk = loop is { } l && Math.Abs(l.LengthBars - 33) <= 3;
                    Check("expected-candidate-33bar-loop", periodOk,
                        "a repeated block of ~33 bars exists in the candidate's bar grid",
                        loop is null ? "no repeated block detected" : $"{loop.LengthBars} bars (repeat {loop.RepeatCount}, sim {Round3(loop.Similarity)}, span {Round3(loop.SpanCoverage)})",
                        "Patch 8A invariant: 33-bar candidate found");
                    Check("expected-candidate-repeat-count", loop is { RepeatCount: >= 2 },
                        "repeat count >= 2",
                        loop is null ? "no block" : $"repeat count {loop.RepeatCount}",
                        "Patch 8A invariant: repeat >= 2");
                    Check("expected-candidate-similarity", loop is not null && loop.Similarity >= 0.90,
                        "similarity >= 0.90",
                        loop is null ? "no block" : $"similarity {Round3(loop.Similarity)}",
                        "Patch 8A invariant: similarity >= 0.90");
                    Check("expected-candidate-span-coverage", loop is not null && loop.SpanCoverage >= 0.80,
                        "span coverage >= 0.80",
                        loop is null ? "no block" : $"span coverage {Round3(loop.SpanCoverage)} ({loop.RepeatCount} * {loop.LengthBars} bars / {evidence.BarCount} total)",
                        "Patch 8A invariant: span >= 0.80 (temporal fraction, not activity proportion)");
                }
            }

            // ---- expectations from the spec corpus acceptance ----
            if (expectation is not null)
            {
                if (expectation.RequireMeterAndDownbeat)
                {
                    bool resolved = grid is { TempoResolved: true, MeterResolved: true, DownbeatResolved: true };
                    Check("meter-and-downbeat-resolved", resolved,
                        "TempoResolved && MeterResolved && DownbeatResolved", grid is null ? "grid not attempted" : $"T/R={grid.TempoResolved} M/R={grid.MeterResolved} D/R={grid.DownbeatResolved}",
                        "spec corpus acceptance");
                }
                if (expectation.ExpectedMeter is { } expectedMeter)
                {
                    string actualMeter = selectedMeter?.ToString() ?? "unknown";
                    Check($"meter-is-{expectedMeter}", string.Equals(actualMeter, expectedMeter, StringComparison.Ordinal),
                        expectedMeter, actualMeter, "spec corpus acceptance");
                }
                if (expectation.ExpectedBpm is { } expectedBpm)
                {
                    bool bpmOk = selectedBpm is { } bpm && Math.Abs(bpm - expectedBpm) <= expectation.BpmTolerance;
                    Check($"bpm-~{expectedBpm}", bpmOk, $"~{expectedBpm} ±{expectation.BpmTolerance}", selectedBpm?.ToString("0.0") ?? "unknown",
                        "spec corpus acceptance");
                }
                if (expectation.ExpectedLoopBars is { } expectedLoop)
                {
                    bool loopOk = primary is { } loop
                        && Math.Abs(loop.LengthBars - expectedLoop) <= expectation.LoopToleranceBars;
                    Check($"loop-period-~{expectedLoop}-bars", loopOk,
                        $"~{expectedLoop} ±{expectation.LoopToleranceBars} bars", primary is null ? "no loop detected" : $"{primary.LengthBars} bars",
                        "spec corpus acceptance; positive STRUCT_LOOP expected");
                }
                if (expectation.RequireTurnaround)
                {
                    bool turnaround = phrases.Any(p => p.label == "TURNAROUND");
                    Check("turnaround-present", turnaround, "a TURNAROUND phrase exists", turnaround ? "present" : "absent",
                        "spec corpus acceptance (XA2020)");
                }
                if (expectation.NoFabrication)
                {
                    bool noFabrication = primary is null || primary.ContentValidated;
                    Check("no-fabricated-loop", noFabrication,
                        "any emitted loop must be content-validated", primary is null ? "no loop emitted" : $"loop {primary.LengthBars} bars, ContentValidated={primary.ContentValidated}",
                        "spec corpus acceptance (Triumphal Arch): no fabricated meter/loop");
                }
            }

            long[] restarts = (timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
                .Where(m => m.Kind == LoopMarkerKind.Restart)
                .Select(m => m.SamplePosition)
                .Distinct()
                .ToArray();

            var receipt = new
            {
                schema = "mdplayer.corpus-midi-receipt/v1",
                song = songName,
                input = new
                {
                    path = Path.GetRelativePath(root, fixture),
                    sha256 = FileHash(fixture),
                    sizeBytes = inputInfo.Length,
                },
                provenance = new
                {
                    gitSha = GitSha(root),
                    branch = GitBranch(root),
                    regeneratedFromAtLeast = "1c02e0df",
                },
                capture = new
                {
                    sourceEvents = (timeline.Notes?.Count ?? 0) + (timeline.Rhythm?.Count ?? 0)
                        + (timeline.Beats?.Count() ?? 0) + (timeline.Timing?.Count() ?? 0),
                    durationSeconds = (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate,
                    loopMarkers = new
                    {
                        entry = (timeline.LoopMarkers ?? Array.Empty<LoopMarker>())
                            .FirstOrDefault(m => m.Kind == LoopMarkerKind.Start)?.SamplePosition,
                        restarts,
                    },
                },
                grid = new
                {
                    attempted = grid?.Attempted ?? false,
                    tempoResolved = grid?.TempoResolved ?? false,
                    meterResolved = grid?.MeterResolved ?? false,
                    downbeatResolved = grid?.DownbeatResolved ?? false,
                    selectedBpm = selectedBpm,
                    selectedMeter = selectedMeter?.ToString(),
                    selectedDownbeatQuarter = downbeat,
                    winnerScore = grid is null ? (double?)null : Round3(grid.WinnerScore),
                    tempoMargin = grid is null ? (double?)null : RoundNull(grid.TempoMargin),
                    meterMargin = grid is null ? (double?)null : RoundNull(grid.MeterMargin),
                    downbeatMargin = grid is null ? (double?)null : RoundNull(grid.DownbeatMargin),
                    rejectionReason = grid?.RejectionReason,
                diagnosticsWarnings = build.Diagnostics.Warnings.ToArray(),
                    breakdown = grid?.Breakdown is { } breakdown ? new
                    {
                        candidatePrior = Round3(breakdown.CandidatePrior),
                        onsetFit = Round3(breakdown.OnsetFit),
                        rhythmRoleFit = RoundNull(breakdown.RhythmRoleFit),
                        restartBoundaryFit = RoundNull(breakdown.RestartBoundaryFit),
                        repeatedContentFit = RoundNull(breakdown.RepeatedContentFit),
                        phraseRegularityFit = RoundNull(breakdown.PhraseRegularityFit),
                        combinedScore = Round3(breakdown.CombinedScore),
                        knownRhythmRoleHits = breakdown.KnownRhythmRoleHits,
                        distinctRhythmRoles = breakdown.DistinctRhythmRoles,
                        spanCoverage = Round3(breakdown.SpanCoverage),
                        materialCoverage = Round3(breakdown.MaterialCoverage),
                    } : null,
                    topCandidates = (grid?.TopCandidates ?? Array.Empty<GridCandidateReceipt>())
                        .Select((c, index) => new
                        {
                            rank = index + 1,
                            bpm = Round3(c.Bpm),
                            meter = c.Meter.ToString(),
                            quarterAtSourceStart = Round3(c.QuarterAtSourceStart),
                            firstDownbeatQuarter = c.FirstDownbeatQuarter is { } q ? Round3(q) : (double?)null,
                            candidatePrior = Round3(c.CandidatePrior),
                            onsetFit = c.Breakdown is null ? (double?)null : Round3(c.Breakdown.OnsetFit),
                            rhythmRoleFit = c.Breakdown is null ? (double?)null : RoundNull(c.Breakdown.RhythmRoleFit),
                            restartBoundaryFit = c.Breakdown is null ? (double?)null : RoundNull(c.Breakdown.RestartBoundaryFit),
                            restartBoundaryErrorBars = c.Breakdown is null ? (double?)null : RoundNull(c.Breakdown.RestartBoundaryErrorBars),
                            restartPeriodBars = c.Breakdown?.RestartPeriodBars,
                            repeatedContentFit = c.Breakdown is null ? (double?)null : RoundNull(c.Breakdown.RepeatedContentFit),
                            repeatStartBar = c.Breakdown?.RepeatStartBar,
                            repeatLengthBars = c.Breakdown?.RepeatLengthBars,
                            repeatCount = c.Breakdown?.RepeatCount,
                            spanCoverage = c.Breakdown is null ? (double?)null : Round3(c.Breakdown.SpanCoverage),
                            materialCoverage = c.Breakdown is null ? (double?)null : Round3(c.Breakdown.MaterialCoverage),
                            phraseRegularity = c.Breakdown is null ? (double?)null : RoundNull(c.Breakdown.PhraseRegularityFit),
                            combinedScore = Round3(c.CombinedScore),
                            tempoMargin = RoundNull(c.TempoMargin),
                            meterMargin = RoundNull(c.MeterMargin),
                            downbeatMargin = RoundNull(c.DownbeatMargin),
                            gateByRestart = c.GateByRestart,
                            gateByRepetition = c.GateByRepetition,
                            gateByRhythm = c.GateByRhythm,
                            rejectionReasons = c.RejectionReasons,
                        }).ToArray(),
                },
                candidateTrace = new
                {
                    expectedCandidateRank = trace.ExpectedCandidateRank,
                    expectedCandidateScore = trace.ExpectedCandidateScore is { } s ? Round3(s) : (double?)null,
                    expectedCandidateBpm = trace.ExpectedCandidateBpm is { } b ? Round3(b) : (double?)null,
                    expectedCandidateMeter = trace.ExpectedCandidateMeter,
                    bestWrongCandidateScore = trace.BestWrongCandidateScore is { } w ? Round3(w) : (double?)null,
                    bestWrongCandidateBpm = trace.BestWrongCandidateBpm is { } wb ? Round3(wb) : (double?)null,
                    expectedMinusBestWrong = trace.ExpectedMinusBestWrong is { } d ? Round3(d) : (double?)null,
                    expectedLoopCandidateRanks = trace.ExpectedLoopCandidateRanks,
                    expectedLoopDetail = trace.ExpectedLoopDetail,
                    barAudit = trace.ExpectedCandidateEvidence is { } audit
                        ? new
                        {
                            barOrigin = Round3(audit.BarOrigin),
                            barCount = audit.BarCount,
                            quartersPerBar = Round3(audit.QuartersPerBar),
                            lastQuarter = Round3(audit.LastQuarter),
                            rangeCovered = audit.RangeCovered,
                            evaluatedMaxPeriod = audit.EvaluatedMaxPeriod,
                            period33Evaluated = audit.Period33Evaluated,
                            start0Evaluated = audit.Start0Evaluated,
                            completeSpansForPeriod33 = audit.CompleteSpansForPeriod33,
                        }
                        : null,
                    restartSnaps = trace.ExpectedCandidateEvidence is { } snaps
                        ? snaps.RestartSnaps.Select(s => new
                        {
                            restartSample = s.RestartSample,
                            restartBarExact = Round3(s.RestartBarExact),
                            chosenBar = s.ChosenBar,
                            errorBars = Round3(s.ErrorBars),
                            dropped = s.Dropped,
                        }).ToArray()
                        : Array.Empty<object>(),
                },
                loops,
                primaryLoop = primary is null ? null : new
                {
                    startBar = primary.StartBar,
                    lengthBars = primary.LengthBars,
                    repeatCount = primary.RepeatCount,
                    similarity = Round3(primary.Similarity),
                    spanCoverage = Round3(primary.SpanCoverage),
                    materialCoverage = Round3(primary.MaterialCoverage),
                    sourceSupported = primary.SourceSupported,
                    contentValidated = primary.ContentValidated,
                    boundaryErrorBars = Round3(primary.BoundaryErrorBars),
                },
                phrases,
                sections,
                pickup = structure.Pickup is not null,
                phraseLoopBoundaryCheck = new { pass = phraseBoundaryViolations == 0, violations = phraseBoundaryViolations },
                percussion = new
                {
                    noteOnCount,
                    distinctNotes = percussionNotes.Distinct().OrderBy(n => n).ToArray(),
                    allNotesInLegalPool = allInLegalSet,
                    unmappedIdentityCount = unmapped.Length,
                    unmappedConductorLines = unmapped.Take(20).ToArray(),
                    deterministicExport = deterministic,
                    outputSha256 = Convert.ToHexString(SHA256.HashData(first.Bytes)).ToLowerInvariant(),
                },
                checks,
                pass,
            };
            return new SongRunResult(new ReceiptEnvelope(songName, receipt, pass), pass);
        }
        catch (Exception ex)
        {
            // A pipeline crash IS corpus evidence: record it and fail the run.
            return SongRunResult.Fail(expectation ?? new SongExpectation(fixture, Path.GetFileName(fixture),
                null, 0, null, 0, null, false, false, false, "pipeline crash"), $"pipeline crashed: {ex.Message}");
        }
    }

    /// <summary>
    /// Patch 8A per-expected-candidate trace: where the CORRECT candidate sits in
    /// the ranking (expectedCandidateRank/score/bpm/meter), the best WRONG
    /// candidate's score (with its BPM, so half/double-tempo aliases like Twilight's
    /// 50 vs 100 are explicit), and expectedMinusBestWrong. The expected-loop scan
    /// finds every top-10 candidate whose own bar grid produces the expected repeat
    /// period (repeat >= 2), and the expected candidate's full structural audit
    /// (bar origin, restart snapping, repetition search) is retained for the
    /// XA2020 evidence invariants.
    /// </summary>
    private sealed record CandidateTraceResult(
        int? ExpectedCandidateRank,
        double? ExpectedCandidateScore,
        double? ExpectedCandidateBpm,
        string? ExpectedCandidateMeter,
        double? BestWrongCandidateScore,
        double? BestWrongCandidateBpm,
        double? ExpectedMinusBestWrong,
        int[] ExpectedLoopCandidateRanks,
        string ExpectedLoopDetail,
        CandidateStructuralEvidence? ExpectedCandidateEvidence);

    private static CandidateTraceResult TraceExpectedCandidates(
        MusicalTimeMapBuildResult build,
        VisualizationTimeline timeline,
        IReadOnlyList<GridCandidateReceipt>? candidates,
        SongExpectation? expectation)
    {
        if (candidates is null || candidates.Count == 0)
        {
            return new CandidateTraceResult(null, null, null, null, null, null, null,
                Array.Empty<int>(), "no grid candidates", null);
        }

        if (expectation is null)
        {
            // Info-only fixture: no expected candidate, no expected loop period.
            return new CandidateTraceResult(null, null, null, null, null, null, null,
                Array.Empty<int>(), "info-only fixture (no expectations)", null);
        }

        var ranked = candidates
            .Select((receipt, index) => (Receipt: receipt, Rank: index + 1))
            .ToArray();

        // Expected-candidate trace: locate the correct candidate (by BPM, plus
        // meter when the spec names one) inside the ranking.
        int? expectedRank = null;
        double? expectedScore = null;
        double? expectedBpm = null;
        string? expectedMeter = null;
        GridCandidateReceipt? expectedReceipt = null;
        if (expectation.ExpectedCandidateBpm is double expectedBpmValue)
        {
            foreach ((GridCandidateReceipt receipt, int rank) in ranked)
            {
                if (Math.Abs(receipt.Bpm - expectedBpmValue) > expectation.ExpectedCandidateBpmTolerance)
                    continue;
                if (expectation.ExpectedCandidateMeter is { } expectedMeterValue
                    && !string.Equals(receipt.Meter.ToString(), expectedMeterValue, StringComparison.Ordinal))
                {
                    continue;
                }
                expectedRank = rank;
                expectedReceipt = receipt;
                expectedScore = receipt.CombinedScore;
                expectedBpm = receipt.Bpm;
                expectedMeter = receipt.Meter.ToString();
                break;
            }
        }

        (double? bestWrongScore, double? bestWrongBpm) = BestWrongScore(ranked, expectedRank);
        double? expectedMinusBestWrong = expectedScore is double expected && bestWrongScore is double wrong
            ? expected - wrong
            : null;

        // Expected-loop-evidence scan: per-candidate structural audit over the
        // top-10 receipts; a candidate qualifies when its own bar grid produces a
        // repeated block of the expected period with at least two complete passes.
        var loopRanks = new List<int>();
        var loopDetail = new List<string>();
        if (expectation.ExpectedLoopBars is int expectedLoop)
        {
            foreach ((GridCandidateReceipt receipt, int rank) in ranked)
            {
                CandidateStructuralEvidence evidence = MusicalStructureAnalyzer
                    .AnalyzeCandidateEvidence(build.Map, ReconstructCandidate(receipt), timeline);
                RepeatedBlock? block = evidence.BestRepeatedBlock;
                if (block is not null
                    && Math.Abs(block.LengthBars - expectedLoop) <= expectation.LoopToleranceBars
                    && block.RepeatCount >= 2)
                {
                    loopRanks.Add(rank);
                    loopDetail.Add(
                        $"rank {rank}: {block.LengthBars} bars, repeat {block.RepeatCount}, " +
                        $"sim {Round3(block.Similarity)}, span {Round3(block.SpanCoverage)}, " +
                        $"mat {Round3(block.MaterialCoverage)}");
                }
            }
        }

        // Full structural audit for the expected candidate (XA2020 invariants).
        CandidateStructuralEvidence? expectedEvidence = expectedReceipt is null
            ? null
            : MusicalStructureAnalyzer.AnalyzeCandidateEvidence(
                build.Map, ReconstructCandidate(expectedReceipt), timeline);

        return new CandidateTraceResult(
            expectedRank,
            expectedScore,
            expectedBpm,
            expectedMeter,
            bestWrongScore,
            bestWrongBpm,
            expectedMinusBestWrong,
            loopRanks.ToArray(),
            loopDetail.Count > 0
                ? string.Join("; ", loopDetail)
                : expectation.ExpectedLoopBars is int expectedLoopBars
                    ? $"no candidate has a ~{expectedLoopBars}-bar repeat (tolerance {expectation.LoopToleranceBars}, repeat >= 2)"
                    : "no expected loop period",
            expectedEvidence);
    }

    /// <summary>Reconstructs the grid candidate a receipt was taken from — the
    /// receipt carries every field the candidate audit consumes.</summary>
    private static MusicalGridCandidate ReconstructCandidate(GridCandidateReceipt receipt) =>
        new(receipt.Bpm, receipt.Meter, receipt.QuarterAtSourceStart,
            receipt.FirstDownbeatQuarter, receipt.CandidatePrior);

    private static (double? Score, double? Bpm) BestWrongScore(
        (GridCandidateReceipt Receipt, int Rank)[] ranked,
        int? expectedRank)
    {
        double? bestScore = null;
        double? bestBpm = null;
        foreach ((GridCandidateReceipt receipt, int rank) in ranked)
        {
            if (expectedRank is int found && rank == found)
                continue;
            if (bestScore is null || receipt.CombinedScore > bestScore)
            {
                bestScore = receipt.CombinedScore;
                bestBpm = receipt.Bpm;
            }
        }
        return (bestScore, bestBpm);
    }

    /// <summary>All channel-9 (GM percussion) note-ons across exported tracks.</summary>
    private static (int[] Notes, int NoteOnCount) CollectPercussion(MusicalMidiExportResult result)
    {
        var notes = new List<int>();
        int count = 0;
        foreach (MidiTrack track in result.Tracks)
        foreach (MidiEventBase evt in track.Events)
        {
            if (evt is MidiNoteEvent note && note.NoteOn && note.Channel == 9)
            {
                notes.Add(note.Note);
                count++;
            }
        }
        return (notes.ToArray(), count);
    }

    /// <summary>Legal channel-9 note set: the full semantic GM drum set
    /// {36,37,38,42,45,48,49,50,57} (GeneralMidiDrumMapper.TryMap), plus
    /// preferred pool 60-81, plus overflow every-other-note 27-127 with the
    /// reserved GM notes skipped (reserved odd notes are excluded from the
    /// overflow by construction, spec P1-13).</summary>
    private static bool IsLegalPercussionNote(int note)
    {
        if (note is >= 60 and <= 81)
            return true;
        if (note is >= 27 and <= 127 && note % 2 == 1 && note is not (37 or 45 or 49 or 57))
            return true;
        return note is 36 or 37 or 38 or 42 or 45 or 48 or 49 or 50 or 57;
    }

    private static string[] ConductorTexts(byte[] bytes)
    {
        try
        {
            MidiFile file = MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
            {
                EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store,
            });
            return file.GetTrackChunks().FirstOrDefault()?.Events
                .OfType<TextEvent>()
                .Select(t => t.Text)
                .ToArray() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static double Round3(double value) => Math.Round(value, 3);
    private static double? RoundNull(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Round(v, 3) : null;

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string GitSha(string root) => Git(root, "rev-parse", "HEAD");
    private static string GitBranch(string root) => Git(root, "branch", "--show-current");

    private static string Git(string root, string verb, string arg)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "git",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add(verb);
            startInfo.ArgumentList.Add(arg);
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
