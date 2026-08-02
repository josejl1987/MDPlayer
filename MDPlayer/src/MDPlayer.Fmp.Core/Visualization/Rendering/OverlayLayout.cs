namespace Fmp.Core.Visualization.Rendering;

internal readonly record struct OverlayRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// Adaptive panel geometry shared by the musical overlay and the Corrscope
/// compositor. The current single composition is the diagnostic full channel
/// grid; the constructor keeps the layout-mode switch so future layouts can
/// add their own geometry without changing the dispatch surface.
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

    public static readonly string[] PanelIds = VisualizationTopologyCompatibility.LegacyPanelIds;

    public static readonly string[] PanelLabels = VisualizationTopologyCompatibility.LegacyPanelLabels;

    public OverlayLayout(int width, int height, double pastSeconds, double futureSeconds)
        : this(width, height, pastSeconds, futureSeconds, panelCount: 12)
    {
    }

    public OverlayLayout(int width, int height, double pastSeconds, double futureSeconds, int panelCount)
        : this(
            width,
            height,
            pastSeconds,
            futureSeconds,
            panelCount,
            VisualizationLayoutMode.Diagnostic,
            scopeHeightOverride: null,
            timelineHeightOverride: null,
            rollZoom: 1.0)
    {
    }

    public OverlayLayout(
        int width,
        int height,
        double pastSeconds,
        double futureSeconds,
        int panelCount,
        VisualizationLayoutMode mode,
        int? scopeHeightOverride = null,
        int? timelineHeightOverride = null,
        double rollZoom = 1.0,
        double? scopeRatioOverride = null,
        VisualizationScopePosition scopePosition = VisualizationScopePosition.Top)
    {
        if (panelCount is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(panelCount), "Panel count must be between 1 and 64.");
        (int columns, int rows) = GridForPanelCount(panelCount);
        if (width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Overlay width must be positive.");
        if (!double.IsFinite(pastSeconds) || pastSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(pastSeconds));
        if (!double.IsFinite(futureSeconds) || futureSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(futureSeconds));
        if (!double.IsFinite(rollZoom) || rollZoom <= 0)
            throw new ArgumentOutOfRangeException(nameof(rollZoom));
        if (scopeHeightOverride is < 0 || timelineHeightOverride is < 0)
            throw new ArgumentOutOfRangeException(nameof(scopeHeightOverride));
        if (scopeRatioOverride is < 0 or > 0.8)
            throw new ArgumentOutOfRangeException(nameof(scopeRatioOverride));

        Width = width;
        Height = height;
        PanelCount = panelCount;
        ColumnCount = columns;
        RowCount = rows;
        PastSeconds = pastSeconds;
        FutureSeconds = futureSeconds;
        Mode = mode;
        RollZoom = rollZoom;
        ScopePosition = scopePosition;
        HasScopes = true;
        HasRoll = true;

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

        SafeHorizontalMargin = Math.Clamp((int)Math.Round(width * (32.0 / 1920.0)), 16, 32);
        SafeVerticalMargin = Math.Clamp((int)Math.Round(height * (24.0 / 1080.0)), 12, 24);

        // Panel sub-regions scale with the panel height, anchored to the
        // reference 1080p proportions (header 20, scope 84, divider 2,
        // timeline 138 out of 244).
        PanelHeaderHeight = Math.Clamp(
            (int)Math.Round(PanelHeight * (DefaultPanelHeaderHeight / (double)DefaultPanelHeight)),
            height >= 720 ? 20 : 12,
            32);

        int availableContentHeight = PanelHeight - PanelHeaderHeight;
        int defaultScopeHeight = (int)Math.Round(
            PanelHeight * (DefaultScopeHeight / (double)DefaultPanelHeight));
        if (scopeHeightOverride is null && scopeRatioOverride is not null)
            defaultScopeHeight = Math.Max(16, (int)Math.Round(availableContentHeight * scopeRatioOverride.Value));

        ScopeHeight = scopeHeightOverride
            ?? Math.Clamp(defaultScopeHeight, 16, Math.Max(16, availableContentHeight / 2));
        DividerHeight = Math.Max(1,
            (int)Math.Round(PanelHeight * (DefaultDividerHeight / (double)DefaultPanelHeight)));
        int derivedTimeline = availableContentHeight - ScopeHeight - DividerHeight;
        TimelineHeight = timelineHeightOverride ?? derivedTimeline;

        if (ScopeHeight < 0 || TimelineHeight < 0
            || PanelHeaderHeight + ScopeHeight + DividerHeight + TimelineHeight > PanelHeight)
            throw new ArgumentOutOfRangeException(nameof(height), "Panel regions exceed the available panel height.");
        if (HasRoll && TimelineHeight < 16)
            throw new ArgumentOutOfRangeException(nameof(height), "Canvas is too small for the panel timeline area.");
        if (HasScopes && ScopeHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(height), "Canvas is too small for the scope area.");
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
    public int SafeHorizontalMargin { get; }
    public int SafeVerticalMargin { get; }
    public int ScopeHeight { get; }
    public int DividerHeight { get; }
    public double WindowSeconds => PastSeconds + FutureSeconds;
    public double PlayheadFraction { get; }
    public int CorrscopeGridHeight => ScopeHeight * RowCount;
    public int CorrscopeGridWidth => Width;
    public double PastSeconds { get; }
    public double FutureSeconds { get; }
    public int PitchLabelWidth { get; }
    public int TimelineHeight { get; }
    public VisualizationLayoutMode Mode { get; }
    public double RollZoom { get; }
    public VisualizationScopePosition ScopePosition { get; }
    public bool HasScopes { get; }
    public bool HasRoll { get; }

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
        int x = OuterMargin + (int)((long)Width * column / ColumnCount);
        int nextX = OuterMargin + (int)((long)Width * (column + 1) / ColumnCount);
        int y = GridY + OuterMargin + row * (PanelHeight + RowGap);
        return new OverlayRect(x, y, nextX - x, PanelHeight);
    }

    public OverlayRect GetHeaderRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        int height = Math.Min(PanelHeaderHeight, panel.Height);
        return new OverlayRect(panel.X, panel.Y, panel.Width, height);
    }

    public OverlayRect GetScopeRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        int y = ScopePosition == VisualizationScopePosition.Bottom
            ? panel.Bottom - ScopeHeight
            : panel.Y + PanelHeaderHeight;
        return new OverlayRect(panel.X, y, panel.Width, ScopeHeight);
    }

    public OverlayRect GetTimelineRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        int y = ScopePosition == VisualizationScopePosition.Bottom
            ? panel.Y + PanelHeaderHeight + DividerHeight
            : panel.Y + PanelHeaderHeight + ScopeHeight + DividerHeight;
        return new OverlayRect(panel.X, y, panel.Width, TimelineHeight);
    }

    public OverlayRect GetPitchedLaneRect(int panelIndex, bool reserveFm3OperatorRibbons)
    {
        OverlayRect timeline = GetTimelineRect(panelIndex);
        int ribbonHeight = reserveFm3OperatorRibbons ? Math.Max(16, timeline.Height / 4) : 0;
        int x = timeline.X + PitchLabelWidth;
        return new OverlayRect(x, timeline.Y, Math.Max(0, timeline.Width - PitchLabelWidth), Math.Max(0, timeline.Height - ribbonHeight));
    }

    public OverlayRect GetFm3OperatorRect(int panelIndex)
    {
        OverlayRect timeline = GetTimelineRect(panelIndex);
        int ribbonHeight = Math.Max(16, timeline.Height / 4);
        return new OverlayRect(
            timeline.X + PitchLabelWidth,
            timeline.Bottom - ribbonHeight,
            Math.Max(0, timeline.Width - PitchLabelWidth),
            Math.Min(ribbonHeight, timeline.Height));
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
        8 => (4, 2),
        <= 9 => (3, 3),
        <= 12 => (3, 4),
        <= 16 => (4, 4),
        <= 20 => (4, 5),
        <= 25 => (5, 5),
        <= 36 => (6, 6),
        <= 49 => (7, 7),
        <= 64 => (8, 8),
        _ => throw new ArgumentOutOfRangeException(nameof(panelCount)),
    };
}
