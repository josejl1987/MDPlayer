namespace Fmp.Core.Analysis;

internal sealed class AnalysisOutput
{
    public int SchemaVersion { get; init; } = 1;
    public string AnalysisId { get; init; } = "";
    public AnalysisEngine Engine { get; init; } = new();
    public string TimingMode { get; init; } = "seconds";
    public AnalysisGlobal Global { get; init; } = new();
    public IReadOnlyList<ChannelAnalysis> Channels { get; init; } = Array.Empty<ChannelAnalysis>();
    public IReadOnlyList<KeyRegion> Keys { get; init; } = Array.Empty<KeyRegion>();
    public IReadOnlyList<HarmonySegment> Harmony { get; init; } = Array.Empty<HarmonySegment>();
    public IReadOnlyList<MotifOccurrence> Motifs { get; init; } = Array.Empty<MotifOccurrence>();
    public IReadOnlyList<ChannelRelationship> Relationships { get; init; } = Array.Empty<ChannelRelationship>();
    public IReadOnlyList<PedalToneCandidate> PedalTones { get; init; } = Array.Empty<PedalToneCandidate>();
    public IReadOnlyList<OstinatoCandidate> Ostinatos { get; init; } = Array.Empty<OstinatoCandidate>();
    public IReadOnlyList<AnalysisBoundary> Boundaries { get; init; } = Array.Empty<AnalysisBoundary>();
    public IReadOnlyList<AnalysisWarning> Warnings { get; init; } = Array.Empty<AnalysisWarning>();
}

internal sealed class AnalysisEngine
{
    public string Name { get; init; } = "mdplayer-music-analysis";
    public string Version { get; init; } = "1.0.2";
    public string Music21Version { get; init; } = "";
    public string PartituraVersion { get; init; }
}

internal sealed class AnalysisGlobal
{
    public KeyInterpretation Key { get; init; } = new();
    public PitchStatistics Pitch { get; init; } = new();
}

internal sealed class KeyInterpretation
{
    public KeyCandidate Primary { get; init; }
    public IReadOnlyList<KeyCandidate> Alternatives { get; init; } = Array.Empty<KeyCandidate>();
    public IReadOnlyList<KeyMethodResult> Methods { get; init; } = Array.Empty<KeyMethodResult>();
    public IReadOnlyDictionary<string, int> WinnerVotes { get; init; }
        = new Dictionary<string, int>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<double>> WinnerCorrelations { get; init; }
        = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
    public IReadOnlyDictionary<string, IReadOnlyList<double>> AlternativeCorrelations { get; init; }
        = new Dictionary<string, IReadOnlyList<double>>(StringComparer.Ordinal);
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Withheld("no key candidate");
}

internal sealed class KeyCandidate
{
    public int TonicPitchClass { get; init; } = -1;
    public string Tonic { get; init; } = "";
    public string Mode { get; init; } = "";
    public double Confidence { get; init; }
}

internal sealed class KeyMethodResult
{
    public string Name { get; init; } = "";
    public string Result { get; init; } = "";
    public double Correlation { get; init; }
}

internal sealed class PitchStatistics
{
    public int NoteCount { get; init; }
    public int MinPitchClass { get; init; } = -1;
    public int MaxPitchClass { get; init; } = -1;
    public double MinMidiPitch { get; init; }
    public double MaxMidiPitch { get; init; }
    public double Ambitus { get; init; }
    public double AverageNoteDuration { get; init; }
    public double NoteDensity { get; init; }
    public IReadOnlyList<double> PitchClassDistribution { get; init; } = Array.Empty<double>();
    public JSymbolicFeatures JSymbolic { get; init; } = new();
}

