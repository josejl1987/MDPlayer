namespace Fmp.Cli;

using Fmp.Core.Visualization;

internal enum AnalysisDetail { Minimal, Standard, Full }

internal sealed class AnalyzeOptions : RenderSettings
{
    public string Input { get; set; }
    public string Timeline { get; set; }
    public string AnalysisPython { get; set; }
    public string AnalysisOutput { get; set; }
    public string AnalysisCache { get; set; }
    public AnalysisDetail Detail { get; set; } = AnalysisDetail.Standard;
    public bool Force { get; set; }
    public int TimeoutMinutes { get; set; } = 10;
    /// <summary>Optional in-memory capture supplied by visualization callers.</summary>
    public VisualizationTimeline CapturedTimeline { get; set; }
    public bool CaptureCache { get; set; } = true;
    public Dictionary<string, string> CaptureDependencies { get; } = new(StringComparer.Ordinal);

    /// <summary>Where human-readable progress lines are written (default stdout).</summary>
    public TextWriter Output { get; set; } = Console.Out;
}

internal static class AnalyzeOptionsParser
{
    public static AnalyzeOptions Parse(string[] args)
    {
        var result = new AnalyzeOptions();
        var reader = new ArgumentReader(args ?? Array.Empty<string>());
        while (reader.HasMore)
        {
            if (reader.TryReadOption(out string name, out string value))
            {
                if (RenderOptionsParser.TryParse(ref reader, name, result)) continue;
                switch (name)
                {
                    case "--timeline": result.Timeline = reader.RequireValue(name); break;
                    case "--analysis-python": result.AnalysisPython = reader.RequireValue(name); break;
                    case "--analysis-output": result.AnalysisOutput = reader.RequireValue(name); break;
                    case "--analysis-cache": result.AnalysisCache = reader.RequireValue(name); break;
                    case "--analysis-detail": result.Detail = ParseDetail(reader.RequireValue(name)); break;
                    case "--analysis-force" when value == null: result.Force = true; break;
                    case "--analysis-timeout-minutes": result.TimeoutMinutes = reader.ReadInt(name); break;
                    default: throw new ArgumentException($"unknown option '{name}'");
                }
            }
            else
            {
                string positional = reader.Next();
                if (positional == "--") continue;
                if (result.Input != null) throw new ArgumentException($"unexpected argument '{positional}'");
                result.Input = positional;
            }
        }
        if (string.IsNullOrWhiteSpace(result.Input) == (string.IsNullOrWhiteSpace(result.Timeline)))
            throw new ArgumentException("specify exactly one input .ovi or --timeline PATH");
        if (result.TimeoutMinutes <= 0) throw new ArgumentException("--analysis-timeout-minutes must be positive");
        return result;
    }

    private static AnalysisDetail ParseDetail(string value) => value.Trim().ToLowerInvariant() switch
    {
        "minimal" => AnalysisDetail.Minimal,
        "standard" => AnalysisDetail.Standard,
        "full" => AnalysisDetail.Full,
        _ => throw new ArgumentException($"unknown analysis detail '{value}' (expected minimal, standard, or full)"),
    };
}
