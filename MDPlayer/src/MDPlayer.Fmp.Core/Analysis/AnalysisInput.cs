using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fmp.Core.Analysis;

internal sealed class AnalysisInput
{
    public int SchemaVersion { get; init; } = 1;
    public string AnalysisId { get; set; } = "";
    public int SampleRate { get; init; }
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public AnalysisTrack Track { get; init; } = new();
    public AnalysisTiming Timing { get; init; } = new();
    public IReadOnlyList<AnalysisChannel> Channels { get; init; } = Array.Empty<AnalysisChannel>();
    public IReadOnlyList<ArpeggioEvidence> ArpeggioEvidence { get; init; } = Array.Empty<ArpeggioEvidence>();
}

internal sealed class AnalysisTrack
{
    public string Title { get; init; } = "";
    public string SourceFormat { get; init; } = "";
    public string SourcePathHint { get; init; } = "";
}

internal sealed class AnalysisTiming
{
    public string TimingMode { get; init; } = "seconds";
    public IReadOnlyList<TempoEvent> TempoEvents { get; init; } = Array.Empty<TempoEvent>();
    public IReadOnlyList<BeatPosition> Beats { get; init; } = Array.Empty<BeatPosition>();
    public IReadOnlyList<MeasurePosition> Measures { get; init; } = Array.Empty<MeasurePosition>();
    public IReadOnlyList<AnalysisLoop> Loops { get; init; } = Array.Empty<AnalysisLoop>();
}

internal sealed record TempoEvent(long Sample, double Bpm, double Confidence);
internal sealed record BeatPosition(long Sample, double Beat);
internal sealed record MeasurePosition(long Sample, int Number);
internal sealed record AnalysisLoop(long Sample, string Kind, int Iteration);

internal sealed class AnalysisChannel
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "unknown";
    public double AnalysisWeight { get; init; } = 1.0;
    public IReadOnlyList<AnalysisNote> Notes { get; init; } = Array.Empty<AnalysisNote>();
}

internal sealed class AnalysisNote
{
    public string Id { get; init; } = "";
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public double StructuralMidiPitch { get; init; }
    public int PitchClass { get; init; } = -1;
    public int Octave { get; init; } = -1;
    public string InstrumentId { get; init; } = "";
    public bool IsRetrigger { get; init; }
    public bool IsPitched { get; init; }
    public bool IsNoise { get; init; }
    public bool Microtonal { get; init; }
    public bool Gliding { get; init; }
    public IReadOnlyList<AnalysisPitchRegion> PitchRegions { get; init; } = Array.Empty<AnalysisPitchRegion>();
}

internal sealed record AnalysisPitchRegion(
    long StartSample,
    long EndSample,
    double MidiPitch,
    double Stability);

internal sealed class ArpeggioEvidence
{
    public string ChannelId { get; init; } = "";
    public long StartSample { get; init; }
    public long EndSample { get; init; }
    public IReadOnlyList<int> PitchClasses { get; init; } = Array.Empty<int>();
    public long PeriodSamples { get; init; }
    public double Regularity { get; init; }
    public double Weight { get; init; }
}

internal static class AnalysisJson
{
    private static readonly JsonSerializerOptions PrettyOptions = CreateOptions(writeIndented: true);
    private static readonly JsonSerializerOptions CanonicalOptions = CreateOptions(writeIndented: false);
    private static readonly JsonSerializerOptions OutputPrettyOptions = CreateOptions(writeIndented: true, ignoreNull: false);
    private static readonly JsonSerializerOptions OutputCanonicalOptions = CreateOptions(writeIndented: false, ignoreNull: false);

    public static string Serialize(AnalysisInput input, bool indented = true)
        => JsonSerializer.Serialize(input, indented ? PrettyOptions : CanonicalOptions);

