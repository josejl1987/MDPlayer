using System.Text;
using Fmp.Core.Audio;
using Fmp.Core.Rendering;

namespace Fmp.Core.Rendering.Corrscope;

/// <summary>
/// Generates a Corrscope-compatible YAML project file from a ScopeRenderer result.
/// The produced YAML can be consumed directly by `corr <project.yaml> -r video.mp4`.
///
/// Defaults are based on Corrscope author's 2025 published OPNA preset
/// (angels.yaml) and the official Yamaha-FM guidance:
///   - 30 ms trigger / 16 ms render windows (smaller render window preserves waveform detail)
///   - Correlation trigger tuned for evolving FM waveforms (edge_strength=1.2,
///     responsiveness=0.8, reset_below=0.6)
///   - Pitch tracking disabled (can mistrack FM note transitions)
///   - Dark background with subtle grid, no vertical midline
///   - Transparent background and grid (8-digit RGBA hex, alpha 00): the
///     composite blends only the waveform signal over the panel body
///     (PanelOverlayRenderer blends the scope alpha mask at ScopeOpacity), so
///     Corrscope's background/grid never contribute an opaque layer. Any hex
///     string override still wins, including a low-alpha grid like
///     "#10141c22".
///   - Per-channel colors and amplification
///   - Rhythm gets a transient-oriented trigger (no waveform memory)
///
/// IMPORTANT: trigger_width and render_width are STRIDE MULTIPLIERS, not time
/// windows. The time window is controlled by trigger_ms/render_ms. Default
/// width is 1 (no stride multiplier) — do not set it to ms-derived values.
///
/// Schema confirmed from upstream corrscope/corrscope.py, triggers.py,
/// channel.py, and renderer.py.
/// </summary>
internal class CorrscopeConfigWriter
{
    /// <summary>
    /// Default trigger settings based on Corrscope author's 2025 OPNA preset
    /// and official Yamaha-FM guidance.
    /// </summary>
    public static class Defaults
    {
        public const double Fps = 60;
        public const double Amplification = 1.0;
        public const int TriggerMs = 30;
        public const int RenderMs = 16;
        public const double MeanResponsiveness = 0.0;
        public const double EdgeStrength = 1.2;
        public const double BufferStrength = 1.0;
        public const double Responsiveness = 0.8;
        public const double ResetBelow = 0.6;
        public const double BufferFalloff = 0.5;
        public const int RenderWidth = 1920;
        public const int RenderHeight = 1080;
        public const double ResDivisor = 1;
        public const string BgColor = "#080a0f00";
        public const string GridColor = "#10141c00";
        public const string MidlineColor = "#10141c00";
        public const double LineWidth = 2.2;
        public const double LineOutlineWidth = 0.6;
        public const string GlobalLineOutlineColor = "#000000";
        public const string LabelColorOverride = "#d8dee9";
        public const int LabelFontSize = 20;
        public const double GridLineWidth = 0.5;
    }

