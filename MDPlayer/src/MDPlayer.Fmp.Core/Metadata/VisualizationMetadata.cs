namespace Fmp.Core.Metadata;

/// <summary>
/// Decoded visualization metadata for display in the top/center overlay bands.
/// All fields are Unicode strings after encoding resolution.
/// </summary>
internal sealed class VisualizationMetadata
{
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Composer { get; init; } = "";
    public string Arranger { get; init; } = "";
    public string SourceFileName { get; init; } = "";
    public bool HadDecodeErrors { get; init; }

    public static VisualizationMetadata Empty { get; } = new();
}
