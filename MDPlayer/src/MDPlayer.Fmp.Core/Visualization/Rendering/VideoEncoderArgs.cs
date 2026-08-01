namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Video encoder selection for the final encode (§19.4). The CLI accepts
/// libx264 or nvenc. Auto selects NVENC when the selected FFmpeg exposes it
/// and otherwise falls back to libx264.
/// </summary>
internal enum VideoEncoder
{
    /// <summary>Choose NVENC when available, otherwise libx264.</summary>
    Auto = 0,
    /// <summary>Software x264 (libx264).</summary>
    LibX264 = 1,
    /// <summary>NVIDIA NVENC H.264 (h264_nvenc).</summary>
    Nvenc = 2,
}

/// <summary>
/// Builds the encoder-specific FFmpeg argument suffix shared by all composers.
/// </summary>
internal static class VideoEncoderArgs
{
    /// <summary>
    /// Appends the video encoder arguments for <paramref name="encoder"/>.
    /// <paramref name="preset"/> is a libx264 preset name and
    /// <paramref name="crf"/> a CRF value; both are translated to the
    /// encoder's own vocabulary (e.g. "ultrafast"/"20" → nvenc p1 / cq 20).
    /// </summary>
    public static void Append(List<string> args, VideoEncoder encoder, string preset, string crf)
    {
        args.Add("-c:v");
        if (encoder == VideoEncoder.Nvenc)
        {
            args.Add("h264_nvenc");
            args.Add("-preset");
            args.Add(NvencPreset(preset));
            // Constant-quality VBR: -cq maps to libx264's -crf semantics.
            args.Add("-rc");
            args.Add("vbr");
            args.Add("-cq");
            args.Add(crf);
            args.Add("-b:v");
            args.Add("0");
        }
        else
        {
            args.Add("libx264");
            args.Add("-preset");
            args.Add(preset);
            args.Add("-crf");
            args.Add(crf);
        }
    }

    private static string NvencPreset(string libx264Preset) => libx264Preset switch
    {
        "ultrafast" => "p1",
        "superfast" => "p2",
        "veryfast" => "p4",
        "faster" => "p3",
        "fast" => "p4",
        "medium" => "p5",
        "slow" => "p6",
        "slower" => "p7",
        _ => "p5",
    };
}
