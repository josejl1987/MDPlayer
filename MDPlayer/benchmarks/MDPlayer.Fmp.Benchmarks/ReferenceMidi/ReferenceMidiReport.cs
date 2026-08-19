using System.Text.Json;

namespace Fmp.Benchmarks;

/// <summary>
/// Aggregates analyzed MIDIs + comparisons into a deterministic
/// reference-baseline report (JSON + Markdown). Severity ranking priority:
/// missing attacks &gt; extra attacks &gt; gross pitch error &gt;
/// duration/retrigger mismatch &gt; tempo mismatch &gt; bend spam &gt;
/// instrument/percussion mismatch. No timestamps are embedded so the report is
/// byte-stable for the same inputs.
///
/// Report command manifest schema (JSON object):
///   {
///     "mdPlayerCommit": "4bf5d7c5...",         // optional; env MDPLAYER_COMMIT or literal used
///     "toolLockHashes": { "vgm2mid": "sha", ... },  // optional
///     "songs": [
///       {
///         "id": "song1",
///         "name": "First Attack",
///         "referenceMidi": "path/to/ref.mid",
///         "candidateMidi": "path/to/cand.mid",
///         // optional; when absent the comparison/analysis is recomputed:
///         "referenceAnalysis": "path/to/ref.analysis.json",
///         "candidateAnalysis": "path/to/cand.analysis.json",
///         "comparison": "path/to/comparison.json"
///       }, ...
///     ]
///   }
/// </summary>
internal static class ReferenceMidiReport
{
    public const string DefaultMdPlayerCommit = "4bf5d7c5";

    /// <summary>Constant category table D0..D15.</summary>
    internal static readonly IReadOnlyList<CategoryDef> Categories = new[]
    {
        new CategoryDef("D0",  "tempo-mismatch"),
        new CategoryDef("D1",  "missing-attacks"),
        new CategoryDef("D2",  "extra-attacks"),
        new CategoryDef("D3",  "gross-pitch-error"),
        new CategoryDef("D4",  "duration-mismatch"),
        new CategoryDef("D5",  "retrigger-mismatch"),
        new CategoryDef("D6",  "bend-spam"),
        new CategoryDef("D7",  "instrument-mismatch"),
        new CategoryDef("D8",  "percussion-mismatch"),
        new CategoryDef("D9",  "precision-low"),
        new CategoryDef("D10", "recall-low"),
        new CategoryDef("D11", "f1-low"),
        new CategoryDef("D12", "onset-jitter"),
        new CategoryDef("D13", "attack-alignment"),
        new CategoryDef("D14", "candidate-empty"),
        new CategoryDef("D15", "other"),
    };

    public sealed record CategoryDef(string Code, string Label);

    internal sealed record Divergence(string SongId, string Category, string Label,
        int Severity, int Priority, double Metric, string Message);

    // severity ordering: higher number = more severe. Priority from the task
    // (this fixes the sort order among ties).
    private static readonly Dictionary<string, int> CategorySeverity = new()
    {
        ["D1"] = 9,   // missing attacks
        ["D2"] = 8,   // extra attacks
        ["D3"] = 7,   // gross pitch error
        ["D4"] = 6,   // duration mismatch
        ["D5"] = 5,   // retrigger mismatch
        ["D0"] = 4,   // tempo mismatch
        ["D6"] = 3,   // bend spam
        ["D7"] = 2,   // instrument mismatch
        ["D8"] = 2,   // percussion mismatch
        ["D9"] = 6,   // precision low
        ["D10"] = 7,  // recall low
        ["D11"] = 6,  // f1 low
        ["D12"] = 4,  // onset jitter
        ["D13"] = 6,  // attack alignment
        ["D14"] = 9,  // candidate empty
        ["D15"] = 1,  // other
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Runs the report from a manifest JSON and writes reference-baseline.json and .md into outputDir.</summary>
    public static int Run(string manifestPath, string outputDir)
    {
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"error: manifest not found: {manifestPath}");
            return 2;
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        JsonElement root = manifest.RootElement;

        string mdPlayerCommit = GetString(root, "mdPlayerCommit")
            ?? Environment.GetEnvironmentVariable("MDPLAYER_COMMIT")
            ?? DefaultMdPlayerCommit;

        var toolLockHashes = new Dictionary<string, string>();
        if (root.TryGetProperty("toolLockHashes", out JsonElement tlh) && tlh.ValueKind == JsonValueKind.Object)
            foreach (JsonProperty p in tlh.EnumerateObject())
                toolLockHashes[p.Name] = p.Value.GetString() ?? "";

        var divergences = new List<ReferenceMidiReport.Divergence>();
        var summaries = new List<object>();

        if (root.TryGetProperty("songs", out JsonElement songsEl) && songsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement song in songsEl.EnumerateArray())
            {
                var built = BuildSongReport(song);
                summaries.Add(built.summary);
                divergences.AddRange(built.divergences);
            }
        }

