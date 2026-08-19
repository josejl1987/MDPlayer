using System.Text.Json;
using Melanchall.DryWetMidi.Core;

namespace Fmp.Benchmarks;

/// <summary>
/// Compares a reference MIDI against a candidate MIDI using the independent
/// note reconstruction from <see cref="ReferenceMidiAnalyzer"/>. Attack
/// matching is monotonic one-to-one with a time tolerance of
/// max(2ms, 2*local ref tick, 2*local cand tick) capped at 25 ms; ties are
/// broken by smallest |time error|, then smallest |effective-pitch error|,
/// then source order. A trackMap.json maps reference track name -> candidate
/// track name; when absent, tracks map by index.
/// </summary>
internal static class ReferenceMidiComparator
{
    public sealed record TrackComparison(
        string? ReferenceName, string? CandidateName,
        int ReferenceAttacks, int CandidateAttacks, int MatchedAttacks,
        int MissingCandidateAttacks, int ExtraCandidateAttacks,
        double Precision, double Recall, double F1);

    public sealed record ComparisonResult(
        string ReferenceMidiSha256, string CandidateMidiSha256,
        IReadOnlyList<TrackComparison> Tracks,
        double OnsetErrorMsMedian, double OnsetErrorMsP95, double OnsetErrorMsMax,
        double DurationDeltaMsMedian, double DurationDeltaMsP95,
        double PitchErrorSemitonesMedian, double PitchErrorSemitonesP95, double PitchErrorSemitonesP99,
        double ReferenceBendsPerNote, double CandidateBendsPerNote,
        int ReferenceRetriggers, int CandidateRetriggers);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string ToJson(ComparisonResult result)
        => JsonSerializer.Serialize(result, JsonOptions);

    /// <summary>
    /// Compares two analyzed MIDIs. If <paramref name="trackMapJsonPath"/> is
    /// provided, reads a JSON object mapping reference track name -> candidate
    /// track name; otherwise tracks are paired by index.
    /// </summary>
    public static ComparisonResult Compare(
        string refMidiPath, string candMidiPath, string? trackMapJsonPath)
    {
        var refAnalysis = ReferenceMidiAnalyzer.Analyze(refMidiPath);
        var candAnalysis = ReferenceMidiAnalyzer.Analyze(candMidiPath);

        var refMap = ReferenceMidiAnalyzer.ReadTempoMap(refMidiPath);
        var candMap = ReferenceMidiAnalyzer.ReadTempoMap(candMidiPath);

        // ---- Track mapping: name -> name, or fallback by index ----
        // Only string-valued properties are mappings; non-string metadata (e.g.
        // "schema": 1) and list-valued metadata ("explicitMappings") are ignored.
        var nameMap = new Dictionary<string, string>();
        if (trackMapJsonPath is not null && File.Exists(trackMapJsonPath))
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(trackMapJsonPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (JsonProperty prop in doc.RootElement.EnumerateObject())
                    if (prop.Value.ValueKind == JsonValueKind.String)
                        nameMap[prop.Name] = prop.Value.GetString();
        }

        var trackPairs = new List<(string? refName, string? candName, int refIdx, int candIdx)>();
        IReadOnlyList<string?> refNames = refAnalysis.Tracks.Select(t => t.Name).ToList();
        IReadOnlyList<string?> candNames = candAnalysis.Tracks.Select(t => t.Name).ToList();

        // Spec §33: an explicit track-map may route one reference track (e.g. a
        // format-0 whole-song reference like vgm2mid) onto the AGGREGATE of the
        // candidate's note-bearing tracks. Map value "*ALL*" means: compare the
        // reference track's attacks against all candidate notes across tracks
        // (the cand *track index* then refers to that aggregated set).
        // A "*DEFAULT*" key supplies the rule for reference tracks not otherwise
        // listed, keeping the versioned track-map.json explicit without
        // hard-coding every GD3 tag name.
        string defaultRule = nameMap.TryGetValue("*DEFAULT*", out string? dr) ? dr : "*INDEX*";
        var aggregateRefIndices = new HashSet<int>();
        var usedCand = new bool[candNames.Count];
        var candByName = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < candNames.Count; i++)
            if (candNames[i] is string cn)
                candByName[cn] = i;

