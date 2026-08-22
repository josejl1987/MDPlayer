using System.Diagnostics;
using System.Runtime.InteropServices;
using Fmp.Core.Analysis;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Renders only the musical overlay: headers, piano-roll/event lanes, borders,
/// and a fixed playhead. Scope viewports remain transparent for Corrscope.
/// Frames are rendered directly into caller-owned RGBA memory.
/// </summary>
internal sealed partial class PanelOverlayRenderer : IDisposable
{
    public sealed class Options
    {
        public int FpsNumerator { get; set; } = 60;
        public int FpsDenominator { get; set; } = 1;
        public VisualizationTimeGrid TimeGrid { get; set; } = VisualizationTimeGrid.None;
        public VisualizationPresentation Presentation { get; set; } = VisualizationPresentation.Empty;
        public string FontPath { get; set; }
        public bool PreferAntialiasedText { get; set; }
        public EffectsMode Effects { get; set; } = EffectsMode.Minimal;
        public NoteColorMode NoteColor { get; set; } = NoteColorMode.Instrument;
        public AnalysisOverlayScene AnalysisOverlay { get; set; } = AnalysisOverlayScene.Empty;
        public VisualizationPalette Palette { get; set; } = VisualizationPalette.Default;
        public int MotionBlurSamples { get; set; } = 1;
        public bool EnablePerformanceMetrics { get; set; }

        /// <summary>
        /// Opacity applied to the scope waveform layer when it is composited
        /// over the painted panel body (0.05..1.0, default 1.0). The scope
        /// frame's alpha channel is used as a per-pixel mask and multiplied by
        /// this value; the result is baked into RGB because the encode path
        /// (RGBA -> yuv420p) drops alpha. At 1.0 with an opaque source the
        /// placement stays a raw copy (byte-identical to the pre-blend path).
        /// </summary>
        public double ScopeOpacity { get; set; } = 1.0;

        /// <summary>
        /// Presentation fade-in for the title bars and musical grid. The CLI
        /// enables this for final video composition; direct renderer callers
        /// keep the default zero-second transition for still tests.
        /// </summary>
        public double IntroSeconds { get; set; }

        /// <summary>Presentation fade-out applied to the grid before the end card.</summary>
        public double OutroSeconds { get; set; }

        /// <summary>
        /// Per-channel energy envelopes (§6.4) derived from stem WAV files.
        /// When non-null, the renderer brightens scope borders and modulates
        /// note flashes based on channel energy. Null disables energy effects.
        /// </summary>
        public ChannelEnergyEnvelope[] Energy { get; set; }
    }

    /// <summary>
    /// Thin per-panel view over the prepared <see cref="PreparedPanel"/>. The
    /// renderer keeps only the prepared semantic descriptor and a back-reference
    /// to the prepared data so the per-frame path never touches
    /// <see cref="NoteEvent"/>/<see cref="RhythmEvent"/> records or performs
    /// color resolution.
    /// </summary>
    private sealed class PanelData
    {
        public int Index;
        public string Id = "";
        public string Label = "";
        public VisualizationTrackKind TrackKind;
        public PreparedPanel Prepared;

        /// <summary>Gutter labels, one per distinct sample identity (row order). Cached to keep the render hot path allocation-free.</summary>
        public string[] SampleRowLabels = Array.Empty<string>();
        /// <summary>Row index (index into <see cref="SampleRowLabels"/>) for each <see cref="PreparedPanel.SamplePlayback"/> entry.</summary>
        public int[] SampleRowByPlaybackIndex = Array.Empty<int>();

        // Dense cursor ids assigned once when a sequential session is created.
        // Keeping them beside the panel avoids hashing the backing arrays on
        // every frame.
        public int MainNoteStreamId = -1;
        public int[] OperatorNoteStreamIds = Array.Empty<int>();
        public int RhythmStreamId = -1;
        public int PlaybackStreamId = -1;
        public int NoiseStreamId = -1;
        public int AggregateStreamId = -1;
    }

    private static readonly HashSet<int> BlackPitchClasses = new() { 1, 3, 6, 8, 10 };