        // Rank divergences: severity (desc), then priority ordinal (desc), then songId (asc) for determinism.
        var ranked = divergences
            .OrderByDescending(d => d.Severity)
            .ThenByDescending(d => d.Priority)
            .ThenBy(d => d.SongId)
            .ToList();

        var report = new
        {
            schema = "mdplayer.reference-baseline/v1",
            mdPlayerCommit,
            toolLockHashes,
            songs = summaries,
            topDivergences = ranked.Take(50),
            listeningNotes = new { status = "placeholder", note = "human listening pass pending" },
            deterministic = true,
        };

        Directory.CreateDirectory(outputDir);
        string jsonPath = Path.Combine(outputDir, "reference-baseline.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(report, JsonOptions));

        string mdPath = Path.Combine(outputDir, "reference-baseline.md");
        File.WriteAllText(mdPath, BuildMarkdown(mdPlayerCommit, summaries, ranked));

        Console.WriteLine($"wrote {Path.GetFullPath(jsonPath)}");
        Console.WriteLine($"wrote {Path.GetFullPath(mdPath)}");
        return 0;
    }

    private static (object summary, List<Divergence> divergences) BuildSongReport(JsonElement song)
    {
        string songId = GetString(song, "id") ?? "song";
        string? name = GetString(song, "name");
        string? refMidi = GetString(song, "referenceMidi");
        string? candMidi = GetString(song, "candidateMidi");
        string? compJson = GetString(song, "comparison");

        string comparison = ReadOrCompute(compJson, refMidi, candMidi, out bool computed);

        // Extract metrics from comparison JSON
        int refAttacks = SumField(comparison, "tracks", "referenceAttacks");
        int candAttacks = SumField(comparison, "tracks", "candidateAttacks");
        int matched = SumField(comparison, "tracks", "matchedAttacks");
        int missing = refAttacks - matched;
        int extra = candAttacks - matched;
        double onsetP95 = Num(comparison, "onsetErrorMsP95");
        double pitchP99 = Num(comparison, "pitchErrorSemitonesP99");
        double durP95 = Num(comparison, "durationDeltaMsP95");
        double refBpn = Num(comparison, "referenceBendsPerNote");
        double candBpn = Num(comparison, "candidateBendsPerNote");
        int refRetrig = AsInt(comparison, "referenceRetriggers");
        int candRetrig = AsInt(comparison, "candidateRetriggers");

        // tempo / instrument cross-check from optional analyses (or .mid on the fly)
        var tempoInfo = ReadTempoInfo(song, refMidi, candMidi);
        var instrInfo = ReadInstrumentInfo(song, refMidi, candMidi);

        List<Divergence> d = new();
        double missingRatio = refAttacks > 0 ? missing / (double)refAttacks : 0.0;
        double extraRatio = candAttacks > 0 ? extra / (double)candAttacks : 0.0;

        if (candAttacks == 0 && refAttacks > 0)
            d.Add(Div(songId, "D14", 100, refAttacks, "candidate produced no attacks"));
        if (missingRatio > 0.05)
            d.Add(Div(songId, "D1", 90 + (int)Math.Min(9, missingRatio * 100), refAttacks, $"missing {missing}/{refAttacks} reference attacks"));
        if (extraRatio > 0.05)
            d.Add(Div(songId, "D2", 85 + (int)Math.Min(9, extraRatio * 100), candAttacks, $"extra {extra}/{candAttacks} candidate attacks"));
        if (pitchP99 > 2.0)
            d.Add(Div(songId, "D3", 80, pitchP99, $"gross pitch error p99={pitchP99:F2} semitones"));
        if (durP95 > 50.0)
            d.Add(Div(songId, "D4", 60, durP95, $"duration mismatch p95={durP95:F1} ms"));
        if (Math.Abs(refRetrig - candRetrig) > 0 && refRetrig >= 0)
            d.Add(Div(songId, "D5", 55, Math.Max(refRetrig, candRetrig), $"retrigger mismatch ref={refRetrig} cand={candRetrig}"));

        if (tempoInfo.tempoMismatch)
            d.Add(Div(songId, "D0", 50, tempoInfo.bpmRatio, $"tempo mismatch ratio={tempoInfo.bpmRatio:F3}"));

        // bend spam: RED if candidate/reference > 4x or candidate > 12 bends/note
        double bendRatio = refBpn > 1e-9 ? candBpn / refBpn : candBpn;
        bool bendRed = bendRatio > 4.0 || candBpn > 12.0;
        if (bendRed)
            d.Add(Div(songId, "D6", 40, bendRatio, $"bend spam cand/ref={bendRatio:F2}x cand={candBpn:F1}/note"));

        if (instrInfo.instrumentMismatch)
            d.Add(Div(songId, "D7", 30, 0, "instrument mismatch across non-percussion channels"));
        if (instrInfo.percussionMismatch)
            d.Add(Div(songId, "D8", 30, 0, "percussion (channel 9) note distribution mismatch"));

        // low precision/recall/F1 signals
        if (refAttacks > 0 && candAttacks > 0)
        {
            double precision = candAttacks > 0 ? matched / (double)candAttacks : 0.0;
            double recall = refAttacks > 0 ? matched / (double)refAttacks : 0.0;
            double f1 = (precision + recall) > 1e-9 ? 2 * precision * recall / (precision + recall) : 0.0;
            if (precision < 0.8) d.Add(Div(songId, "D9", 62, precision, $"precision={precision:F3}"));
            if (recall < 0.8) d.Add(Div(songId, "D10", 75, recall, $"recall={recall:F3}"));
            if (f1 < 0.8) d.Add(Div(songId, "D11", 60, f1, $"F1={f1:F3}"));
        }

        object summary = new
        {
            songId,
            name,
            referenceMidi = ResolveRelative(refMidi),
            candidateMidi = ResolveRelative(candMidi),
            comparisonComputed = computed,
            metrics = new
            {
                referenceAttacks = refAttacks,
                candidateAttacks = candAttacks,
                matchedAttacks = matched,
                missingAttacks = missing,
                extraAttacks = extra,
                precision = refAttacks > 0 || candAttacks > 0 ? (candAttacks > 0 ? matched / (double)candAttacks : 0.0) : 1.0,
                recall = refAttacks > 0 ? matched / (double)refAttacks : 0.0,
                onsetErrorMsP95 = onsetP95,
                pitchErrorSemitonesP99 = pitchP99,
                durationDeltaMsP95 = durP95,
                referenceBendsPerNote = refBpn,
                candidateBendsPerNote = candBpn,
                referenceRetriggers = refRetrig,
                candidateRetriggers = candRetrig,
            },
            tempo = tempoInfo.avgBpmPair,
            divergenceCount = d.Count,
        };

        return (summary, d);
    }

