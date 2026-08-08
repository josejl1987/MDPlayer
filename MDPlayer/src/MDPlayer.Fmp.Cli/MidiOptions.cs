using System.Globalization;
using Fmp.Core.Timing;

namespace Fmp.Cli;

/// <summary>The maximum MIDI-valid division (ticks per quarter note, §48).</summary>
internal static class MidiOptionsLimits
{
    public const int MaxPpq = 32767;
}

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

    /// <summary>Finite positive tempo override (BPM). Required for --tempo-source fixed.</summary>
    public double? Bpm { get; set; }

    /// <summary>
    /// Explicit beat phase for fixed/manual timing. SIGN CONVENTION (used
    /// consistently everywhere - CLI, Application, and core
    /// MusicalTimeMapOptions.BeatOffsetSamples): the offset is ADDED to each
    /// sample position before converting to quarter notes, so the map maps sample
    /// s - quarter (s + BeatOffsetSamples)/samplesPerQuarter and quarter
    /// position zero is reached at sample -BeatOffsetSamples. 0/unset means
    /// "no explicit phase" and the grid is not claimed aligned. This is the single
    /// phase-override option - no synonymous phase options.
    /// </summary>
    public long? BeatOffsetSamples { get; set; }

    /// <summary>Time signature, e.g. 4/4. Optional; never defaults to 4/4 (section 48).</summary>
    public Meter Meter { get; set; }

    /// <summary>Sample of the first bar start (downbeat); requires a compatible meter.</summary>
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
                    case "--meter":
                        {
                            string raw = reader.RequireValue(name);
                            Meter? m = Meter.TryParse(raw);
                            if (m is null)
                                throw new ArgumentException($"--meter must be 'numerator/denominator' (e.g. 4/4), got '{raw}'");
                            result.Meter = m;
                            break;
                        }
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

        // T036: option semantics (section 48).
        if (result.Ppq <= 0)
            throw new ArgumentException("--ppq must be positive");
        if (result.Ppq > MidiOptionsLimits.MaxPpq)
            throw new ArgumentException($"--ppq must not exceed {MidiOptionsLimits.MaxPpq} (MIDI-valid division)");
        if (result.Bpm is double bpm)
        {
            if (!double.IsFinite(bpm))
                throw new ArgumentException("--bpm must be a finite number");
            if (bpm <= 0)
                throw new ArgumentException("--bpm must be positive");
        }
        if (result.TempoSource == MidiTempoSource.Fixed && result.Bpm is null)
            throw new ArgumentException("--tempo-source fixed requires --bpm");
        if (result.FirstDownbeatSample is not null && result.Meter is null)
            throw new ArgumentException("--first-downbeat-sample requires --meter (e.g. --meter 4/4)");
        if (string.IsNullOrWhiteSpace(result.Output))
            throw new ArgumentException("--output (or -o) is required");
        if (string.IsNullOrWhiteSpace(result.Timeline) && string.IsNullOrWhiteSpace(result.Input))
            throw new ArgumentException("specify an input track or --timeline PATH");
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
