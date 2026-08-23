namespace Fmp.Core.Visualization.Rendering;

internal readonly record struct OverlayRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>
/// A candidate panel grid chosen by the layout resolver. Columns and rows are
/// inputs from resolution, not derived from the panel count on its own.
/// </summary>
internal readonly record struct ResolvedPanelGrid(
    int Columns,
    int Rows);

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
    public const int DefaultPanelHeaderHeight = 28;
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
        int columns = 0,
        int rows = 0,
        VisualizationLayoutVariant variant = VisualizationLayoutVariant.DiagnosticGrid,
        int? scopeHeightOverride = null,
        int? timelineHeightOverride = null,
        double rollZoom = 1.0,
        double? scopeRatioOverride = null,
        VisualizationScopePosition scopePosition = VisualizationScopePosition.Top,
        bool? showScopes = null,
        bool? showRoll = null)
    {
        if (panelCount is < 1 or > 64)
            throw new ArgumentOutOfRangeException(nameof(panelCount), "Panel count must be between 1 and 64.");
        if (columns <= 0 || rows <= 0)
            (columns, rows) = GridForPanelCount(panelCount);
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
        Variant = variant;
        RollZoom = rollZoom;
        ScopePosition = scopePosition;
        // Only the full diagnostic grid renders the piano-roll/timeline semantic
        // lane. The overview and device variants keep the scope region (used as
        // a compact per-channel or aggregate waveform) but drop the pitch lane.
        // An explicit showScopes/showRoll override (from the layout resolver's
        // capability decision) wins over the variant-derived default so a
        // density fallback can hand the space to the remaining content.
        HasScopes = showScopes ?? true;
        HasRoll = showRoll ?? (variant is VisualizationLayoutVariant.DiagnosticGrid
            or VisualizationLayoutVariant.PerformanceLanes);

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
        if (width < 480 && variant is VisualizationLayoutVariant.DiagnosticGrid
            or VisualizationLayoutVariant.PerformanceLanes)
            throw new ArgumentOutOfRangeException(nameof(width), "Overlay width must be at least 480 pixels.");
        if (width < 240)
            throw new ArgumentOutOfRangeException(nameof(width), "Overlay width must be at least 240 pixels.");
        if (gridHeight < 240 && variant == VisualizationLayoutVariant.DiagnosticGrid)
            throw new ArgumentOutOfRangeException(nameof(height), "Overlay grid height must be at least 240 pixels.");
        if (gridHeight < 40)
            throw new ArgumentOutOfRangeException(nameof(height), "Overlay grid height must be at least 40 pixels.");

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

        // DiagnosticGrid uses one aligned body: the waveform is the signal
        // portion of the roll rather than a stacked standalone scope.
        // The 28px reference lands exactly on the default 1080p panel. On tight
        // high-channel-count grids the header yields down to a 12px floor so the
        // scope keeps its validator minimum (56@>=720, scaled below).
        PanelHeaderHeight = (int)Math.Round(PanelHeight * (DefaultPanelHeaderHeight / (double)DefaultPanelHeight));
        int minimumScopeHeight = Height >= 720 ? 56 : Math.Max(24, (int)Math.Round(56 * Height / 720.0));
        // Scope = min(default, availableContent / 2); keep that upper bound at
        // least the scope floor so a tall header can't starve the scope.
        int maxHeaderForScope = PanelHeight - 2 * minimumScopeHeight;
        PanelHeaderHeight = Math.Clamp(PanelHeaderHeight, 12, Math.Max(12, maxHeaderForScope));
        PanelHeaderHeight = Math.Min(PanelHeaderHeight, 40);
        if (Variant == VisualizationLayoutVariant.PerformanceLanes)
        {
            // Lanes have no header chrome: the full band is the roll and the
            // channel label lives in the left pitch gutter instead.
            PanelHeaderHeight = 0;
        }

        int availableContentHeight = PanelHeight - PanelHeaderHeight;
        int defaultScopeHeight = (int)Math.Round(
            PanelHeight * (DefaultScopeHeight / (double)DefaultPanelHeight));
        if (scopeHeightOverride is null && scopeRatioOverride is not null)
            defaultScopeHeight = Math.Max(16, (int)Math.Round(availableContentHeight * scopeRatioOverride.Value));

        if (UsesIntegratedRoll)
        {
            // DiagnosticGrid has one shared body below the header. Any legacy
            // timeline override describes the old stacked geometry and would
            // be double-counted when ScopeHeight mirrors the body height. The
            // scope reservation disappears entirely when scopes are disabled so
            // no invisible gap remains in the roll.
            TimelineHeight = availableContentHeight;
            ScopeHeight = HasScopes ? TimelineHeight : 0;
            DividerHeight = 0;
        }
        else if (HasScopes)
        {
            DividerHeight = Math.Max(1,
                (int)Math.Round(PanelHeight * (DefaultDividerHeight / (double)DefaultPanelHeight)));
            int maxScopeHeight = Math.Max(0, availableContentHeight - DividerHeight);
            ScopeHeight = Math.Clamp(
                scopeHeightOverride
                    ?? Math.Clamp(defaultScopeHeight, 16, Math.Max(16, availableContentHeight / 2)),
                0,
                maxScopeHeight);
            int derivedTimeline = Math.Max(0, availableContentHeight - ScopeHeight - DividerHeight);
            TimelineHeight = Math.Clamp(
                timelineHeightOverride ?? derivedTimeline,
                0,
                derivedTimeline);
        }
        else
        {
            // No scope region: hand the whole content height to the roll so no
            // invisible scope-sized gap remains.
            ScopeHeight = 0;
            DividerHeight = 0;
            TimelineHeight = timelineHeightOverride ?? availableContentHeight;
        }

        // When the roll is disabled, give its space back to the scope/content so
        // the panel never leaves a reserved-but-empty lane.
        if (!HasRoll)
        {
            TimelineHeight = 0;
            if (HasScopes)
            {
                ScopeHeight = Math.Clamp(
                    scopeHeightOverride ?? Math.Max(16, availableContentHeight),
                    0,
                    Math.Max(0, availableContentHeight));
                DividerHeight = 0;
            }
        }

        bool integratedScope = UsesIntegratedRoll;
        int occupiedPanelHeight = PanelHeaderHeight
            + (integratedScope ? TimelineHeight : ScopeHeight + DividerHeight + TimelineHeight);
        if (ScopeHeight < 0 || TimelineHeight < 0 || occupiedPanelHeight > PanelHeight)
            throw new ArgumentOutOfRangeException(nameof(height), "Panel regions exceed the available panel height.");
        if (HasRoll && TimelineHeight < 16)
            // The resolver must avoid selecting a configuration whose roll cannot
            // hold at least 16px; only an impossible caller geometry should throw.
            throw new ArgumentOutOfRangeException(nameof(height), "Canvas is too small for the panel timeline area.");
        if (HasScopes && ScopeHeight < 1)
            throw new ArgumentOutOfRangeException(nameof(height), "Canvas is too small for the scope area.");
        // The left lane gutter carries semantic channel identity and the
        // current-note badge, with pitch-axis labels sharing its right edge.
        // Keep it compact but stable at video sizes instead of letting the
        // status area collapse into the graph.
        PitchLabelWidth = Math.Clamp(
            (int)Math.Round(width * (100.0 / 1920.0)),
            90,
            125);
        // Internal padding for the lane gutter: identity/status text is
        // left-aligned while pitch/rhythm labels remain right-aligned.
        PitchLabelInsetLeft = Math.Max(3, PitchLabelWidth / 5);
        PitchLabelInsetRight = Math.Max(3, PitchLabelWidth / 7);
        if (PitchLabelInsetLeft + PitchLabelInsetRight > PitchLabelWidth)
        {
            PitchLabelInsetLeft = PitchLabelWidth / 2;
            PitchLabelInsetRight = PitchLabelWidth / 2;
        }
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
    /// <summary>Left clear inset inside the pitch-label gutter.</summary>
    public int PitchLabelInsetLeft { get; }
    /// <summary>Right clear inset inside the pitch-label gutter.</summary>
    public int PitchLabelInsetRight { get; }
    public int TimelineHeight { get; }
    public VisualizationLayoutMode Mode { get; }
    public VisualizationLayoutVariant Variant { get; }
    public double RollZoom { get; }
    public VisualizationScopePosition ScopePosition { get; }
    public bool HasScopes { get; }
    public bool HasRoll { get; }

    /// <summary>
    /// True when the roll occupies the panel body directly (header above,
    /// pitch gutter carved out of the left edge) instead of a stacked
    /// scope/divider/timeline column. Both the diagnostic grid and the
    /// performance lanes share this integrated grammar.
    /// </summary>
    public bool UsesIntegratedRoll => HasRoll && (Variant == VisualizationLayoutVariant.DiagnosticGrid
        || Variant == VisualizationLayoutVariant.PerformanceLanes);

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
        if (Variant == VisualizationLayoutVariant.PerformanceLanes)
        {
            // Lanes carry no header band; the label lives in the pitch gutter
            // (see HeaderSlots). A zero-height header also disables every
            // dynamic header draw via the existing Height <= 0 guards.
            return new OverlayRect(panel.X, panel.Y, panel.Width, 0);
        }
        int height = Math.Min(PanelHeaderHeight, panel.Height);
        return new OverlayRect(panel.X, panel.Y, panel.Width, height);
    }

    public OverlayRect GetScopeRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        if (UsesIntegratedRoll)
        {
            OverlayRect body = GetTimelineRect(panelIndex);
            int gutter = Math.Min(PitchLabelWidth, body.Width);
            return new OverlayRect(body.X + gutter, body.Y, body.Width - gutter, body.Height);
        }
        int y = ScopePosition == VisualizationScopePosition.Bottom
            ? panel.Bottom - ScopeHeight
            : panel.Y + PanelHeaderHeight;
        return new OverlayRect(panel.X, y, panel.Width, ScopeHeight);
    }

    /// <summary>
    /// Fixed three-slot layout for a panel header: channel name, live pitch /
    /// state, then patch/algorithm. Slots share the header rectangle and fill
    /// the usable width (10px left/right insets, 4px accent bar excluded), so
    /// all three strings hold identical vertical bounds and can never collide
    /// or cross the panel boundary.
    /// </summary>
    public PanelHeaderLayout HeaderSlots(int panelIndex)
    {
        OverlayRect header = GetHeaderRect(panelIndex);
        const int accentBar = 4;
        const int leftInset = 10;
        const int rightInset = 10;

        if (Variant == VisualizationLayoutVariant.PerformanceLanes)
        {
            // The lane label is a compact caption at the top of the pitch
            // gutter: left-aligned, clear of the right-aligned pitch/rhythm
            // labels that share the same column. State/patch slots collapse —
            // lanes render no live header text.
            OverlayRect timeline = GetTimelineRect(panelIndex);
            int gutter = Math.Min(PitchLabelWidth, Math.Max(0, timeline.Width));
            var name = new OverlayRect(
                timeline.X + PitchLabelInsetLeft,
                timeline.Y + 1,
                Math.Max(0, gutter - PitchLabelInsetLeft - PitchLabelInsetRight),
                Math.Min(12, Math.Max(0, timeline.Height)));
            var empty = new OverlayRect(name.X, name.Y, 0, 0);
            return new PanelHeaderLayout(name, empty, empty);
        }

        int usableLeft = header.X + accentBar + leftInset;
        int rightLimit = header.Right - rightInset;
        int usable = Math.Max(0, rightLimit - usableLeft);
        if (usable <= 0)
            return new PanelHeaderLayout(header, header, header);

        // Name 22%, state 23%, patch remaining 55%. The last slot absorbs any
        // rounding so the three widths always sum to the usable width.
        int nameWidth = (int)Math.Round(usable * 0.22);
        int stateWidth = (int)Math.Round(usable * 0.23);
        int patchWidth = Math.Max(0, usable - nameWidth - stateWidth);

        return new PanelHeaderLayout(
            new OverlayRect(usableLeft, header.Y, nameWidth, header.Height),
            new OverlayRect(usableLeft + nameWidth, header.Y, stateWidth, header.Height),
            new OverlayRect(usableLeft + nameWidth + stateWidth, header.Y, patchWidth, header.Height));
    }

    public OverlayRect GetTimelineRect(int panelIndex)
    {
        OverlayRect panel = GetPanelRect(panelIndex);
        if (UsesIntegratedRoll)
            return new OverlayRect(panel.X, panel.Y + PanelHeaderHeight, panel.Width, TimelineHeight);
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
        // Canonical clock lives in FrameSampleClock; this forwarder keeps the
        // historical call sites stable.
        => FrameSampleClock.SampleAtFrame(frameIndex, sampleRate, fpsNumerator, fpsDenominator);

    private static int ClampBand(int value, int min, int max)
        => Math.Clamp(value, min, max);

    /// <summary>
    /// Searches candidate column counts and returns the panel grid whose every
    /// panel meets the given minimum dimensions, preferring shapes close to the
    /// target aspect ratio then fewer rows. Returns null when no grid can fit.
    /// </summary>
    internal static ResolvedPanelGrid? FindGrid(
        int panelCount,
        int availableWidth,
        int availableHeight,
        int minimumPanelWidth,
        int minimumPanelHeight)
    {
        ResolvedPanelGrid? best = null;
        double targetAspect = minimumPanelWidth / (double)minimumPanelHeight;

        for (int columns = 1; columns <= panelCount; columns++)
        {
            int rows = (panelCount + columns - 1) / columns;
            int panelWidth = availableWidth / columns;
            int panelHeight = availableHeight / rows;

            if (panelWidth < minimumPanelWidth
                || panelHeight < minimumPanelHeight)
            {
                continue;
            }

            if (best is null || IsBetter(columns, rows, best.Value, targetAspect))
                best = new ResolvedPanelGrid(columns, rows);
        }

        return best;
    }

    /// <summary>
    /// True when the nominal panel geometry for the given grid meets the full
    /// diagnostic minimums (width &gt;= 300, height &gt;= 150).
    /// </summary>
    internal static bool FitsFullDiagnostic(
        int width,
        int height,
        int columns,
        int rows,
        int topBarHeight,
        int bottomBarHeight)
    {
        int gridHeight = height - topBarHeight - bottomBarHeight;
        int panelWidth = width / columns;
        int panelHeight = gridHeight / rows;
        return panelWidth >= 300 && panelHeight >= 150;
    }

    private static bool IsBetter(
        int columns,
        int rows,
        ResolvedPanelGrid current,
        double targetAspect)
    {
        double candidateAspect = columns / (double)rows;
        double currentAspect = current.Columns / (double)current.Rows;
        double candidateSpread = Math.Abs(candidateAspect - targetAspect);
        double currentSpread = Math.Abs(currentAspect - targetAspect);

        // Prefer shapes close to the target aspect ratio, then fewer rows.
        if (candidateSpread != currentSpread)
            return candidateSpread < currentSpread;
        return rows < current.Rows;
    }

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

    /// <summary>
    /// The default balanced grid for a panel count, used by the legacy
    /// diagnostic-grid geometry and by low-level renderer fixtures that must
    /// exercise the full-grid rendering path regardless of the responsive
    /// fallback decision.
    /// </summary>
    internal static ResolvedPanelGrid DefaultGrid(int panelCount)
    {
        (int columns, int rows) = GridForPanelCount(panelCount);
        return new ResolvedPanelGrid(columns, rows);
    }
}