    // ================= helpers =================

    private static Divergence Div(string songId, string code, int severity, double metric, string message)
        => new(songId, code, Label(code), severity, CategorySeverity[code], Round(metric), message);

    private static string Label(string code)
    {
        foreach (var c in Categories)
            if (c.Code == code) return c.Label;
        return code;
    }

    private static string ReadOrCompute(string? compJson, string? refMidi, string? candMidi, out bool computed)
    {
        if (!string.IsNullOrEmpty(compJson) && File.Exists(compJson))
        {
            computed = false;
            return File.ReadAllText(compJson);
        }
        computed = true;
        if (string.IsNullOrEmpty(refMidi) || string.IsNullOrEmpty(candMidi))
            return "{}";
        ReferenceMidiComparator.ComparisonResult result = ReferenceMidiComparator.Compare(refMidi, candMidi, trackMapJsonPath: null);
        return ReferenceMidiComparator.ToJson(result);
    }

    private static (bool tempoMismatch, double bpmRatio, object avgBpmPair) ReadTempoInfo(JsonElement song, string? refMidi, string? candMidi)
    {
        double refAvg = AverageBpm(song, "referenceAnalysis", refMidi);
        double candAvg = AverageBpm(song, "candidateAnalysis", candMidi);
        if (refAvg <= 0 || candAvg <= 0)
            return (false, refAvg > 0 && candAvg <= 0 ? double.PositiveInfinity : 0.0, new { referenceBpm = refAvg, candidateBpm = candAvg });
        double ratio = candAvg / refAvg;
        bool mismatch = ratio < 0.9 || ratio > 1.11;
        return (mismatch, ratio, new { referenceBpm = Round(refAvg), candidateBpm = Round(candAvg) });
    }

