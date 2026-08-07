using System.Globalization;
using Fmp.Core.Timing;

namespace Fmp.Cli;

internal enum MidiTempoSource
{
    Auto,
    Driver,
    Symbolic,
    Fixed,
}

internal sealed class MidiOptions : BatchRenderSettings
{
    public string Input { get; set; }
    public string Output { get; set; }
    public string Timeline { get; set; }

    public int Ppq { get; set; } = 960;
    public MidiTempoSource TempoSource { get; set; } = MidiTempoSource.Auto;
    public double? Bpm { get; set; }
    public long? BeatOffsetSamples { get; set; }
    public Meter Meter { get; set; }
    public long? FirstDownbeatSample { get; set; }
    public string Quantize { get; set; } = "off";
    public string TimingReport { get; set; }
    public bool StrictTiming { get; set; }
    public bool EmitPitchBend { get; set; } = true;
    public int BendRange { get; set; } = 2;
    public bool UsePercussionChannel { get; set; } = true;
    public TextWriter OutputWriter { get; set; } = Console.Out;
}

internal static class MidiOptionsParser
{
    public static MidiOptions Parse(string[] args)
    {
        var result = new MidiOptions();
        var reader = new ArgumentReader(args ?? Array.Empty<string>());
        while (reader.HasMore)
        {
            if (reader.TryReadOption(out string name, out string value))
            {
                if (RenderOptionsParser.TryParse(ref reader, name, result)) continue;
                switch (name)
                {
                    case "--timeline": result.Timeline = reader.RequireValue(name); break;
                    case "--output":
                    case "-o":
                        result.Output = reader.RequireValue(name);
                        break;
                    case "--ppq": result.Ppq = reader.ReadInt(name); break;
                    case "--tempo-source": result.TempoSource = ParseTempoSource(reader.RequireValue(name)); break;
                    case "--bpm": result.Bpm = reader.ReadDouble(name); break;
                    case "--beat-offset-samples": result.BeatOffsetSamples = long.Parse(reader.RequireValue(name), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--meter": result.Meter = Meter.TryParse(reader.RequireValue(name)); break;
                    case "--first-downbeat-sample": result.FirstDownbeatSample = long.Parse(reader.RequireValue(name), System.Globalization.CultureInfo.InvariantCulture); break;
                    case "--quantize": result.Quantize = reader.RequireValue(name); break;
                    case "--timing-report": result.TimingReport = reader.RequireValue(name); break;
                    case "--strict-timing" when value == null: result.StrictTiming = true; break;
                    case "--no-pitch-bend" when value == null: result.EmitPitchBend = false; break;
                    case "--bend-range": result.BendRange = reader.ReadInt(name); break;
                    case "--no-percussion-channel" when value == null: result.UsePercussionChannel = false; break;
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
        if (result.Ppq <= 0)
            throw new ArgumentException("--ppq must be positive");
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new ArgumentException("--output (or -o) is required");
        if (string.IsNullOrWhiteSpace(result.Timeline) && string.IsNullOrWhiteSpace(result.Input))
            throw new ArgumentException("specify an input track or --timeline PATH");
        if (result.Meter is null)
            result.Meter = null;
        return result;
    }

    private static MidiTempoSource ParseTempoSource(string source) => source.ToLowerInvariant() switch
    {
        "auto" => MidiTempoSource.Auto,
        "driver" => MidiTempoSource.Driver,
        "symbolic" => MidiTempoSource.Symbolic,
        "fixed" => MidiTempoSource.Fixed,
        _ => throw new ArgumentException($"unknown --tempo-source '{source}'"),
    };
}
