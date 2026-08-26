namespace Fmp.Cli;

/// <summary>
/// MIDI transcription options. Raw source transport only: fixed 120 BPM /
/// PPQ 960, no musical-grid mode.
/// </summary>
internal sealed class MidiOptions : BatchRenderSettings
{
    public string Input { get; set; }
    public string Output { get; set; }
    public string Timeline { get; set; }

    /// <summary>Optional path for retaining the captured source timeline.</summary>
    public string TimelineOut { get; set; }

    /// <summary>Optional raw endpoint/pitch-state report.</summary>
    public string PitchReport { get; set; }

    /// <summary>
    /// When true, emit one SMF file per semantic source voice (FM1..FM6, SSG,
    /// DAC/sample, rhythm) instead of a single all-channel file. <see cref="Output"/>
    /// is then treated as a directory. Every channel file shares the same global
    /// transport (division, 120 BPM tempo at tick 0, lead-in, total duration) as
    /// the all-channel source, so consumers that tile or diff per-voice files see
    /// one common time base. This is a standalone transcription feature; the
    /// visualization pipeline never consumes MIDI.
    /// </summary>
    public bool Channels { get; set; }

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
                if (RenderOptionsParser.TryParse(ref reader, name, result))
                    continue;
                switch (name)
                {
                    case "--timeline": result.Timeline = reader.RequireValue(name); break;
                    case "--timeline-out": result.TimelineOut = reader.RequireValue(name); break;
                    case "--output":
                    case "-o": result.Output = reader.RequireValue(name); break;
                    case "--pitch-report": result.PitchReport = reader.RequireValue(name); break;
                    case "--channels" when value == null: result.Channels = true; break;
                    default: throw new ArgumentException($"unknown option '{name}'");
                }
            }
            else
            {
                string positional = reader.Next();
                if (positional == "--") continue;
                if (result.Input != null)
                    throw new ArgumentException($"unexpected argument '{positional}'");
                result.Input = positional;
            }
        }

        if (string.IsNullOrWhiteSpace(result.Output))
            throw new ArgumentException("--output (or -o) is required");
        if (string.IsNullOrWhiteSpace(result.Timeline) && string.IsNullOrWhiteSpace(result.Input))
            throw new ArgumentException("specify an input track or --timeline PATH");
        return result;
    }
}