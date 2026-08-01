namespace Fmp.Core.Visualization.Rendering;

internal readonly record struct OverlayRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// Fixed 3x4 panel geometry shared by the musical overlay and the Corrscope
/// compositor. The canvas is split into three vertical regions: a top
/// metadata bar, the 3x4 panel grid, and a bottom credits/progress bar.
/// Scope rectangles are intentionally transparent in the overlay so the
/// stable Corrscope layer can remain the sole oscilloscope.
/// </summary>
internal sealed class OverlayLayout
{
    /// <summary>Height of the top metadata bar at the reference 1080p geometry.</summary>
    public const int DefaultTopBarHeight = 64;
    /// <summary>Height of the bottom credits/progress bar at the reference 1080p geometry.</summary>
    public const int DefaultBottomBarHeight = 40;
    /// <summary>Reference panel height at 1080p (976 / 4).</summary>
    public const int DefaultPanelHeight = 244;
    /// <summary>Reference scope height at 1080p.</summary>
    public const int DefaultScopeHeight = 84;
    /// <summary>Reference panel header height at 1080p.</summary>
    public const int DefaultPanelHeaderHeight = 20;
    /// <summary>Reference divider height at 1080p.</summary>
    public const int DefaultDividerHeight = 2;
    /// <summary>Reference timeline height at 1080p (244 - 20 - 84 - 2).</summary>
    public const int DefaultTimelineHeight = 138;

    public const int Columns = 3;
    public const int Rows = 4;

    public static readonly string[] PanelIds =
    [
        "ym2608.0.fm.1", "ym2608.0.fm.2", "ym2608.0.fm.3",
        "ym2608.0.fm.4", "ym2608.0.fm.5", "ym2608.0.fm.6",
        "ym2608.0.ssg.1", "ym2608.0.ssg.2", "ym2608.0.ssg.3",
        "ym2608.0.rhythm", "ym2608.0.adpcm-b", "ppz8.0",
    ];

    public static readonly string[] PanelLabels =
    [
        "FM1", "FM2", "FM3",
        "FM4", "FM5", "FM6",
        "SSG1", "SSG2", "SSG3",
        "RHYTHM", "ADPCM-B", "PPZ8",
    ];

    public OverlayLayout(int width, int height, double pastSeconds, double futureSeconds)
        : this(width, height, pastSeconds, futureSeconds, panelCount: 12)
    {
    }