        for (int i = 0; i < refNames.Count; i++)
        {
            string? rn = refNames[i];
            int candIdx = -1;
            bool aggregate = false;

            if (rn is string refName && nameMap.TryGetValue(refName, out string? mapped))
            {
                if (mapped == "*ALL*")
                {
                    aggregate = true;
                }
                else if (!string.IsNullOrEmpty(mapped) && candByName.TryGetValue(mapped, out int mappedIdx))
                {
                    candIdx = mappedIdx;
                }
                else if (i < candNames.Count)
                {
                    candIdx = i;
                }
            }
            else if (defaultRule == "*ALL*")
            {
                aggregate = true;
            }
            else if (defaultRule != "*INDEX*" && candByName.TryGetValue(defaultRule, out int defaultIdx))
            {
                candIdx = defaultIdx;
            }
            else if (rn is string sameName && candByName.TryGetValue(sameName, out int sameNameIdx))
            {
                candIdx = sameNameIdx;
            }
            else if (i < candNames.Count)
            {
                candIdx = i;
            }

            if (aggregate)
            {
                aggregateRefIndices.Add(i);
                trackPairs.Add((rn, "*ALL* (aggregate)", i, -1));
            }
            else if (candIdx >= 0 && !usedCand[candIdx])
            {
                usedCand[candIdx] = true;
                trackPairs.Add((rn, candNames[candIdx], i, candIdx));
            }
            else
            {
                trackPairs.Add((rn, null, i, -1));
            }
        }

        // ---- Build attack lists per mapped pair ----
        var trackComparisons = new List<TrackComparison>();
        var onsetErrorsMs = new List<double>();
        var durationDeltasMs = new List<double>();
        var pitchErrors = new List<double>();

        foreach (var (refName, candName, refIdx, candIdx) in trackPairs)
        {
            var refAttacks = refAnalysis.Notes
                .Where(n => n.Track == refIdx)
                .Select(n => new Attack(n.StartSeconds, n.ActivePitchAtAttack,
                    n.StartTick, n.StartSeconds, n.EndSeconds, n.Port, n.Channel, n.EndTick))
                .OrderBy(a => a.Time).ToList();

            List<Attack> candAttacks = candIdx >= 0
                ? candAnalysis.Notes
                    .Where(n => n.Track == candIdx)
                    .Select(n => new Attack(n.StartSeconds, n.ActivePitchAtAttack,
                        n.StartTick, n.StartSeconds, n.EndSeconds, n.Port, n.Channel, n.EndTick))
                    .OrderBy(a => a.Time).ToList()
                : new List<Attack>();

            // "*ALL*": aggregate every candidate note-bearing track (format-0
            // reference vs format-1 physical-voice candidate). Non-note metadata
            // tracks contribute no notes, so this naturally collapses onto the
            // real voices.
            if (aggregateRefIndices.Contains(refIdx))
            {
                candAttacks = candAnalysis.Notes
                    .Select(n => new Attack(n.StartSeconds, n.ActivePitchAtAttack,
                        n.StartTick, n.StartSeconds, n.EndSeconds, n.Port, n.Channel, n.EndTick))
                    .OrderBy(a => a.Time).ToList();
            }

            (int matched, List<double> trackOnset, List<double> trackDur, List<double> trackPitch) =
                MatchAttacks(refAttacks, candAttacks, refMap, candMap);

            int refCount = refAttacks.Count;
            int candCount = candAttacks.Count;
            int missing = refCount - matched;
            int extra = candCount - matched;
            double precision = candCount == 0 ? (refCount == 0 ? 1.0 : 0.0) : (double)matched / candCount;
            double recall = refCount == 0 ? (candCount == 0 ? 1.0 : 0.0) : (double)matched / refCount;
            double f1 = (precision + recall) > 1e-9 ? 2 * precision * recall / (precision + recall) : 0.0;

            trackComparisons.Add(new TrackComparison(
                refName, candName, refCount, candCount, matched,
                missing, extra, precision, recall, f1));

            onsetErrorsMs.AddRange(trackOnset);
            durationDeltasMs.AddRange(trackDur);
            pitchErrors.AddRange(trackPitch);
        }