    private static double AverageBpm(JsonElement song, string analysisField, string? midiPath)
    {
        string? js = GetString(song, analysisField);
        if (!string.IsNullOrEmpty(js) && File.Exists(js))
        {
            using var doc = JsonDocument.Parse(ToJsonObject(File.ReadAllText(js)));
            if (doc.RootElement.TryGetProperty("tempo", out JsonElement tempo) && tempo.ValueKind == JsonValueKind.Array)
            {
                var bpms = tempo.EnumerateArray().Select(t => t.TryGetProperty("bpm", out JsonElement b) && b.TryGetDouble(out double d) ? d : 0.0).Where(x => x > 0).ToList();
                return bpms.Count > 0 ? bpms.Average() : 0.0;
            }
        }
        if (!string.IsNullOrEmpty(midiPath) && File.Exists(midiPath))
        {
            var analysis = ReferenceMidiAnalyzer.Analyze(midiPath);
            var bpms = analysis.Tempo.Where(t => t.Bpm > 0).Select(t => t.Bpm).ToList();
            return bpms.Count > 0 ? bpms.Average() : 0.0;
        }
        return 0.0;
    }

    private static (bool instrumentMismatch, bool percussionMismatch) ReadInstrumentInfo(JsonElement song, string? refMidi, string? candMidi)
    {
        (var refEndpoints, var refPerc) = EndpointsOf(song, "referenceAnalysis", refMidi);
        (var candEndpoints, var candPerc) = EndpointsOf(song, "candidateAnalysis", candMidi);
        bool instrumentMismatch = false;
        if (refEndpoints.Count > 0 && candEndpoints.Count > 0)
        {
            var refNonPerc = refEndpoints.Where(e => e.Channel != 9).ToHashSet();
            var candNonPerc = candEndpoints.Where(e => e.Channel != 9).ToHashSet();
            instrumentMismatch = !refNonPerc.SetEquals(candNonPerc);
        }
        bool percussionMismatch = refPerc > 0 && candPerc > 0 && Math.Abs(refPerc - candPerc) / (double)Math.Max(1, refPerc) > 0.3;
        return (instrumentMismatch, percussionMismatch);
    }

    private static (List<(int Port, int Channel)> endpoints, int percNotes) EndpointsOf(JsonElement song, string analysisField, string? midiPath)
    {
        var endpoints = new List<(int, int)>();
        int percNotes = 0;
        string? js = GetString(song, analysisField);
        if (!string.IsNullOrEmpty(js) && File.Exists(js))
        {
            using var doc = JsonDocument.Parse(ToJsonObject(File.ReadAllText(js)));
            if (doc.RootElement.TryGetProperty("byEndpoint", out JsonElement be) && be.ValueKind == JsonValueKind.Array)
            {
                foreach (var ep in be.EnumerateArray())
                {
                    int port = GetInt(ep, "port");
                    int channel = GetInt(ep, "channel");
                    endpoints.Add((port, channel));
                    if (channel == 9)
                        percNotes += ep.TryGetProperty("notes", out JsonElement n) ? n.GetArrayLength() : 0;
                }
            }
        }
        else if (!string.IsNullOrEmpty(midiPath) && File.Exists(midiPath))
        {
            var analysis = ReferenceMidiAnalyzer.Analyze(midiPath);
            foreach (var n in analysis.Notes)
            {
                endpoints.Add((n.Port, n.Channel));
                if (n.Channel == 9) percNotes++;
            }
        }
        return (endpoints.Distinct().ToList(), percNotes);
    }

