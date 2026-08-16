using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Cli;
using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;

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

    /// <summary>Calibration-loop microcorpus: exactly the three songs the
    /// maintainer iterates on, run against a CACHED timeline so every experiment
    /// measures scoring changes only, never capture differences.</summary>
    private static readonly string[] MicrocorpusFixtures =
    [
        "XA2020.OVI",
        "05 - Twilight Express.vgz",
        "26 - Robotnik.vgz",
    ];

    public static int Run(string[] args)
    {
        string root = MidiFixtureResolver.FindRepositoryRoot(Environment.CurrentDirectory);

        // Microcorpus mode (calibration loop): exactly the three songs the
        // maintainer's hypothesis loop runs against the cached timeline, printing
        // the compact before/after table. No receipt files are written.
        if (args.Length > 1 && args[1] == "--micro")
            return RunMicrocorpus(root);

        // Section-12 mode (spec 12 regression output): the 8-song corpus report
        // with every Section-12 column per song, measured from the SAME
        // capture -> build -> export pipeline and a decode-only wall-clock/pitch
        // measurement. Output is a markdown table; the orchestrator appends the
        // produced section to midi.md. No receipt files are written.
        if (args.Length > 1 && args[1] == "--section12")
            return RunSection12(root);

        // Acceptance mode (maintainer checkpoint): exactly the five acceptance
        // songs from the spec corpus acceptance — the same set the full run
        // evaluates — against the cached timeline, printed as the compact table
        // plus a per-check pass/fail line. Holdout songs never run here.
        if (args.Length > 1 && args[1] == "--acceptance")
            return RunAcceptance(root);

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

    /// <summary>
    /// Calibration-loop microcorpus: runs exactly the three microcorpus songs
    /// against the (cached) timeline and prints the compact before/after table
    /// the maintainer's hypothesis loop consumes. No receipt files are written —
    /// the timeline cache is the only artifact — so scoring changes show up in
    /// the T/M/D + bpm/meter/rejection/loop columns without touching the golden
    /// baseline receipts.
    /// </summary>
    private static int RunMicrocorpus(string root)
    {
        Console.WriteLine("MDPlayer calibration microcorpus (cached timeline)");
        Console.WriteLine("song | T | M | D | bpm | meter | rejection | loopBars | expectedBpm(±) | expectedLoopBars");
        bool allPass = true;
        foreach (string fixtureName in MicrocorpusFixtures)
        {
            string? fixture = ResolveTracked(root, fixtureName);
            if (fixture is null)
            {
                Console.WriteLine($"{fixtureName} | ERROR | fixture not found");
                allPass = false;
                continue;
            }
            SongExpectation? expectation = AcceptanceSongs.FirstOrDefault(e =>
                e.Fixture.Equals(Path.GetFileName(fixture), StringComparison.OrdinalIgnoreCase));
            SongRunResult result = RunSong(root, fixture, expectation);
            allPass &= result.Pass;
            Console.WriteLine(MicroTableRow(result, expectation));
            // READ-ONLY CALIBRATION DIAGNOSTIC (Exp 3): fresh per-candidate
            // evidence from the in-memory receipt (never written to disk).
            Console.Error.WriteLine(JsonSerializer.Serialize(
                new { song = result.Receipt.SongName, receipt = result.Receipt.Receipt }));
        }
        return allPass ? 0 : 1;
    }

    /// <summary>Acceptance checkpoint: exactly the five acceptance songs from
    /// the spec corpus acceptance (<see cref="AcceptanceSongs"/>), run against
    /// the cached timeline and printed as the compact table plus a per-check
    /// pass/fail line. No receipt files are written — same shape as the
    /// microcorpus calibration mode, just the full acceptance set.</summary>
    private static int RunAcceptance(string root)
    {
        Console.WriteLine("MDPlayer acceptance corpus (cached timeline)");
        Console.WriteLine("song | T | M | D | bpm | meter | rejection | loopBars | expectedBpm(±) | expectedLoopBars");
        bool allPass = true;
        foreach (SongExpectation expectation in AcceptanceSongs)
        {
            string? fixture = ResolveTracked(root, expectation.Fixture);
            if (fixture is null)
            {
                Console.WriteLine($"{expectation.SongName} | ERROR | fixture not found");
                allPass = false;
                continue;
            }
            SongRunResult result = RunSong(root, fixture, expectation);
            allPass &= result.Pass;
            Console.WriteLine(MicroTableRow(result, expectation));
            Console.WriteLine($"  checks: {(result.Pass ? "PASS" : "FAIL")} | {CheckSummary(result)}");
        }
        return allPass ? 0 : 1;
    }

    /// <summary>Space-joined per-check verdicts from the in-memory receipt
    /// (grid resolution, bpm tolerance, loop period, percussion, determinism,
    /// phrases, Patch 8A evidence — whatever the receipt pipeline asserted).</summary>
    private static string CheckSummary(SongRunResult result)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Receipt.Receipt));
        JsonElement receipt = doc.RootElement;
        if (receipt.TryGetProperty("error", out _))
            return "pipeline error (no checks)";
        if (!receipt.TryGetProperty("checks", out JsonElement checks) || checks.ValueKind != JsonValueKind.Array)
            return "no checks";
        return string.Join(" ",
            checks.EnumerateArray().Select(c =>
                $"{c.GetProperty("name").GetString()}={(c.GetProperty("pass").GetBoolean() ? "PASS" : "FAIL")}"));
    }

    /// <summary>One compact table row, read back from the song receipt so the
    /// table always reflects exactly what the receipt pipeline computed.</summary>
    private static string MicroTableRow(SongRunResult result, SongExpectation? expectation)
    {
        using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(result.Receipt.Receipt));
        JsonElement receipt = doc.RootElement;
        if (receipt.TryGetProperty("error", out JsonElement error))
            return $"{result.Receipt.SongName} | ERROR | {error.GetString()}";

        JsonElement grid = receipt.GetProperty("grid");
        string yesNo(bool value) => value ? "Y" : "N";
        string t = yesNo(grid.GetProperty("tempoResolved").GetBoolean());
        string m = yesNo(grid.GetProperty("meterResolved").GetBoolean());
        string d = yesNo(grid.GetProperty("downbeatResolved").GetBoolean());
        string bpm = grid.TryGetProperty("selectedBpm", out JsonElement bpmEl) && bpmEl.ValueKind == JsonValueKind.Number
            ? bpmEl.GetDouble().ToString("0.##")
            : "-";
        string meter = grid.TryGetProperty("selectedMeter", out JsonElement meterEl) && meterEl.ValueKind == JsonValueKind.String
            ? meterEl.GetString()!
            : "-";
        string rejection = "-";
        if (grid.TryGetProperty("rejectionReason", out JsonElement rej)
            && rej.ValueKind == JsonValueKind.String
            && !string.IsNullOrEmpty(rej.GetString()))
        {
            string reason = rej.GetString()!;
            double? margin = new double?[]
            {
                grid.TryGetProperty("tempoMargin", out JsonElement tm) && tm.ValueKind == JsonValueKind.Number ? tm.GetDouble() : null,
                grid.TryGetProperty("meterMargin", out JsonElement mm) && mm.ValueKind == JsonValueKind.Number ? mm.GetDouble() : null,
                grid.TryGetProperty("downbeatMargin", out JsonElement dm) && dm.ValueKind == JsonValueKind.Number ? dm.GetDouble() : null,
            }.Where(v => v is not null).Min();
            rejection = margin is double minMargin ? $"{reason} (m {minMargin:0.###})" : reason;
        }
        string loopBars = "-";
        if (receipt.TryGetProperty("primaryLoop", out JsonElement loop) && loop.ValueKind == JsonValueKind.Object)
            loopBars = loop.GetProperty("lengthBars").GetInt32().ToString();

        string expectedBpm = "-";
        if (expectation?.ExpectedBpm is double expected)
            expectedBpm = $"{expected}(±{expectation.BpmTolerance})";
        else if (expectation?.ExpectedCandidateBpm is double expectedCandidate)
            expectedBpm = $"{expectedCandidate}(±{expectation.ExpectedCandidateBpmTolerance})";
        string expectedLoop = expectation?.ExpectedLoopBars is int expectedLoopBars ? expectedLoopBars.ToString() : "-";

        return $"{result.Receipt.SongName} | {t} | {m} | {d} | {bpm} | {meter} | {rejection} | {loopBars} | {expectedBpm} | {expectedLoop}";
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

    /// <summary>Gitignored timeline cache directory
    /// (tests/Corpus/artifacts/linux-baseline/timeline-cache/), sibling of the
    /// receipt artifacts.</summary>
    private static string TimelineCacheDir(string root)
    {
        string[] candidates =
        [
            Path.Combine(root, "MDPlayer", "tests", "Corpus", "artifacts", "linux-baseline", "timeline-cache"),
            Path.Combine(root, "tests", "Corpus", "artifacts", "linux-baseline", "timeline-cache"),
        ];
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) || candidate == candidates[0])
                return candidate;
        }
        return candidates[0];
    }

    /// <summary>Cache key: full source identity (path|size|mtime|sha256 via
    /// <see cref="TimelineCaptureFileIdentity"/>) plus every capture setting that
    /// can change the captured timeline. A change in any of them invalidates the
    /// entry — exactly what the calibration loop needs.</summary>
    private static string TimelineCacheKey(string fixture, BatchRenderSettings settings) =>
        $"{TimelineCaptureFileIdentity.FileIdentity(fixture)}|loops={settings.Loops}|sr={settings.SampleRate}|max={settings.MaxDuration}|fade={settings.Fade}|tail={settings.Tail}|timeout={settings.Timeout}";

    private sealed record TimelineCacheMeta(string Key, DateTime CapturedUtc);

    private sealed record TimelineCacheResult(
        VisualizationTimeline Timeline,
        bool Used,
        bool RoundTripVerified,
        string RelativePath);

    /// <summary>
    /// Returns the cached timeline when a valid entry exists for (source,
    /// settings); otherwise captures fresh and writes the cache — only when the
    /// entry is missing/stale, never rewritten on every run. The cache is the
    /// canonical <see cref="VisualizationJsonWriter"/> JSON (atomic temp-file
    /// write, validated), keyed by a sidecar meta file carrying the full key. On
    /// a fresh capture the written file is immediately reloaded through the same
    /// read path and compared byte-for-byte (canonical serialization) against the
    /// in-memory timeline: that proves the cached run is semantically identical
    /// to a fresh capture, so subsequent experiments measure scoring changes
    /// only.
    /// </summary>
    private static TimelineCacheResult GetOrCaptureTimeline(string root, string fixture, BatchRenderSettings settings)
    {
        string cacheDir = TimelineCacheDir(root);
        string key = TimelineCacheKey(fixture, settings);
        string keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();
        string stem = Sanitize(Path.GetFileNameWithoutExtension(fixture)) + "-" + keyHash;
        string timelinePath = Path.Combine(cacheDir, stem + ".timeline.json");
        string metaPath = Path.Combine(cacheDir, stem + ".meta.json");

        if (File.Exists(timelinePath) && File.Exists(metaPath))
        {
            try
            {
                TimelineCacheMeta? meta = JsonSerializer.Deserialize<TimelineCacheMeta>(File.ReadAllText(metaPath));
                if (meta is not null && string.Equals(meta.Key, key, StringComparison.Ordinal))
                {
                    VisualizationTimeline cached = VisualizationJsonWriter.Read(timelinePath);
                    return new TimelineCacheResult(cached, true, true, Path.GetRelativePath(root, timelinePath));
                }
            }
            catch
            {
                // Corrupt/stale cache entry — fall through to a fresh capture.
            }
        }

        VisualizationTimeline timeline = TimelineCaptureService.Capture(fixture, null, settings);
        bool verified = false;
        try
        {
            Directory.CreateDirectory(cacheDir);
            VisualizationJsonWriter.Write(timelinePath, timeline);
            File.WriteAllText(metaPath, JsonSerializer.Serialize(new TimelineCacheMeta(key, DateTime.UtcNow)));
            verified = string.Equals(
                VisualizationJsonWriter.Serialize(timeline),
                VisualizationJsonWriter.Serialize(VisualizationJsonWriter.Read(timelinePath)),
                StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"corpus-receipts: timeline cache write failed for {Path.GetFileName(fixture)}: {ex.Message}");
        }
        return new TimelineCacheResult(timeline, false, verified, Path.GetRelativePath(root, timelinePath));
    }

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
            // Timeline cache: reuse a previous capture of the same source file
            // with the same capture settings (key = source identity + settings).
            // The cached timeline is loaded through the canonical JSON round-trip,
            // so later experiments measure scoring changes only, never capture
            // differences. A fresh capture is verified immediately (see
            // GetOrCaptureTimeline) before the entry is trusted.
            TimelineCacheResult cache = GetOrCaptureTimeline(root, fixture, settings);
            VisualizationTimeline timeline = cache.Timeline;
            Console.Error.WriteLine($"corpus-receipts: timeline cache [{(cache.Used ? "hit" : "miss→captured")}] " +
                $"roundTripVerified={cache.RoundTripVerified} {Path.GetFileName(fixture)}");

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
                // A phrase CROSSES the loop boundary only when it straddles one of
                // the loop's two edges (start or end). Phrases entirely inside the
                // loop span, or entirely outside it, do not cross and are not
                // violations (spec P1-12).
                int loopStart = loop.StartBar;
                int loopEnd = loop.StartBar + loop.LengthBars;
                bool crossesStart = phrase.StartBar < loopStart && phrase.EndBar > loopStart;
                bool crossesEnd = phrase.StartBar < loopEnd && phrase.EndBar > loopEnd;
                if (crossesStart || crossesEnd)
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
                if (trace.ExpectedCandidateEvidence is { } evidence && expectation.ExpectedLoopBars is { } expectedLoop)
                {
                    RepeatedBlock? loop = evidence.BestRepeatedBlock;
                    bool periodOk = loop is { } l && Math.Abs(l.LengthBars - expectedLoop) <= 3;
                    Check($"expected-candidate-~{expectedLoop}-bar-loop", periodOk,
                        $"a repeated block of ~{expectedLoop} bars exists in the candidate's bar grid",
                        loop is null ? "no repeated block detected" : $"{loop.LengthBars} bars (repeat {loop.RepeatCount}, sim {Round3(loop.Similarity)}, span {Round3(loop.SpanCoverage)})",
                        "Patch 8A invariant: expected-candidate loop period");
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
                    cache = new
                    {
                        used = cache.Used,
                        path = cache.RelativePath,
                        roundTripVerified = cache.RoundTripVerified,
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
    /// Spec 12 regression output: the 8-song corpus report. Runs the SAME
    /// capture -> build -> export pipeline as <see cref="RunSong"/> (timeline
    /// cache, full symbolic map, no meter override) for the five acceptance
    /// songs plus the three additional fixtures, then reports every Section-12
    /// column measured from real pipeline data. The three error maxima are
    /// measured with an independent decode-only wall clock (Set Tempo events,
    /// SMF division and ticks only — never MusicalTimeMap) and the running
    /// pitch-bend state per channel, mirroring the spec-10 fidelity oracle.
    /// BPM/grid calibration is NEVER touched here (spec 13 freeze).
    /// </summary>
    private static int RunSection12(string root)
    {
        var corpus = new List<(string Fixture, SongExpectation? Expectation)>();
        foreach (SongExpectation e in AcceptanceSongs)
            corpus.Add((Path.Combine(root, e.Fixture), e));
        foreach (string additional in AdditionalFixtures)
            corpus.Add((Path.Combine(root, additional), null));

        const string mapper = "MusicalMidiExporter";
        var rows = new List<string>();
        var sourceCounts = corpus.GroupBy(c => FileHash(c.Fixture))
            .ToDictionary(g => g.Key, g => g.Count());
        var wallClock = new Stopwatch();
        int failures = 0;

        foreach ((string fixture, SongExpectation? expectation) in corpus)
        {
            try
            {
                var settings = new BatchRenderSettings
                {
                    AssetsDir = root,
                    Loops = 2,
                    MaxDuration = 300,
                    Timeout = 120,
                    SampleRate = Sr,
                };
                settings.ValidateCommon();
                TimelineCacheResult cache = GetOrCaptureTimeline(root, fixture, settings);
                VisualizationTimeline timeline = cache.Timeline;
                MusicalTimeMapBuildResult build = MusicalTimeMapBuilder.Build(timeline,
                    new MusicalTimeMapOptions { DetectTempoChanges = true });
                MusicalStructure structure = MusicalStructureAnalyzer.Analyze(build.Map, timeline);
                var exporter = new MusicalMidiExporter(build.Map, Ppq,
                    new MusicalMidiExportOptions { EmitPitchBend = true, EmitMarkers = true })
                {
                    Diagnostics = build.Diagnostics,
                    Structure = structure,
                };
                wallClock.Restart();
                MusicalMidiExportResult first = exporter.Export(timeline);
                wallClock.Stop();

                SourceAttackCounters counters = first.AttackCounters;
                PercussionFidelityReceipt perc = first.Percussion;

                string sourceId = FileHash(fixture);
                int sourceCount = sourceCounts[sourceId];
                double dur = (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate;
                int reattacks = (timeline.Notes ?? Array.Empty<Fmp.Core.Visualization.NoteEvent>()).Count(n => n.IsRetrigger);

                // Per-voice IOI (median of consecutive onset gaps per voice, then
                // the mean across voices) and per-voice pitch CV (sd/mean of
                // InitialMidiNote, then the mean across voices).
                var voices = (timeline.Notes ?? Array.Empty<Fmp.Core.Visualization.NoteEvent>())
                    .GroupBy(n => n.ChannelId)
                    .Select(g => g.OrderBy(n => n.StartSample).ToArray())
                    .Where(g => g.Length >= 2)
                    .ToArray();
                double ioi = 0;
                double pitchCv = 0;
                if (voices.Length > 0)
                {
                    double[] medians = voices
                        .Select(v => MedianGapSeconds(v))
                        .Where(m => m is not null)
                        .Select(m => m!.Value)
                        .ToArray();
                    ioi = medians.Length > 0 ? medians.Average() : 0;
                    double[] cvs = voices
                        .Select(v => PitchCoefficientOfVariation(v))
                        .Where(c => c is not null)
                        .Select(c => c!.Value)
                        .ToArray();
                    pitchCv = cvs.Length > 0 ? cvs.Average() : 0;
                }

                // Pitch-lift (distance in semitones after rekeying): max absolute
                // accepted tuning across domains released by the pitch stage.
                double pitchLift = first.PitchDiagnostics.Domains
                    .Where(d => d.Accepted && d.TuningCents is { } t && double.IsFinite(t))
                    .Select(d => Math.Abs(d.TuningCents!.Value) / 100.0)
                    .DefaultIfEmpty(0)
                    .Max();

                int notesLost = Math.Max(0, counters.SourceNoteCount - counters.InitialNoteOnCount);
                int sourcePercussion = perc.NativeRhythmEvents + perc.AggregateHits + perc.ClassifiedNoteEvents;
                int drumsLost = Math.Max(0, sourcePercussion - perc.ExportedGmDrumEvents);

                Section12Decode decoded = DecodeSection12(first.Bytes);
                double decodedDur = decoded.WallSeconds(decoded.MaxTick);
                (double maxOnErr, double maxOffErr, double maxPitchErr, int pairedNotes, int onsCount) =
                    PairErrors(timeline, decoded);

                string song = expectation?.SongName ?? Path.GetFileName(fixture);
                // Attribution gate: numeric maxima require >=95% pairing coverage.
                // Songs where the exporter remaps percussive onsets to GM drums
                // (spec §8) decode more note-ons than the source pool holds, so the
                // single-cursor join cannot attribute those extra notes — a numeric
                // maximum there would be an artifact, not a fidelity measurement.
                bool attributable = onsCount > 0 && pairedNotes * 20 >= onsCount * 19;
                string onCell = attributable ? Round6(maxOnErr).ToString("0.######") : "—";
                string offCell = attributable ? Round6(maxOffErr).ToString("0.######") : "—";
                string pitchCell = attributable && maxPitchErr <= 1.0
                    ? Round6(maxPitchErr).ToString("0.######")
                    : "—";
                rows.Add(
                    $"| `{song}` | `{sourceId[..12]} ({sourceCount})` | `{mapper} ({corpus.Count})` | {counters.InitialNoteOnCount} |" +
                    $" `{GitSha(root)[..8]}` | {Round3(wallClock.Elapsed.TotalSeconds)} | {Round3(dur)} | {reattacks} | {Round3(ioi)} |" +
                    $" {RoundNull(pitchCv)?.ToString("0.###") ?? "—"} | {Round3(pitchLift)} | {notesLost} | {drumsLost} | {counters.DroppedSourceAttacks} |" +
                    $" `corpus-midi-export/v1` | {Round3(decodedDur)} | {counters.SourceNoteCount} | {counters.SameTickAttackCollisions} |" +
                    $" {perc.NativeRhythmEvents} | {perc.ClassifiedNoteEvents} | {perc.KnownRoleEvents} | {perc.ExportedGmDrumEvents} |" +
                    $" {onCell} | {offCell} | {pitchCell} |");
            }
            catch (Exception ex)
            {
                failures++;
                string song = expectation?.SongName ?? Path.GetFileName(fixture);
                rows.Add($"| `{song}` | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | — | **FAIL: {Sanitize(ex.Message)}** |");
            }
        }

        Console.WriteLine("## Corpus report (§12) — 8-song corpus");
        Console.WriteLine();
        Console.WriteLine("| Source | SourceID(CNT) | Mapper(CNT) | AllNotes | vs | Wall | Dur | Reattacks | IOI | PitchCV | PitchLift | NotesLost | DrumsLost | droppedSrc | Suite | DecodedDur | SrcAttacks | SameTick | NativePerc | FMClassified | KnownRole | GmDrums | MaxOnErr | MaxOffErr | MaxPitchErr |");
        Console.WriteLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (string row in rows) Console.WriteLine(row);
        Console.WriteLine();
        Console.WriteLine($"Mapper participated in {corpus.Count} successful exports; source counts are corpus-appearance counts. " +
            "Wall = export wall-clock (sec). IOI = mean across voices of per-voice median inter-onset-interval (sec). " +
            "PitchCV = mean across voices of per-voice pitch coefficient of variation. PitchLift = max accepted rekey distance (semitones). " +
            "Errors are decode-only maxima (sec / semitones); BPM/grid calibration untouched (spec 13).");

        return failures == 0 ? 0 : 2;
    }

    private sealed record Section12Decode(
        IReadOnlyList<(long Tick, int Note, int Channel, double EffectivePitch)> NoteOns,
        IReadOnlyList<(long Tick, int Note, int Channel)> NoteOffs,
        Func<long, double> WallSeconds,
        long MaxTick);

    /// <summary>Decode-only measurement (spec 10 oracle semantics): wall clock is
    /// rebuilt purely from the SMF Set Tempo events, division and ticks; effective
    /// pitch = note number + running channel pitch-bend state. Channel 9 (GM drums)
    /// participates in timing and pitch like every channel — the exporter writes
    /// drum pitch at the source pitch (see <c>TrackSlot.PitchFor</c>).</summary>
    private static Section12Decode DecodeSection12(byte[] bytes)
    {
        var file = MidiFile.Read(new MemoryStream(bytes), new ReadingSettings
        {
            EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store,
        });
        TempoMap tempoMap = file.GetTempoMap();
        double WallSeconds(long tick) =>
            TimeConverter.ConvertTo<MetricTimeSpan>(tick, tempoMap).TotalMicroseconds / 1_000_000.0;

        var ons = new List<(long Tick, int Note, int Channel, double EffectivePitch)>();
        var offs = new List<(long Tick, int Note, int Channel)>();
        long maxTick = 0;

        // Per-channel running pitch bend (14-bit pitch wheel value, 0-16383).
        int[] rawBend = new int[16];
        Array.Fill(rawBend, 8192);
        const double bendRangeSemitones = 24.0;

        foreach (TrackChunk chunk in file.GetTrackChunks())
        {
            long runningTick = 0;
            foreach (MidiEvent ev in chunk.Events)
            {
                runningTick += ev.DeltaTime;
                long tick = runningTick;
                if (ev is PitchBendEvent bend)
                {
                    int ch = bend.Channel;
                    rawBend[ch] = bend.PitchValue;
                }
                else if (ev is NoteOnEvent on)
                {
                    if (on.Velocity == 0) continue;
                    int ch = on.Channel;
                    double semis = (rawBend[ch] - 8192) * bendRangeSemitones / 8192.0;
                    ons.Add((tick, (int)on.NoteNumber, ch, (int)on.NoteNumber + semis));
                    maxTick = Math.Max(maxTick, tick);
                }
                else if (ev is NoteOffEvent off)
                {
                    offs.Add((tick, (int)off.NoteNumber, (int)off.Channel));
                    maxTick = Math.Max(maxTick, tick);
                }
            }
        }

        ons.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick)
            : a.Note != b.Note ? a.Note.CompareTo(b.Note) : a.Channel.CompareTo(b.Channel));
        offs.Sort((a, b) => a.Tick != b.Tick ? a.Tick.CompareTo(b.Tick)
            : a.Note != b.Note ? a.Note.CompareTo(b.Note) : a.Channel.CompareTo(b.Channel));

        return new Section12Decode(ons, offs, WallSeconds, maxTick);
    }

    /// <summary>Pair decoded melodic notes to the source melodic pool with an
    /// order-preserving merge join over ONE global cursor. SampleToTick is
    /// monotonic, so the decoded stream (globally sorted by tick) aligns with the
    /// source pool (sorted by StartSample) 1:1 — one cursor, never per-channel
    /// restarts (those double-consume the pool and fabricated magnitudes).
    /// Timing includes channel 9 (GM drums) because the exporter maps drum
    /// samples through the same map; PITCH excludes channel 9 and same-tick
    /// groups: drum numbers are deliberate GM remaps (spec §8), and within a
    /// same-tick group the decoded (tick, note) sort can cross the source
    /// (sample, pitch) order, so those pitch values are not attributable.
    /// Rhythm-family sources are excluded from the pool because they never carry
    /// source pitch comparable to a decoded note. A self-audit line reports
    /// paired/unpaired coverage.</summary>
    private static (double MaxOnErr, double MaxOffErr, double MaxPitchErr, int PairedNotes, int OnsCount) PairErrors(
        VisualizationTimeline timeline, Section12Decode decoded)
    {
        var sourcePool = (timeline.Notes ?? Array.Empty<Fmp.Core.Visualization.NoteEvent>())
            .Where(n => !IsRhythmSource(n))
            .OrderBy(n => n.StartSample)
            .ThenBy(n => n.InitialMidiNote)
            .ToArray();

        // Off-side pairing walks an END-sorted pool: NoteOff order is monotonic
        // in EndSample, but overlapping notes make end order differ from start
        // order — walking the start pool against end times mispairs (an artifact
        // observed as 40-130s spurious maxOff on real files).
        var sourcePoolByEnd = sourcePool.OrderBy(n => n.EndSample)
            .ThenBy(n => n.InitialMidiNote)
            .ToArray();

        var allOns = decoded.NoteOns
            .OrderBy(o => o.Tick)
            .ThenBy(o => o.Note)
            .ThenBy(o => o.Channel)
            .ToList();
        var allOffs = decoded.NoteOffs
            .OrderBy(o => o.Tick)
            .ThenBy(o => o.Note)
            .ThenBy(o => o.Channel)
            .ToList();

        double maxOn = 0, maxOff = 0, maxPitch = 0;
        int pairedNotes = 0, unpairedDecoded = 0;

        int src = 0;
        for (int i = 0; i < allOns.Count; i++)
        {
            // Advance to the nearest next source note (monotonic join).
            while (src + 1 < sourcePool.Length)
            {
                double curDist = Math.Abs(decoded.WallSeconds(allOns[i].Tick) - sourcePool[src].StartSample / (double)timeline.SampleRate);
                double nextDist = Math.Abs(decoded.WallSeconds(allOns[i].Tick) - sourcePool[src + 1].StartSample / (double)timeline.SampleRate);
                if (nextDist < curDist) src++;
                else break;
            }
            if (src >= sourcePool.Length) { unpairedDecoded += allOns.Count - i; break; }

            double expectedOn = sourcePool[src].StartSample / (double)timeline.SampleRate;
            double actualOn = decoded.WallSeconds(allOns[i].Tick);
            maxOn = Math.Max(maxOn, Math.Abs(actualOn - expectedOn));

            bool pitchComparable = allOns[i].Channel != 9
                && (i == 0 || allOns[i - 1].Tick != allOns[i].Tick)
                && (i + 1 >= allOns.Count || allOns[i + 1].Tick != allOns[i].Tick);
            if (pitchComparable)
                maxPitch = Math.Max(maxPitch, Math.Abs(allOns[i].EffectivePitch - sourcePool[src].InitialMidiNote));
            pairedNotes++;
            src++;
        }

        int srcOff = 0;
        for (int i = 0; i < allOffs.Count; i++)
        {
            while (srcOff + 1 < sourcePoolByEnd.Length)
            {
                double curDist = Math.Abs(decoded.WallSeconds(allOffs[i].Tick) - sourcePoolByEnd[srcOff].EndSample / (double)timeline.SampleRate);
                double nextDist = Math.Abs(decoded.WallSeconds(allOffs[i].Tick) - sourcePoolByEnd[srcOff + 1].EndSample / (double)timeline.SampleRate);
                if (nextDist < curDist) srcOff++;
                else break;
            }
            if (srcOff >= sourcePoolByEnd.Length) break;
            double expectedOff = sourcePoolByEnd[srcOff].EndSample / (double)timeline.SampleRate;
            double actualOff = decoded.WallSeconds(allOffs[i].Tick);
            maxOff = Math.Max(maxOff, Math.Abs(actualOff - expectedOff));
            srcOff++;
        }

        Console.Error.WriteLine($"merge-join: paired={pairedNotes} unpairedDecoded={unpairedDecoded} " +
            $"ons={allOns.Count} offs={allOffs.Count} srcPool={sourcePool.Length} " +
            $"maxOn={maxOn:0.000000} maxOff={maxOff:0.000000} maxPitch={maxPitch:0.000000}");
        return (maxOn, maxOff, maxPitch, pairedNotes, allOns.Count);
    }

    /// <summary>True when the source note is a rhythm-family voice that the
    /// exporter routes to channel 9 as a GM-drum remap (spec §8, D6/D7).</summary>
    private static bool IsRhythmSource(Fmp.Core.Visualization.NoteEvent note) =>
        InstrumentIdentity.TryParse(note.InstrumentId, out InstrumentIdentity identity)
        && identity.Family == IdentityFamily.Rhythm;

    private static double? MedianGapSeconds(Fmp.Core.Visualization.NoteEvent[] voice)
    {
        if (voice.Length < 2) return null;
        var gaps = new List<double>();
        for (int i = 1; i < voice.Length; i++)
            gaps.Add((voice[i].StartSample - voice[i - 1].StartSample) / (double)Sr);
        gaps.Sort();
        return gaps.Count % 2 == 1
            ? gaps[gaps.Count / 2]
            : (gaps[gaps.Count / 2 - 1] + gaps[gaps.Count / 2]) / 2.0;
    }

    private static double? PitchCoefficientOfVariation(Fmp.Core.Visualization.NoteEvent[] voice)
    {
        double mean = voice.Average(n => n.InitialMidiNote);
        if (Math.Abs(mean) < 1e-9) return null;
        double variance = voice.Average(n =>
        {
            double d = n.InitialMidiNote - mean;
            return d * d;
        });
        return Math.Sqrt(variance) / mean;
    }

    private static double Round6(double value) => Math.Round(value, 6);

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
                    .AnalyzeCandidateEvidence(build.Map, ReconstructCandidate(receipt), timeline, build.PercussionEvidence);
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
                build.Map, ReconstructCandidate(expectedReceipt), timeline, build.PercussionEvidence);

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
