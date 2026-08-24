namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private void DrawStaticPcmLanes(Span<byte> frame, PanelData panel, OverlayRect timeline)
    {
        FillRect(frame, new OverlayRect(timeline.X, timeline.Y, _layout.PitchLabelWidth, timeline.Height), HeaderBackground);

        // For sample lanes, one row per distinct sample identity (S000, S001,
        // ...); otherwise fall back to the declared static rows.
        string[] rows = panel.SampleRowLabels.Length > 0
            ? panel.SampleRowLabels
            : panel.Prepared.Rows.Length > 0
                ? BuildDeclaredRowLabels(panel.Prepared)
                : Array.Empty<string>();

        if (rows.Length > 0)
        {
            int rowHeight = Math.Max(1, timeline.Height / rows.Length);
            for (int row = 0; row < rows.Length; row++)
            {
                int y = timeline.Y + row * rowHeight;
                DrawHorizontalLine(frame, timeline.X + _layout.PitchLabelWidth, timeline.Right - 1, y, GridLine);
                DrawGutterLabelRightAligned(
                    frame,
                    timeline,
                    rows[row],
                    y + Math.Max(1, (rowHeight - 7) / 2));
            }
        }
    }

    /// <summary>Copies the declared panel-row labels into a reusable array.</summary>
    private static string[] BuildDeclaredRowLabels(PreparedPanel panel)
    {
        var labels = new string[panel.Rows.Length];
        for (int i = 0; i < panel.Rows.Length; i++)
            labels[i] = panel.Rows[i].Label;
        return labels;
    }

    private void DrawStaticEventLane(Span<byte> frame, OverlayRect timeline)
    {
        FillRect(frame, timeline, TimelineBackground);
        DrawHorizontalLine(frame, timeline.X, timeline.Right - 1, timeline.Y + timeline.Height / 2,
            new OverlayColor(48, 52, 66, 150));
    }

    private void DrawPcmVoicePanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        (double Min, double Max)? sharedRange = null;
        if (panel.Prepared.MainNotes.Length > 0 && _cameras[panel.Index] != null)
        {
            sharedRange = GetPitchRange(panel, currentSample);
            DrawPitchGrid(frame, panel, _cameras[panel.Index], currentSample, false, sharedRange);
            DrawPitchedPanel(frame, panel, currentSample, false, sharedRange);
        }

        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);
        OverlayRect lane = new(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth),
            timeline.Height);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        SamplePlaybackEvent[] events = panel.Prepared.SamplePlayback;
        int rowCount = Math.Max(1, panel.SampleRowLabels.Length);

        int first;
        if (!(_activeSequentialState?.TryGetPlaybackFirst(
                panel.PlaybackStreamId, events, windowStart, out first) ?? false))
            first = LowerBoundPlayback(events, windowStart);
        for (int index = first; index < events.Length; index++)
        {
            SamplePlaybackEvent value = events[index];
            if (value.StartSample >= windowEnd)
                break;
            if (value.EndSample <= windowStart)
                continue;
            int row = index < panel.SampleRowByPlaybackIndex.Length
                ? panel.SampleRowByPlaybackIndex[index]
                : 0;
            DrawSamplePlayback(frame, panel, value, lane, currentSample, row, rowCount);
        }

        DrawDacActivity(frame, panel, lane, currentSample, windowStart, windowEnd);
        DrawDacHits(frame, panel, lane, currentSample, windowStart, windowEnd);
    }

    private void DrawDacActivity(
        Span<byte> frame,
        PanelData panel,
        OverlayRect lane,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        foreach (DacActivityEvent value in panel.Prepared.DacActivity)
        {
            if (value.EndSample <= windowStart || value.StartSample >= windowEnd)
                continue;

            double left = Math.Max(lane.X,
                _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
            double right = Math.Min(lane.Right,
                _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
            if (right <= left)
                continue;

            double level = Math.Clamp(value.Level, 0, 1);
            int height = Math.Clamp(
                (int)Math.Round(Math.Max(2, lane.Height - 8) * level),
                2,
                Math.Max(2, lane.Height - 4));
            int y = lane.Y + Math.Max(0, (lane.Height - height) / 2);
            bool active = value.StartSample <= currentSample && currentSample < value.EndSample;
            OverlayColor color = IdentityColor(value.SampleId, panel.Prepared.Accent)
                .Lighten(active ? 0.25 : 0.08)
                .WithAlpha((byte)Math.Clamp(95 + level * 110, 0, 220));
            FillRectFractionalX(frame, left, right, y, height, color);
        }
    }

    private void DrawDacHits(
        Span<byte> frame,
        PanelData panel,
        OverlayRect lane,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        foreach (DacHitEvent value in panel.Prepared.DacHits)
        {
            if (value.EndSample <= windowStart || value.StartSample >= windowEnd)
                continue;

            double left = Math.Max(lane.X,
                _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
            double right = Math.Min(lane.Right,
                _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
            if (right <= left)
                continue;

            bool active = value.StartSample <= currentSample && currentSample < value.EndSample;
            OverlayColor color = DacHitColor(value.Classification, panel.Prepared.Accent)
                .WithAlpha((byte)(active ? 235 : 180));
            int width = Math.Max(1, (int)Math.Ceiling(right - left));
            int leftI = Math.Max(lane.X, (int)Math.Floor(left));
            int height = Math.Clamp(
                4 + (int)Math.Round(Math.Clamp(value.PeakLevel, 0, 1) * Math.Min(10, lane.Height - 6)),
                4,
                Math.Max(4, lane.Height - 2));
            int y = lane.Y + Math.Max(0, (lane.Height - height) / 2);
            FillRect(frame, new OverlayRect(leftI, y, Math.Min(width, lane.Right - leftI), height), color);
            if (active)
            {
                StrokeRect(frame, new OverlayRect(leftI, y,
                    Math.Min(width, lane.Right - leftI), height), BrightText.WithAlpha(220), 1);
            }
        }
    }

    private void DrawSamplePlayback(
        Span<byte> frame,
        PanelData panel,
        SamplePlaybackEvent value,
        OverlayRect lane,
        long currentSample,
        int rowIndex = 0,
        int rowCount = 1)
    {
        double left = Math.Max(lane.X, _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        double right = Math.Min(lane.Right, _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
        if (right <= left)
            return;

        // Assign each distinct sample identity a horizontal row band within the
        // lane (spec §19: one row per unique sample).
        int rowHeight = _layout.Variant == VisualizationLayoutVariant.PerformanceLanes
            ? Math.Min(24, Math.Max(12, lane.Height / Math.Max(1, rowCount)))
            : Math.Max(10, lane.Height / Math.Max(1, rowCount));
        int height = Math.Max(8, rowHeight - 2);
        int y = _layout.Variant == VisualizationLayoutVariant.PerformanceLanes
            ? lane.Y + Math.Max(0, (lane.Height - rowCount * rowHeight) / 2)
                + Math.Min(rowCount - 1, Math.Max(0, rowIndex)) * rowHeight
                + (rowHeight - height) / 2
            : lane.Y + Math.Min(rowCount - 1, Math.Max(0, rowIndex)) * rowHeight
                + (rowHeight - height) / 2;

        OverlayColor accent = panel.Prepared.Accent;
        SampleDefinition sample = panel.Prepared.SamplesById.TryGetValue(value.SampleId, out SampleDefinition resolved)
            ? resolved
            : null;
        OverlayColor identity = IdentityColor(value.SampleId, accent);
        FillRectFractionalX(frame, left, right, y, height, identity.WithAlpha(70));
        StrokeRect(frame, new OverlayRect((int)Math.Round(left), y,
            Math.Max(1, (int)Math.Round(right - left)), height), identity.WithAlpha(190), 1);

        if (sample?.Preview is { Length: > 0 } preview)
        {
            OverlayRect viewport = new(
                Math.Max(lane.X, (int)Math.Round(left) + 2),
                y + 2,
                Math.Max(1, (int)Math.Round(right - left) - 4),
                Math.Max(1, height - 4));
            WaveformPreviewRenderer.DrawEnvelope(frame, Width, viewport, preview, identity.WithAlpha(210));
            WaveformPreviewRenderer.DrawLoopRegion(frame, Width, viewport, sample.LoopStart, sample.LoopEnd,
                sample.SourceLengthSamples, BrightText.WithAlpha(170));
        }
        else
        {
            DrawText(frame, Math.Max(lane.X, (int)Math.Round(left) + 3), y + Math.Max(1, height / 2 - 4),
                "PREVIEW UNAVAILABLE", MutedText, 1, Math.Min(lane.Right - 2, (int)Math.Round(right) - 2));
        }

        int startX = (int)Math.Round(_layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        if (startX >= lane.X && startX < lane.Right)
            DrawVerticalLine(frame, startX, y - 1, Math.Min(lane.Bottom - 1, y + height), accent.Lighten(0.45));

        if (value.StartSample <= currentSample && currentSample < value.EndSample)
        {
            double progress = PlaybackProgress(value, sample, currentSample);
            WaveformPreviewRenderer.DrawPlaybackCursor(frame, Width,
                new OverlayRect((int)Math.Round(left), y, Math.Max(1, (int)Math.Round(right - left)), height),
                progress, BrightText.WithAlpha(230));
        }

        string label = ShortAssetLabel(sample?.DisplayName, value.SampleId);
        if (right - left >= 28)
            DrawText(frame, Math.Max(lane.X, (int)Math.Round(left) + 3), y + 1, label,
                BrightText.WithAlpha(210), 1, Math.Min(lane.Right - 2, (int)Math.Round(right) - 2));
    }

    private double PlaybackProgress(
        SamplePlaybackEvent value,
        SampleDefinition sample,
        long currentSample)
    {
        double eventProgress = (currentSample - value.StartSample)
            / (double)Math.Max(1, value.EndSample - value.StartSample);
        if (sample == null || sample.SourceLengthSamples <= 0)
            return Math.Clamp(eventProgress, 0, 1);

        double nativeRate = sample.NativeSampleRate is int rate && rate > 0
            ? rate
            : _timeline.SampleRate;
        double sourcePosition = (currentSample - value.StartSample)
            / (double)_timeline.SampleRate * nativeRate * value.PlaybackRate;
        int sourceLength = sample.SourceLengthSamples;
        if (!value.Looping)
            return Math.Clamp(sourcePosition / sourceLength, 0, 1);

        int loopStart = sample.LoopStart is int start && start < sourceLength ? Math.Max(0, start) : 0;
        int loopEnd = sample.LoopEnd is int end && end > loopStart
            ? Math.Min(sourceLength, end)
            : sourceLength;
        int loopLength = Math.Max(1, loopEnd - loopStart);
        if (sourcePosition < loopStart)
            return Math.Clamp(sourcePosition / sourceLength, 0, 1);

        double loopPosition = (sourcePosition - loopStart) % loopLength;
        if (sample.LoopMode == SampleLoopMode.PingPong)
        {
            double cycle = loopLength * 2.0;
            double phase = (sourcePosition - loopStart) % cycle;
            loopPosition = phase <= loopLength ? phase : cycle - phase;
        }
        return Math.Clamp((loopStart + loopPosition) / sourceLength, 0, 1);
    }

    private void DrawGenericActivityPanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);
        OverlayRect lane = new(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth),
            timeline.Height);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        int barHeight = Math.Max(4, lane.Height / 3);
        int barY = lane.Y + Math.Max(0, (lane.Height - barHeight) / 2);

        foreach (PreparedNote note in panel.Prepared.MainNotes)
        {
            if (note.EndSample <= windowStart || note.StartSample >= windowEnd)
                continue;
            double left = Math.Max(lane.X,
                _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane));
            double right = Math.Min(lane.Right,
                _layout.SampleToX(note.EndSample, currentSample, _timeline.SampleRate, lane));
            if (right > left)
                FillRectFractionalX(frame, left, right, barY, barHeight, note.Fill);
        }

        foreach (SamplePlaybackEvent value in panel.Prepared.SamplePlayback)
        {
            if (value.EndSample <= windowStart || value.StartSample >= windowEnd)
                continue;
            DrawSamplePlayback(frame, panel, value, lane, currentSample);
        }

        foreach (NoiseStateEvent value in panel.Prepared.Noise)
        {
            if (value.EndSample <= windowStart || value.StartSample >= windowEnd)
                continue;
            double left = Math.Max(lane.X,
                _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
            double right = Math.Min(lane.Right,
                _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
            if (right > left)
                FillRectFractionalX(frame, left, right, barY, barHeight, panel.Prepared.Accent.WithAlpha(170));
        }

        foreach (PreparedRhythmEvent value in panel.Prepared.Rhythm)
        {
            if (value.SamplePosition < windowStart || value.SamplePosition >= windowEnd)
                continue;
            int x = (int)Math.Round(_layout.SampleToX(
                value.SamplePosition, currentSample, _timeline.SampleRate, lane));
            if (x >= lane.X && x < lane.Right)
                DrawVerticalLine(frame, x, lane.Y, lane.Bottom - 1, panel.Prepared.Accent);
        }

        foreach (AggregateHitEvent value in panel.Prepared.AggregateHits)
        {
            if (value.SamplePosition < windowStart || value.SamplePosition >= windowEnd)
                continue;
            int x = (int)Math.Round(_layout.SampleToX(
                value.SamplePosition, currentSample, _timeline.SampleRate, lane));
            if (x >= lane.X && x < lane.Right)
                DrawVerticalLine(frame, x, lane.Y, lane.Bottom - 1, panel.Prepared.Accent);
        }
    }

    private void DrawPlaceholderPanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        // The static layer owns the true fallback label. Keeping this path
        // empty prevents sequential renders from accumulating duplicate text.
    }

    private static int LowerBoundPlayback(SamplePlaybackEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].EndSample <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private static string ShortAssetLabel(string displayName, string id)
        => string.IsNullOrWhiteSpace(displayName) ? id : displayName;

    private static OverlayColor IdentityColor(string id, OverlayColor fallback)
    {
        if (string.IsNullOrEmpty(id))
            return fallback;
        uint hash = 2166136261;
        for (int index = 0; index < id.Length; index++)
            hash = (hash ^ id[index]) * 16777619;
        byte r = (byte)(90 + hash % 100);
        byte g = (byte)(90 + (hash >> 8) % 100);
        byte b = (byte)(110 + (hash >> 16) % 100);
        return new OverlayColor(r, g, b, fallback.A);
    }

    private static OverlayColor DacHitColor(DacHitClass classification, OverlayColor fallback)
        => classification switch
        {
            DacHitClass.Kick => new OverlayColor(232, 128, 76, fallback.A),
            DacHitClass.Snare => new OverlayColor(94, 185, 226, fallback.A),
            DacHitClass.Tom => new OverlayColor(181, 126, 232, fallback.A),
            _ => fallback.Lighten(0.10),
        };
}
