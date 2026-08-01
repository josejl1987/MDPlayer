namespace Fmp.Core.Visualization.Rendering;

/// <summary>Header text prepared once with the instrument snapshot.</summary>
internal sealed record PreparedInstrumentText(
    string ShortLabel,
    string ChangeLabel,
    string BadgeLabel,
    string[] OperatorLabels)
{
    public static PreparedInstrumentText Empty { get; } = new("", "", "", Array.Empty<string>());
}