    private static string BuildMarkdown(string commit, List<object> summaries, List<Divergence> ranked)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# MDPlayer reference baseline");
        sb.AppendLine();
        sb.AppendLine($"- mdPlayer commit: `{commit}`");
        sb.AppendLine($"- songs analyzed: {summaries.Count}");
        sb.AppendLine($"- top divergences: {ranked.Count}");
        sb.AppendLine();
        foreach (var s in summaries)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(s, JsonOptions));
            var root = doc.RootElement;
            var m = root.GetProperty("metrics");
            sb.AppendLine($"## {GetString(root, "songId")} — {GetString(root, "name") ?? ""}");
            sb.AppendLine();
            sb.AppendLine($"| reference attacks | candidate attacks | matched | missing | extra |");
            sb.AppendLine($"|---|---|---|---|---|");
            sb.AppendLine($"| {m.GetProperty("referenceAttacks")} | {m.GetProperty("candidateAttacks")} | {m.GetProperty("matchedAttacks")} | {m.GetProperty("missingAttacks")} | {m.GetProperty("extraAttacks")} |");
            sb.AppendLine();
        }
        sb.AppendLine("## Top divergences");
        sb.AppendLine();
        if (ranked.Count == 0)
        {
            sb.AppendLine("None.");
        }
        else
        {
            sb.AppendLine("| song | category | severity | metric | message |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var d in ranked)
                sb.AppendLine($"| {d.SongId} | {d.Category} | {d.Severity} | {d.Metric:F3} | {d.Message} |");
        }
        sb.AppendLine();
        sb.AppendLine("## Listening notes (placeholder)");
        sb.AppendLine();
        sb.AppendLine("Human listening pass pending — results below are machine-ruled only.");
        sb.AppendLine();
        return sb.ToString();
    }

    // ---- JSON element helpers ----
    private static string? GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) ? (v.ValueKind == JsonValueKind.Null ? null : v.GetString()) : null;

    private static int GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int r) ? r : 0;

    private static int AsInt(string json, string name)
    {
        using var doc = JsonDocument.Parse(ToJsonObject(json));
        return doc.RootElement.TryGetProperty(name, out JsonElement v) && v.TryGetInt32(out int r) ? r : 0;
    }

    private static double Num(string json, string name)
    {
        using var doc = JsonDocument.Parse(ToJsonObject(json));
        if (doc.RootElement.TryGetProperty(name, out JsonElement v) && v.TryGetDouble(out double r))
            return r;
        return 0.0;
    }

    private static int SumField(string json, string arrayProp, string field)
    {
        using var doc = JsonDocument.Parse(ToJsonObject(json));
        if (doc.RootElement.TryGetProperty(arrayProp, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array)
            return arr.EnumerateArray().Sum(e => GetInt(e, field));
        return 0;
    }

    /// <summary>
    /// The analyzer/comparator print a human banner line before their JSON
    /// (e.g. "MDPlayer Visualization Benchmark (§23.4)"). Strip any leading
    /// non-JSON text so the report can parse the embedded document deterministically.
    /// </summary>
    private static string ToJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        int i = 0;
        while (i < json.Length && json[i] != '{' && json[i] != '[') i++;
        if (i >= json.Length) return "{}";
        return json.Substring(i);
    }

    private static double Round(double x) => double.IsFinite(x) ? Math.Round(x, 4) : x;

    private static string? ResolveRelative(string? path) => path;
}
