using System.Diagnostics;
using System.Text.Json;

namespace Fmp.Application.Export;

/// <summary>Opt-in, allocation-aware measurements for one export invocation.</summary>
public sealed record ExportPhaseReceipt(
    string Fixture,
    string Phase,
    long WallMilliseconds,
    long CpuMilliseconds,
    long AllocatedBytes,
    long EventCount,
    bool Applicable = true);

public sealed record ExportPerformanceComparison(
    string Status,
    ExportPhaseReceipt? Baseline,
    ExportPhaseReceipt? Candidate,
    long? WallDeltaMilliseconds,
    long? AllocationDeltaBytes,
    double? WallSpeedup);

public sealed class ExportPerformanceSummary
{
    public required string Fixture { get; init; }
    public required object Input { get; init; }
    public required object Configuration { get; init; }
    public required object Environment { get; init; }
    public IReadOnlyList<ExportPhaseReceipt> Phases { get; init; } = Array.Empty<ExportPhaseReceipt>();
    public ExportPerformanceComparison Comparison { get; init; } =
        new("not-run", null, null, null, null, null);

    /// <summary>
    /// Mirror of the Core percussion-fidelity receipt (spec §11, D8) populated
    /// when performance receipts are enabled; null otherwise.
    /// </summary>
    public object? Percussion { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });

    public IReadOnlyList<string> ToHumanReadable()
    {
        return Phases.Select(p => p.Applicable
            ? $"phase={p.Phase}; wall-ms={p.WallMilliseconds}; cpu-ms={p.CpuMilliseconds}; "
              + $"allocated-bytes={p.AllocatedBytes}; events={p.EventCount}"
            : $"phase={p.Phase}; applicable=false")
            .Append($"comparison-status={Comparison.Status}; baseline={Comparison.Baseline?.Phase ?? "N/A"}; candidate={Comparison.Candidate?.Phase ?? "N/A"}")
            .ToArray();
    }
}

internal sealed class ExportPerformanceRecorder
{
    private readonly string _fixture;
    private readonly object _input;
    private readonly object _configuration;
    private readonly List<ExportPhaseReceipt> _phases = new();
    public ExportPerformanceRecorder(string fixture, object input, object configuration)
    {
        _fixture = fixture;
        _input = input;
        _configuration = configuration;
        // The application MIDI boundary has no ownership of these pipeline stages;
        // retaining explicit N/A rows makes reports comparable with CLI/video runs.
        foreach (string phase in new[] { "capture", "frame-rendering", "pipe-writes", "encoding" })
            _phases.Add(new ExportPhaseReceipt(fixture, phase, 0, 0, 0, 0, false));
    }

    public T Measure<T>(string phase, long eventCount, Func<T> action)
    {
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
        Stopwatch watch = Stopwatch.StartNew();
        try { return action(); }
        finally
        {
            watch.Stop();
            _phases.Add(new ExportPhaseReceipt(_fixture, phase, watch.ElapsedMilliseconds,
                (long)Process.GetCurrentProcess().TotalProcessorTime.Subtract(cpuBefore).TotalMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated, eventCount));
        }
    }

    public ExportPerformanceSummary Complete(object? percussion = null) => new()
    {
        Fixture = _fixture,
        Input = _input,
        Configuration = _configuration,
        Environment = new
        {
            Runtime = Environment.Version.ToString(),
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
        },
        Phases = _phases.ToArray(),
        Percussion = percussion,
    };
}