    public OverlayLayout(int width, int height, double pastSeconds, double futureSeconds, int panelCount)
    {
        if (panelCount is < 1 or > 20)
            throw new ArgumentOutOfRangeException(nameof(panelCount), "Panel count must be between 1 and 20.");
        (int columns, int rows) = GridForPanelCount(panelCount);
        if (width % columns != 0)
            throw new ArgumentException($"Width must be divisible by {columns}.", nameof(width));
        if (!double.IsFinite(pastSeconds) || pastSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(pastSeconds));
        if (!double.IsFinite(futureSeconds) || futureSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(futureSeconds));

        Width = width;
        Height = height;
        PanelCount = panelCount;
        ColumnCount = columns;
        RowCount = rows;
        PastSeconds = pastSeconds;
        FutureSeconds = futureSeconds;

        // Reserve the top and bottom metadata bands. The bands scale
        // proportionally with the canvas height, clamped to sensible minimums,
        // and the bottom bar is then nudged so the remaining grid height is an
        // exact multiple of Rows — silent integer truncation is never allowed
        // because Corrscope and the overlay must agree on exact cell sizes.
        int nominalTop = ClampBand((int)Math.Round(height * (DefaultTopBarHeight / 1080.0)), 32, 96);
        int nominalBottom = ClampBand((int)Math.Round(height * (DefaultBottomBarHeight / 1080.0)), 24, 64);

        int gridHeight = height - nominalTop - nominalBottom;
        // Adjust the bottom bar so the grid divides evenly into Rows.
        int remainder = gridHeight % RowCount;
        if (remainder != 0)
        {
            // Push the shortfall into the bottom bar so the top bar stays stable
            // (the top bar carries the title, which should not jump around).
            nominalBottom += remainder;
            gridHeight = height - nominalTop - nominalBottom;
        }

        if (gridHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Canvas is too small for the metadata bands and panel grid.");
        if (gridHeight % RowCount != 0)
            throw new ArgumentException($"Grid height ({gridHeight}) must be divisible by {RowCount}.", nameof(height));
        if (width < 480)
            throw new ArgumentOutOfRangeException(nameof(width), "Overlay width must be at least 480 pixels.");
        if (gridHeight < 240)
            throw new ArgumentOutOfRangeException(nameof(height), "Overlay grid height must be at least 240 pixels.");

        TopBarHeight = nominalTop;
        BottomBarHeight = nominalBottom;
        GridHeight = gridHeight;

        OuterMargin = 0;
        ColumnGap = 0;
        RowGap = 0;

        PanelWidth = width / ColumnCount;
        PanelHeight = GridHeight / RowCount;
        if (PanelWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Canvas is too narrow for the panel layout.");

        // Panel sub-regions scale with the panel height, anchored to the
        // reference 1080p proportions (header 20, scope 84, divider 2,
        // timeline 138 out of 244).
        PanelHeaderHeight = Math.Clamp(
            (int)Math.Round(PanelHeight * (DefaultPanelHeaderHeight / (double)DefaultPanelHeight)),
            height >= 720 ? 20 : 12,
            32);
        ScopeHeight = Math.Clamp(
            (int)Math.Round(PanelHeight * (DefaultScopeHeight / (double)DefaultPanelHeight)),
            24, PanelHeight / 2);
        DividerHeight = Math.Max(1, (int)Math.Round(PanelHeight * (DefaultDividerHeight / (double)DefaultPanelHeight)));
        TimelineHeight = PanelHeight - PanelHeaderHeight - ScopeHeight - DividerHeight;
        if (TimelineHeight < 16)
            throw new ArgumentOutOfRangeException(nameof(height), "Canvas is too small for the panel timeline area.");
        PitchLabelWidth = Math.Max(20, width / 80);
        PlayheadFraction = pastSeconds / (pastSeconds + futureSeconds);
    }

    public int Width { get; }
    public int Height { get; }
    public int PanelCount { get; }
    public int ColumnCount { get; }
    public int RowCount { get; }
    public int TopBarHeight { get; }
    public int BottomBarHeight { get; }
    public int GridHeight { get; }
    public int GridY => TopBarHeight;
    public int OuterMargin { get; }
    public int ColumnGap { get; }
    public int RowGap { get; }
    public int PanelWidth { get; }
    public int PanelHeight { get; }
    public int PanelHeaderHeight { get; }
    public int ScopeHeight { get; }
    public int DividerHeight { get; }
    public double WindowSeconds => PastSeconds + FutureSeconds;
    public double PlayheadFraction { get; }
    public int CorrscopeGridHeight => ScopeHeight * RowCount;
    public double PastSeconds { get; }
    public double FutureSeconds { get; }
    public int PitchLabelWidth { get; }
    public int TimelineHeight { get; }

    public OverlayRect TopBarRect => new(0, 0, Width, TopBarHeight);

    public OverlayRect BottomBarRect => new(0, Height - BottomBarHeight, Width, BottomBarHeight);

    /// <summary>Reserved lower band for the optional analysis harmony strip.</summary>
    public OverlayRect HarmonyStripRect
    {
        get
        {
            int height = Math.Clamp(BottomBarHeight / 2, 10, 20);
            return new OverlayRect(0, BottomBarRect.Bottom - height, Width, height);
        }
    }

    /// <summary>Upper band retained for progress, phrase, and motif markers.</summary>
    public OverlayRect AnalysisMarkerRect
    {
        get
        {
            OverlayRect strip = HarmonyStripRect;
            return new OverlayRect(BottomBarRect.X, BottomBarRect.Y, BottomBarRect.Width, strip.Y - BottomBarRect.Y);
        }
    }

    public int GetScopeRowDestinationY(int row)
    {
        if (row < 0 || row >= RowCount)
            throw new ArgumentOutOfRangeException(nameof(row));
        return GetScopeRect(Math.Min(row * ColumnCount, PanelCount - 1)).Y;
    }

    public OverlayRect GetPanelRect(int panelIndex)
    {
        if (panelIndex < 0 || panelIndex >= PanelCount)
            throw new ArgumentOutOfRangeException(nameof(panelIndex));
        int column = panelIndex % ColumnCount;
        int row = panelIndex / ColumnCount;
        int x = OuterMargin + column * (PanelWidth + ColumnGap);
        int y = GridY + OuterMargin + row * (PanelHeight + RowGap);
        return new OverlayRect(x, y, PanelWidth, PanelHeight);
    }

    public OverlayRect GetHeaderRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        return new OverlayRect(panel.X, panel.Y, panel.Width, PanelHeaderHeight);
    }

    public OverlayRect GetScopeRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        return new OverlayRect(panel.X, panel.Y + PanelHeaderHeight, panel.Width, ScopeHeight);
    }