    public static string Serialize<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, indented ? PrettyOptions : CanonicalOptions);

    public static string SerializeOutput<T>(T value, bool indented = true)
        => JsonSerializer.Serialize(value, indented ? OutputPrettyOptions : OutputCanonicalOptions);

    public static AnalysisInput DeserializeInput(string json)
        => JsonSerializer.Deserialize<AnalysisInput>(json, CanonicalOptions)
            ?? throw new JsonException("Analysis input is empty.");

    public static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, CanonicalOptions)
            ?? throw new JsonException("Analysis JSON is empty.");

    public static string CanonicalizeWithoutId(AnalysisInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var copy = new AnalysisInput
        {
            SchemaVersion = input.SchemaVersion,
            AnalysisId = "",
            SampleRate = input.SampleRate,
            StartSample = input.StartSample,
            EndSample = input.EndSample,
            Track = input.Track ?? new AnalysisTrack(),
            Timing = CanonicalTiming(input.Timing),
            Channels = (input.Channels ?? Array.Empty<AnalysisChannel>())
                .Where(channel => channel is not null)
                .OrderBy(channel => channel.Id, StringComparer.Ordinal)
                .Select(CanonicalChannel)
                .ToArray(),
            ArpeggioEvidence = (input.ArpeggioEvidence ?? Array.Empty<ArpeggioEvidence>())
                .Where(evidence => evidence is not null)
                .OrderBy(evidence => evidence.StartSample)
                .ThenBy(evidence => evidence.EndSample)
                .ThenBy(evidence => evidence.ChannelId, StringComparer.Ordinal)
                .Select(CanonicalArpeggio)
                .ToArray(),
        };
        return Serialize(copy, indented: false);
    }

    private static AnalysisTiming CanonicalTiming(AnalysisTiming timing)
    {
        timing ??= new AnalysisTiming();
        return new AnalysisTiming
        {
            TimingMode = timing.TimingMode ?? "seconds",
            TempoEvents = (timing.TempoEvents ?? Array.Empty<TempoEvent>())
                .OrderBy(value => value.Sample).ThenBy(value => value.Bpm).ToArray(),
            Beats = (timing.Beats ?? Array.Empty<BeatPosition>())
                .OrderBy(value => value.Sample).ThenBy(value => value.Beat).ToArray(),
            Measures = (timing.Measures ?? Array.Empty<MeasurePosition>())
                .OrderBy(value => value.Sample).ThenBy(value => value.Number).ToArray(),
            Loops = (timing.Loops ?? Array.Empty<AnalysisLoop>())
                .OrderBy(value => value.Sample).ThenBy(value => value.Iteration)
                .ThenBy(value => value.Kind, StringComparer.Ordinal).ToArray(),
        };
    }

    private static AnalysisChannel CanonicalChannel(AnalysisChannel channel) => new()
    {
        Id = channel.Id ?? "",
        Kind = channel.Kind ?? "unknown",
        AnalysisWeight = channel.AnalysisWeight,
        Notes = (channel.Notes ?? Array.Empty<AnalysisNote>())
            .Where(note => note is not null)
            .OrderBy(note => note.StartSample).ThenBy(note => note.EndSample)
            .ThenBy(note => note.Id, StringComparer.Ordinal)
            .Select(CanonicalNote).ToArray(),
    };

    private static AnalysisNote CanonicalNote(AnalysisNote note) => new()
    {
        Id = note.Id ?? "",
        StartSample = note.StartSample,
        EndSample = note.EndSample,
        StructuralMidiPitch = note.StructuralMidiPitch,
        PitchClass = note.PitchClass,
        Octave = note.Octave,
        InstrumentId = note.InstrumentId ?? "",
        IsRetrigger = note.IsRetrigger,
        IsPitched = note.IsPitched,
        IsNoise = note.IsNoise,
        Microtonal = note.Microtonal,
        Gliding = note.Gliding,
        PitchRegions = (note.PitchRegions ?? Array.Empty<AnalysisPitchRegion>())
            .Where(region => region is not null)
            .OrderBy(region => region.StartSample)
            .ThenBy(region => region.EndSample)
            .ThenBy(region => region.MidiPitch)
            .ToArray(),
    };

    private static ArpeggioEvidence CanonicalArpeggio(ArpeggioEvidence value) => new()
    {
        ChannelId = value.ChannelId ?? "",
        StartSample = value.StartSample,
        EndSample = value.EndSample,
        PitchClasses = (value.PitchClasses ?? Array.Empty<int>()).OrderBy(x => x).ToArray(),
        PeriodSamples = value.PeriodSamples,
        Regularity = value.Regularity,
        Weight = value.Weight,
    };

    private static JsonSerializerOptions CreateOptions(bool writeIndented, bool ignoreNull = true) => new()
    {
        WriteIndented = writeIndented,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = ignoreNull
            ? JsonIgnoreCondition.WhenWritingNull
            : JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
