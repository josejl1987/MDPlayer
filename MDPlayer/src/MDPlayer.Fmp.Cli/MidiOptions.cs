namespace Fmp.Cli;

/// <summary>The maximum MIDI-valid division (ticks per quarter note).</summary>
internal static class MidiOptionsLimits
{
    public const int MaxPpq = 32767;
}

/// <summary>
/// Raw MIDI transcription options. Musical interpretation is deliberately not
/// configurable here: tempo, meter, downbeat, quantization, tuning normalization
/// and track-layout transforms belong to separate downstream tools.
/// </summary>
internal sealed class MidiOptions : BatchRenderSettings
{
    public string Input { get; set; }
    public string Output { get; set; }
    public string Timeline { get; set; }
    public int Ppq { get; set; } = 960;

    /// <summary>Optional raw source-time fidelity report.</summary>
    public string TimingReport { get; set; }

    /// <summary>Optional raw endpoint/pitch-state report.</summary>
    public string PitchReport { get; set; }

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
            if (reader.TryReadOption(out string name, out _))
            {
                if (RenderOptionsParser.TryParse(ref reader, name, result))
                    continue;
                switch (name)
                {
                    case "--timeline": result.Timeline = reader.RequireValue(name); break;
                    case "--output":
                    case "-o": result.Output = reader.RequireValue(name); break;
                    case "--ppq": result.Ppq = reader.ReadInt(name); break;
                    case "--timing-report": result.TimingReport = reader.RequireValue(name); break;
                    case "--pitch-report": result.PitchReport = reader.RequireValue(name); break;
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

        if (result.Ppq <= 0)
            throw new ArgumentException("--ppq must be positive");
        if (result.Ppq > MidiOptionsLimits.MaxPpq)
            throw new ArgumentException($"--ppq must not exceed {MidiOptionsLimits.MaxPpq} (MIDI-valid division)");
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new ArgumentException("--output (or -o) is required");
        if (string.IsNullOrWhiteSpace(result.Timeline) && string.IsNullOrWhiteSpace(result.Input))
            throw new ArgumentException("specify an input track or --timeline PATH");
        return result;
    }
}