        return new ComparisonResult(
            refAnalysis.File.Sha256, candAnalysis.File.Sha256,
            trackComparisons,
            MedianMs(onsetErrorsMs), PercentileMs(onsetErrorsMs, 0.95), MaxMs(onsetErrorsMs),
            MedianMs(durationDeltasMs), PercentileMs(durationDeltasMs, 0.95),
            MedianSemitones(pitchErrors), PercentileSemitones(pitchErrors, 0.95), PercentileSemitones(pitchErrors, 0.99),
            refAnalysis.PitchControl.MedianBendsPerNote, candAnalysis.PitchControl.MedianBendsPerNote,
            refAnalysis.Structural.SamePitchRetriggers, candAnalysis.Structural.SamePitchRetriggers);
    }

    /// <summary>
    /// Monotonic one-to-one attack matching with time tolerance.
    /// </summary>
    private static (int matched, List<double> onsetMs, List<double> durMs, List<double> pitchErr)
        MatchAttacks(List<Attack> refAttacks, List<Attack> candAttacks,
            ReferenceMidiAnalyzer.TempoMap refMap, ReferenceMidiAnalyzer.TempoMap candMap)
    {
        int matched = 0;
        var onsetMs = new List<double>();
        var durMs = new List<double>();
        var pitchErr = new List<double>();
        int c = 0;

        foreach (Attack refA in refAttacks)
        {
            double refTickDur = ReferenceMidiAnalyzer.TickDurationSeconds(refMap, refA.StartTick);
            double bestDt = double.MaxValue;
            double bestDp = double.MaxValue;
            int bestIndex = -1;
            for (int j = c; j < candAttacks.Count; j++)
            {
                Attack cand = candAttacks[j];
                double candTickDur = ReferenceMidiAnalyzer.TickDurationSeconds(candMap, cand.StartTick);
                double tolerance = Math.Min(0.025,
                    Math.Max(0.002, Math.Max(2 * refTickDur, 2 * candTickDur)));
                double timeDiff = cand.Time - refA.Time;
                if (timeDiff > tolerance)
                    break; // candidates only move later; no further within tolerance
                if (Math.Abs(timeDiff) > tolerance)
                    continue;
                double pitchDiff = Math.Abs(cand.EffectivePitch - refA.EffectivePitch);
                double dt = Math.Abs(timeDiff);
                if (dt < bestDt - 1e-12 || (Math.Abs(dt - bestDt) < 1e-12 && pitchDiff < bestDp))
                {
                    bestDt = dt;
                    bestDp = pitchDiff;
                    bestIndex = j;
                }
            }

            if (bestIndex >= 0)
            {
                Attack best = candAttacks[bestIndex];
                matched++;
                onsetMs.Add(bestDt * 1000.0);
                durMs.Add(Math.Abs(best.Duration - refA.Duration) * 1000.0);
                pitchErr.Add(bestDp);
                c = bestIndex + 1; // monotonic: never reuse a matched candidate
            }
        }

        return (matched, onsetMs, durMs, pitchErr);
    }

    private sealed record Attack(double Time, double EffectivePitch, long StartTick,
        double StartTime, double EndTime, int Port, int Channel, long EndTick)
    {
        public double Duration => EndTime - StartTime;
    }

    // ---- percentile helpers ----

    private static double MedianMs(List<double> xs)
        => xs.Count == 0 ? 0.0 : Percentile(xs, 0.50);

    private static double PercentileMs(List<double> xs, double p)
        => xs.Count == 0 ? 0.0 : Percentile(xs, p);

    private static double MaxMs(List<double> xs)
        => xs.Count == 0 ? 0.0 : xs.Max();

    private static double MedianSemitones(List<double> xs)
        => xs.Count == 0 ? 0.0 : Percentile(xs, 0.50);

    private static double PercentileSemitones(List<double> xs, double p)
        => xs.Count == 0 ? 0.0 : Percentile(xs, p);

    private static double Percentile(List<double> xs, double p)
    {
        var sorted = xs.OrderBy(x => x).ToList();
        int idx = Math.Clamp((int)Math.Floor(p * (sorted.Count - 1)), 0, sorted.Count - 1);
        return sorted[idx];
    }
}
