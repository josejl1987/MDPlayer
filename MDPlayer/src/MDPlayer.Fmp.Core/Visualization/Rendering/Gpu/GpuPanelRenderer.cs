using System.Diagnostics;
using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Scratch-benchmark knob for the GPU cost split (see ScratchGpuSplitBenchmark;
/// never used by the production pipeline). Selects variants of
/// <see cref="GpuPanelRenderer.RenderCompositeFrame"/> so draw cost, scope
/// texture upload, flush/submit sync, and readback can be measured in
/// isolation.
/// </summary>
internal enum GpuBenchmarkMode
{
    /// <summary>Production path: full draw + async flush + readback.</summary>
    Full,

    /// <summary>Case A: full draw + synchronous flush, NO readback.</summary>
    DrawOnlySyncFlush,

    /// <summary>Case B: clear only + readback (pure readback cost).</summary>
    ClearOnlyReadback,

    /// <summary>Case D: full draw, scopes disabled + readback.</summary>
    FullNoScopes,
}

/// <summary>
/// GPU-first visualization renderer. Renders the same information and visual
/// language as <see cref="PanelOverlayRenderer"/> (panel ordering, channel
/// identity, note pitch/timing/duration, fixed playhead, scope placement,
/// instrument/patch text, rhythm hits, color identity, major layout modes)
/// but each frame is fully redrawn through a GPU-backed Skia surface
/// (<see cref="GpuSkiaContext"/>) using only high-level primitives:
/// <c>DrawRect/DrawRoundRect/DrawLine/DrawCircle/DrawPath/DrawImage/DrawText/
/// Clear</c>.
///
/// No per-pixel APIs and no static caching: every frame runs the same pipeline
/// — background clear → chrome → time grid → playheads → one Corrscope texture
/// upload → dynamic content → text → flush → ONE readback into the caller's
/// RGBA frame slot.
/// </summary>
internal sealed partial class GpuPanelRenderer : IFrameOverlayRenderer
{
    private readonly VisualizationTimeline _timeline;
    private readonly PanelOverlayRenderer.Options _options;
    private readonly OverlayLayout _layout;
    private readonly VisualizationTopology _topology;
    private readonly OverlayScene _scene;
    private readonly PreparedPanel[] _panels;
    private readonly VisualizationPresentation _presentation;
    private readonly double _samplesPerFrame;
    private readonly VisualizationTimeGridLine[] _timeGrid;
    private readonly GpuSkiaContext _context;
    private readonly RenderPerformanceMetrics _performance;
    private readonly object _gate = new();

    private readonly string[] _clockBySecond;
    private readonly string[] _loopLabelByFrame;
    private readonly string _totalClockString;

    // Skia resources reused across every frame (the hot path allocates
    // nothing): Skia copies paint attributes at draw time, so mutating Color
    // on these fields between primitives is allocation-free.
    private readonly SKPaint _fillPaint;
    private readonly SKPaint _textPaint;
    private readonly SKFont _font;
    private readonly SKTypeface _typeface;
    private readonly SKPaint _scopePaint;

    /// <summary>Per-panel scope cell copies (source grid slice → destination rect).</summary>
    private readonly ScopePlan[] _scopePlans;

    /// <summary>Row index (into the chrome gutter labels) per <see cref="SamplePlaybackEvent"/>.</summary>
    private readonly int[][] _sampleRowByPlayback;

    /// <summary>Distinct sample-label count per panel (PCM lane band divisor).</summary>
    private int[] _sampleLaneRowCounts;

    // Scope-grid texture upload: a raster bitmap holding the premultiplied
    // Corrscope grid, minted into a fresh SKImage every frame so the GPU always
    // uploads the current pixels, then one DrawImage per panel cell.
    private SKBitmap _scopeBitmap;
    private byte[] _scopePixels;
    private SKImage _scopeImage;

    // Static chrome cache: time-invariant content (metadata bars, panel bodies,
    // headers, pitch/rhythm grids, static text) rendered ONCE into an offscreen
    // GPU surface and drawn back as a single textured quad per frame. Cuts the
    // dominant redundant fragment work the profiler showed inside the readback
    // wait (DrawChrome was the largest per-frame cost on chrome-heavy layouts).
    private SKSurface _chromeSurface;
    private SKImage _chromeImage;

    private OverlayColor _primaryText;
    private OverlayColor _secondaryText;
    private OverlayColor _tertiaryText;

    private static readonly string[] PitchClassNames =
        ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    /// <summary>Prepared pitch labels ("C4", "C4 +50c", ...) for the header state slot.</summary>
    private static readonly string[] PitchLabels = BuildPitchLabels();

    private static readonly HashSet<int> BlackPitchClasses = new() { 1, 3, 6, 8, 10 };

    /// <summary>
    /// Scratch-benchmark variant selection; <see cref="GpuBenchmarkMode.Full"/>
    /// is the production path. Internal: only the scratch split benchmark sets
    /// this.
    /// </summary>
    internal GpuBenchmarkMode BenchmarkMode { get; set; } = GpuBenchmarkMode.Full;

    public GpuPanelRenderer(
        VisualizationTimeline timeline,
        ResolvedVisualizationLayout layout,
        PanelOverlayRenderer.Options options = null)
    {
        _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
        ArgumentNullException.ThrowIfNull(layout);
        if (timeline.SampleRate <= 0)
            throw new ArgumentException("Timeline sample rate must be positive.", nameof(timeline));
        if (timeline.EndSample < timeline.StartSample)
            throw new ArgumentException("Timeline end precedes its start.", nameof(timeline));

        _options = options ?? new PanelOverlayRenderer.Options();
        _performance = new RenderPerformanceMetrics(_options.EnablePerformanceMetrics, "Gpu");
        _options.Palette ??= VisualizationPalette.Default;
        if (_options.FpsNumerator <= 0 || _options.FpsDenominator <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Frame rate must be positive.");
        if (_options.ScopeOpacity is < 0.05 or > 1.0)
            throw new ArgumentOutOfRangeException(nameof(options), "Scope opacity must be between 0.05 and 1.0.");

        _layout = layout.Geometry;
        _topology = layout.Topology;
        _presentation = _options.Presentation ?? VisualizationPresentation.Empty;
        _samplesPerFrame = timeline.SampleRate * (double)_options.FpsDenominator / _options.FpsNumerator;

        // Consume the SAME prepared scene data the CPU renderer uses: notes /
        // rhythm pre-sorted, colors pre-resolved, pitch ranges computed once.
        _scene = OverlaySceneBuilder.Build(
            timeline,
            _layout,
            _topology,
            samplesPerFrame: _samplesPerFrame,
            noteColorMode: _options.NoteColor,
            palette: _options.Palette);
        _panels = _scene.Panels;
        _scopePlans = BuildScopePlans();
        _sampleRowByPlayback = BuildSampleRowByPlayback();

        _timeGrid = VisualizationTimeGridBuilder.Build(timeline, _options.TimeGrid);

        _totalClockString = FormatTime(
            Math.Max(0, _timeline.EndSample - _timeline.StartSample) / (double)_timeline.SampleRate);
        _clockBySecond = BuildClockStrings(_totalClockString);
        _loopLabelByFrame = BuildLoopLabels();
        BuildTextHierarchy();

        // Strict GPU initialization: any GL/Ganesh/surface failure throws here
        // (there is no silent raster fallback).
        _context = new GpuSkiaContext(_layout.Width, _layout.Height);

        _typeface = LoadTypeface(_options.FontPath);
        _font = new SKFont(_typeface, 14f);
        _fillPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
        _textPaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Typeface = _typeface,
        };
        _scopePaint = new SKPaint { IsAntialias = false };
    }

    public int Width => _layout.Width;
    public int Height => _layout.Height;
    public int FrameByteCount => checked(Width * Height * 4);
    public int ScopeFrameByteCount => checked(
        _layout.CorrscopeGridWidth * _layout.CorrscopeGridHeight * 4);
    public int FpsNumerator => _options.FpsNumerator;
    public int FpsDenominator => _options.FpsDenominator;
    public OverlayLayout Layout => _layout;
    public RenderPerformanceSnapshot Performance => _performance.Snapshot(Width, Height);

    /// <summary>
    /// Clears the counters so a benchmark window can start fresh without
    /// rebuilding renderer or GPU state (warm-up frames do not contaminate
    /// results).
    /// </summary>
    internal void ResetPerformanceMetrics() => _performance.Reset();

    public long TotalFrames
        => FrameSampleClock.FrameCount(
            Math.Max(0, _timeline.EndSample - _timeline.StartSample),
            _timeline.SampleRate,
            FpsNumerator,
            FpsDenominator);

    /// <summary>Identifies the renderer backend in the startup diagnostics.</summary>
    public static string BackendId => "skia-gpu";

    internal SKCanvas Canvas => _context.Canvas;

    private OverlayColor CanvasBackground => _options.Palette.CanvasBackground;
    private OverlayColor HeaderBackground => _options.Palette.HeaderBackground;
    private OverlayColor TimelineBackground => _options.Palette.TimelineBackground;
    private OverlayColor BlackKeyBand => _options.Palette.BlackKeyBand.WithAlpha(
        Math.Min(_options.Palette.BlackKeyBand.A, (byte)90));
    private OverlayColor GridLine => _options.Palette.GridLine;
    private OverlayColor Border => _options.Palette.Border;
    private OverlayColor MutedText => _options.Palette.MutedText;
    private OverlayColor BrightText => _options.Palette.BrightText;
    private OverlayColor PlayheadColor => _options.Palette.Playhead;

    /// <summary>The Skia paint used for filled geometry (color mutated per draw).</summary>
    internal SKPaint FillPaint => _fillPaint;

    /// <summary>The Skia paint used for text (color mutated per draw).</summary>
    internal SKPaint TextPaint => _textPaint;

    internal SKFont Font => _font;

    public void RenderFrame(long frameIndex, Span<byte> destination)
        => RenderCompositeFrame(frameIndex, ReadOnlySpan<byte>.Empty, destination);

    /// <summary>
    /// Full per-frame pipeline. The GPU path always redraws the complete frame
    /// from the cleared canvas: chrome, scope texture, dynamics and text, then
    /// one flush and one readback into <paramref name="destination"/>.
    /// </summary>
    public void RenderCompositeFrame(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination,
        bool scopeFramesAreOpaque = false)
    {
        ValidateFrame(frameIndex, destination);
        if (!scopeGrid.IsEmpty && scopeGrid.Length < ScopeFrameByteCount)
        {
            throw new ArgumentException(
                $"Scope grid requires at least {ScopeFrameByteCount} bytes, got {scopeGrid.Length}.",
                nameof(scopeGrid));
        }

        lock (_gate)
        {
            long renderStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            long allocatedBefore = _performance.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
            if (_performance.Enabled)
                _performance.Frames++;

            long currentSample = Math.Min(
                _timeline.EndSample,
                _timeline.StartSample + FrameSampleClock.SampleAtFrame(
                    frameIndex, _timeline.SampleRate, FpsNumerator, FpsDenominator));

            try
            {
                long drawStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;

                if (BenchmarkMode == GpuBenchmarkMode.ClearOnlyReadback)
                {
                    // Case B: clear only (BeginFrame clears), then read back.
                    // Isolates the pure GPU→CPU readback cost.
                    _context.BeginFrame(ToSk(CanvasBackground));
                }
                else
                {
                    // 1) Background + chrome. Chrome is time-invariant, so it is
                    //    rendered once into an offscreen GPU surface and drawn
                    //    back as one textured quad (see EnsureChromeCache).
                    _context.BeginFrame(ToSk(CanvasBackground));
                    EnsureChromeCache();
                    if (_chromeImage is not null)
                        Canvas.DrawImage(_chromeImage, 0, 0);
                    else
                        DrawChrome();

                    // 2) Time grid (dynamic window).
                    long gridStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    DrawTimeGrid(currentSample);
                    if (_performance.Enabled)
                        _performance.GridLineTicks += Stopwatch.GetTimestamp() - gridStart;

                    // 3) Fixed playheads (before the scope/dynamic layers, matching the
                    //    CPU draw order).
                    DrawPlayheads();

                    // 4) ONE Corrscope texture upload + one DrawImage per panel.
                    //    Case D (FullNoScopes) skips the upload entirely so the
                    //    scope contribution can be isolated.
                    if (!scopeGrid.IsEmpty && BenchmarkMode != GpuBenchmarkMode.FullNoScopes)
                    {
                        long scopeStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        PlaceScopeRows(scopeGrid, scopeFramesAreOpaque);
                        if (_performance.Enabled)
                            _performance.ScopeUploadTicks += Stopwatch.GetTimestamp() - scopeStart;
                    }

                    // 5) Dynamic panel content + per-frame header state/patch text.
                    long dynamicStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    DrawDynamicPanels(currentSample);
                    if (_performance.Enabled)
                        _performance.DynamicTicks += Stopwatch.GetTimestamp() - dynamicStart;

                    // 6) Dynamic metadata text (clock, loop label, progress).
                    long textStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    DrawClock(currentSample);
                    DrawLoopLabel(frameIndex);
                    DrawProgress(currentSample);
                    if (_performance.Enabled)
                        _performance.TextTicks += Stopwatch.GetTimestamp() - textStart;
                }

                if (_performance.Enabled)
                    _performance.GpuDrawTicks += Stopwatch.GetTimestamp() - drawStart;

                // 7) Flush. Case A blocks until the GPU has finished (draw +
                //    submit cost, no readback); the production path stays async.
                long flushStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                if (BenchmarkMode == GpuBenchmarkMode.DrawOnlySyncFlush)
                    _context.FlushSync();
                else
                    _context.Flush();
                if (_performance.Enabled)
                    _performance.GpuFlushSyncTicks += Stopwatch.GetTimestamp() - flushStart;

                // 7b) ONE readback into the frame slot (skipped in case A).
                if (BenchmarkMode != GpuBenchmarkMode.DrawOnlySyncFlush)
                {
                    long readStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    _context.ReadPixels(destination);
                    if (_performance.Enabled)
                        _performance.GpuReadbackTicks += Stopwatch.GetTimestamp() - readStart;
                }
            }
            finally
            {
                // Detach the context from this thread after every frame so the
                // caller's thread may safely acquire it (GLFW ownership rule).
                _context.ReleaseCurrent();
            }

            if (_performance.Enabled)
            {
                _performance.RenderTicks += Stopwatch.GetTimestamp() - renderStart;
                _performance.FullRedraws++;
                _performance.RenderedPixels += (long)Width * Height;
                _performance.FinishFrame(allocatedBefore);
            }
        }
    }

    internal void RenderForPipelinedSlot(SinglePassComposer.FrameSlot slot, long frameIndex, bool scopeFramesAreOpaque, out int pboSlot)
    {
        pboSlot = -1;
        ValidateFrame(frameIndex, slot.Frame);
        if (!slot.Grid.AsSpan().IsEmpty && slot.Grid.Length < ScopeFrameByteCount)
            throw new ArgumentException($"Scope grid requires at least {ScopeFrameByteCount} bytes.", nameof(slot));

        lock (_gate)
        {
            long renderStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            long allocatedBefore = _performance.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
            if (_performance.Enabled)
                _performance.Frames++;

            long currentSample = Math.Min(
                _timeline.EndSample,
                _timeline.StartSample + FrameSampleClock.SampleAtFrame(
                    frameIndex, _timeline.SampleRate, FpsNumerator, FpsDenominator));

            try
            {
                long drawStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;

                if (BenchmarkMode == GpuBenchmarkMode.ClearOnlyReadback)
                {
                    _context.BeginFrame(ToSk(CanvasBackground));
                }
                else
                {
                    _context.BeginFrame(ToSk(CanvasBackground));
                    EnsureChromeCache();
                    if (_chromeImage is not null)
                        Canvas.DrawImage(_chromeImage, 0, 0);
                    else
                        DrawChrome();

                    long gridStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    DrawTimeGrid(currentSample);
                    if (_performance.Enabled)
                        _performance.GridLineTicks += Stopwatch.GetTimestamp() - gridStart;

                    DrawPlayheads();

                    if (slot.HasGrid && slot.Grid.Length >= ScopeFrameByteCount && BenchmarkMode != GpuBenchmarkMode.FullNoScopes)
                    {
                        long scopeStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        PlaceScopeRows(slot.Grid, scopeFramesAreOpaque);
                        if (_performance.Enabled)
                            _performance.ScopeUploadTicks += Stopwatch.GetTimestamp() - scopeStart;
                    }

                    long dynamicStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    DrawDynamicPanels(currentSample);
                    if (_performance.Enabled)
                        _performance.DynamicTicks += Stopwatch.GetTimestamp() - dynamicStart;

                    long textStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                    DrawClock(currentSample);
                    DrawLoopLabel(frameIndex);
                    DrawProgress(currentSample);
                    if (_performance.Enabled)
                        _performance.TextTicks += Stopwatch.GetTimestamp() - textStart;
                }

                if (_performance.Enabled)
                    _performance.GpuDrawTicks += Stopwatch.GetTimestamp() - drawStart;

                long flushStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                if (BenchmarkMode == GpuBenchmarkMode.DrawOnlySyncFlush)
                {
                    _context.FlushSync();
                    if (_performance.Enabled)
                        _performance.GpuFlushSyncTicks += Stopwatch.GetTimestamp() - flushStart;
                    // Sync benchmark skips readback.
                    return;
                }
                else
                {
                    // Pipelined async readback: enqueue without blocking.
                    if (_context.HasAsyncReadback)
                    {
                        pboSlot = _context.BeginAsyncReadback();
                    }
                    else
                    {
                        _context.Flush();
                        if (_performance.Enabled)
                            _performance.GpuFlushSyncTicks += Stopwatch.GetTimestamp() - flushStart;
                        long readStart = _performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                        _context.ReadPixels(slot.Frame);
                        if (_performance.Enabled)
                            _performance.GpuReadbackTicks += Stopwatch.GetTimestamp() - readStart;
                        return;
                    }
                    if (_performance.Enabled)
                        _performance.GpuFlushSyncTicks += Stopwatch.GetTimestamp() - flushStart;
                }
            }
            finally
            {
                _context.ReleaseCurrent();
            }

            if (_performance.Enabled)
            {
                _performance.RenderTicks += Stopwatch.GetTimestamp() - renderStart;
                _performance.FullRedraws++;
                _performance.RenderedPixels += (long)Width * Height;
                _performance.FinishFrame(allocatedBefore);
            }
        }
    }

    public void WriteStaticFrame(Span<byte> destination)
    {
        // The GPU path never caches a static layer; the "static" export is a
        // full frame at the timeline start without scope or dynamics.
        RenderCompositeFrame(0, ReadOnlySpan<byte>.Empty, destination);
    }

    public ISequentialCompositeSession CreateSequentialSession(bool scopeFramesAreOpaque = false)
        => new GpuSequentialSession(this, scopeFramesAreOpaque);

    /// <summary>
    /// Renders the time-invariant chrome into an offscreen GPU surface exactly
    /// once and snapshots it as an SKImage. Per-frame chrome then costs a
    /// single textured quad instead of the full fill/text command stream.
    /// Called with the GL context current on the rendering thread; falls back
    /// to per-frame DrawChrome if the offscreen surface cannot be created.
    /// </summary>
    private void EnsureChromeCache()
    {
        if (_chromeImage is not null)
            return;
        try
        {
            _chromeSurface ??= _context.CreateOffscreenSurface();
            if (_chromeSurface is null)
                return;
            SKCanvas canvas = _chromeSurface.Canvas;
            canvas.Clear(ToSk(CanvasBackground));
            using (_context.UseCanvas(canvas))
            {
                DrawChrome();
            }
            canvas.Flush();
            _chromeImage = _chromeSurface.Snapshot();
        }
        catch
        {
            // Cache failure is non-fatal: fall back to per-frame DrawChrome.
            _chromeImage?.Dispose();
            _chromeImage = null;
        }
    }

    /// <summary>
    /// The GPU composites onto an opaque framebuffer (alpha always 255), so
    /// premultiplied RGB is identical to straight RGB and consumers can skip
    /// the full-frame unpremultiply pass.
    /// </summary>
    public bool ProducesOpaqueFrames => true;

    public void Dispose()
    {
        _font?.Dispose();
        _typeface?.Dispose();
        _fillPaint?.Dispose();
        _textPaint?.Dispose();
        _scopePaint?.Dispose();
        _scopeImage?.Dispose();
        _scopeBitmap?.Dispose();
        _scopePixels = null;
        _chromeImage?.Dispose();
        _chromeSurface?.Dispose();
        _context?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ------------------------------------------------------------------
    // Small shared helpers (backend-agnostic, allocation-free).
    // ------------------------------------------------------------------

    private void ValidateFrame(long frameIndex, Span<byte> destination)
    {
        if (destination.Length < FrameByteCount)
        {
            throw new ArgumentException(
                "destination is smaller than one RGBA frame",
                nameof(destination));
        }
        if (frameIndex < 0 || frameIndex >= TotalFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameIndex), $"Frame index {frameIndex} out of range 0..{TotalFrames - 1}.");
        }
    }

    /// <summary>Pitch→Y mapping inside a lane (bottom = minimum MIDI).</summary>
    internal static int MidiToY(double midi, double minimum, double maximum, OverlayRect lane)
    {
        if (!double.IsFinite(midi) || maximum <= minimum)
            return lane.Bottom - 1;
        double fraction = (midi - minimum) / (maximum - minimum);
        fraction = Math.Clamp(fraction, 0, 1);
        return lane.Bottom - 1 - (int)Math.Round(fraction * Math.Max(0, lane.Height - 1));
    }

    internal static SKColor ToSk(OverlayColor c) => new(c.R, c.G, c.B, c.A);

    /// <summary>Upper-bound search: first element with StartSample &gt; sample.</summary>
    /// <summary>
    /// First note that can intersect the window starting at
    /// <paramref name="windowStart"/>. Returns the index just before the first
    /// note that begins strictly after the window start, so a sustained note
    /// that began before the left edge and is still active inside the window
    /// is retained (the caller's EndSample check keeps or skips it).
    /// </summary>
    private static int FindFirstVisibleNote(PreparedNote[] notes, long windowStart)
    {
        int low = 0;
        int high = notes.Length;

        while (low < high)
        {
            int middle = low + (high - low) / 2;

            if (notes[middle].StartSample < windowStart)
                low = middle + 1;
            else
                high = middle;
        }

        return Math.Max(0, low - 1);
    }

    private static int LowerBoundRhythm(PreparedRhythmEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].SamplePosition < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>
    /// Index of the first note whose start is strictly after
    /// <paramref name="sample"/> (upper-bound style). Used by
    /// <see cref="FindActive"/> for the "most recent note at sample" lookup.
    /// </summary>
    private static int LowerBoundNotes(PreparedNote[] notes, long sample)
    {
        int low = 0;
        int high = notes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].StartSample <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    /// <summary>The most recent note active at <paramref name="sample"/> (or null).</summary>
    private static PreparedNote FindActive(PreparedNote[] notes, long sample)
    {
        for (int index = LowerBoundNotes(notes, sample) - 1; index >= 0 && index >= LowerBoundNotes(notes, sample) - 2; index--)
        {
            PreparedNote note = notes[index];
            if (note.StartSample <= sample && sample < note.EndSample)
                return note;
        }
        return null;
    }

    private static string[] BuildPitchLabels()
    {
        const int CentsPerNote = 201;
        var labels = new string[128 * CentsPerNote];
        for (int midi = 0; midi < 128; midi++)
        {
            string baseName = PitchClassNames[midi % 12] + (midi / 12 - 1);
            for (int cents = -100; cents <= 100; cents++)
            {
                labels[midi * CentsPerNote + cents + 100] = Math.Abs(cents) < 8
                    ? baseName
                    : baseName + " " + (cents > 0 ? "+" : "") + cents + "c";
            }
        }
        return labels;
    }

    private static string FormatPitchWithCents(double actualMidi)
    {
        if (!double.IsFinite(actualMidi) || actualMidi < 0)
            return "";
        int nearestMidi = (int)Math.Round(actualMidi);
        if ((uint)nearestMidi >= 128u)
            return "";
        int cents = Math.Clamp((int)Math.Round((actualMidi - nearestMidi) * 100), -100, 100);
        return PitchLabels[nearestMidi * 201 + cents + 100];
    }

    private static string FormatTime(double seconds)
    {
        seconds = Math.Max(0, seconds);
        int total = (int)Math.Floor(seconds);
        return $"{total / 60:00}:{total % 60:00}";
    }

    private string[] BuildClockStrings(string totalClock)
    {
        long durationSeconds = Math.Max(0, _timeline.EndSample - _timeline.StartSample)
            / _timeline.SampleRate;
        int count = checked((int)Math.Min(int.MaxValue - 1L, durationSeconds + 1));
        var clocks = new string[count];
        for (int second = 0; second < clocks.Length; second++)
            clocks[second] = FormatTime(second) + " / " + totalClock;
        return clocks;
    }

    private string[] BuildLoopLabels()
    {
        int frameCount = checked((int)Math.Max(1, Math.Min(int.MaxValue - 1L, TotalFrames)));
        var labels = new string[frameCount];
        foreach (LoopMarker marker in _timeline.LoopMarkers)
        {
            long relative = Math.Max(0, marker.SamplePosition - _timeline.StartSample);
            int first = Math.Clamp(
                (int)Math.Round(relative * FpsNumerator / (double)(_timeline.SampleRate * FpsDenominator)),
                0,
                labels.Length - 1);
            int last = Math.Min(
                labels.Length,
                first + Math.Max(1, (int)Math.Round(0.600 * FpsNumerator / FpsDenominator)));
            string label = marker.Kind == LoopMarkerKind.Start
                ? "LOOP0"
                : "LOOP" + marker.Iteration;
            for (int frame = first; frame < last; frame++)
                labels[frame] = label;
        }
        return labels;
    }

    private void BuildTextHierarchy()
    {
        byte baseValue = Math.Max(
            HeaderBackground.R,
            Math.Max(HeaderBackground.G, HeaderBackground.B));
        baseValue = (byte)Math.Max(238, Math.Min(250, baseValue + 60));
        _primaryText = new OverlayColor(baseValue, baseValue, baseValue, 230);
        _secondaryText = new OverlayColor(baseValue, baseValue, baseValue, 184);
        _tertiaryText = new OverlayColor(baseValue, baseValue, baseValue, 140);
    }

    private static SKTypeface LoadTypeface(string fontPath)
    {
        if (!string.IsNullOrWhiteSpace(fontPath) && File.Exists(fontPath))
        {
            try
            {
                return SKTypeface.FromFile(fontPath) ?? SKTypeface.Default;
            }
            catch
            {
                return SKTypeface.Default;
            }
        }
        try
        {
            return SKTypeface.Default;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Sequential export session: the GPU path has no static-layer caching,
    // so every sequential frame is a full redraw of the same pipeline.
    // ------------------------------------------------------------------

    private sealed class GpuSequentialSession : IDeferredCompletionSession
    {
        private readonly GpuPanelRenderer _renderer;
        private readonly bool _scopeFramesAreOpaque;
        private bool _initialized;
        private readonly Queue<(SinglePassComposer.FrameSlot Slot, int PboSlot)> _pending = new();
        private const int MaxInFlightConst = 3;

        public GpuSequentialSession(GpuPanelRenderer renderer, bool scopeFramesAreOpaque)
        {
            _renderer = renderer;
            _scopeFramesAreOpaque = scopeFramesAreOpaque;
        }

        public int MaxInFlight => _renderer._context.HasAsyncReadback ? MaxInFlightConst : 0;

        public int PendingCount => _pending.Count;

        public void Initialize(Span<byte> destination)
        {
            if (destination.Length < _renderer.FrameByteCount)
                throw new ArgumentException(
                    $"Destination requires at least {_renderer.FrameByteCount} bytes.",
                    nameof(destination));
            _initialized = true;
        }

        public void RenderNext(
            long frameIndex,
            ReadOnlySpan<byte> scopeGrid,
            Span<byte> destination)
        {
            if (!_initialized)
                throw new InvalidOperationException("The sequential session must be initialized first.");
            // Non-pipelined path (single-frame preview / tests / benchmark modes):
            // keep the original synchronous contract.
            _renderer.RenderCompositeFrame(frameIndex, scopeGrid, destination, _scopeFramesAreOpaque);
        }

        public void Submit(SinglePassComposer.FrameSlot slot, long frameIndex, SinglePassComposer.PipelineMetrics metrics)
        {
            if (!_initialized)
                throw new InvalidOperationException("The sequential session must be initialized first.");
            if (!_renderer._context.HasAsyncReadback || MaxInFlight == 0)
            {
                long stageStart = Stopwatch.GetTimestamp();
                _renderer.RenderCompositeFrame(frameIndex, slot.Grid, slot.Frame, _scopeFramesAreOpaque);
                if (metrics != null) metrics.OverlayTicks += Stopwatch.GetTimestamp() - stageStart;
                _pending.Enqueue((slot, -1));
                return;
            }

            long drawStart = Stopwatch.GetTimestamp();
            _renderer.RenderForPipelinedSlot(slot, frameIndex, _scopeFramesAreOpaque, out int pboSlot);
            if (metrics != null) metrics.OverlayTicks += Stopwatch.GetTimestamp() - drawStart;
            _pending.Enqueue((slot, pboSlot));
        }

        public bool TryDequeueCompleted(SinglePassComposer.PipelineMetrics metrics, out SinglePassComposer.FrameSlot slot)
        {
            slot = null;
            if (_pending.Count == 0)
                return false;
            var (peekSlot, pboSlot) = _pending.Peek();
            if (pboSlot == -1)
            {
                _pending.Dequeue();
                slot = peekSlot;
                return true;
            }
            long t0 = metrics != null && _renderer._performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            bool ok = _renderer._context.TryCompleteAsyncReadback(pboSlot, peekSlot.Frame, block: false);
            if (ok)
            {
                if (metrics != null && _renderer._performance.Enabled)
                    _renderer._performance.GpuReadbackTicks += Stopwatch.GetTimestamp() - t0;
                _pending.Dequeue();
                slot = peekSlot;
                return true;
            }
            return false;
        }

        public SinglePassComposer.FrameSlot WaitForOldest(SinglePassComposer.PipelineMetrics metrics)
        {
            if (_pending.Count == 0)
                throw new InvalidOperationException("No pending frames");
            var (peekSlot, pboSlot) = _pending.Peek();
            if (pboSlot == -1)
            {
                _pending.Dequeue();
                return peekSlot;
            }
            long t0 = metrics != null && _renderer._performance.Enabled ? Stopwatch.GetTimestamp() : 0;
            _renderer._context.TryCompleteAsyncReadback(pboSlot, peekSlot.Frame, block: true);
            if (metrics != null && _renderer._performance.Enabled)
                _renderer._performance.GpuReadbackTicks += Stopwatch.GetTimestamp() - t0;
            _pending.Dequeue();
            return peekSlot;
        }

        public void CompleteAll(SinglePassComposer.PipelineMetrics metrics)
        {
            while (_pending.Count > 0)
            {
                var (peekSlot, pboSlot) = _pending.Peek();
                if (pboSlot == -1)
                {
                    _pending.Dequeue();
                    continue;
                }
                long t0 = metrics != null && _renderer._performance.Enabled ? Stopwatch.GetTimestamp() : 0;
                _renderer._context.TryCompleteAsyncReadback(pboSlot, peekSlot.Frame, block: true);
                if (metrics != null && _renderer._performance.Enabled)
                    _renderer._performance.GpuReadbackTicks += Stopwatch.GetTimestamp() - t0;
                _pending.Dequeue();
            }
        }
    }
}