    /// <summary>
    /// Write a Corrscope YAML project file.
    /// </summary>
    /// <param name="yamlPath">Output path for the YAML file.</param>
    /// <param name="outputDir">Scope output directory (for relative path resolution).</param>
    /// <param name="result">Scope render result (stems list).</param>
    /// <param name="audioDir">Audio directory relative to outputDir, e.g. "audio".</param>
    /// <param name="overrides">Optional overrides for default corrscope settings.</param>
    public static void Write(string yamlPath, string outputDir, ScopeRenderer.ScopeResult result,
        string audioDir = "audio", CorrscopeOverrides overrides = null)
    {
        overrides ??= new CorrscopeOverrides();

        var sb = new StringBuilder();

        // YAML header
        sb.AppendLine("!Config");
        sb.AppendLine();

        // Master audio (relative path from YAML dir)
        string masterRel = GetRelativeWavPath(outputDir, result, audioDir, "master");
        sb.AppendLine($"master_audio: {EscapeYamlValue(masterRel)}");
        sb.AppendLine($"fps: {FormatFps(overrides.Fps ?? Defaults.Fps)}");
        sb.AppendLine($"amplification: {overrides.Amplification ?? Defaults.Amplification}");
        sb.AppendLine($"trigger_ms: {overrides.TriggerMs ?? Defaults.TriggerMs}");
        sb.AppendLine($"render_ms: {overrides.RenderMs ?? Defaults.RenderMs}");
        sb.AppendLine();

        // Trigger config (Yamaha FM tuned, based on Corrscope author's 2025 preset)
        sb.AppendLine("trigger:");
        sb.AppendLine("  !CorrelationTriggerConfig");
        sb.AppendLine($"  mean_responsiveness: {overrides.MeanResponsiveness ?? Defaults.MeanResponsiveness}");
        sb.AppendLine($"  edge_strength: {overrides.EdgeStrength ?? Defaults.EdgeStrength}");
        sb.AppendLine($"  buffer_strength: {overrides.BufferStrength ?? Defaults.BufferStrength}");
        sb.AppendLine($"  responsiveness: {overrides.Responsiveness ?? Defaults.Responsiveness}");
        sb.AppendLine($"  buffer_falloff: {overrides.BufferFalloff ?? Defaults.BufferFalloff}");
        sb.AppendLine($"  reset_below: {overrides.ResetBelow ?? Defaults.ResetBelow}");
        // Pitch tracking disabled by default — can mistrack FM note transitions
        sb.AppendLine("  pitch_tracking:");
        sb.AppendLine();

        // Channel list — the caller supplies stems in their desired grid order
        // (layout projection orders them by panel index). Preserve that order:
        // re-sorting here would undo the topology-based arrangement and can
        // misalign grid cells against the overlay panels.
        // Silently skip stems that have no audible content (below -50 dB threshold)
        var orderedChannels = result.Stems
            .Where(s => (overrides?.IncludeMasterAsChannel == true || s.Name != "master") && s.Success)
            .ToList();

        var audibleChannels = new List<ScopeRenderer.StemResult>();
        var silentChannels = new List<ScopeRenderer.StemResult>();
        string resolvedAudioDir = Path.Combine(outputDir, audioDir);

        if (overrides?.IncludeSilentChannels == true)
        {
            // Fixed-grid consumers need channel positions to remain stable even
            // when a track does not use a sample or one of the melodic voices.
            audibleChannels.AddRange(orderedChannels);
        }
        else
        {
            foreach (var stem in orderedChannels)
            {
                string wavFullPath = Path.Combine(resolvedAudioDir, stem.Name + ".wav");
                if (File.Exists(wavFullPath) && IsSilentWav(wavFullPath, silenceThresholdDb: -50))
                    silentChannels.Add(stem);
                else
                    audibleChannels.Add(stem);
            }

            if (silentChannels.Count > 0)
            {
                sb.AppendLine($"# Active channels: {audibleChannels.Count}/{orderedChannels.Count} (silent: {string.Join(", ", silentChannels.Select(s => s.Name))})");
            }

            // Corrscope treats an empty `channels:` value as null and crashes
            // with "TypeError: object of type 'NoneType' has no len()" before
            // rendering, and a video with no scope content is useless anyway.
            // So when every captured stem is below the silence threshold (short
            // captures, very quiet tracks), force at least the first ordered
            // channel in so the config remains renderable and the scope stays
            // visible instead of silently vanishing or aborting the render.
            if (audibleChannels.Count == 0
                && orderedChannels.Count > 0)
            {
                audibleChannels.Add(orderedChannels[0]);
            }
        }

        sb.AppendLine("channels:");
        foreach (var stem in audibleChannels)
        {
            string wavRel = GetRelativeWavPath(outputDir, result, audioDir, stem.Name);
            sb.AppendLine("- !ChannelConfig");
            sb.AppendLine($"  wav_path: {EscapeYamlValue(wavRel)}");
            string channelLabel = overrides?.HideLabels == true ? "" : GetChannelLabel(stem);
            sb.AppendLine($"  label: {EscapeYamlValue(channelLabel)}");
            string wavFullPath = Path.Combine(resolvedAudioDir, stem.Name + ".wav");

            // Per-channel amplification: derive a stable gain from the WAV peak,
            // falling back to the legacy family default when the file is unavailable.
            double channelAmp;
            if (overrides.PerChannelAmplification != null)
            {
                channelAmp = overrides.PerChannelAmplification(stem.Name)
                    ?? (stem.DefaultAmplification > 0 ? stem.DefaultAmplification : 1.0);
            }
            else
            {
                try
                {
                    channelAmp = ComputeChannelGain(wavFullPath);
                }
                catch
                {
                    channelAmp = stem.DefaultAmplification > 0 ? stem.DefaultAmplification : 1.0;
                }
            }
            sb.AppendLine($"  amplification: {channelAmp}");

            int channelWidth = Math.Max(1, stem.WindowWidth);
            if (channelWidth > 1)
            {
                sb.AppendLine($"  render_width: {channelWidth}");
                sb.AppendLine($"  trigger_width: {channelWidth}");
            }

            // Per-channel color
            string color = overrides.PerChannelColor?.Invoke(stem.Name) ?? stem.DefaultColor;
            if (color != null)
                sb.AppendLine($"  line_color: {EscapeYamlValue(color)}");

            // Percussive waveforms get a transient-oriented trigger override;
            // percussion doesn't benefit from waveform memory (buffer_strength=0,
            // pure edge detection). The semantic class is supplied by the decoder
            // or presentation adapter, so stem naming is not part of this decision.
            if (stem.SemanticClass == ScopeSemanticClass.Percussive)
            {
                sb.AppendLine("  trigger:");
                sb.AppendLine("    edge_strength: 2.5");
                sb.AppendLine("    buffer_strength: 0");
                sb.AppendLine("    responsiveness: 1");
                sb.AppendLine("    reset_below: 0");
            }
            else if (stem.SemanticClass == ScopeSemanticClass.PulseStable)
            {
                sb.AppendLine("  trigger:");
                sb.AppendLine("    edge_strength: 1.5");
                sb.AppendLine("    buffer_strength: 0.5");
                sb.AppendLine("    responsiveness: 1.0");
                sb.AppendLine("    reset_below: 0.5");
            }
        }
        sb.AppendLine();

        // Default label
        sb.AppendLine("default_label: !FileName");
        sb.AppendLine();

        // Layout — 2 columns for 6+ channels so each waveform is wider
        // and shows more detail; fewer columns makes waveforms too narrow.
        int ncols = overrides.LayoutNCols ?? (audibleChannels.Count >= 6 ? 2 : 0);
        int renderHeight = overrides.RenderHeight ?? Defaults.RenderHeight;
        sb.AppendLine("layout:");
        sb.AppendLine("  !LayoutConfig");
        sb.AppendLine($"  orientation: {overrides.LayoutOrientation ?? "h"}");
        sb.AppendLine($"  stereo_orientation: {overrides.LayoutStereoOrientation ?? "overlay"}");
        sb.AppendLine($"  ncols: {ncols}");
        sb.AppendLine();

        // Renderer config — transparent background/grid (mask-only frames),
        // no bright colors; the composite blends only the waveform signal.
        sb.AppendLine("render:");
        sb.AppendLine("  !RendererConfig");
        sb.AppendLine($"  width: {overrides.RenderWidth ?? Defaults.RenderWidth}");
        sb.AppendLine($"  height: {renderHeight}");
        sb.AppendLine($"  line_width: {overrides.LineWidth ?? Defaults.LineWidth}");
        sb.AppendLine($"  line_outline_width: {overrides.LineOutlineWidth ?? Defaults.LineOutlineWidth}");
        sb.AppendLine($"  grid_line_width: {overrides.GridLineWidth ?? Defaults.GridLineWidth}");
        sb.AppendLine($"  global_line_outline_color: {EscapeYamlValue(overrides.GlobalLineOutlineColor ?? Defaults.GlobalLineOutlineColor)}");
        sb.AppendLine($"  bg_color: {EscapeYamlValue(overrides.BgColor ?? Defaults.BgColor)}");
        sb.AppendLine($"  grid_color: {EscapeYamlValue(overrides.GridColor ?? Defaults.GridColor)}");
        sb.AppendLine($"  midline_color: {EscapeYamlValue(overrides.MidlineColor ?? Defaults.MidlineColor)}");
        sb.AppendLine($"  v_midline: {BoolStr(overrides.VMidline ?? false)}");
        sb.AppendLine($"  h_midline: {BoolStr(overrides.HMidline ?? false)}");
        sb.AppendLine($"  antialiasing: {BoolStr(overrides.Antialiasing ?? true)}");
        sb.AppendLine($"  res_divisor: {overrides.ResDivisor ?? Defaults.ResDivisor}");
        // Label styling
        sb.AppendLine("  label_font: !Font");
        sb.AppendLine($"    size: {overrides.LabelFontSize ?? Defaults.LabelFontSize}");
        sb.AppendLine("    bold: true");
        sb.AppendLine("  label_position: !LabelPosition LeftTop");
        sb.AppendLine($"  label_padding_ratio: {overrides.LabelPaddingRatio ?? 0.5}");
        sb.AppendLine($"  label_color_override: {EscapeYamlValue(overrides.LabelColorOverride ?? Defaults.LabelColorOverride)}");
        sb.AppendLine();

        // FFmpeg output config (template; path is set by CLI --render)
        // Omit ffmpeg_cli section entirely when using defaults, since
        // corrscope's CLI sets the path and uses sensible defaults.
        bool useFfmpegDefaults = string.IsNullOrEmpty(overrides?.FfmpegArgs)
            && overrides?.FfmpegVideoTemplate == null
            && overrides?.FfmpegAudioTemplate == null;
        if (!useFfmpegDefaults)
        {
            sb.AppendLine("ffmpeg_cli:");
            sb.AppendLine("  !FFmpegOutputConfig");
            sb.AppendLine("  path: null");
            if (!string.IsNullOrEmpty(overrides?.FfmpegArgs))
                sb.AppendLine($"  args: {EscapeYamlValue(overrides.FfmpegArgs)}");
            if (overrides?.FfmpegVideoTemplate != null)
                sb.AppendLine($"  video_template: {EscapeYamlValue(overrides.FfmpegVideoTemplate)}");
            if (overrides?.FfmpegAudioTemplate != null)
                sb.AppendLine($"  audio_template: {EscapeYamlValue(overrides.FfmpegAudioTemplate)}");
            sb.AppendLine();
        }

        // Write with atomic rename
        string partialPath = yamlPath + ".partial";
        File.WriteAllText(partialPath, sb.ToString(), new UTF8Encoding(false));
        if (File.Exists(yamlPath))
            File.Delete(yamlPath);
        File.Move(partialPath, yamlPath);
    }