    /// <summary>
    /// Precomputed C-octave labels for pitch-grid rows (§20.1: no per-frame
    /// string formatting). Index: midi / 12 - 1 → octave label string.
    /// Covers MIDI 0–127, i.e. octaves -1 through 10.
    /// </summary>
    private static readonly string[] COctaveLabels = BuildCOctaveLabels();
    private static readonly string[] PitchClassNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] PitchLabels = BuildPitchLabels();

    private static string[] BuildCOctaveLabels()
    {
        var labels = new string[12];
        for (int i = 0; i < 12; i++)
            labels[i] = "C" + (i - 1);
        return labels;
    }

    private VisualizationPalette Palette => _options.Palette;
    private OverlayColor CanvasBackground => Palette.CanvasBackground;
    private OverlayColor HeaderBackground => Palette.HeaderBackground;
    private OverlayColor TimelineBackground => Palette.TimelineBackground;
    // Keep the pitch guide subordinate to the time-aligned waveform. Custom
    // palettes may request less opacity, but never make this semantic band
    // opaque over DiagnosticGrid's waveform layer.
    private OverlayColor BlackKeyBand => Palette.BlackKeyBand.WithAlpha(Math.Min(Palette.BlackKeyBand.A, (byte)90));
    private OverlayColor GridLine => Palette.GridLine;
    private OverlayColor Border => Palette.Border;
    private OverlayColor MutedText => Palette.MutedText;
    private OverlayColor BrightText => Palette.BrightText;
    private OverlayColor Playhead => Palette.Playhead;

    // Three-level text contrast hierarchy against the panel/bar background.
    // Primary ~90%, Secondary ~72%, Tertiary ~55% opacity on a near-white tint.
    private OverlayColor _primaryText;
    private OverlayColor _secondaryText;
    private OverlayColor _tertiaryText;

    /// <summary>Near-white reference for text hierarchy (all channels equal).</summary>
    private static readonly byte[] TextHierarchyAlphas = [230, 184, 140];

    private void BuildTextHierarchy()
    {
        byte baseValue = Math.Max(HeaderBackground.R, Math.Max(HeaderBackground.G, HeaderBackground.B));
        // Lifted near-white so even the secondary/tertiary levels stay legible
        // and the primary reads as crisp white against the panel background.
        baseValue = (byte)Math.Max(238, Math.Min(250, baseValue + 60));
        _primaryText = new OverlayColor(baseValue, baseValue, baseValue, TextHierarchyAlphas[0]);
        _secondaryText = new OverlayColor(baseValue, baseValue, baseValue, TextHierarchyAlphas[1]);
        _tertiaryText = new OverlayColor(baseValue, baseValue, baseValue, TextHierarchyAlphas[2]);
    }

    /// <summary>Primary text: channel names, title, clock (~90% alpha).</summary>
    private OverlayColor PrimaryText => _primaryText;
    /// <summary>Secondary text: live pitch, patch, instrument (~72% alpha).</summary>
    private OverlayColor SecondaryText => _secondaryText;
    /// <summary>Tertiary text: lane labels and supplementary state (~55% alpha).</summary>
    private OverlayColor TertiaryText => _tertiaryText;

    /// <summary>
    /// Leftmost X of the clock region in the top bar. The fallback title is
    /// trimmed to <see cref="TopBarFallbackTitleMaxX"/> so it can never reach
    /// this column.
    /// </summary>
    internal int TopBarClockLeft =>
        _layout.TopBarRect.Right - _layout.SafeHorizontalMargin - _fullFallbackClockWidth;

    /// <summary>Trimmed right-hand limit of the fallback top-bar title.</summary>
    internal int TopBarFallbackTitleMaxX =>
        _layout.TopBarRect.Right - _layout.SafeHorizontalMargin - _fullFallbackClockWidth - 40;

    /// <summary>
    /// Notes are born with an enlarged onset cap for this many milliseconds
    /// (§8.5: 80–140 ms — 110 is the deterministic midpoint).
    /// </summary>
    private const double OnsetCapEnlargedMs = 110;

    /// <summary>
    /// Duration of the FM instrument-change overlay (§13.2): the ALG/FB/AMS/PMS
    /// text + operator bars replace the normal patch token for 800 ms.
    /// </summary>
    private const double InstrumentChangeOverlayMs = 800;

    /// <summary>Width of each compact operator bar in the header (§13.3).</summary>
    private const int OperatorBarWidth = 4;
    private const int OperatorBarGap = 2;

    /// <summary>
    /// Alpha of the half-visible end marker drawn at the panel edge when a
    /// note continues past the visible window (§8.6 timeline clipping).
    /// </summary>
    private const byte ClippedEndMarkerAlpha = 130;

    /// <summary>Width of the onset/end cap in lanes wider than 480 px.</summary>
    private const int CapWidthDivisor = 100;

    private readonly VisualizationTimeline _timeline;
    private readonly Options _options;
    private readonly OverlayLayout _layout;
    private readonly VisualizationTopology _topology;
    private readonly OverlayScene _scene;
    private readonly PanelData[] _panels;
    private readonly Dictionary<string, InstrumentDefinition> _instrumentById;
    private readonly byte[] _staticFrame;
    private readonly byte[] _motionBlurScratch;
    private readonly OverlayRect[] _dynamicRestoreRects;
    private readonly OverlayRect[] _dynamicRestoreRectsWithScope;
    private readonly RectCopyPlan[] _dynamicRestorePlans;
    private readonly RectCopyPlan[] _dynamicRestorePlansWithScope;
    private readonly ScopeCopyPlan[] _scopeCopyPlans;
    private readonly PitchCamera[] _cameras;
    private readonly VisualizationPresentation _presentation;
    private readonly OverlayColor[] _panelAccents;
    private readonly ChannelEnergyEnvelope[] _energyByPanel;
    private readonly bool[] _hasAudioEnergyByPanel;
    private readonly int[] _activityLabelCounts;
    private readonly string[] _activityLabelCache;
    private readonly string[] _headerStateInputs;
    private readonly string[] _headerStateOutputs;
    private readonly int[] _headerStateWidths;
    private readonly string[] _headerPatchInputs;
    private readonly string[] _headerPatchOutputs;
    private readonly int[] _headerPatchWidths;
    private readonly ChipPanelHeaderBuilder.Cursor[] _chipHeaderCursors;
    private readonly bool _unicodePresentationRendered;
    private readonly double _samplesPerFrame;
    private readonly long _taperSamples;
    private readonly long _introSamples;
    private readonly long _outroSamples;
    private readonly EffectsMode _effects;
    private readonly AnalysisOverlayScene _analysisOverlay;
    private readonly RenderPerformanceMetrics _performance;
    private SequentialRenderState? _activeSequentialState;
    internal bool TestDisableZohRuns { get; set; }

    /// <summary>Test seam exposing the private FillRect for equivalence tests.</summary>
    internal void RenderFillRectForTest(Span<byte> frame, int left, int top, int width, int height, OverlayColor color)
        => FillRect(frame, new OverlayRect(left, top, width, height), color);
    private readonly double[][] _laneBaseAlphas;
    private readonly long[] _laneBaseAlphaSamples;
    private readonly long[] _laneBaseAlphaWindowStarts;
    private readonly double[] _laneBaseAlphaSamplesPerPixel;
    private readonly int[] _laneBaseAlphaPlayheadX;
    private readonly byte[][] _laneGridCache;
    private readonly double[] _laneGridMinMidi;
    private readonly double[] _laneGridMaxMidi;

    /// <summary>
    /// Active-note flash (§9.1): 120 ms, 40% white mix, 120% max size, cubic
    /// ease-out. The flash is a transient on top of the steady active
    /// lightening — it decays to zero, leaving the +0.14 active state.
    /// </summary>
    private const double ActiveFlashMs = 120;
    private const double ActiveFlashWhiteMix = 0.40;
    private const double ActiveFlashMaxScale = 1.20;

    /// <summary>
    /// Rhythm impact decay (§15.2): the strong visible phase holds full
    /// brightness, then the trail fades to zero alpha by the total decay. Both
    /// use absolute event age (currentSample - SamplePosition) so the result is
    /// order-independent (§21).
    /// </summary>
    private const double RhythmStrongPhaseMs = 90;
    private const double RhythmTotalDecayMs = 300;

    /// <summary>
    /// Horizontal offset of the rhythm pan tick from the voice-row centre
    /// (§15.3). Pan=-1 (left) shifts the tick left; Pan=+1 (right) shifts it
    /// right. The impact block itself never moves — only the tick.
    /// </summary>
    private const int RhythmPanTickOffset = 6;

    /// <summary>
    /// Onset contact ripple (§9.2): 220 ms, expanding rings centered at the
    /// playhead × onset-pitch intersection. Final radius scales with the panel
    /// height so it reads at both 720p and 1080p.
    /// </summary>
    private const double RippleMs = 280;
    private const double RippleInitialRadius = 2;
    private const byte RippleInitialAlpha = 205;

    // The clock string ("MM:SS / MM:SS") only changes once per whole second.
    // Build every reachable value during preparation so arbitrary frame access
    // never inserts into a dictionary or allocates a string.
    private readonly string[] _clockBySecond;
    private readonly string _totalClockString;
    private readonly int _fullFallbackClockWidth;
    private readonly string[] _loopLabelByFrame;
    private readonly VisualizationTimeGridLine[] _timeGrid;
    private readonly object _motionBlurGate = new();

    public PanelOverlayRenderer(
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        Options options = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        ArgumentNullException.ThrowIfNull(layout);
        if (timeline.SampleRate <= 0)
            throw new ArgumentException("Timeline sample rate must be positive.", nameof(timeline));
        if (timeline.EndSample < timeline.StartSample)
            throw new ArgumentException("Timeline end precedes its start.", nameof(timeline));

        _options = options ?? new Options();
        _performance = new RenderPerformanceMetrics(_options.EnablePerformanceMetrics);
        _options.Palette ??= VisualizationPalette.Default;
        if (_options.MotionBlurSamples is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(options), "Motion blur samples must be between 1 and 8.");
        if (_options.ScopeOpacity is < 0.05 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(options), "Scope opacity must be between 0.05 and 1.0.");
        if (_options.FpsNumerator <= 0 || _options.FpsDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Frame rate must be positive.");

        _topology = layout.Topology;
        _layout = layout.Geometry;
        _presentation = _options.Presentation ?? VisualizationPresentation.Empty;
        _instrumentById = timeline.Instruments.ToDictionary(x => x.Id, StringComparer.Ordinal);
        if (!double.IsFinite(_options.IntroSeconds) || _options.IntroSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Intro duration must be finite and non-negative.");
        if (!double.IsFinite(_options.OutroSeconds) || _options.OutroSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Outro duration must be finite and non-negative.");
        _samplesPerFrame = timeline.SampleRate * (double)_options.FpsDenominator / _options.FpsNumerator;
        _introSamples = checked((long)Math.Round(_options.IntroSeconds * timeline.SampleRate));
        _outroSamples = checked((long)Math.Round(_options.OutroSeconds * timeline.SampleRate));
        // Normal-release taper window (§8.6): 40–80 ms — 60 ms is the midpoint.
        _taperSamples = (long)Math.Round(0.060 * timeline.SampleRate);
        _effects = _options.Effects;
        _analysisOverlay = _options.AnalysisOverlay ?? AnalysisOverlayScene.Empty;
        long layoutStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        _timeGrid = VisualizationTimeGridBuilder.Build(timeline, _options.TimeGrid);

        // Build the prepared scene once: notes/rhythm are sorted, colors are
        // pre-resolved, and pitch ranges are computed here so the per-frame hot
        // path performs no color calculation, sorting, or list building.
        // The presentation (title/credits) is handled separately by the static
        // frame builder, so metadata is left at its default.
        _scene = OverlaySceneBuilder.Build(
            timeline,
            _layout,
            _topology,
            samplesPerFrame: _samplesPerFrame,
            noteColorMode: _options.NoteColor,
            palette: _options.Palette);
        _panels = BuildPanels();
        AssignPanelStreamIds();
        _laneBaseAlphas = new double[_panels.Length][];
        _laneBaseAlphaSamples = new long[_panels.Length];
        _laneBaseAlphaWindowStarts = new long[_panels.Length];
        _laneBaseAlphaSamplesPerPixel = new double[_panels.Length];
        _laneBaseAlphaPlayheadX = new int[_panels.Length];
        Array.Fill(_laneBaseAlphaSamples, long.MinValue);
        _laneGridCache = new byte[_panels.Length][];
        _laneGridMinMidi = new double[_panels.Length];
        _laneGridMaxMidi = new double[_panels.Length];
        for (int i = 0; i < _panels.Length; i++) { _laneGridMinMidi[i] = double.NaN; _laneGridMaxMidi[i] = double.NaN; }
        _activityLabelCounts = new int[_panels.Length];
        _activityLabelCache = new string[_panels.Length];
        _headerStateInputs = new string[_panels.Length];
        _headerStateOutputs = new string[_panels.Length];
        _headerStateWidths = new int[_panels.Length];
        Array.Fill(_headerStateWidths, int.MinValue);
        _headerPatchInputs = new string[_panels.Length];
        _headerPatchOutputs = new string[_panels.Length];
        _headerPatchWidths = new int[_panels.Length];
        Array.Fill(_headerPatchWidths, int.MinValue);
        _chipHeaderCursors = new ChipPanelHeaderBuilder.Cursor[_panels.Length];
        for (int index = 0; index < _chipHeaderCursors.Length; index++)
            _chipHeaderCursors[index] = new ChipPanelHeaderBuilder.Cursor();
        Array.Fill(_activityLabelCounts, int.MinValue);
        _cameras = BuildCameras();
        if (_performance.Enabled)
            _performance.LayoutTicks += Stopwatch.GetTimestamp() - layoutStart;
        _staticFrame = new byte[FrameByteCount];
        _motionBlurScratch = _options.MotionBlurSamples > 1
            ? new byte[FrameByteCount]
            : null;
        _totalClockString = FormatTime(
            Math.Max(0, _timeline.EndSample - _timeline.StartSample) / (double)_timeline.SampleRate);
        _clockBySecond = BuildClockStrings(_totalClockString);
        _fullFallbackClockWidth = BitmapFont.MeasureText($"00:00 / {_totalClockString}", 2);
        _loopLabelByFrame = BuildLoopLabels();
        _dynamicRestoreRects = BuildDynamicRestoreRects(scopeFramePresent: false);
        _dynamicRestoreRectsWithScope = BuildDynamicRestoreRects(scopeFramePresent: true);
        _dynamicRestorePlans = BuildRectCopyPlans(_dynamicRestoreRects);
        _dynamicRestorePlansWithScope = BuildRectCopyPlans(_dynamicRestoreRectsWithScope);
        _scopeCopyPlans = BuildScopeCopyPlans();

        // Per-panel accents come straight from the prepared scene — the builder
        // resolved them from each panel's stable semantic identity.
        _panelAccents = new OverlayColor[_panels.Length];
        for (int i = 0; i < _panels.Length; i++)
        {
            _panelAccents[i] = Palette.ResolveAccent(_panels[i].Id, i);
        }

        // §6.4: build a per-panel energy lookup keyed by panel index.
        _energyByPanel = BuildEnergyLookup(_options.Energy);
        _hasAudioEnergyByPanel = BuildAudioEnergyFlags(_energyByPanel);

        // Validate glyph coverage before the static layer is built. Missing
        // non-ASCII glyphs are a publish-time error, never a bitmap '?'.
        string[] trackLabels = _scene.Panels.Select(panel => panel.Label).ToArray();
        _unicodePresentationRendered = UnicodeStaticTextRenderer.CanRender(
            _presentation,
            _options.FontPath,
            trackLabels,
            _options.PreferAntialiasedText);

        BuildTextHierarchy();
        long staticLayerStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        BuildStaticFrame(_staticFrame, drawFallbackText: !_unicodePresentationRendered);

        if (_unicodePresentationRendered)
        {
            foreach (int index in Enumerable.Range(0, _panels.Length))
            {
                OverlayRect header = _layout.GetHeaderRect(index);
                int labelWidth = Math.Min(Math.Max(0, header.Width - 14), 76);
                FillRect(_staticFrame, new OverlayRect(header.X + 4, header.Y, labelWidth, header.Height), HeaderBackground);
            }
            UnicodeStaticTextRenderer.TryDraw(
                _staticFrame, Width, Height, _layout, _presentation, _options.FontPath, trackLabels);
        }
        if (_performance.Enabled)
            _performance.StaticLayerTicks += Stopwatch.GetTimestamp() - staticLayerStart;
    }

    public int Width => _layout.Width;
    public int Height => _layout.Height;
    internal VisualizationTopology Topology => _topology;
    public int FrameByteCount => checked(Width * Height * 4);
    public int ScopeFrameByteCount => checked(
        _layout.CorrscopeGridWidth * _layout.CorrscopeGridHeight * 4);
    public int FpsNumerator => _options.FpsNumerator;
    public int FpsDenominator => _options.FpsDenominator;
    public OverlayLayout Layout => _layout;
    internal RenderPerformanceSnapshot Performance => _performance.Snapshot(Width, Height);

    internal void ResetPerformanceMetrics() => _performance.Reset();

    public long TotalFrames
        => FrameSampleClock.FrameCount(
            Math.Max(0, _timeline.EndSample - _timeline.StartSample),
            _timeline.SampleRate,
            FpsNumerator,
            FpsDenominator);

    /// <summary>
    /// Renders a full single-layer frame (static chrome + dynamic content) into
    /// <paramref name="destination"/>. Kept for tests and legacy callers; the
    /// pipeline uses <see cref="RenderDynamicFrame"/> (static background
    /// composited by FFmpeg) or <see cref="RenderCompositeFrame"/> (scope grid
    /// composited in memory).
    /// </summary>
    public void RenderFrame(long frameIndex, Span<byte> destination)
        => RenderCompositeFrame(frameIndex, ReadOnlySpan<byte>.Empty, destination);

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Renders only the dynamic overlay content into a transparent destination.
    /// The static chrome (panel backgrounds, borders, labels, metadata bars,
    /// title/credits) is provided separately — FFmpeg composites it as a looped
    /// still, so it is drawn once instead of copied into every frame.
    /// </summary>
    public void RenderDynamicFrame(long frameIndex, Span<byte> destination)
    {
        ValidateFrame(frameIndex, destination);
        destination.Clear();
        DrawDynamicCore(frameIndex, destination);
    }

    /// <summary>
    /// Renders the full frame into <paramref name="destination"/>: the static
    /// chrome is copied in, the Corrscope scope grid strip (raw RGBA frames,
    /// width × CorrscopeGridHeight) is blended into the transparent scope holes,
    /// and the dynamic content is drawn on top. Used by the single-pass
    /// compositor, which composites the scope and overlay in memory and encodes
    /// once. Pass an empty <paramref name="scopeGrid"/> to skip the scope rows.
    /// </summary>
    public void RenderCompositeFrame(
        long frameIndex, ReadOnlySpan<byte> scopeGrid, Span<byte> destination,
        bool scopeFramesAreOpaque = false)
    {
        if (_options.MotionBlurSamples > 1)
        {
            RenderMotionBlurFrame(frameIndex, scopeGrid, destination, scopeFramesAreOpaque);
            return;
        }

        RenderCompositeFrameSingle(frameIndex, scopeGrid, destination, scopeFramesAreOpaque);
    }

    private void RenderCompositeFrameSingle(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination,
        bool scopeFramesAreOpaque = false)
    {
        ValidateFrame(frameIndex, destination);
        if (!scopeGrid.IsEmpty)
        {
            int gridBytes = ScopeFrameByteCount;
            if (scopeGrid.Length < gridBytes)
                throw new ArgumentException(
                    $"Scope grid requires at least {gridBytes} bytes, got {scopeGrid.Length}.",
                nameof(scopeGrid));
        }

        long renderStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = _performance.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        _staticFrame.AsSpan().CopyTo(destination);
        if (_performance.Enabled)
        {
            _performance.Frames++;
            _performance.FullRedraws++;
            _performance.FullFrameCopies++;
            _performance.SurfaceCopies++;
            _performance.CopiedBytes += FrameByteCount;
            _performance.RenderedPixels += (long)Width * Height;
        }
        // The playhead is a background time reference: draw it before the scope
        // rows so the (opaque) waveform can cover it, keeping the current-sample
        // signal visible exactly at the playhead column.
        long compositingStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        DrawPlayheads(destination);
        if (!scopeGrid.IsEmpty)
            PlaceScopeRows(scopeGrid, destination, scopeFramesAreOpaque);
        if (_performance.Enabled)
            _performance.CompositingTicks += Stopwatch.GetTimestamp() - compositingStart;
        long dynamicStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        DrawDynamicCore(frameIndex, destination);
        if (_performance.Enabled)
        {
            _performance.DynamicTicks += Stopwatch.GetTimestamp() - dynamicStart;
            _performance.RenderTicks += Stopwatch.GetTimestamp() - renderStart;
            _performance.FinishFrame(allocatedBefore);
        }
    }

    /// <summary>Draws the per-panel playhead cursor (playhead position is a fixed
    /// window fraction, independent of the current sample).</summary>
    private void DrawPlayheads(Span<byte> destination)
    {
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
            DrawPlayhead(destination, panelIndex);
    }

    private void RenderMotionBlurFrame(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination,
        bool scopeFramesAreOpaque = false)
    {
        // The temporal scratch frame is prepared once and reused to keep the
        // hot path allocation-free. Serialize users of that scratch buffer so
        // random-access callers can still query the same renderer in parallel.
        lock (_motionBlurGate)
        {
            ValidateFrame(frameIndex, destination);
            int samples = _options.MotionBlurSamples;
            destination.Clear();
            int firstOffset = -(samples / 2);
            for (int sample = 0; sample < samples; sample++)
            {
                long sampledFrame = Math.Clamp(
                    frameIndex + firstOffset + sample,
                    0,
                    TotalFrames - 1);
                RenderCompositeFrameSingle(
                    sampledFrame, scopeGrid, _motionBlurScratch, scopeFramesAreOpaque);
                if (sample == 0)
                {
                    _motionBlurScratch.AsSpan().CopyTo(destination);
                    continue;
                }

                for (int offset = 0; offset < FrameByteCount; offset++)
                {
                    destination[offset] = (byte)((destination[offset] * sample
                        + _motionBlurScratch[offset]) / (sample + 1));
                }
            }
        }
    }

    internal void RenderForSession(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination,
        SequentialRenderState state,
        bool scopeFramesAreOpaque = false)
    {
        if (_options.MotionBlurSamples > 1)
        {
            RenderMotionBlurFrame(frameIndex, scopeGrid, destination, scopeFramesAreOpaque);
            return;
        }

        ValidateFrameForSession(frameIndex, destination);
        if (_performance.Enabled)
        {
            _performance.Frames++;
            _performance.PartialRedraws++;
        }
        long renderStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        long allocatedBefore = _performance.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
        long compositingStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        // An opaque scope placement at full opacity replaces the whole scope
        // body, so only the gutter/bars/headers need restoring. The alpha
        // blend leaves transparent pixels' RGB untouched, so the body must be
        // restored from the static layer first — otherwise previous frames
        // ghost through the translucent waveform.
        bool scopeReplacesBody =
            !scopeGrid.IsEmpty && scopeFramesAreOpaque && _options.ScopeOpacity >= 1.0;
        RestoreDynamicRegions(destination, scopeReplacesBody);
        // The playhead is a background time reference: draw it before the
        // scope rows (same order as RenderCompositeFrameSingle) so the
        // waveform covers it and the current-sample signal stays visible at
        // the playhead column.
        DrawPlayheads(destination);
        PlaceScopeRowsForSession(scopeGrid, destination, scopeFramesAreOpaque);
        if (_performance.Enabled)
            _performance.CompositingTicks += Stopwatch.GetTimestamp() - compositingStart;
        long dynamicStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        DrawDynamicForSession(frameIndex, destination, state);
        if (_performance.Enabled)
        {
            _performance.DynamicTicks += Stopwatch.GetTimestamp() - dynamicStart;
            _performance.RenderTicks += Stopwatch.GetTimestamp() - renderStart;
            _performance.FinishFrame(allocatedBefore);
        }
    }

    internal SequentialCompositeSession CreateSequentialSession(bool scopeFramesAreOpaque = false)
        => new(this, scopeFramesAreOpaque);

    /// <summary>
    /// Assigns per-panel stream ids exactly once per panel layout. Called at
    /// construction and again (idempotently) when a sequential session is
    /// created, so direct and sequential renders always see the same stream
    /// numbering — and therefore the same drawn content (FM3 operator
    /// ribbons included from the first frame).
    /// </summary>
    private void AssignPanelStreamIds()
    {
        int noteStream = 0;
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PanelData panel = _panels[panelIndex];
            PreparedPanel prepared = panel.Prepared;
            panel.MainNoteStreamId = noteStream++;
            panel.OperatorNoteStreamIds = new int[prepared.OperatorNotes.Length];
            for (int operatorIndex = 0; operatorIndex < prepared.OperatorNotes.Length; operatorIndex++)
                panel.OperatorNoteStreamIds[operatorIndex] = noteStream++;
            panel.RhythmStreamId = panelIndex;
            panel.NoiseStreamId = panelIndex;
            panel.PlaybackStreamId = panelIndex;
            panel.AggregateStreamId = panelIndex;
        }
    }

    private double[] GetBaseAlphasForLane(int panelIndex, OverlayRect lane, long windowStart, double samplesPerPixel, long currentSample, int playheadX)
    {
        if (_laneBaseAlphas[panelIndex] != null
            && _laneBaseAlphaSamples[panelIndex] == currentSample
            && _laneBaseAlphaWindowStarts[panelIndex] == windowStart
            && _laneBaseAlphaSamplesPerPixel[panelIndex] == samplesPerPixel
            && _laneBaseAlphaPlayheadX[panelIndex] == playheadX
            && _laneBaseAlphas[panelIndex].Length == lane.Width)
            return _laneBaseAlphas[panelIndex];
        double[] alphas = new double[lane.Width];
        for (int i = 0; i < lane.Width; i++)
        {
            int x = lane.X + i;
            double sample = windowStart + (x + 0.5 - lane.X) * samplesPerPixel;
            double temporal = Math.Abs(x - playheadX) <= 2 ? 1.0 : sample > currentSample ? 0.35 : 0.70 - 0.35 * Math.Clamp(Math.Max(0, (currentSample - sample) / (double)_timeline.SampleRate) / 0.25, 0, 1);
            alphas[i] = NormalRibbonOpacity * temporal;
        }
        _laneBaseAlphas[panelIndex] = alphas;
        _laneBaseAlphaSamples[panelIndex] = currentSample;
        _laneBaseAlphaWindowStarts[panelIndex] = windowStart;
        _laneBaseAlphaSamplesPerPixel[panelIndex] = samplesPerPixel;
        _laneBaseAlphaPlayheadX[panelIndex] = playheadX;
        return alphas;
    }

    internal SequentialRenderState CreateSequentialRenderState()
    {
        // Idempotent: the same stream numbering is already assigned at
        // construction so random-access and sequential renders always share
        // one content set (operator ribbons included from frame 0).
        AssignPanelStreamIds();
        var streams = new List<PreparedNote[]>();
        var rhythmStreams = new List<PreparedRhythmEvent[]>();
        var noiseStreams = new List<NoiseStateEvent[]>();
        var playbackStreams = new List<SamplePlaybackEvent[]>();
        var aggregateStreams = new List<AggregateHitEvent[]>();
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PanelData panel = _panels[panelIndex];
            PreparedPanel prepared = _panels[panelIndex].Prepared;
            streams.Add(prepared.MainNotes);
            for (int operatorIndex = 0; operatorIndex < prepared.OperatorNotes.Length; operatorIndex++)
                streams.Add(prepared.OperatorNotes[operatorIndex]);
            rhythmStreams.Add(prepared.Rhythm);
            noiseStreams.Add(prepared.Noise);
            playbackStreams.Add(prepared.SamplePlayback);
            aggregateStreams.Add(prepared.AggregateHits);
        }
        return new SequentialRenderState(
            streams.ToArray(),
            rhythmStreams.ToArray(),
            noiseStreams.ToArray(),
            playbackStreams.ToArray(),
            aggregateStreams.ToArray(),
            _timeGrid,
            _performance);
    }

    internal sealed class SequentialRenderState
    {
        private readonly int[] _nextIndexes;
        private readonly int[] _activeIndexes;
        private readonly long[] _lastWindowStarts;
        private readonly long[] _lastActiveSamples;
        private readonly int[] _rhythmIndexes;
        private readonly long[] _lastRhythmSamples;
        private readonly int[] _noiseIndexes;
        private readonly long[] _lastNoiseSamples;
        private readonly int[] _playbackIndexes;
        private readonly long[] _lastPlaybackSamples;
        private readonly int[] _aggregateIndexes;
        private readonly long[] _lastAggregateSamples;
        private readonly int[] _timeGridIndexes;
        private readonly long[] _lastTimeGridSamples;
        private readonly RenderPerformanceMetrics _performance;

        internal SequentialRenderState(
            PreparedNote[][] streams,
            PreparedRhythmEvent[][] rhythmStreams,
            NoiseStateEvent[][] noiseStreams,
            SamplePlaybackEvent[][] playbackStreams,
            AggregateHitEvent[][] aggregateStreams,
            VisualizationTimeGridLine[] timeGrid,
            RenderPerformanceMetrics performance)
        {
            _performance = performance;
            _nextIndexes = new int[streams.Length];
            _activeIndexes = new int[streams.Length];
            _lastWindowStarts = new long[streams.Length];
            _lastActiveSamples = new long[streams.Length];
            Array.Fill(_lastWindowStarts, long.MinValue);
            Array.Fill(_lastActiveSamples, long.MinValue);

            _rhythmIndexes = new int[rhythmStreams.Length];
            _lastRhythmSamples = new long[rhythmStreams.Length];
            Array.Fill(_lastRhythmSamples, long.MinValue);

            _noiseIndexes = new int[noiseStreams.Length];
            _lastNoiseSamples = new long[noiseStreams.Length];
            Array.Fill(_lastNoiseSamples, long.MinValue);

            _playbackIndexes = new int[playbackStreams.Length];
            _lastPlaybackSamples = new long[playbackStreams.Length];
            Array.Fill(_lastPlaybackSamples, long.MinValue);

            _aggregateIndexes = new int[aggregateStreams.Length];
            _lastAggregateSamples = new long[aggregateStreams.Length];
            Array.Fill(_lastAggregateSamples, long.MinValue);

            _timeGridIndexes = new int[1];
            _lastTimeGridSamples = new long[1];
            Array.Fill(_lastTimeGridSamples, long.MinValue);
            _ = timeGrid;
        }

        internal bool TryGetFirst(int streamId, PreparedNote[] notes, long windowStart, out int first)
        {
            if ((uint)streamId >= (uint)_nextIndexes.Length
                || windowStart < _lastWindowStarts[streamId])
            {
                first = 0;
                return false;
            }

            int next = _nextIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < notes.Length && notes[next].StartSample < windowStart)
                next++;
            if (_performance.Enabled)
                _performance.PianoRollCursorAdvances += next - before;
            _nextIndexes[streamId] = next;
            _lastWindowStarts[streamId] = windowStart;
            first = Math.Max(0, next - 1);
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetRhythmFirst(
            int streamId, PreparedRhythmEvent[] events, long sample, out int first)
        {
            if ((uint)streamId >= (uint)_rhythmIndexes.Length
                || sample < _lastRhythmSamples[streamId])
            {
                first = 0;
                return false;
            }

            int next = _rhythmIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < events.Length && events[next].SamplePosition < sample)
                next++;
            _rhythmIndexes[streamId] = next;
            _lastRhythmSamples[streamId] = sample;
            if (_performance.Enabled)
                _performance.SourceCursorAdvances += next - before;
            first = next;
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetNoiseFirst(
            int streamId, NoiseStateEvent[] events, long sample, out int first)
        {
            if ((uint)streamId >= (uint)_noiseIndexes.Length
                || sample < _lastNoiseSamples[streamId])
            {
                first = 0;
                return false;
            }

            int next = _noiseIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < events.Length && events[next].StartSample < sample)
                next++;
            _noiseIndexes[streamId] = next;
            _lastNoiseSamples[streamId] = sample;
            if (_performance.Enabled)
                _performance.SourceCursorAdvances += next - before;
            first = next;
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetPlaybackFirst(
            int streamId, SamplePlaybackEvent[] events, long sample, out int first)
        {
            if ((uint)streamId >= (uint)_playbackIndexes.Length
                || sample < _lastPlaybackSamples[streamId])
            {
                first = 0;
                return false;
            }

            int next = _playbackIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < events.Length && events[next].StartSample < sample)
                next++;
            _playbackIndexes[streamId] = next;
            _lastPlaybackSamples[streamId] = sample;
            if (_performance.Enabled)
                _performance.SourceCursorAdvances += next - before;
            first = next;
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetAggregateFirst(
            int streamId, AggregateHitEvent[] events, long sample, out int first)
        {
            if ((uint)streamId >= (uint)_aggregateIndexes.Length
                || sample < _lastAggregateSamples[streamId])
            {
                first = 0;
                return false;
            }

            int next = _aggregateIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < events.Length && events[next].SamplePosition < sample)
                next++;
            _aggregateIndexes[streamId] = next;
            _lastAggregateSamples[streamId] = sample;
            if (_performance.Enabled)
                _performance.SourceCursorAdvances += next - before;
            first = next;
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetAggregateUpper(
            int streamId, AggregateHitEvent[] events, long sample, out int upper)
        {
            if ((uint)streamId >= (uint)_aggregateIndexes.Length
                || sample < _lastAggregateSamples[streamId])
            {
                upper = 0;
                return false;
            }

            int next = _aggregateIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < events.Length && events[next].SamplePosition <= sample)
                next++;
            _aggregateIndexes[streamId] = next;
            _lastAggregateSamples[streamId] = sample;
            if (_performance.Enabled)
                _performance.SourceCursorAdvances += next - before;
            upper = next;
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetTimeGridFirst(
            VisualizationTimeGridLine[] lines, long sample, out int first)
        {
            const int streamId = 0;
            if (sample < _lastTimeGridSamples[streamId])
            {
                first = 0;
                return false;
            }

            int next = _timeGridIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < lines.Length && lines[next].Sample < sample)
                next++;
            _timeGridIndexes[streamId] = next;
            _lastTimeGridSamples[streamId] = sample;
            if (_performance.Enabled)
                _performance.SourceCursorAdvances += next - before;
            first = next;
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }

        internal bool TryGetActive(int streamId, PreparedNote[] notes, long sample, out PreparedNote active)
        {
            active = null;
            if ((uint)streamId >= (uint)_activeIndexes.Length
                || sample < _lastActiveSamples[streamId])
            {
                return false;
            }

            int next = _activeIndexes[streamId];
            int before = next;
            long stateStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            while (next < notes.Length && notes[next].StartSample <= sample)
                next++;
            if (_performance.Enabled)
                _performance.PianoRollCursorAdvances += next - before;
            _activeIndexes[streamId] = next;
            _lastActiveSamples[streamId] = sample;

            for (int index = next - 1, lower = Math.Max(0, next - 2); index >= lower; index--)
            {
                PreparedNote note = notes[index];
                if (note.StartSample <= sample && sample < note.EndSample)
                {
                    active = note;
                    break;
                }
            }
            if (_performance.Enabled)
                _performance.FrameStateTicks += Stopwatch.GetTimestamp() - stateStart;
            return true;
        }
    }

    internal void ValidateFrameForSession(long frameIndex, Span<byte> destination)
        => ValidateFrame(frameIndex, destination);

    internal void RestoreDynamicRegions(Span<byte> destination, bool scopeReplacesBody = false)
    {
        long restoredPixels = 0;
        RectCopyPlan[] restorePlans = scopeReplacesBody
            ? _dynamicRestorePlansWithScope
            : _dynamicRestorePlans;
        int stride = Width * 4;
        foreach (RectCopyPlan plan in restorePlans)
        {
            int sourceOffset = plan.Offset;
            int destinationOffset = plan.Offset;
            for (int row = 0; row < plan.Rows; row++)
            {
                _staticFrame.AsSpan(sourceOffset, plan.RowBytes)
                    .CopyTo(destination.Slice(destinationOffset, plan.RowBytes));
                sourceOffset += stride;
                destinationOffset += stride;
                if (_performance.Enabled)
                {
                    _performance.SurfaceCopies++;
                    _performance.CopiedBytes += plan.RowBytes;
                }
            }
            if (_performance.Enabled)
                restoredPixels += (long)plan.RowBytes / 4 * plan.Rows;
        }
        if (_performance.Enabled)
        {
            long framePixels = (long)Width * Height;
            _performance.RenderedPixels += restoredPixels;
            _performance.AvoidedPixels += Math.Max(0, framePixels - restoredPixels);
        }
    }

    internal void PlaceScopeRowsForSession(
        ReadOnlySpan<byte> scopeGrid, Span<byte> destination, bool scopeFramesAreOpaque = false)
    {
        if (scopeGrid.IsEmpty)
            return;
        int gridBytes = ScopeFrameByteCount;
        if (scopeGrid.Length < gridBytes)
            throw new ArgumentException(
                $"Scope grid requires at least {gridBytes} bytes, got {scopeGrid.Length}.",
                nameof(scopeGrid));
        PlaceScopeRows(scopeGrid, destination, scopeFramesAreOpaque);
    }

    internal void DrawDynamicForSession(
        long frameIndex,
        Span<byte> destination,
        SequentialRenderState state)
    {
        SequentialRenderState? previous = _activeSequentialState;
        _activeSequentialState = state;
        try
        {
            DrawDynamicCore(frameIndex, destination);
        }
        finally
        {
            _activeSequentialState = previous;
        }
    }

    private void ValidateFrame(long frameIndex, Span<byte> destination)
    {
        if (frameIndex < 0 || frameIndex >= TotalFrames)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        if (destination.Length < FrameByteCount)
            throw new ArgumentException($"Destination requires at least {FrameByteCount} bytes.", nameof(destination));
    }

    private void DrawDynamicCore(
        long frameIndex,
        Span<byte> destination,
        bool drawSemantic = true)
    {
        long relativeSample = FrameSampleClock.SampleAtFrame(
            frameIndex,
            _timeline.SampleRate,
            FpsNumerator,
            FpsDenominator);
        long currentSample = Math.Min(_timeline.EndSample, _timeline.StartSample + relativeSample);

        long textStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        DrawClock(destination, currentSample);
        DrawLoopLabel(destination, frameIndex);
        DrawProgress(destination, currentSample);
        DrawAnalysisHud(destination, currentSample);
        DrawAnalysisHarmonyStrip(destination, currentSample);
        DrawAnalysisProgressMarkers(destination, currentSample);
        if (_performance.Enabled)
            _performance.TextTicks += Stopwatch.GetTimestamp() - textStart;

        long layoutStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
        DrawTimeGrid(destination, currentSample);
        if (_performance.Enabled)
            _performance.LayoutTicks += Stopwatch.GetTimestamp() - layoutStart;

        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PanelData panel = _panels[panelIndex];
            // Overview and device variants use a compact visual grammar: header
            // (name + current state), compact waveform/activity in the scope
            // region, and no piano-roll / pitch-gutter / operator content.
            // Performance lanes keep the full roll grammar in full-width bands.
            if (_layout.Variant is not (VisualizationLayoutVariant.DiagnosticGrid
                or VisualizationLayoutVariant.PerformanceLanes))
            {
                long waveformStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                if (drawSemantic)
                    DrawOverviewDynamicPanel(destination, panel, currentSample);
                DrawEnergyScopeBorder(destination, panel.Index, currentSample);
                if (_performance.Enabled)
                    _performance.WaveformTicks += Stopwatch.GetTimestamp() - waveformStart;
                continue;
            }

            // Dispatch from the immutable semantic descriptor prepared before
            // frame rendering. Scope-only compositions deliberately suppress
            // semantic geometry; their panels have no timeline height.
            if (_layout.HasRoll)
            {
                switch (panel.TrackKind)
                {
                    case VisualizationTrackKind.Pitched:
                        long pitchedGridStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        (double Min, double Max) pitchedRange = GetPitchRange(panel, currentSample);
                        DrawPitchGrid(destination, panel, _cameras[panel.Index], currentSample, false, pitchedRange);
                        if (_performance.Enabled)
                        {
                            long gridTicks = Stopwatch.GetTimestamp() - pitchedGridStart;
                            _performance.PitchGridTicks += gridTicks;
                            _performance.PianoRollTicks += gridTicks;
                        }
                        if (drawSemantic)
                        {
                            long pitchedRibbonStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                            if (panel.Prepared.UsesSsgModes)
                                DrawSsgPanel(destination, panel, currentSample, pitchedRange);
                            else
                                DrawPitchedPanel(destination, panel, currentSample, false, pitchedRange);
                            if (_performance.Enabled)
                            {
                                long ribbonTicks = Stopwatch.GetTimestamp() - pitchedRibbonStart;
                                _performance.RibbonTicks += ribbonTicks;
                                _performance.PianoRollTicks += ribbonTicks;
                            }
                        }
                        break;
                    case VisualizationTrackKind.FmOperatorGroup:
                        long fmGridStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        (double Min, double Max) fmRange = GetPitchRange(panel, currentSample);
                        DrawPitchGrid(destination, panel, _cameras[panel.Index], currentSample, true, fmRange);
                        if (_performance.Enabled)
                        {
                            long gridTicks = Stopwatch.GetTimestamp() - fmGridStart;
                            _performance.PitchGridTicks += gridTicks;
                            _performance.PianoRollTicks += gridTicks;
                        }
                        if (drawSemantic)
                        {
                            long fmRibbonStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                            DrawPitchedPanel(destination, panel, currentSample, true, fmRange);
                            DrawFm3OperatorRibbons(destination, panel, currentSample, fmRange);
                            if (_performance.Enabled)
                            {
                                long ribbonTicks = Stopwatch.GetTimestamp() - fmRibbonStart;
                                _performance.RibbonTicks += ribbonTicks;
                                _performance.PianoRollTicks += ribbonTicks;
                            }
                        }
                        break;
                    case VisualizationTrackKind.WaveTable:
                        long wtGridStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        (double Min, double Max) wavetableRange = GetPitchRange(panel, currentSample);
                        DrawPitchGrid(destination, panel, _cameras[panel.Index], currentSample, false, wavetableRange);
                        if (_performance.Enabled)
                        {
                            long gridTicks = Stopwatch.GetTimestamp() - wtGridStart;
                            _performance.PitchGridTicks += gridTicks;
                            _performance.PianoRollTicks += gridTicks;
                        }
                        if (drawSemantic)
                        {
                            long wtRibbonStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                            DrawPitchedPanel(destination, panel, currentSample, false, wavetableRange);
                            if (_performance.Enabled)
                            {
                                long ribbonTicks = Stopwatch.GetTimestamp() - wtRibbonStart;
                                _performance.RibbonTicks += ribbonTicks;
                                _performance.PianoRollTicks += ribbonTicks;
                            }
                            long wtStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                            DrawWavetablePanel(destination, panel, currentSample);
                            if (_performance.Enabled)
                                _performance.WaveformTicks += Stopwatch.GetTimestamp() - wtStart;
                        }
                        break;
                    case VisualizationTrackKind.Sample:
                        long sampleStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        if (drawSemantic)
                            DrawPcmVoicePanel(destination, panel, currentSample);
                        if (_performance.Enabled)
                            _performance.WaveformTicks += Stopwatch.GetTimestamp() - sampleStart;
                        break;
                    case VisualizationTrackKind.Noise:
                        if (drawSemantic)
                            DrawNoisePanel(destination, panel, currentSample);
                        break;
                    case VisualizationTrackKind.AggregateActivity:
                        if (drawSemantic)
                            DrawAggregatePanel(destination, panel, currentSample);
                        break;
                    case VisualizationTrackKind.Percussion:
                        if (drawSemantic)
                            DrawRhythmPanel(destination, panel, currentSample);
                        break;
                    case VisualizationTrackKind.Generic:
                        if (drawSemantic)
                            DrawGenericActivityPanel(destination, panel, currentSample);
                        break;
                    case VisualizationTrackKind.ParameterActivity:
                    case VisualizationTrackKind.Unsupported:
                        DrawPlaceholderPanel(destination, panel, currentSample);
                        break;
                }

                // Per-channel dynamic state in the panel header is retained
                // for the diagnostic grid.
                long headerStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                DrawDynamicPanelHeader(destination, panel, currentSample);
                if (_performance.Enabled)
                    _performance.TextTicks += Stopwatch.GetTimestamp() - headerStart;
            }
            DrawEnergyScopeBorder(destination, panel.Index, currentSample);
        }

        DrawPresentationTransition(destination, currentSample);
    }

    private void DrawPresentationTransition(Span<byte> destination, long currentSample)
    {
        if (_introSamples <= 0 && _outroSamples <= 0)
            return;

        if (_introSamples > 0)
        {
            long elapsed = currentSample - _timeline.StartSample;
            if (elapsed >= 0 && elapsed < _introSamples)
            {
                OverlayColor black = new(0, 0, 0, TransitionAlpha(elapsed, _introSamples, fadeIn: true));
                FillRect(destination, _layout.TopBarRect, black);
                FillRect(destination, _layout.BottomBarRect, black);
                FillGridContent(destination, black);
            }
        }

        if (_outroSamples > 0)
        {
            long start = _timeline.EndSample - _outroSamples;
            if (currentSample >= start)
            {
                OverlayColor black = new(
                    0,
                    0,
                    0,
                    TransitionAlpha(currentSample - start, _outroSamples, fadeIn: false));
                FillGridContent(destination, black);
            }
        }
    }

    private void FillGridContent(Span<byte> destination, OverlayColor color)
    {
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            FillRect(destination, _layout.GetScopeRect(panelIndex), color);
            FillRect(destination, _layout.GetTimelineRect(panelIndex), color);
        }
    }

    private void DrawTimeGrid(Span<byte> frame, long currentSample)
    {
        if (!_layout.HasRoll || _timeGrid.Length == 0)
            return;

        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        int first;
        if (!(_activeSequentialState?.TryGetTimeGridFirst(_timeGrid, windowStart, out first) ?? false))
            first = LowerBoundTimeGrid(windowStart);
        for (int index = first; index < _timeGrid.Length; index++)
        {
            VisualizationTimeGridLine line = _timeGrid[index];
            if (line.Sample > windowEnd)
                break;
            if (line.Sample < windowStart)
                continue;

            OverlayColor color = line.Kind switch
            {
                VisualizationTimeGridLineKind.Measure => new OverlayColor(120, 132, 164, line.Analytical ? (byte)76 : (byte)126),
                VisualizationTimeGridLineKind.Beat => new OverlayColor(95, 105, 132, line.Analytical ? (byte)54 : (byte)92),
                _ => new OverlayColor(72, 80, 104, line.Analytical ? (byte)32 : (byte)56),
            };
            for (int panelIndex = 0; panelIndex < _layout.PanelCount; panelIndex++)
            {
                OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
                int laneX = timeline.X + Math.Min(_layout.PitchLabelWidth, timeline.Width);
                int laneWidth = Math.Max(1, timeline.Width - Math.Min(_layout.PitchLabelWidth, timeline.Width));
                int x = (int)Math.Round(_layout.SampleToX(
                    line.Sample,
                    currentSample,
                    _timeline.SampleRate,
                    new OverlayRect(laneX, timeline.Y, laneWidth, timeline.Height)));
                if (x >= laneX && x < timeline.Right)
                    DrawVerticalLine(frame, x, timeline.Y, timeline.Bottom - 1, color);
            }
        }
    }

    private int LowerBoundTimeGrid(long sample)
    {
        int low = 0;
        int high = _timeGrid.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (_timeGrid[middle].Sample < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static byte TransitionAlpha(long elapsed, long duration, bool fadeIn)
    {
        double t = Math.Clamp(elapsed / (double)duration, 0, 1);
        double eased = t * t * (3 - 2 * t);
        double alpha = fadeIn ? 255 * (1 - eased) : 255 * eased;
        return (byte)Math.Clamp(Math.Round(alpha), 0, 255);
    }

    private void DrawAnalysisHud(Span<byte> frame, long currentSample)
    {
        if (ReferenceEquals(_analysisOverlay, AnalysisOverlayScene.Empty))
            return;

        OverlayRect bar = _layout.TopBarRect;
        string section = FindCurrentSection(currentSample);
        string harmony = FindCurrentHarmony(currentSample);
        string key = _analysisOverlay.KeyLabel;
        string relationship = _analysisOverlay.Relationships.Length > 0
            ? _analysisOverlay.Relationships[0].Label
            : null;
        if (section is null && harmony is null && string.IsNullOrEmpty(key) && relationship is null)
            return;

        string clock = FormatClock(currentSample);
        int cursor = bar.Right - _layout.SafeHorizontalMargin - BitmapFont.MeasureText(clock, 2) - 10;
        int scale = Height >= 720 ? 2 : 1;
        int leftLimit = bar.X + _layout.SafeHorizontalMargin;
        cursor = DrawAnalysisLabel(frame, harmony, BrightText, scale, cursor, leftLimit);
        cursor = DrawAnalysisLabel(frame, section, MutedText, scale, cursor, leftLimit);
        cursor = DrawAnalysisLabel(frame, relationship, MutedText, scale, cursor, leftLimit);
        DrawAnalysisLabel(frame, key, MutedText, scale, cursor, leftLimit);
    }

    private void DrawAnalysisHarmonyStrip(Span<byte> frame, long currentSample)
    {
        if (ReferenceEquals(_analysisOverlay, AnalysisOverlayScene.Empty)
            || _analysisOverlay.Harmony.Length == 0)
            return;

        OverlayRect strip = _layout.HarmonyStripRect;
        FillRect(frame, strip, new OverlayColor(10, 12, 19, 235));
        DrawHorizontalLine(frame, strip.X, strip.Right - 1, strip.Y, new OverlayColor(76, 82, 103, 190));

        long length = _timeline.EndSample - _timeline.StartSample;
        if (length <= 0)
            return;
        int scale = Height >= 720 ? 2 : 1;
        int textY = strip.Y + Math.Max(1, (strip.Height - 7 * scale) / 2);
        int lastRight = strip.X;
        foreach (AnalysisHarmonyMarker marker in _analysisOverlay.Harmony)
        {
            int left = strip.X + (int)Math.Round(
                Math.Clamp((marker.StartSample - _timeline.StartSample) / (double)length, 0, 1) * strip.Width);
            int right = strip.X + (int)Math.Round(
                Math.Clamp((marker.EndSample - _timeline.StartSample) / (double)length, 0, 1) * strip.Width);
            left = Math.Clamp(left, strip.X, strip.Right);
            right = Math.Clamp(right, left, strip.Right);
            if (right <= left || right <= lastRight)
                continue;

            bool active = marker.StartSample <= currentSample && currentSample < marker.EndSample;
            bool tentative = marker.Confidence < AnalysisConfidencePolicy.KeyStrongMinimum;
            if (active)
                FillRect(frame, new OverlayRect(left, strip.Y + 1, right - left, Math.Max(1, strip.Height - 1)),
                    new OverlayColor(54, 61, 91, tentative ? (byte)120 : (byte)220));
            int available = right - left - 4;
            int textWidth = BitmapFont.MeasureText(marker.Label, scale);
            if (textWidth <= available && textWidth > 0)
            {
                int x = left + Math.Max(2, (right - left - textWidth) / 2);
                DrawText(frame, x, textY, marker.Label,
                    active
                        ? tentative ? new OverlayColor(222, 226, 238, 170) : BrightText
                        : tentative ? new OverlayColor(139, 146, 167, 105) : MutedText,
                    scale, Math.Min(right - 2, strip.Right - 1));
            }
            lastRight = right;
        }
    }

    private int DrawAnalysisLabel(
        Span<byte> frame,
        string label,
        OverlayColor color,
        int scale,
        int right,
        int leftLimit)
    {
        if (string.IsNullOrEmpty(label))
            return right;
        int width = BitmapFont.MeasureText(label, scale);
        int x = right - width;
        if (x < leftLimit)
            return right;
        DrawText(frame, x, _layout.TopBarRect.Y + Math.Max(2, (_layout.TopBarRect.Height - 7 * scale) / 2), label, color, scale, right);
        return x - 10;
    }

    private void DrawAnalysisProgressMarkers(Span<byte> frame, long currentSample)
    {
        if (ReferenceEquals(_analysisOverlay, AnalysisOverlayScene.Empty))
            return;

        OverlayRect bar = _layout.BottomBarRect;
        foreach (AnalysisSectionMarker marker in _analysisOverlay.Sections)
        {
            DrawAnalysisMarker(frame, bar, marker.Sample, AnalysisMarkerKind.Section);
            DrawAnalysisLaneMarker(frame, marker.Sample, currentSample);
        }
        foreach (AnalysisProgressMarker marker in _analysisOverlay.PhraseMarkers)
            DrawAnalysisMarker(frame, bar, marker.Sample, marker.Kind);
        OverlayRect markerBar = _layout.AnalysisMarkerRect;
        foreach (AnalysisMotifMarker marker in _analysisOverlay.MotifMarkers)
        {
            DrawAnalysisMarker(frame, markerBar, marker.StartSample, AnalysisMarkerKind.Motif);
            DrawAnalysisMarkerLabel(frame, markerBar, marker.StartSample, marker.MotifId);
        }
    }

    private void DrawAnalysisLaneMarker(Span<byte> frame, long sample, long currentSample)
    {
        long length = _timeline.EndSample - _timeline.StartSample;
        if (length <= 0)
            return;

        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        for (int panelIndex = 0; panelIndex < _layout.PanelCount; panelIndex++)
        {
            OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
            int labelWidth = Math.Min(_layout.PitchLabelWidth, timeline.Width);
            OverlayRect lane = new(
                timeline.X + labelWidth,
                timeline.Y,
                Math.Max(1, timeline.Width - labelWidth),
                timeline.Height);
            if (sample < windowStart || sample > windowEnd)
                continue;
            int x = (int)Math.Round(_layout.SampleToX(sample, currentSample, _timeline.SampleRate, lane));
            if (x >= lane.X && x < lane.Right)
                DrawVerticalLine(frame, x, lane.Y, lane.Bottom - 1,
                    new OverlayColor(255, 205, 112, 150));
        }
    }

    private void DrawAnalysisMarkerLabel(Span<byte> frame, OverlayRect bar, long sample, string label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return;
        long length = _timeline.EndSample - _timeline.StartSample;
        if (length <= 0)
            return;
        int scale = Height >= 720 ? 2 : 1;
        int x = bar.X + (int)Math.Round(Math.Clamp(
            (sample - _timeline.StartSample) / (double)length, 0, 1) * Math.Max(0, bar.Width - 1));
        int width = BitmapFont.MeasureText(label, scale);
        int left = Math.Clamp(x - width / 2, bar.X + 1, Math.Max(bar.X + 1, bar.Right - width - 1));
        DrawText(frame, left, bar.Y + 1, label, new OverlayColor(186, 125, 255, 220), scale, bar.Right - 1);
    }

    private void DrawAnalysisMarker(
        Span<byte> frame,
        OverlayRect bar,
        long sample,
        AnalysisMarkerKind kind)
    {
        long length = _timeline.EndSample - _timeline.StartSample;
        if (length <= 0)
            return;
        double fraction = (sample - _timeline.StartSample) / (double)length;
        int x = bar.X + (int)Math.Round(Math.Clamp(fraction, 0, 1) * Math.Max(0, bar.Width - 1));
        int bottom = kind switch
        {
            AnalysisMarkerKind.Section => Math.Min(bar.Bottom - 1, bar.Y + 14),
            AnalysisMarkerKind.Phrase => Math.Min(bar.Bottom - 1, bar.Y + 10),
            _ => Math.Min(bar.Bottom - 1, bar.Y + 8),
        };
        OverlayColor color = kind switch
        {
            AnalysisMarkerKind.Section => new OverlayColor(255, 205, 112, 230),
            AnalysisMarkerKind.Phrase => new OverlayColor(150, 170, 220, 190),
            _ => new OverlayColor(186, 125, 255, 210),
        };
        DrawVerticalLine(frame, x, bar.Y + 4, bottom, color);
    }

    private string FindCurrentSection(long currentSample)
    {
        AnalysisSectionMarker[] markers = _analysisOverlay.Sections;
        int index = FindLastAtOrBefore(markers, currentSample, marker => marker.Sample);
        return index >= 0 ? markers[index].Label : null;
    }

    private string FindCurrentHarmony(long currentSample)
    {
        AnalysisHarmonyMarker[] markers = _analysisOverlay.Harmony;
        int low = 0;
        int high = markers.Length - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (markers[middle].StartSample <= currentSample)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return candidate >= 0
            && currentSample < markers[candidate].EndSample
            ? markers[candidate].Label
            : null;
    }

    private static int FindLastAtOrBefore<T>(
        IReadOnlyList<T> values,
        long sample,
        Func<T, long> getSample)
    {
        int low = 0;
        int high = values.Count - 1;
        int candidate = -1;
        while (low <= high)
        {
            int middle = low + (high - low) / 2;
            if (getSample(values[middle]) <= sample)
            {
                candidate = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }
        return candidate;
    }

    private OverlayRect[] BuildDynamicRestoreRects(bool scopeFramePresent)
    {
        // The presentation fade temporarily covers both metadata bars. They
        // must be restored every frame so a previous fade cannot persist in
        // the sequential buffer. The scope itself is also dynamic: Corrscope
        // replaces its pixels and DrawEnergyScopeBorder draws on its perimeter.
        // When the scope placement replaces the whole body (opaque source at
        // full opacity), that body is already overwritten before dynamic
        // drawing, so only its static gutter needs restoring. With an alpha
        // blend the body must be restored from the static layer, so the
        // no-scope plan set is used instead. With no scope frame, restore the
        // complete body from the static layer.
        // A few clipped contact/grid primitives intentionally terminate on a
        // region edge. Restore the one-pixel static seam as well so a
        // sequential session cannot retain an edge pixel that a direct frame
        // starts from freshly copied static chrome. Shared publishing
        // compositions have no dynamic per-panel headers or scope rows, so
        // restoring only their timeline regions avoids copying static pixels
        // on every frame.
        var rects = new List<OverlayRect>(2 + _panels.Length * 4);
        rects.Add(_layout.TopBarRect);
        rects.Add(_layout.BottomBarRect);
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            rects.Add(_layout.GetHeaderRect(panelIndex));
            OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
            bool integratedScope =
                _layout.UsesIntegratedRoll;
            if (scopeFramePresent && integratedScope)
            {
                // Corrscope replaces the complete body to the right of the
                // pitch gutter before dynamic semantic drawing. Restore only
                // the gutter; copying the scope body from the static layer is
                // redundant memory traffic in this path.
                int gutter = Math.Min(_layout.PitchLabelWidth, timeline.Width);
                if (gutter > 0)
                    rects.Add(new OverlayRect(
                        timeline.X, timeline.Y, gutter, timeline.Height));
            }
            else
            {
                rects.Add(timeline);
            }
            if (!integratedScope)
                rects.Add(_layout.GetScopeRect(panelIndex));
        }

        // Restore runs before every dynamic draw, so copying a superset of a
        // region is always safe. Merge vertically adjacent rects that share
        // the same X/Width into one copy and drop rects fully contained by
        // another (the header covers the one-pixel panel seam). This cuts the
        // number of CopyRect calls per frame without copying extra pixels.
        var merged = new List<OverlayRect>(rects.Count);
        foreach (OverlayRect rect in rects)
        {
            bool covered = false;
            for (int i = 0; i < merged.Count; i++)
            {
                OverlayRect existing = merged[i];
                if (existing.X == rect.X && existing.Width == rect.Width
                    && existing.Y <= rect.Y && existing.Bottom >= rect.Bottom)
                {
                    covered = true;
                    break;
                }
                if (rect.X == existing.X && rect.Width == existing.Width
                    && rect.Y <= existing.Y && rect.Bottom >= existing.Bottom)
                {
                    merged[i] = rect;
                    covered = true;
                    break;
                }
            }
            if (covered)
                continue;
            merged.Add(rect);
        }

        bool mergedAny = true;
        while (mergedAny)
        {
            mergedAny = false;
            for (int i = 0; i < merged.Count; i++)
            {
                OverlayRect a = merged[i];
                for (int j = i + 1; j < merged.Count; j++)
                {
                    OverlayRect b = merged[j];
                    if (a.X != b.X || a.Width != b.Width)
                        continue;
                    if (a.Bottom == b.Y || b.Bottom == a.Y)
                    {
                        int top = Math.Min(a.Y, b.Y);
                        int bottom = Math.Max(a.Bottom, b.Bottom);
                        merged[i] = new OverlayRect(a.X, top, a.Width, bottom - top);
                        merged.RemoveAt(j);
                        mergedAny = true;
                        break;
                    }
                }
                if (mergedAny)
                    break;
            }
        }
        return merged.ToArray();
    }

    private RectCopyPlan[] BuildRectCopyPlans(OverlayRect[] rects)
    {
        var plans = new List<RectCopyPlan>(rects.Length);
        foreach (OverlayRect rect in rects)
        {
            int left = Math.Clamp(rect.X, 0, Width);
            int right = Math.Clamp(rect.Right, 0, Width);
            int top = Math.Clamp(rect.Y, 0, Height);
            int bottom = Math.Clamp(rect.Bottom, 0, Height);
            int rowBytes = (right - left) * 4;
            if (rowBytes <= 0 || bottom <= top)
                continue;
            plans.Add(new RectCopyPlan(
                (top * Width + left) * 4,
                rowBytes,
                bottom - top));
        }
        return plans.ToArray();
    }

    private ScopeCopyPlan[] BuildScopeCopyPlans()
    {
        int sourceWidth = _layout.CorrscopeGridWidth;
        int sourceStride = sourceWidth * 4;
        int scopeHeight = _layout.ScopeHeight;
        var plans = new List<ScopeCopyPlan>(_panels.Length);
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            int row = panelIndex / _layout.ColumnCount;
            int column = panelIndex % _layout.ColumnCount;
            OverlayRect scope = _layout.GetScopeRect(panelIndex);
            int copyWidth = Math.Min(sourceWidth / _layout.ColumnCount, scope.Width);
            if (copyWidth <= 0 || scopeHeight <= 0)
                continue;

            int srcY = row * scopeHeight;
            int srcX = _layout.Variant == VisualizationLayoutVariant.DiagnosticGrid
                ? scope.X
                : column * copyWidth;
            plans.Add(new ScopeCopyPlan(
                srcY * sourceStride + srcX * 4,
                (scope.Y * Width + scope.X) * 4,
                copyWidth * 4,
                scopeHeight));
        }
        return plans.ToArray();
    }

    public byte[] RenderFrame(long frameIndex)
    {
        var bytes = new byte[FrameByteCount];
        RenderFrame(frameIndex, bytes);
        return bytes;
    }

    /// <summary>
    /// Writes the pre-rendered static layer (panel chrome, metadata bars,
    /// title/credits, transparent scope holes) as raw RGBA to
    /// <paramref name="destination"/>. Used to export a single still image that
    /// FFmpeg loops behind the dynamic overlay.
    /// </summary>
    public void WriteStaticFrame(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(_staticFrame, 0, _staticFrame.Length);
    }

    internal void WriteStaticFrame(Span<byte> destination)
    {
        if (destination.Length < _staticFrame.Length)
            throw new ArgumentException($"Destination requires at least {_staticFrame.Length} bytes.", nameof(destination));
        _staticFrame.AsSpan().CopyTo(destination);
    }

    /// <summary>
    /// Copies each scope cell from the Corrscope grid strip into its panel and
    /// blends it over the painted panel body. The scope frame's alpha channel
    /// is a per-pixel mask (alpha 0 = transparent background, 255 = waveform
    /// line); the effective pixel opacity is
    /// <c>a = (srcAlpha / 255) * ScopeOpacity</c> and the result is baked into
    /// RGB with destination alpha forced to 255, because the encode path
    /// (RGBA -> yuv420p) drops alpha — a real per-pixel alpha channel cannot
    /// survive the encode. Opaque sources at full opacity take the raw-copy
    /// fast path, byte-identical to the pre-blend placement.
    /// DiagnosticGrid reads each panel cell offset by its pitch gutter so the
    /// waveform's playhead aligns with the shared body; overview frames use the
    /// plain per-column cell. Each grid row holds one cell per column; the
    /// cell for panel <c>row*ColumnCount + column</c> starts at
    /// <c>column * PanelWidth</c> within row <c>row</c>.
    /// </summary>
    private void PlaceScopeRows(
        ReadOnlySpan<byte> scopeGrid, Span<byte> destination, bool scopeFramesAreOpaque = false)
    {
        int sourceStride = _layout.CorrscopeGridWidth * 4;
        int destinationStride = Width * 4;
        // Integer 16.16 fixed-point scale of ScopeOpacity (65536 == 1.0),
        // computed once per placement call.
        int alphaScale = (int)Math.Round(_options.ScopeOpacity * 65536.0);
        bool fastPath = scopeFramesAreOpaque && alphaScale >= 65536;
        foreach (ScopeCopyPlan plan in _scopeCopyPlans)
        {
            int src = plan.SourceOffset;
            int dst = plan.DestinationOffset;
            for (int y = 0; y < plan.Rows; y++)
            {
                if (_performance.Enabled)
                {
                    _performance.ScopeCopies++;
                    _performance.CopiedBytes += plan.RowBytes;
                }
                if (fastPath)
                {
                    scopeGrid.Slice(src, plan.RowBytes)
                        .CopyTo(destination.Slice(dst, plan.RowBytes));
                }
                else
                {
                    BlendScopeRow(
                        scopeGrid.Slice(src, plan.RowBytes),
                        destination.Slice(dst, plan.RowBytes),
                        alphaScale);
                }
                src += sourceStride;
                dst += destinationStride;
            }
        }
    }

    /// <summary>
    /// Per-pixel alpha blend of one scope row over the panel body:
    /// <c>dst.RGB = src.RGB * a + dst.RGB * (1 - a)</c> with
    /// <c>a = (srcAlpha / 255) * ScopeOpacity</c>, then <c>dst.A = 255</c>.
    /// Integer 16.16 fixed point keeps the hot path allocation-free; the
    /// rounding matches the acceptance criteria (src * 0.5 + dst * 0.5 at
    /// opacity 0.5 within one LSB).
    /// </summary>
    private static void BlendScopeRow(
        ReadOnlySpan<byte> source, Span<byte> destination, int alphaScale)
    {
        // Row-level fast paths: waveform masks are overwhelmingly either
        // fully transparent (background) or fully opaque (line pixels).
        // Detecting those rows collapses them to a fill or a copy without
        // per-pixel branching.
        byte firstA = source[3];
        bool uniformAlpha = true;
        for (int i = 3; i < source.Length; i += 4)
        {
            if (source[i] != firstA) { uniformAlpha = false; break; }
        }
        if (uniformAlpha && firstA == 0 && alphaScale <= 65536)
        {
            // Fully transparent row: destination RGB untouched, alpha forced
            // opaque. Write the alpha plane directly.
            for (int offset = 3; offset < destination.Length; offset += 4)
                destination[offset] = 255;
            return;
        }
        if (uniformAlpha && firstA >= 255)
        {
            if (alphaScale >= 65536)
            {
                source.CopyTo(destination);
                for (int offset = 3; offset < destination.Length; offset += 4)
                    destination[offset] = 255;
                return;
            }
            BlendScopeRowCore(source, destination, alphaScale);
            return;
        }
        if (uniformAlpha)
        {
            // Uniform partial alpha: blend terms constant per channel.
            int alpha = (firstA * alphaScale + 127) / 255;
            if (alpha >= 65536) { source.CopyTo(destination); return; }
            if (alpha <= 0)
            {
                for (int offset = 3; offset < destination.Length; offset += 4)
                    destination[offset] = 255;
                return;
            }
            for (int offset = 0; offset < source.Length; offset += 4)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int diff = source[offset + channel] - destination[offset + channel];
                    destination[offset + channel] = (byte)(
                        destination[offset + channel] + ((diff * alpha + 32768) >> 16));
                }
                destination[offset + 3] = 255;
            }
            return;
        }
        BlendScopeRowCore(source, destination, alphaScale);
    }

    /// <summary>Per-pixel fallback; identical to the original implementation.</summary>
    private static void BlendScopeRowCore(
        ReadOnlySpan<byte> source, Span<byte> destination, int alphaScale)
    {
        for (int offset = 0; offset < source.Length; offset += 4)
        {
            int alpha = (source[offset + 3] * alphaScale + 127) / 255;
            if (alpha >= 65536)
            {
                destination[offset] = source[offset];
                destination[offset + 1] = source[offset + 1];
                destination[offset + 2] = source[offset + 2];
                destination[offset + 3] = 255;
                continue;
            }
            if (alpha > 0)
            {
                for (int channel = 0; channel < 3; channel++)
                {
                    int diff = source[offset + channel] - destination[offset + channel];
                    destination[offset + channel] = (byte)(
                        destination[offset + channel] + ((diff * alpha + 32768) >> 16));
                }
            }
            destination[offset + 3] = 255;
        }
    }

    /// <summary>
    /// Builds a per-panel energy lookup indexed by panel index.
    /// </summary>
    private ChannelEnergyEnvelope[] BuildEnergyLookup(ChannelEnergyEnvelope[] energy)
    {
        var lookup = new ChannelEnergyEnvelope[_panels.Length];
        if (energy == null || energy.Length == 0)
            return lookup;

        var byChannel = new Dictionary<string, ChannelEnergyEnvelope>(energy.Length, StringComparer.Ordinal);
        foreach (var env in energy)
            byChannel[env.ChannelId] = env;

        for (int i = 0; i < _panels.Length; i++)
        {
            if (byChannel.TryGetValue(_panels[i].Id, out var env))
                lookup[i] = env;
        }
        return lookup;
    }

    private static bool[] BuildAudioEnergyFlags(ChannelEnergyEnvelope[] energy)
    {
        var flags = new bool[energy.Length];
        for (int panelIndex = 0; panelIndex < energy.Length; panelIndex++)
        {
            float[] activity = energy[panelIndex]?.FrameActivity;
            if (activity is null)
                continue;
            for (int index = 0; index < activity.Length; index++)
            {
                if (activity[index] > 0.02f)
                {
                    flags[panelIndex] = true;
                    break;
                }
            }
        }
        return flags;
    }

    private PanelData[] BuildPanels()
    {
        var panels = new PanelData[_scene.Panels.Length];
        for (int index = 0; index < panels.Length; index++)
        {
            PreparedPanel prepared = _scene.Panels[index];
            panels[index] = new PanelData
            {
                Index = index,
                Id = prepared.Id,
                Label = prepared.Label,
                TrackKind = prepared.Track.Kind,
                Prepared = prepared,
                SampleRowLabels = BuildSampleRowLabels(prepared),
                SampleRowByPlaybackIndex = BuildSampleRowByPlaybackIndex(prepared),
            };
        }
        return panels;
    }

    /// <summary>Precomputes one gutter label per distinct sample identity (row order).</summary>
    private static string[] BuildSampleRowLabels(PreparedPanel prepared)
        => BuildSampleRowTable(prepared).Labels;

    private readonly record struct SampleRowTable(string[] Labels, Dictionary<string, int> RowByLabel);

    /// <summary>
    /// One preparation pass over the playback events: hash-based uniqueness,
    /// one sort, and a label → row dictionary. O(S log S) instead of the
    /// previous quadratic Contains/IndexOf scans.
    /// </summary>
    private static SampleRowTable BuildSampleRowTable(PreparedPanel prepared)
    {
        var distinct = new HashSet<string>(StringComparer.Ordinal);
        foreach (SamplePlaybackEvent e in prepared.SamplePlayback)
        {
            if (string.IsNullOrEmpty(e.SampleId))
                continue;
            string label = ShortAssetLabel(
                prepared.SamplesById.TryGetValue(e.SampleId, out SampleDefinition s) ? s.DisplayName : null,
                e.SampleId);
            distinct.Add(label);
        }
        string[] labels = distinct.ToArray();
        Array.Sort(labels, StringComparer.Ordinal);
        var rowByLabel = new Dictionary<string, int>(labels.Length, StringComparer.Ordinal);
        for (int i = 0; i < labels.Length; i++)
            rowByLabel[labels[i]] = i;
        return new SampleRowTable(labels, rowByLabel);
    }

    /// <summary>Precomputes the row index per playback event (parallel to <see cref="PreparedPanel.SamplePlayback"/>).</summary>
    private static int[] BuildSampleRowByPlaybackIndex(PreparedPanel prepared)
    {
        SampleRowTable table = BuildSampleRowTable(prepared);
        var playback = prepared.SamplePlayback;
        int[] rows = new int[playback.Length];
        for (int i = 0; i < playback.Length; i++)
        {
            SamplePlaybackEvent e = playback[i];
            int row = 0;
            if (!string.IsNullOrEmpty(e.SampleId))
            {
                string label = ShortAssetLabel(
                    prepared.SamplesById.TryGetValue(e.SampleId, out SampleDefinition s) ? s.DisplayName : null,
                    e.SampleId);
                if (!table.RowByLabel.TryGetValue(label, out row))
                    row = 0;
            }
            rows[i] = row;
        }
        return rows;
    }

    private PitchCamera[] BuildCameras()
    {
        var cameras = new PitchCamera[_panels.Length];
        for (int index = 0; index < _panels.Length; index++)
        {
            PanelData panel = _panels[index];
            bool pitchTrack = panel.TrackKind is VisualizationTrackKind.Pitched
                or VisualizationTrackKind.FmOperatorGroup
                or VisualizationTrackKind.WaveTable;
            bool pitchedSample = panel.TrackKind == VisualizationTrackKind.Sample
                && panel.Prepared.MainNotes.Length > 0;
            if (pitchTrack || pitchedSample)
            {
                // §11.1: FM3 operator mode may use up to 30 semitones so
                // operator pitches remain visible; other panels cap at 24.
                bool extended = panel.TrackKind == VisualizationTrackKind.FmOperatorGroup;
                int laneHeight = _layout.GetPitchedLaneRect(index, extended).Height;
                cameras[index] = new PitchCamera(
                    panel.Prepared.CameraNotes,
                    laneHeight,
                    _timeline.SampleRate,
                    _layout.PastSeconds,
                    _layout.FutureSeconds,
                    _timeline.StartSample,
                    _timeline.EndSample,
                    FpsNumerator,
                    FpsDenominator,
                    allowExtendedSpan: extended,
                    rollZoom: _layout.RollZoom);
            }
            else
            {
                cameras[index] = null;
            }
        }
        return cameras;
    }


    private void BuildStaticFrame(Span<byte> frame, bool drawFallbackText)
    {
        Fill(frame, CanvasBackground);

        for (int index = 0; index < _panels.Length; index++)
        {
            OverlayRect panel = _layout.GetPanelRect(index);
            OverlayRect header = _layout.GetHeaderRect(index);
            OverlayRect scope = _layout.GetScopeRect(index);
            OverlayRect timeline = _layout.GetTimelineRect(index);

            // Compact overview/device panels share one static grammar: fill the
            // panel, open the scope region, draw the accent + channel name, and
            // skip all the full-grid chrome (pitch gutter, FM3 ribbons, rhythm
            // rows, PCM lanes) that cannot fit. Performance lanes keep the
            // full-grid roll grammar but drop the boxed chrome.
            if (_layout.Variant is not (VisualizationLayoutVariant.DiagnosticGrid
                or VisualizationLayoutVariant.PerformanceLanes))
            {
                DrawOverviewStaticPanel(frame, _panels[index]);
                continue;
            }

            bool lanes = _layout.Variant == VisualizationLayoutVariant.PerformanceLanes;
            if (!lanes)
            {
                FillRect(frame, panel, HeaderBackground);
                FillRect(frame, timeline, TimelineBackground);
                StrokeRect(frame, panel, Border, 1);
            }
            else
            {
                FillRect(frame, timeline, TimelineBackground);
            }
            // The scope region stays a transparent hole for the Corrscope
            // waveform, including DiagnosticGrid's integrated body where the
            // scope is the signal portion of the roll. Notes and lane chrome
            // drawn afterward remain visible over the hole.
            ClearRect(frame, scope);

            if (!lanes)
            {
                OverlayColor accent = _panelAccents[index];
                FillRect(frame, new OverlayRect(header.X, header.Y, 4, header.Height), accent);
                if (header.Height > 0)
                {
                    // Channel name is clipped to its dedicated header slot so it
                    // can never collide with the live state or patch columns.
                    OverlayRect nameSlot = _layout.HeaderSlots(index).Name;
                    DrawText(
                        frame,
                        nameSlot.X,
                        nameSlot.Y + Math.Max(2, (nameSlot.Height - 14) / 2),
                        Ellipsize(_panels[index].Label, 2, Math.Max(0, nameSlot.Width)),
                        PrimaryText,
                        2,
                        nameSlot.Right);
                }
            }
            else
            {
                // Lane label: compact scale-1 caption at the top of the pitch
                // gutter, left-aligned so right-aligned pitch/rhythm labels in
                // the same column stay readable.
                OverlayRect nameSlot = _layout.HeaderSlots(index).Name;
                if (nameSlot.Width > 0)
                {
                    DrawText(
                        frame,
                        nameSlot.X,
                        nameSlot.Y,
                        Ellipsize(_panels[index].Label, 1, nameSlot.Width),
                        PrimaryText,
                        1,
                        nameSlot.Right);
                }
            }

            switch (_panels[index].TrackKind)
            {
                case VisualizationTrackKind.Pitched:
                case VisualizationTrackKind.FmOperatorGroup:
                case VisualizationTrackKind.WaveTable:
                {
                    // Pitch label column background (chrome). The grid lines and
                    // "C3" labels are dynamic — they depend on the visible pitch
                    // range — so only the static background lives here.
                    bool reserveFm3Ribbons = _panels[index].TrackKind == VisualizationTrackKind.FmOperatorGroup;
                    OverlayRect lane = _layout.GetPitchedLaneRect(index, reserveFm3Ribbons);
                    FillRect(frame, new OverlayRect(timeline.X, lane.Y, _layout.PitchLabelWidth, lane.Height), HeaderBackground);

                    if (_panels[index].TrackKind == VisualizationTrackKind.FmOperatorGroup)
                        DrawStaticFm3Ribbons(frame, index);
                    break;
                }
                case VisualizationTrackKind.Sample:
                    DrawStaticPcmLanes(frame, _panels[index], timeline);
                    if (!_panels[index].Prepared.HasTrackEvents)
                        DrawText(frame, timeline.X + 10, timeline.Y + Math.Max(2, timeline.Height / 2 - 4),
                            "SILENT", MutedText, 1, timeline.Right - 8);
                    break;
                case VisualizationTrackKind.Noise:
                case VisualizationTrackKind.AggregateActivity:
                    DrawStaticEventLane(frame, timeline);
                    break;
                case VisualizationTrackKind.Percussion:
                    DrawStaticRhythmRows(frame, index);
                    break;
                case VisualizationTrackKind.Generic:
                    DrawStaticEventLane(frame, timeline);
                    if (!_panels[index].Prepared.HasTrackEvents)
                        DrawText(frame, timeline.X + 10, timeline.Y + Math.Max(2, timeline.Height / 2 - 4),
                            "ACTIVITY", MutedText, 1, timeline.Right - 8);
                    break;
                case VisualizationTrackKind.ParameterActivity:
                case VisualizationTrackKind.Unsupported:
                    DrawStaticPcmLanes(frame, _panels[index], timeline);
                    if (!_panels[index].Prepared.HasTrackEvents)
                    {
                        string stateLabel = HasEnergyEnvelope(index)
                            ? HasAudioEnergy(index)
                                ? "AUDIO / EVENTS UNKNOWN"
                                : "SILENT"
                            : "NO DATA";
                        DrawText(frame, timeline.X + 10, timeline.Y + Math.Max(2, timeline.Height / 2 - 4), stateLabel, MutedText, 1, timeline.Right - 8);
                    }
                    break;
            }
        }

        // Performance lanes: thin separators between adjacent bands instead of
        // boxed panel borders, so the eye reads one shared time axis.
        if (_layout.Variant == VisualizationLayoutVariant.PerformanceLanes)
        {
            for (int index = 1; index < _panels.Length; index++)
            {
                int separatorY = _layout.GetPanelRect(index).Y;
                DrawHorizontalLine(frame, 0, Width - 1, separatorY - 1, Border);
            }
        }

        // Top and bottom metadata bars (chrome). The clock and progress bar are
        // dynamic and drawn per frame; the title/subtitle/credits are static.
        OverlayRect topBar = _layout.TopBarRect;
        OverlayRect bottomBar = _layout.BottomBarRect;
        FillRect(frame, topBar, HeaderBackground);
        FillRect(frame, bottomBar, HeaderBackground);

        if (drawFallbackText)
        {
            // Bitmap-font title/subtitle/credits — the fallback when no CJK
            // font is available. When a font IS available, the unicode renderer
            // overwrites these regions with anti-aliased glyphs.
            // The clock string is constant-width ("MM:SS / MM:SS"), so the
            // right edge of the clock — and therefore the title limit — is fixed.
            // The clock is reserved first; the title is trimmed into whatever
            // remains so the two can never overlap.
            int fullClockWidth = _fullFallbackClockWidth;
            int titleMaxX = topBar.Right - _layout.SafeHorizontalMargin - fullClockWidth - 40;

            if (titleMaxX > topBar.X + _layout.SafeHorizontalMargin)
            {
                // Title (scale 3, 21px tall) and clock share the bar's vertical
                // centre so they read as one baseline-anchored metadata line.
                int titleCenterY = topBar.Y + Math.Max(2, (topBar.Height - 21) / 2);
                string title = Ellipsize(_presentation.Title, 3, titleMaxX - (topBar.X + _layout.SafeHorizontalMargin));
                DrawText(frame, topBar.X + _layout.SafeHorizontalMargin, titleCenterY, title, PrimaryText, 3, titleMaxX);

                if (!string.IsNullOrEmpty(_presentation.Subtitle))
                {
                    // Subtitle rides one glyph-height line below the title.
                    int subtitleCenterY = topBar.Y + Math.Max(2, (topBar.Height - 21) / 2) + 24;
                    string subtitle = Ellipsize(_presentation.Subtitle, 2, titleMaxX - (topBar.X + _layout.SafeHorizontalMargin));
                    DrawText(frame, topBar.X + _layout.SafeHorizontalMargin, subtitleCenterY, subtitle, SecondaryText, 2, titleMaxX);
                }
            }

            if (!string.IsNullOrEmpty(_presentation.Credits))
            {
                const int progressHeight = 4;
                int creditsY = bottomBar.Y + progressHeight
                    + Math.Max(2, (bottomBar.Height - progressHeight - 14) / 2);
                string credits = Ellipsize(_presentation.Credits, 2, bottomBar.Width - 2 * _layout.SafeHorizontalMargin);
                DrawText(frame, bottomBar.X + _layout.SafeHorizontalMargin, creditsY, credits, MutedText, 2, bottomBar.Right - _layout.SafeHorizontalMargin);
            }
        }
    }

    private void DrawPitchGrid(
        Span<byte> frame,
        PanelData panel,
        PitchCamera camera,
        long currentSample,
        bool reserveFm3OperatorRibbons,
        (double Min, double Max)? sharedRange = null)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, reserveFm3OperatorRibbons);
        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);

        if (camera == null)
        {
            DrawPitchGridRange(frame, lane, timeline, 48, 72, panel.Index);
            return;
        }

        var (minMidi, maxMidi) = sharedRange ?? camera.GetPreciseRange(currentSample);
        DrawPitchGridRange(frame, lane, timeline, minMidi, maxMidi, panel.Index);
    }

    /// <summary>
    /// Draws a pitch label right-aligned inside the pitch gutter, keeping
    /// <see cref="OverlayLayout.PitchLabelInsetLeft"/> clear of the panel and
    /// <see cref="OverlayLayout.PitchLabelInsetRight"/> clear of the lane grid
    /// line.
    /// </summary>
    private void DrawPitchLabelRightAligned(
        Span<byte> frame,
        OverlayRect timeline,
        OverlayRect lane,
        string label,
        int labelY)
    {
        int gutter = _layout.PitchLabelWidth;
        int minX = timeline.X + _layout.PitchLabelInsetLeft;
        int maxX = timeline.X + gutter - _layout.PitchLabelInsetRight;
        int labelWidth = BitmapFont.MeasureText(label, 1);
        int x = maxX - labelWidth;
        if (x < minX)
            x = minX;
        DrawText(frame, x, labelY, label, TertiaryText, 1, maxX);
    }

    private void DrawPitchGridRange(Span<byte> frame, OverlayRect lane, OverlayRect timeline, double minMidi, double maxMidi, int panelIndex = -1)
    {
        if (lane.Width <= 0 || lane.Height <= 0)
            return;
        bool laneCached = false;
        bool canCacheLane = panelIndex >= 0 && panelIndex < _laneGridCache.Length && !_layout.UsesIntegratedRoll;
        int laneRowBytes = lane.Width * 4;
        if (canCacheLane)
        {
            byte[] cached = _laneGridCache[panelIndex];
            if (cached != null && cached.Length == lane.Width * lane.Height * 4
                && _laneGridMinMidi[panelIndex] == minMidi && _laneGridMaxMidi[panelIndex] == maxMidi)
            {
                for (int y = 0; y < lane.Height; y++)
                {
                    int srcOffset = y * laneRowBytes;
                    int dstOffset = ((lane.Y + y) * Width + lane.X) * 4;
                    cached.AsSpan(srcOffset, laneRowBytes).CopyTo(frame.Slice(dstOffset, laneRowBytes));
                }
                laneCached = true;
            }
        }
        if (laneCached)
        {
            // Lane grid (black bands + C lines) is cached; still need to draw
            // pitch labels which live in the timeline gutter, not the lane.
            int firstLabelSemitone = (int)Math.Floor(minMidi);
            int lastLabelSemitone = (int)Math.Ceiling(maxMidi);
            for (int midi = firstLabelSemitone; midi <= lastLabelSemitone; midi++)
            {
                if (Mod(midi, 12) == 0)
                {
                    int octaveIndex = midi / 12 - 1;
                    if ((uint)octaveIndex < COctaveLabels.Length)
                    {
                        string label = COctaveLabels[octaveIndex];
                        int labelY = MidiToY(midi, minMidi, maxMidi, lane) - 3;
                        if (labelY >= lane.Y && labelY + 7 <= lane.Bottom)
                            DrawPitchLabelRightAligned(frame, timeline, lane, label, labelY);
                    }
                }
            }
            return;
        }
        int firstSemitone = (int)Math.Floor(minMidi);
        int lastSemitone = (int)Math.Ceiling(maxMidi);
        // When caching, render into a scratch lane buffer first so the cached
        // bytes are exactly what a cache hit would blit — including the
        // TimelineBackground the bands are translucent over. The static frame
        // already carries that background inside the lane rect.
        Span<byte> gridTarget;
        byte[] scratch = null;
        bool willCache = canCacheLane;
        if (willCache)
        {
            byte[] cache = _laneGridCache[panelIndex];
            int needed = lane.Width * lane.Height * 4;
            if (cache == null || cache.Length != needed)
            {
                cache = new byte[needed];
                _laneGridCache[panelIndex] = cache;
            }
            // Seed with the static frame's lane rect: the translucent band
            // blend must land on the same background a cold render would see.
            for (int y = 0; y < lane.Height; y++)
            {
                int srcOffset = ((lane.Y + y) * Width + lane.X) * 4;
                _staticFrame.AsSpan(srcOffset, laneRowBytes).CopyTo(cache.AsSpan(y * laneRowBytes, laneRowBytes));
            }
            scratch = cache;
            gridTarget = scratch;
        }
        else
        {
            gridTarget = frame;
        }
        {
            // Two timed passes: bands (the bulk pixel work) and lines/labels.
            // Integrated lanes draw the grid over the live scope waveform, so
            // every band pixel is a genuine translucent blend over varying
            // content — measured as the dominant pitched-lane cost. The bands
            // are omitted there; non-integrated lanes keep them (uniform
            // static background => single packed fill).
            long bandStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            if (!_layout.UsesIntegratedRoll)
            {
                for (int midi = (int)Math.Floor(minMidi); midi <= (int)Math.Ceiling(maxMidi); midi++)
                {
                    if (!BlackPitchClasses.Contains(Mod(midi, 12)))
                        continue;
                    int yTop = MidiToY(midi + 0.5, minMidi, maxMidi, lane);
                    int yBottom = MidiToY(midi - 0.5, minMidi, maxMidi, lane);
                    int top = Math.Min(yTop, yBottom);
                    int bottom = Math.Max(yTop, yBottom);
                    FillRect(gridTarget, new OverlayRect(lane.X, top, lane.Width, Math.Max(1, bottom - top)), BlackKeyBand);
                }
            }
            if (_performance.Enabled)
            {
                long bandTicks = Stopwatch.GetTimestamp() - bandStart;
                _performance.PitchBandTicks += bandTicks;
                _performance.PitchGridTicks += bandTicks;
            }
            long lineStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            for (int midi = (int)Math.Floor(minMidi); midi <= (int)Math.Ceiling(maxMidi); midi++)
            {
                if (Mod(midi, 12) != 0)
                    continue;
                DrawHorizontalLine(gridTarget, lane.X, lane.Right - 1, MidiToY(midi, minMidi, maxMidi, lane), GridLine);
                // §20.1: use the precomputed label — no per-frame string formatting.
                int octaveIndex = midi / 12 - 1;
                // Clamp for relative/unusual pitch models (e.g. SPC relative
                // semitones) that can produce midi=0 or negative values.
                if ((uint)octaveIndex < COctaveLabels.Length)
                {
                    string label = COctaveLabels[octaveIndex];
                    int labelY = MidiToY(midi, minMidi, maxMidi, lane) - 3;
                    // Pitch labels are dynamic and must remain inside the
                    // lane that will be restored by a sequential session.
                    // Without this guard a bottom-edge glyph can spill into
                    // the next panel header and leave stale pixels after a
                    // seek or frame transition.
                    if (labelY >= lane.Y && labelY + 7 <= lane.Bottom)
                        DrawPitchLabelRightAligned(frame, timeline, lane, label, labelY);
                }
            }
            if (_performance.Enabled)
            {
                long lineTicks = Stopwatch.GetTimestamp() - lineStart;
                _performance.GridLineTicks += lineTicks;
                _performance.PitchGridTicks += lineTicks;
            }
        }
        if (willCache)
        {
            // Blit the freshly rendered lane buffer to the frame.
            for (int y = 0; y < lane.Height; y++)
            {
                int srcOffset = y * laneRowBytes;
                int dstOffset = ((lane.Y + y) * Width + lane.X) * 4;
                scratch.AsSpan(srcOffset, laneRowBytes).CopyTo(frame.Slice(dstOffset, laneRowBytes));
            }
            _laneGridMinMidi[panelIndex] = minMidi;
            _laneGridMaxMidi[panelIndex] = maxMidi;
        }
    }

    private readonly struct RectCopyPlan
    {
        public RectCopyPlan(int offset, int rowBytes, int rows)
        {
            Offset = offset;
            RowBytes = rowBytes;
            Rows = rows;
        }

        public int Offset { get; }
        public int RowBytes { get; }
        public int Rows { get; }
    }

    private readonly struct ScopeCopyPlan
    {
        public ScopeCopyPlan(int sourceOffset, int destinationOffset, int rowBytes, int rows)
        {
            SourceOffset = sourceOffset;
            DestinationOffset = destinationOffset;
            RowBytes = rowBytes;
            Rows = rows;
        }

        public int SourceOffset { get; }
        public int DestinationOffset { get; }
        public int RowBytes { get; }
        public int Rows { get; }
    }



}