    public OverlayRect GetTimelineRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        int y = panel.Y + PanelHeaderHeight + ScopeHeight + DividerHeight;
        return new OverlayRect(panel.X, y, panel.Width, TimelineHeight);
    }

    public OverlayRect GetPitchedLaneRect(int panelIndex, bool reserveFm3OperatorRibbons)
    {
        OverlayRect timeline = GetTimelineRect(panelIndex);
        int ribbonHeight = reserveFm3OperatorRibbons ? Math.Max(16, timeline.Height / 4) : 0;
        int x = timeline.X + PitchLabelWidth;
        return new OverlayRect(x, timeline.Y, timeline.Width - PitchLabelWidth, timeline.Height - ribbonHeight);
    }

    public OverlayRect GetFm3OperatorRect(int panelIndex)
    {
        OverlayRect timeline = GetTimelineRect(panelIndex);
        int ribbonHeight = Math.Max(16, timeline.Height / 4);
        return new OverlayRect(
            timeline.X + PitchLabelWidth,
            timeline.Bottom - ribbonHeight,
            timeline.Width - PitchLabelWidth,
            ribbonHeight);
    }

    public int GetPlayheadX(int panelIndex)
    {
        OverlayRect timeline = GetTimelineRect(panelIndex);
        return timeline.X + PitchLabelWidth
            + (int)Math.Round((timeline.Width - PitchLabelWidth) * PlayheadFraction);
    }

    public long WindowStartSample(long currentSample, int sampleRate)
        => currentSample - (long)Math.Round(PastSeconds * sampleRate);

    public long WindowEndSample(long currentSample, int sampleRate)
        => currentSample + (long)Math.Round(FutureSeconds * sampleRate);

    public double SampleToX(long sample, long currentSample, int sampleRate, OverlayRect lane)
    {
        long windowStart = WindowStartSample(currentSample, sampleRate);
        double windowSamples = WindowSeconds * sampleRate;
        return lane.X + (sample - windowStart) * lane.Width / windowSamples;
    }

    public static long FrameToSample(long frameIndex, int sampleRate, int fpsNumerator, int fpsDenominator)
    {
        if (frameIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (sampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (fpsNumerator <= 0 || fpsDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(fpsNumerator));

        decimal sample = (decimal)frameIndex * sampleRate * fpsDenominator / fpsNumerator;
        return (long)decimal.Round(sample, 0, MidpointRounding.AwayFromZero);
    }

    private static int ClampBand(int value, int min, int max)
        => Math.Clamp(value, min, max);

    private static (int Columns, int Rows) GridForPanelCount(int panelCount) => panelCount switch
    {
        1 => (1, 1),
        2 => (2, 1),
        <= 4 => (2, 2),
        <= 6 => (3, 2),
        <= 9 => (3, 3),
        <= 12 => (3, 4),
        <= 16 => (4, 4),
        <= 20 => (4, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(panelCount)),
    };
}