    /// <summary>
    /// Get the adapter-provided stable display label for a stem.
    /// </summary>
    private static string GetChannelLabel(ScopeRenderer.StemResult stem)
    {
        return stem.Label;
    }

    /// <summary>
    /// Get the WAV path relative to the YAML directory.
    /// </summary>
    private static string GetRelativeWavPath(string outputDir, ScopeRenderer.ScopeResult result,
        string audioDir, string stemName)
    {
        return $"{audioDir}/{stemName}.wav";
    }

    private static bool TryLocateWavData(Stream stream, out int channels, out int bitsPerSample,
        out long dataStart, out long dataSize)
    {
        channels = 0;
        bitsPerSample = 0;
        dataStart = 0;
        dataSize = 0;

        var header = new byte[44];
        if (stream.Read(header, 0, header.Length) < header.Length)
            return false;

        channels = header[22] | (header[23] << 8);
        bitsPerSample = header[34] | (header[35] << 8);

        if (System.Text.Encoding.ASCII.GetString(header, 36, 4) == "data")
        {
            dataStart = 44;
            dataSize = header[40] | (header[41] << 8) | (header[42] << 16) | (header[43] << 24);
        }
        else
        {
            dataStart = 44;
            dataSize = stream.Length - 44;
            stream.Seek(12, SeekOrigin.Begin);
            long pos = 12;
            long end = stream.Length;
            while (pos + 8 <= end)
            {
                byte[] chunkId = new byte[4];
                byte[] sizeBytes = new byte[4];
                int readHeader = stream.Read(chunkId, 0, 4);
                int readSize = stream.Read(sizeBytes, 0, 4);
                if (readHeader < 4 || readSize < 4)
                    break;

                int chunkSize = sizeBytes[0] | (sizeBytes[1] << 8) |
                    (sizeBytes[2] << 16) | (sizeBytes[3] << 24);
                if (System.Text.Encoding.ASCII.GetString(chunkId) == "data")
                {
                    dataStart = stream.Position;
                    dataSize = chunkSize;
                    break;
                }

                long skip = chunkSize + (chunkSize & 1);
                pos = stream.Position + skip;
                if (pos > end)
                    break;
                stream.Seek(pos, SeekOrigin.Begin);
            }
        }

        return channels > 0;
    }

