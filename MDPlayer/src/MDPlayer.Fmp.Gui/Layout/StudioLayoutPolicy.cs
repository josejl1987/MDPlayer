namespace Fmp.Gui.Layout;

internal enum StudioLayoutMode
{
    Wide,
    Compact,
}

internal sealed record StudioLayoutDecision(
    StudioLayoutMode Mode,
    double SettingsWidth,
    double DiagnosticsWidth,
    bool DiagnosticsVisible);

/// <summary>
/// Window-width layout policy for the studio: a wide mode with a fixed 300px
/// diagnostics rail, and a compact mode that hides the rail (kept reachable via
/// the preview-header toggle). The rail is never squeezed below 300px; below
/// <see cref="WideMinimumWidth"/> the whole rail is hidden instead.
/// </summary>
internal static class StudioLayoutPolicy
{
    public const double SettingsWidth = 320;
    public const double DiagnosticsWidth = 300;
    public const double SplitterWidth = 6;
    public const double HorizontalMargin = 24;
    public const double MinimumPreviewWidth = 640;

    public static double WideMinimumWidth =>
        SettingsWidth
        + DiagnosticsWidth
        + (SplitterWidth * 2)
        + HorizontalMargin
        + MinimumPreviewWidth;

    public static StudioLayoutDecision Resolve(double clientWidth)
    {
        if (clientWidth >= WideMinimumWidth)
        {
            return new StudioLayoutDecision(
                StudioLayoutMode.Wide,
                SettingsWidth,
                DiagnosticsWidth,
                DiagnosticsVisible: true);
        }

        return new StudioLayoutDecision(
            StudioLayoutMode.Compact,
            SettingsWidth,
            DiagnosticsWidth: 0,
            DiagnosticsVisible: false);
    }
}
