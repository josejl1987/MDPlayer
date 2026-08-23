using System.Runtime.InteropServices;
using SkiaSharp;

namespace Fmp.Core.Visualization.Rendering.Gpu;

/// <summary>
/// Per-frame grid infrastructure for the GPU renderer: the dynamic time grid
/// (measures/beats), the fixed per-panel playheads, and the ONE Corrscope
/// texture upload + DrawImage per panel. Mirrors the CPU draw order: grid →
/// playheads → scope rows → dynamics. No per-pixel APIs; everything is drawn
/// through high-level Skia primitives.
/// </summary>
internal sealed partial class GpuPanelRenderer
{
    private void DrawTimeGrid(long currentSample)
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
                VisualizationTimeGridLineKind.Measure => new OverlayColor(
                    120, 132, 164, line.Analytical ? (byte)76 : (byte)126),
                VisualizationTimeGridLineKind.Beat => new OverlayColor(
                    95, 105, 132, line.Analytical ? (byte)54 : (byte)92),
                _ => new OverlayColor(
                    72, 80, 104, line.Analytical ? (byte)32 : (byte)56),
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
                    DrawVerticalLine(x, timeline.Y, timeline.Bottom - 1, color);
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

    /// <summary>Fixed playhead (window fraction), drawn per panel before the scope layer.</summary>
    private void DrawPlayheads()
    {
        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            OverlayRect timeline = _layout.GetTimelineRect(panelIndex);
            int x = _layout.GetPlayheadX(panelIndex);
            DrawVerticalLine(x, timeline.Y, timeline.Bottom - 1, PlayheadColor);
            if (x + 1 < timeline.Right)
                DrawVerticalLine(x + 1, timeline.Y, timeline.Bottom - 1, PlayheadColor.WithAlpha(55));
        }
    }

    // ------------------------------------------------------------------
    // Scope placement: ONE texture upload per frame, one DrawImage per cell.
    // ------------------------------------------------------------------

    /// <summary>One panel's scope cell: a source slice of the grid → destination rect.</summary>
    private readonly struct ScopePlan
    {
        public readonly int SourceOffset;
        public readonly int DstX;
        public readonly int DstY;
        public readonly int RowBytes;
        public readonly int Rows;

        public ScopePlan(int sourceOffset, int dstX, int dstY, int rowBytes, int rows)
        {
            SourceOffset = sourceOffset;
            DstX = dstX;
            DstY = dstY;
            RowBytes = rowBytes;
            Rows = rows;
        }
    }

    private ScopePlan[] BuildScopePlans()
    {
        int sourceWidth = _layout.CorrscopeGridWidth;
        int sourceStride = sourceWidth * 4;
        int scopeHeight = _layout.ScopeHeight;
        var plans = new List<ScopePlan>(_panels.Length);
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
            plans.Add(new ScopePlan(
                srcY * sourceStride + srcX * 4,
                scope.X,
                scope.Y,
                copyWidth * 4,
                scopeHeight));
        }
        return plans.ToArray();
    }

    /// <summary>
    /// Blends each scope cell from the Corrscope grid strip into its panel.
    /// The grid bytes are premultiplied into the upload bitmap and drawn with
    /// a white paint whose alpha carries <see cref="PanelOverlayRenderer.Options.ScopeOpacity"/>
    /// (the scope frame's own alpha remains the per-pixel mask). Opaque sources
    /// at full opacity keep the raw-copy fast path.
    /// </summary>
    private void PlaceScopeRows(ReadOnlySpan<byte> scopeGrid, bool scopeFramesAreOpaque)
    {
        int rows = _layout.CorrscopeGridHeight;
        float opacity = (float)Math.Clamp(_options.ScopeOpacity, 0, 1);
        bool opaque = scopeFramesAreOpaque && opacity >= 1f;

        EnsureScopeImage(scopeGrid, rows, opaque);

        int sourceStride = _layout.CorrscopeGridWidth * 4;
        foreach (ScopePlan plan in _scopePlans)
        {
            if (_performance.Enabled)
            {
                _performance.ScopeCopies += plan.Rows;
                _performance.CopiedBytes += plan.RowBytes * plan.Rows;
            }
            int srcRow = plan.SourceOffset / sourceStride;
            int srcX = plan.SourceOffset % sourceStride / 4;
            var srcRect = new SKRect(
                srcX, srcRow, srcX + plan.RowBytes / 4, srcRow + plan.Rows);
            var dstRect = new SKRect(
                plan.DstX, plan.DstY, plan.DstX + plan.RowBytes / 4, plan.DstY + plan.Rows);
            byte pa = opaque ? (byte)255 : (byte)Math.Round(opacity * 255);
            _fillPaint.Color = new SKColor(255, 255, 255, pa);
            Canvas.DrawImage(_scopeImage, srcRect, dstRect, _fillPaint);
        }
    }

    private void EnsureScopeImage(ReadOnlySpan<byte> scopeGrid, int rows, bool opaque)
    {
        int stride = _layout.CorrscopeGridWidth * 4;
        int needed = stride * rows;
        if (_scopeBitmap == null || _scopeImageBytes != needed)
        {
            _scopePixels = new byte[needed];
            _scopeBitmap?.Dispose();
            _scopeBitmap = new SKBitmap();
            var info = new SKImageInfo(_layout.CorrscopeGridWidth, rows, SKColorType.Rgba8888, SKAlphaType.Premul);
            var handle = GCHandle.Alloc(_scopePixels, GCHandleType.Pinned);
            _scopeBitmap.InstallPixels(info, handle.AddrOfPinnedObject(), info.RowBytes,
                (_, ctx) => ((GCHandle)ctx).Free(), handle);
            _scopeImageBytes = needed;
        }
        // Always upload premultiplied bytes (the bitmap is Premul), and mint a
        // fresh SKImage per frame: SKImages are immutable wrappers, so relying
        // on NotifyPixelsChanged() through a cached image can serve stale
        // pixels once the grid content changes.
        PreMultiplyInto(scopeGrid, _scopePixels, opaque);
        _scopeBitmap.NotifyPixelsChanged();
        _scopeImage?.Dispose();
        _scopeImage = SKImage.FromBitmap(_scopeBitmap);
    }

    private int _scopeImageBytes;

    private static void PreMultiplyInto(ReadOnlySpan<byte> straight, Span<byte> premul, bool sourceOpaque = false)
    {
        int n = Math.Min(straight.Length, premul.Length);
        if (sourceOpaque)
        {
            // Alpha is 255 for every pixel (caller-declared opaque source at
            // full scope opacity): straight == premultiplied, so a plain copy
            // is exact and skips all the per-pixel math.
            straight.CopyTo(premul);
            return;
        }

        // Word-wise hot path. Scope sources draw with binary alpha — stroke
        // pixels at 255 over a cleared transparent background — so almost
        // every pixel is either copied verbatim or zeroed. This replaced a
        // per-channel loop that first cost ~44 ms/frame in integer divisions
        // and still ~30 ms/frame as multiply-shifts; word-wise it is ~2 ms.
        // The general multiply-shift runs only for rare partial-alpha pixels.
        ReadOnlySpan<uint> src = MemoryMarshal.Cast<byte, uint>(straight[..n]);
        Span<uint> dst = MemoryMarshal.Cast<byte, uint>(premul[..n]);
        for (int i = 0; i < src.Length; i++)
        {
            uint px = src[i];
            uint alpha = px >> 24;
            if (alpha == 255)
                dst[i] = px;                       // opaque: straight == premul
            else if (alpha == 0)
                dst[i] = 0;                        // transparent: everything zeroes
            else
                dst[i] = ((px & 0xFF) * alpha * 32897 + 2097152 >> 23)
                    | (((px >> 8 & 0xFF) * alpha * 32897 + 2097152 >> 23) << 8)
                    | (((px >> 16 & 0xFF) * alpha * 32897 + 2097152 >> 23) << 16)
                    | (alpha << 24);               // partial: exact (v*a)/255 rounding
        }
    }

    // ------------------------------------------------------------------
    // Shared 1px raster helpers (delegated to Canvas with the reusable paint).
    // ------------------------------------------------------------------

    private void DrawVerticalLine(int x, int yTop, int yBottom, OverlayColor color)
    {
        if (yBottom < yTop)
            return;
        _fillPaint.Color = ToSk(color);
        Canvas.DrawRect(new SKRect(x, yTop, x + 1, yBottom + 1), _fillPaint);
    }

    }