    /// <summary>
    /// Check if a WAV file is essentially silent (all samples below a threshold).
    /// Reads a sample of the file to determine peak level.
    /// </summary>
    private static bool IsSilentWav(string wavPath, double silenceThresholdDb = -50)
    {
        try
        {
            using var stream = File.OpenRead(wavPath);
            if (!TryLocateWavData(stream, out int channels, out int bitsPerSample,
                    out long dataStart, out long dataSize))
                return true;
            if (bitsPerSample != 16)
                return false;

            // Scan the entire data region with evenly-spaced samples.
            // A middle-50% window would miss channels that enter late,
            // play only briefly, or have sparse rhythm hits.
                        // Scan the ENTIRE data region sequentially.
            // Every single sample is checked (100% accurate). Early-exits on
            // finding any sample above threshold, so channels with audio
            // return quickly after the first non-silent sample.
            long totalBytes = dataSize > 0 ? dataSize : (stream.Length - dataStart);
            if (totalBytes <= 0) return true;

            double threshold = Math.Pow(10.0, silenceThresholdDb / 20.0) * 32768.0;
            int peak = 0;
            int bytesPerSample = channels * 2;
            int bufSize = 65536;
            byte[] buf = new byte[bufSize];

            stream.Seek(dataStart, SeekOrigin.Begin);
            while (peak < threshold)
            {
                int read = stream.Read(buf, 0, bufSize);
                if (read < bytesPerSample) break;
                int samples = read / bytesPerSample;
                int end = samples * bytesPerSample;
                for (int off = 0; off < end && peak < threshold; off += bytesPerSample)
                {
                    for (int c = 0; c < channels; c++)
                    {
                        int o = off + c * 2;
                        short s = (short)(buf[o] | (buf[o + 1] << 8));
                        int abs = s < 0 ? -s : s;
                        if (abs > peak) peak = abs;
                    }
                }
            }

            return peak < threshold;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compute a stable per-channel gain from the 99.5th percentile sample peak.
    /// </summary>
    private static double ComputeChannelGain(string wavPath)
    {
        using var stream = File.OpenRead(wavPath);
        if (!TryLocateWavData(stream, out int channels, out int bitsPerSample,
                out long dataStart, out long dataSize) || bitsPerSample != 16)
            throw new InvalidDataException("Unsupported WAV format.");

        long totalBytes = dataSize > 0 ? Math.Min(dataSize, stream.Length - dataStart) : stream.Length - dataStart;
        if (channels < 1 || totalBytes < channels * 2L)
            throw new InvalidDataException("Expected PCM WAV data.");

        int bytesPerFrame = channels * 2;
        long totalSamples = totalBytes / bytesPerFrame;
        int sampleStride = (int)Math.Max(1, (totalSamples + 199_999) / 200_000);
        var absoluteSamples = new List<int>((int)Math.Min(totalSamples / sampleStride, 200_000));
        byte[] buffer = new byte[64 * 1024];
        long sampleIndex = 0;
        long bytesRemaining = totalSamples * bytesPerFrame;
        stream.Seek(dataStart, SeekOrigin.Begin);

        while (bytesRemaining > 0)
        {
            int requested = (int)Math.Min(buffer.Length, bytesRemaining);
            int read = stream.Read(buffer, 0, requested);
            if (read < bytesPerFrame)
                break;

            int usable = read - (read % bytesPerFrame);
            for (int offset = 0; offset < usable; offset += bytesPerFrame, sampleIndex++)
            {
                if (sampleIndex % sampleStride != 0)
                    continue;

                int framePeak = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = offset + channel * 2;
                    short sample = (short)(buffer[sampleOffset] | (buffer[sampleOffset + 1] << 8));
                    framePeak = Math.Max(framePeak, Math.Abs((int)sample));
                }
                absoluteSamples.Add(framePeak);
            }

            bytesRemaining -= usable;
        }

        if (absoluteSamples.Count == 0)
            return 1.0;

        absoluteSamples.Sort();
        int percentileIndex = Math.Min(
            absoluteSamples.Count - 1,
            (int)(0.995 * absoluteSamples.Count));
        int p995 = absoluteSamples[percentileIndex];
        if (p995 == 0)
            return 1.0;

        double gain = Math.Min(12.0, 0.72 / (p995 / 32768.0));
        return Math.Round(gain, 2);
    }

    private static string EscapeYamlValue(string value)
    {
        if (value == null) return "null";
        if (value.Contains(':') || value.Contains('#') || value.Contains('{') ||
            value.Contains('}') || value.Contains('[') || value.Contains(']') ||
            value.Contains(',') || value.Contains('&') || value.Contains('*') ||
            value.Contains('?') || value.Contains('|') || value.Contains('-') ||
            value.Contains('<') || value.Contains('>') || value.Contains('=') ||
            value.Contains('!') || value.Contains('%') || value.Contains('@') ||
            value.Contains('`') || value.StartsWith(' ') || value.StartsWith('"') ||
            value.Contains('\'') || value == "" || value == "null" || value == "true" ||
            value == "false")
        {
            string escaped = value.Replace("\\", "\\\\").Replace("\"", "\\\"");
            return $"\"{escaped}\"";
        }
        return value;
    }

    private static string BoolStr(bool value) => value ? "true" : "false";

    /// <summary>
    /// Formats the frame rate for the Corrscope YAML. Corrscope accepts a real
    /// number, which matters for NTSC-family sources whose nominal FPS is
    /// fractional (e.g. 60000/1001 ≈ 59.94) rather than an integer 60.
    /// </summary>
    private static string FormatFps(double fps)
        => fps.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Optional overrides for Corrscope YAML generation.
/// Null values fall back to <see cref="CorrscopeConfigWriter.Defaults"/>.
/// </summary>
internal class CorrscopeOverrides
{
    public double? Fps { get; set; }
    public double? Amplification { get; set; }
    public int? TriggerMs { get; set; }
    public int? RenderMs { get; set; }
    public double? MeanResponsiveness { get; set; }
    public double? EdgeStrength { get; set; }
    public double? BufferStrength { get; set; }
    public double? Responsiveness { get; set; }
    public double? ResetBelow { get; set; }
    public double? BufferFalloff { get; set; }

    // Render config
    public int? RenderWidth { get; set; }
    public int? RenderHeight { get; set; }
    public double? ResDivisor { get; set; }
    public double? LineWidth { get; set; }
    public double? LineOutlineWidth { get; set; }
    public double? GridLineWidth { get; set; }
    public string GlobalLineOutlineColor { get; set; }
    public string BgColor { get; set; }
    public string GridColor { get; set; }
    public string MidlineColor { get; set; }
    public bool? VMidline { get; set; }
    public bool? HMidline { get; set; }
    public bool? Antialiasing { get; set; }
    public int? LabelFontSize { get; set; }
    public double? LabelPaddingRatio { get; set; }
    public string LabelColorOverride { get; set; }

    // Layout config
    public string LayoutOrientation { get; set; }
    public string LayoutStereoOrientation { get; set; }
    public int? LayoutNCols { get; set; }
    public bool? IncludeSilentChannels { get; set; }
    public bool? IncludeMasterAsChannel { get; set; }
    public bool? HideLabels { get; set; }

    // Per-channel customization (delegates, called with stem name)
    public Func<string, double?> PerChannelAmplification { get; set; }
    public Func<string, string> PerChannelColor { get; set; }

    // FFmpeg config
    public string FfmpegArgs { get; set; }
    public string FfmpegVideoTemplate { get; set; }
    public string FfmpegAudioTemplate { get; set; }
}
