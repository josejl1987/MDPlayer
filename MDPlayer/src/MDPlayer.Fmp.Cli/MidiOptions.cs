using Fmp.Core.Timing;

namespace Fmp.Cli;

/// <summary>The maximum MIDI-valid division (ticks per quarter note).</summary>
internal static class MidiOptionsLimits
{
    public const int MaxPpq = 32767;
}

/// <summary>
/// MIDI transcription options. Raw source transport remains the default; the
/// optional musical-grid mode serializes the validated/inferred time map without
/// changing source event time or pitch.
/// </summary>
internal sealed class MidiOptions : BatchRenderSettings
{
    public string Input { get; set; }
    public string Output { get; set; }
    public string Timeline { get; set; }
    public int Ppq { get; set; } = 960;

    public bool MusicalGrid { get; set; }

    /// <summary>Explicit musical tempo override; implies musical-grid mode.</summary>
    public double? FixedBpm { get; set; }

    /// <summary>Explicit time-signature override; implies musical-grid mode.</summary>
    public Meter? Meter { get; set; }

    /// <summary>Signed source-sample phase override; implies musical-grid mode.</summary>
    public long? BeatOffsetSamples { get; set; }

    /// <summary>Fail when the requested musical timing cannot be resolved.</summary>
    public bool StrictTiming { get; set; }

    /// <summary>Optional raw source-time fidelity report.</summary>
    public string TimingReport { get; set; }

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
                    case "--output":
                    case "-o": result.Output = reader.RequireValue(name); break;
                    case "--ppq": result.Ppq = reader.ReadInt(name); break;
                    case "--musical-grid" when value == null: result.MusicalGrid = true; break;
                    case "--bpm":
                        result.FixedBpm = reader.ReadDouble(name);
                        result.MusicalGrid = true;
                        break;
                    case "--meter":
                        result.Meter = ParseMeter(reader.RequireValue(name), name);
                        result.MusicalGrid = true;
                        break;
                    case "--beat-offset":
                    case "--beat-offset-samples":
                        result.BeatOffsetSamples = reader.ReadLong(name);
                        result.MusicalGrid = true;
                        break;
                    case "--strict-timing" when value == null:
                        result.StrictTiming = true;
                        result.MusicalGrid = true;
                        break;
                    case "--timing-report": result.TimingReport = reader.RequireValue(name); break;
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

        if (result.Ppq <= 0)
            throw new ArgumentException("--ppq must be positive");
        if (result.Ppq > MidiOptionsLimits.MaxPpq)
            throw new ArgumentException($"--ppq must not exceed {MidiOptionsLimits.MaxPpq} (MIDI-valid division)");
        if (result.FixedBpm is <= 0)
            throw new ArgumentException("--bpm must be positive");
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new ArgumentException("--output (or -o) is required");
        if (string.IsNullOrWhiteSpace(result.Timeline) && string.IsNullOrWhiteSpace(result.Input))
            throw new ArgumentException("specify an input track or --timeline PATH");
        return result;
    }

    private static Meter ParseMeter(string value, string option)
    {
        Meter? meter = Meter.TryParse(value);
        if (meter is null || (meter.Denominator & (meter.Denominator - 1)) != 0)
            throw new ArgumentException($"invalid meter for {option}: '{value}'");
        return meter;
    }
}
