namespace Fmp.Cli;

/// <summary>
/// Render options shared by the render, batch and visualize commands.
/// Command-specific option classes derive from this.
/// </summary>
internal class RenderSettings
{
    public string FmpCom { get; set; }
    public string AssetsDir { get; set; }
    public List<string> SearchPaths { get; } = [];
    public int SampleRate { get; set; } = 44_100;
    public int Loops { get; set; } = 2;
    public double Fade { get; set; } = 5.0;
    public double Tail { get; set; } = 0.5;
    public double MaxDuration { get; set; } = 300.0;

    /// <summary>
    /// Audio-mix gain in decibels applied to the YM2608 SSG/PSG group. Affects
    /// the synthesized master WAV, final video audio, and every generated stem;
    /// does not alter Corrscope waveform height. 0 dB = unchanged. Valid range
    /// [-60, +12]. See <see cref="Fmp.Core.Audio.Mdsound.MdsoundFmpChipSink.ToMdsoundVolume"/>.
    /// </summary>
    public double SsgGainDb { get; set; }

    /// <summary>Optional explicit duration override (render command only).</summary>
    public double? Duration { get; set; }

    /// <summary>Optional wall-clock render timeout in seconds (render command only).</summary>
    public double? Timeout { get; set; }

    /// <summary>Optional register-trace output path (render command only).</summary>
    public string TracePath { get; set; }
}

/// <summary>
/// Parses the options shared by the render, batch and visualize commands.
/// Returns true when <paramref name="name"/> was recognized and applied to
/// <paramref name="settings"/>; false when it is a command-specific option.
/// Malformed values throw ArgumentException via <see cref="ArgumentReader"/>.
/// </summary>
internal static class RenderOptionsParser
{
    public static bool TryParse(ref ArgumentReader reader, string name, RenderSettings settings)
    {
        switch (name)
        {
            case "--fmp-com": settings.FmpCom = reader.RequireValue(name); return true;
            case "--assets-dir": settings.AssetsDir = reader.RequireValue(name); return true;
            case "-I":
            case "--search-path": settings.SearchPaths.Add(reader.RequireValue(name)); return true;
            case "--sample-rate": settings.SampleRate = reader.ReadInt(name); return true;
            case "--loops": settings.Loops = reader.ReadInt(name); return true;
            case "--fade": settings.Fade = reader.ReadDouble(name); return true;
            case "--tail": settings.Tail = reader.ReadDouble(name); return true;
            case "--max-duration": settings.MaxDuration = reader.ReadDouble(name); return true;
            case "--ssg-gain-db":
                settings.SsgGainDb = reader.ReadDouble(name);
                if (settings.SsgGainDb is < -60 or > 12)
                    throw new ArgumentException("--ssg-gain-db must be between -60 and +12");
                return true;
            default: return false;
        }
    }
}
