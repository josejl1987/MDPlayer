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
        public int Width { get; set; } = 1920;
        public int Height { get; set; } = 1080;
        public int FpsNumerator { get; set; } = 60;
        public int FpsDenominator { get; set; } = 1;
        public double PastSeconds { get; set; } = 0.75;
        public double FutureSeconds { get; set; } = 2.25;
        public double RollZoom { get; set; } = 1.0;
        public int? ScopeHeight { get; set; }
        public int? TimelineHeight { get; set; }
        public double? ScopeRatio { get; set; }
        public VisualizationScopePosition ScopePosition { get; set; } = VisualizationScopePosition.Top;
        public VisualizationChannelFilter Channels { get; set; } = VisualizationChannelFilter.All;
        public VisualizationGroupBy GroupBy { get; set; } = VisualizationGroupBy.None;
        public VisualizationTimeGrid TimeGrid { get; set; } = VisualizationTimeGrid.None;
        public VisualizationPresentation Presentation { get; set; } = VisualizationPresentation.Empty;
        public string FontPath { get; set; }
        public bool PreferAntialiasedText { get; set; }
        public EffectsMode Effects { get; set; } = EffectsMode.Minimal;
        public NoteColorMode NoteColor { get; set; } = NoteColorMode.Instrument;
        public VisualizationLayoutMode LayoutMode { get; set; } = VisualizationLayoutMode.Diagnostic;
        public AnalysisOverlayScene AnalysisOverlay { get; set; } = AnalysisOverlayScene.Empty;
        public VisualizationPalette Palette { get; set; } = VisualizationPalette.Default;
        public int MotionBlurSamples { get; set; } = 1;
        /// <summary>
        /// Semantic raster backend. CPU is the default for direct renderer
        /// callers; the CLI resolves auto before constructing this renderer.
        /// </summary>
        public VisualizationRendererMode Renderer { get; set; } = VisualizationRendererMode.Cpu;

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
    private OverlayColor BlackKeyBand => Palette.BlackKeyBand;
    private OverlayColor GridLine => Palette.GridLine;
    private OverlayColor Border => Palette.Border;
    private OverlayColor MutedText => Palette.MutedText;
    private OverlayColor BrightText => Palette.BrightText;
    private OverlayColor Playhead => Palette.Playhead;

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
    private readonly PitchCamera[] _cameras;
    private readonly VisualizationPresentation _presentation;
    private readonly OverlayColor[] _panelAccents;
    private readonly ChannelEnergyEnvelope[] _energyByPanel;
    private readonly bool _unicodePresentationRendered;
    private readonly double _samplesPerFrame;
    private readonly long _taperSamples;
    private readonly long _introSamples;
    private readonly long _outroSamples;
    private readonly EffectsMode _effects;
    private readonly AnalysisOverlayScene _analysisOverlay;

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
    private readonly string[] _loopLabelByFrame;
    private readonly VisualizationTimeGridLine[] _timeGrid;
    private readonly VisualizationOpenClRenderer _openClRenderer = null;
    private readonly int[] _gpuPrimitiveData = null;
    private readonly byte[] _gpuFrame = null;
    private readonly object _gpuFrameGate = new();
    private readonly object _motionBlurGate = new();

    public PanelOverlayRenderer(VisualizationTimeline timeline, Options options = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        if (timeline.SampleRate <= 0)
            throw new ArgumentException("Timeline sample rate must be positive.", nameof(timeline));
        if (timeline.EndSample < timeline.StartSample)
            throw new ArgumentException("Timeline end precedes its start.", nameof(timeline));

        _options = options ?? new Options();
        _options.Palette ??= VisualizationPalette.Default;
        if (_options.MotionBlurSamples is < 1 or > 8)
            throw new ArgumentOutOfRangeException(nameof(options), "Motion blur samples must be between 1 and 8.");
        if (_options.FpsNumerator <= 0 || _options.FpsDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Frame rate must be positive.");

        VisualizationLayoutMode selectedLayout = VisualizationLayoutModeResolver.Resolve(
            timeline, _options.LayoutMode);
        _topology = VisualizationTopologyBuilder.Build(
            timeline, selectedLayout, _options.Channels, _options.GroupBy);
        _layout = new OverlayLayout(
            _options.Width,
            _options.Height,
            _options.PastSeconds,
            _options.FutureSeconds,
            _topology.Panels.Count,
            selectedLayout,
            _options.ScopeHeight,
            _options.TimelineHeight,
            _options.RollZoom,
            _options.ScopeRatio,
            _options.ScopePosition);
        // The compositor remains constructible for synthetic empty timelines
        // used by low-level video-pipeline tests. The publishing CLI rejects
        // such captures before it reaches this renderer (§10.4).
        if (VisualizationContentAvailability.HasRenderableContent(timeline))
            VisualizationLayoutValidator.Validate(_layout, _topology);
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
        _cameras = BuildCameras();
        _staticFrame = new byte[FrameByteCount];
        _motionBlurScratch = _options.MotionBlurSamples > 1
            ? new byte[FrameByteCount]
            : null;
        _totalClockString = FormatTime(
            Math.Max(0, _timeline.EndSample - _timeline.StartSample) / (double)_timeline.SampleRate);
        _clockBySecond = BuildClockStrings(_totalClockString);
        _loopLabelByFrame = BuildLoopLabels();
        _dynamicRestoreRects = BuildDynamicRestoreRects();

        // Per-panel accents come straight from the prepared scene — the builder
        // resolved them from each panel's stable semantic identity.
        _panelAccents = new OverlayColor[_panels.Length];
        for (int i = 0; i < _panels.Length; i++)
        {
            double emphasis = _layout.Mode is VisualizationLayoutMode.Diagnostic
                or VisualizationLayoutMode.DiagnosticV2
                ? 0
                : Math.Clamp(
                    (_panels[i].Prepared.Track.SalienceScore - 1.0) / 1.5,
                    0,
                    1);
            _panelAccents[i] = Palette.ResolveAccent(_panels[i].Id, i)
                .Lighten(emphasis * 0.20);
        }

        // §6.4: build a per-panel energy lookup keyed by panel index.
        _energyByPanel = BuildEnergyLookup(_options.Energy);

        // Validate glyph coverage before the static layer is built. Missing
        // non-ASCII glyphs are a publish-time error, never a bitmap '?'.
        string[] trackLabels = _scene.Panels.Select(panel => panel.Label).ToArray();
        _unicodePresentationRendered = UnicodeStaticTextRenderer.CanRender(
            _presentation,
            _options.FontPath,
            trackLabels,
            _options.PreferAntialiasedText);

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

        if (_options.Renderer == VisualizationRendererMode.Gpu)
        {
            int primitiveCapacity = ComputeGpuPrimitiveCapacity();
            if (!VisualizationOpenClRenderer.TryCreate(
                    Width,
                    Height,
                    primitiveCapacity,
                    out _openClRenderer,
                    out string reason))
            {
                throw new InvalidOperationException($"GPU semantic renderer unavailable: {reason}");
            }
            _gpuPrimitiveData = new int[checked(primitiveCapacity * 9)];
            _gpuFrame = new byte[FrameByteCount];
        }
    }

    public int Width => _layout.Width;
    public int Height => _layout.Height;
    public int FrameByteCount => checked(Width * Height * 4);
    public int ScopeFrameByteCount => checked(
        _layout.CorrscopeGridWidth * _layout.CorrscopeGridHeight * 4);
    public int FpsNumerator => _options.FpsNumerator;
    public int FpsDenominator => _options.FpsDenominator;
    public OverlayLayout Layout => _layout;

    public long TotalFrames
    {
        get
        {
            long samples = Math.Max(0, _timeline.EndSample - _timeline.StartSample);
            decimal frames = (decimal)samples * FpsNumerator
                / (_timeline.SampleRate * FpsDenominator);
            return (long)decimal.Ceiling(frames);
        }
    }

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
        _openClRenderer?.Dispose();
        GC.SuppressFinalize(this);
    }

    ~PanelOverlayRenderer()
    {
        _openClRenderer?.Dispose();
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
    /// chrome is copied in, the Corrscope scope grid strip (raw RGB0 frames,
    /// width × CorrscopeGridHeight) is placed into the transparent scope holes,
    /// and the dynamic content is drawn on top. Used by the single-pass
    /// compositor, which composites the scope and overlay in memory and encodes
    /// once. Pass an empty <paramref name="scopeGrid"/> to skip the scope rows.
    /// </summary>
    public void RenderCompositeFrame(long frameIndex, ReadOnlySpan<byte> scopeGrid, Span<byte> destination)
    {
        if (_options.MotionBlurSamples > 1)
        {
            RenderMotionBlurFrame(frameIndex, scopeGrid, destination);
            return;
        }

        RenderCompositeFrameSingle(frameIndex, scopeGrid, destination);
    }

    private void RenderCompositeFrameSingle(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination)
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

        if (_openClRenderer != null)
        {
            RenderGpuFrame(frameIndex, scopeGrid, destination);
            return;
        }

        _staticFrame.AsSpan().CopyTo(destination);
        if (!scopeGrid.IsEmpty)
            PlaceScopeRows(scopeGrid, destination);
        DrawDynamicCore(frameIndex, destination);
    }

    private void RenderMotionBlurFrame(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination)
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
                RenderCompositeFrameSingle(sampledFrame, scopeGrid, _motionBlurScratch);
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
        Span<byte> destination)
    {
        if (_options.MotionBlurSamples > 1)
        {
            RenderMotionBlurFrame(frameIndex, scopeGrid, destination);
            return;
        }

        if (_openClRenderer != null)
        {
            RenderGpuFrame(frameIndex, scopeGrid, destination);
            return;
        }

        ValidateFrameForSession(frameIndex, destination);
        RestoreDynamicRegions(destination);
        PlaceScopeRowsForSession(scopeGrid, destination);
        DrawDynamicForSession(frameIndex, destination);
    }

    internal SequentialCompositeSession CreateSequentialSession()
        => new(this);

    internal void ValidateFrameForSession(long frameIndex, Span<byte> destination)
        => ValidateFrame(frameIndex, destination);

    internal void RestoreDynamicRegions(Span<byte> destination)
    {
        foreach (OverlayRect rect in _dynamicRestoreRects)
            CopyRect(_staticFrame, destination, rect);
    }

    internal void PlaceScopeRowsForSession(ReadOnlySpan<byte> scopeGrid, Span<byte> destination)
    {
        if (scopeGrid.IsEmpty)
            return;
        int gridBytes = ScopeFrameByteCount;
        if (scopeGrid.Length < gridBytes)
            throw new ArgumentException(
                $"Scope grid requires at least {gridBytes} bytes, got {scopeGrid.Length}.",
                nameof(scopeGrid));
        PlaceScopeRows(scopeGrid, destination);
    }

    internal void DrawDynamicForSession(long frameIndex, Span<byte> destination)
        => DrawDynamicCore(frameIndex, destination);

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
        long relativeSample = OverlayLayout.FrameToSample(
            frameIndex,
            _timeline.SampleRate,
            FpsNumerator,
            FpsDenominator);
        long currentSample = Math.Min(_timeline.EndSample, _timeline.StartSample + relativeSample);

        DrawClock(destination, currentSample);
        DrawLoopLabel(destination, frameIndex);
        DrawProgress(destination, currentSample);
        DrawAnalysisHud(destination, currentSample);
        DrawAnalysisHarmonyStrip(destination, currentSample);
        DrawAnalysisProgressMarkers(destination, currentSample);
        DrawTimeGrid(destination, currentSample);

        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PanelData panel = _panels[panelIndex];
            // Dispatch from the immutable semantic descriptor prepared before
            // frame rendering. Scope-only compositions deliberately suppress
            // semantic geometry; their panels have no timeline height.
            if (_layout.HasRoll)
            {
                switch (panel.TrackKind)
                {
                    case VisualizationTrackKind.Pitched:
                        DrawPitchGrid(destination, panel, _cameras[panel.Index], currentSample, false);
                        if (drawSemantic)
                        {
                            if (panel.Prepared.UsesSsgModes)
                                DrawSsgPanel(destination, panel, currentSample);
                            else
                                DrawPitchedPanel(destination, panel, currentSample, false);
                        }
                        break;
                    case VisualizationTrackKind.FmOperatorGroup:
                        DrawPitchGrid(destination, panel, _cameras[panel.Index], currentSample, true);
                        if (drawSemantic)
                        {
                            DrawPitchedPanel(destination, panel, currentSample, true);
                            DrawFm3OperatorRibbons(destination, panel, currentSample);
                        }
                        break;
                    case VisualizationTrackKind.WaveTable:
                        DrawPitchGrid(destination, panel, _cameras[panel.Index], currentSample, false);
                        if (drawSemantic)
                        {
                            DrawPitchedPanel(destination, panel, currentSample, false);
                            DrawWavetablePanel(destination, panel, currentSample);
                        }
                        break;
                    case VisualizationTrackKind.Sample:
                        if (drawSemantic)
                            DrawPcmVoicePanel(destination, panel, currentSample);
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
                    case VisualizationTrackKind.ParameterActivity:
                    case VisualizationTrackKind.Unsupported:
                        DrawPlaceholderPanel(destination, panel, currentSample);
                        break;
                }

                // Shared publishing compositions reserve their header for the
                // static composition label. Per-channel dynamic state would
                // repeat across the compact lanes and adds no useful cue to
                // the unified roll; diagnostic/split layouts retain it.
                if (!_layout.IsSharedComposition)
                    DrawDynamicPanelHeader(destination, panel, currentSample);
                DrawPlayhead(destination, panel.Index);
            }
            DrawEnergyScopeBorder(destination, panel.Index, currentSample);
        }

        // ScopeStage reserves a compact synchronized activity strip below the
        // mosaic. It shares the window and playhead of the scopes above.
        if (_layout.IsScopeStage)
            DrawScopeStageStrip(destination, currentSample);

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
        int first = LowerBoundTimeGrid(windowStart);
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

    private OverlayRect[] BuildDynamicRestoreRects()
    {
        // The presentation fade temporarily covers both metadata bars. They
        // must be restored every frame so a previous fade cannot persist in
        // the sequential buffer. The scope itself is also dynamic: Corrscope replaces its pixels and
        // DrawEnergyScopeBorder draws on its perimeter. Restore the complete
        // scope rectangle before placing the next Corrscope strip so the
        // sequential session cannot retain a previous frame's border.
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
        if (_layout.IsSharedComposition)
        {
            rects.Add(_layout.GetScopeRect(0));
            for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
            {
                rects.Add(_layout.GetHeaderRect(panelIndex));
                rects.Add(_layout.GetTimelineRect(panelIndex));
            }
        }
        else
        {
            for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
            {
                rects.Add(_layout.GetHeaderRect(panelIndex));
                rects.Add(_layout.GetTimelineRect(panelIndex));
                rects.Add(_layout.GetScopeRect(panelIndex));
            }
        }

        // The ScopeStage activity strip is dynamic content; restore it every
        // frame so a sequential session cannot retain a previous frame.
        if (_layout.IsScopeStage)
            rects.Add(_layout.ScopeStageStripRect);

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

    private void CopyRect(byte[] source, Span<byte> destination, OverlayRect rect)
    {
        int left = Math.Clamp(rect.X, 0, Width);
        int right = Math.Clamp(rect.Right, 0, Width);
        int top = Math.Clamp(rect.Y, 0, Height);
        int bottom = Math.Clamp(rect.Bottom, 0, Height);
        int rowBytes = (right - left) * 4;
        if (rowBytes <= 0 || bottom <= top)
            return;
        // Restore runs before every dynamic draw, so copying a superset of a
        // region is always safe. An in-bounds rect's rows are contiguous in
        // memory only when the rect spans the full frame width; for narrower
        // rects a stride gap separates rows. Rather than issue one CopyTo per
        // row (the dominant cost when dozens of rects are restored every
        // frame), copy the full-width slab that covers the rect's rows: it is
        // one contiguous CopyTo, and the extra pixels are static chrome that
        // the subsequent dynamic draws repaint anyway.
        if (rect.Y >= 0 && rect.Bottom <= Height)
        {
            int offset = (rect.Y * Width) * 4;
            int byteCount = (rect.Bottom - rect.Y) * Width * 4;
            source.AsSpan(offset, byteCount)
                .CopyTo(destination.Slice(offset, byteCount));
            return;
        }
        for (int y = top; y < bottom; y++)
        {
            source.AsSpan((y * Width + left) * 4, rowBytes)
                .CopyTo(destination.Slice((y * Width + left) * 4, rowBytes));
        }
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
    /// Copies each scope row from the Corrscope grid strip into the transparent
    /// scope holes of a composite frame. The grid is raw RGB0 (alpha bytes are
    /// 0), so the alpha of every placed pixel is forced to 255 to make the
    /// scopes opaque over the canvas.
    /// </summary>
    private void PlaceScopeRows(ReadOnlySpan<byte> scopeGrid, Span<byte> destination)
    {
        int sourceWidth = _layout.CorrscopeGridWidth;
        int sourceStride = sourceWidth * 4;
        int scopeHeight = _layout.ScopeHeight;
        for (int row = 0; row < _layout.RowCount; row++)
        {
            OverlayRect scope = _layout.IsSharedComposition
                ? _layout.SharedScopeRect
                : _layout.GetScopeRect(Math.Min(row * _layout.ColumnCount, _layout.PanelCount - 1));
            int destX = scope.X;
            int destY = _layout.IsSharedComposition
                ? scope.Y
                : _layout.GetScopeRowDestinationY(row);
            int copyWidth = Math.Min(sourceWidth, scope.Width);
            int srcY = row * scopeHeight;
            for (int y = 0; y < scopeHeight; y++)
            {
                int src = (srcY + y) * sourceStride;
                int dst = ((destY + y) * Width + destX) * 4;
                int copyBytes = copyWidth * 4;
                scopeGrid.Slice(src, copyBytes).CopyTo(destination.Slice(dst, copyBytes));
                for (int x = 0; x < copyBytes; x += 4)
                    destination[dst + x + 3] = 255;
            }
            if (_layout.IsSharedComposition)
                break;
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
            };
        }
        return panels;
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

            FillRect(frame, panel, HeaderBackground);
            ClearRect(frame, scope);
            FillRect(frame, timeline, TimelineBackground);
            StrokeRect(frame, panel, Border, 1);
            StrokeRect(frame, scope, Border.WithAlpha(180), 1);

            OverlayColor accent = _panelAccents[index];
            FillRect(frame, new OverlayRect(header.X, header.Y, 4, header.Height), accent);
            DrawText(
                frame,
                header.X + 10,
                header.Y + Math.Max(2, (header.Height - 14) / 2),
                _panels[index].Label,
                BrightText,
                2,
                header.Right - 8);

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
            int fullClockWidth = BitmapFont.MeasureText($"00:00 / {_totalClockString}", 2);
            int titleMaxX = topBar.Right - _layout.SafeHorizontalMargin - fullClockWidth - 40;

            if (titleMaxX > topBar.X + _layout.SafeHorizontalMargin)
            {
                string title = Ellipsize(_presentation.Title, 3, titleMaxX - (topBar.X + _layout.SafeHorizontalMargin));
                DrawText(frame, topBar.X + _layout.SafeHorizontalMargin, topBar.Y + 9, title, BrightText, 3, titleMaxX);

                if (!string.IsNullOrEmpty(_presentation.Subtitle))
                {
                    string subtitle = Ellipsize(_presentation.Subtitle, 2, titleMaxX - (topBar.X + _layout.SafeHorizontalMargin));
                    DrawText(frame, topBar.X + _layout.SafeHorizontalMargin, topBar.Y + 39, subtitle, MutedText, 2, titleMaxX);
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

    private void DrawPitchGrid(Span<byte> frame, PanelData panel, PitchCamera camera, long currentSample, bool reserveFm3OperatorRibbons)
    {
        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, reserveFm3OperatorRibbons);
        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);

        if (camera == null)
        {
            // Fallback: use default range.
            DrawPitchGridRange(frame, lane, timeline, 48, 72);
            return;
        }

        var (minMidi, maxMidi) = camera.GetPreciseRange(currentSample);
        DrawPitchGridRange(frame, lane, timeline, minMidi, maxMidi);
    }
    private void DrawPitchGridRange(Span<byte> frame, OverlayRect lane, OverlayRect timeline, double minMidi, double maxMidi)
    {
        if (lane.Width <= 0 || lane.Height <= 0)
            return;
        int firstSemitone = (int)Math.Floor(minMidi);
        int lastSemitone = (int)Math.Ceiling(maxMidi);
        for (int midi = firstSemitone; midi <= lastSemitone; midi++)
        {
            int yTop = MidiToY(midi + 0.5, minMidi, maxMidi, lane);
            int yBottom = MidiToY(midi - 0.5, minMidi, maxMidi, lane);
            int top = Math.Min(yTop, yBottom);
            int bottom = Math.Max(yTop, yBottom);
            if (BlackPitchClasses.Contains(Mod(midi, 12)))
                FillRect(frame, new OverlayRect(lane.X, top, lane.Width, Math.Max(1, bottom - top)), BlackKeyBand);

            if (Mod(midi, 12) == 0)
            {
                DrawHorizontalLine(frame, lane.X, lane.Right - 1, MidiToY(midi, minMidi, maxMidi, lane), GridLine);
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
                        DrawText(frame, timeline.X + 2, labelY, label, MutedText, 1, lane.X - 2);
                }
            }
        }
    }




}