internal sealed class JSymbolicFeatures
{
    public IReadOnlyList<double> PitchClassDistribution { get; init; } = new double[12];
    public IReadOnlyList<double> MelodicIntervalHistogram { get; init; } = [0];
    public double NoteDensity { get; init; }
    public double AverageNoteDuration { get; init; }
    public double PitchVariety { get; init; }
    public double PitchClassVariety { get; init; }
    public double MostCommonPitchClassPrevalence { get; init; }
    public double RelativeStrengthOfTopPitchClasses { get; init; }
}

internal sealed class ChannelAnalysis
{
    public string ChannelId { get; init; } = "";
    public int NoteCount { get; init; }
    public double MinMidiPitch { get; init; }
    public double MaxMidiPitch { get; init; }
    public double Ambitus { get; init; }
    public double MedianMidiPitch { get; init; }
    public double StepRatio { get; init; }
    public double RepetitionRatio { get; init; }
    public double IntervalDiversity { get; init; }
    public IReadOnlyList<int> DirectedIntervals { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> UndirectedIntervals { get; init; } = Array.Empty<int>();
    public IReadOnlyList<double> PitchClassDistribution { get; init; } = Array.Empty<double>();
}

internal sealed class KeyRegion
{
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public int TonicPitchClass { get; init; } = -1;
    public string Tonic { get; init; } = "";
    public string Mode { get; init; } = "";
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Withheld("local timing unavailable");
}

internal sealed class HarmonySegment
{
    public string Status { get; init; } = "";
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public IReadOnlyList<int> PitchClasses { get; init; } = Array.Empty<int>();
    public int BassPitchClass { get; init; } = -1;
    public string Chord { get; init; } = "";
    public string Symbol { get; init; } = "";
    public string Roman { get; init; }
    public AnalysisConfidence RomanConfidence { get; init; }
    public IReadOnlyList<string> Alternatives { get; init; } = Array.Empty<string>();
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Withheld("insufficient pitch coverage");
}

internal sealed class PedalToneCandidate
{
    public string Status { get; init; } = "";
    public string ChannelId { get; init; } = "";
    public int PitchClass { get; init; } = -1;
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public double Coverage { get; init; }
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Withheld("insufficient sustained coverage");
}

internal sealed class OstinatoCandidate
{
    public string Status { get; init; } = "";
    public string ChannelId { get; init; } = "";
    public IReadOnlyList<int> PitchClasses { get; init; } = Array.Empty<int>();
    public int Occurrences { get; init; }
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public double Regularity { get; init; }
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Withheld("insufficient repeated pattern");
}

internal sealed class MotifOccurrence
{
    public string Status { get; init; } = "";
    public string MotifId { get; init; } = "";
    public string ChannelId { get; init; } = "";
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public int Transposition { get; init; }
    public double Similarity { get; init; }
}

internal sealed class ChannelRelationship
{
    public string Status { get; init; } = "";
    public string SourceChannelId { get; init; } = "";
    public string TargetChannelId { get; init; } = "";
    public string Relationship { get; init; } = "";
    public int Interval { get; init; }
    public double Coverage { get; init; }
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Withheld("insufficient overlap");
}

internal sealed class AnalysisBoundary
{
    public string Status { get; init; } = "";
    public long Sample { get; init; }
    public string Kind { get; init; } = "";
    public AnalysisConfidence Confidence { get; init; } = AnalysisConfidence.Observed();
}

internal sealed class AnalysisWarning
{
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
}

internal sealed class AnalysisConfidence
{
    public double Score { get; init; }
    public string Certainty { get; init; } = "withheld";
    public string Method { get; init; } = "";
    public IReadOnlyList<string> Evidence { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Limitations { get; init; } = Array.Empty<string>();

    public static AnalysisConfidence Observed(string method = "timeline") => new()
    {
        Score = 1,
        Certainty = "observed",
        Method = method,
    };

    public static AnalysisConfidence Withheld(string limitation) => new()
    {
        Score = 0,
        Certainty = "withheld",
        Limitations = string.IsNullOrEmpty(limitation) ? Array.Empty<string>() : [limitation],
    };
}
