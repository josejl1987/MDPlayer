namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private void DrawStaticPcmLanes(Span<byte> frame, PanelData panel, OverlayRect timeline)
    {
        FillRect(frame, new OverlayRect(timeline.X, timeline.Y, _layout.PitchLabelWidth, timeline.Height), HeaderBackground);
        if (panel.Prepared.Rows.Length > 0)
        {
            int rowHeight = Math.Max(1, timeline.Height / panel.Prepared.Rows.Length);
            for (int row = 0; row < panel.Prepared.Rows.Length; row++)
            {
                int y = timeline.Y + row * rowHeight;
                DrawHorizontalLine(frame, timeline.X + _layout.PitchLabelWidth, timeline.Right - 1, y, GridLine);
                DrawGutterLabelRightAligned(
                    frame,
                    timeline,
                    panel.Prepared.Rows[row].Label,
                    y + Math.Max(1, (rowHeight - 7) / 2));
            }
        }
    }

    private void DrawStaticEventLane(Span<byte> frame, OverlayRect timeline)
    {
        FillRect(frame, timeline, TimelineBackground);
        DrawHorizontalLine(frame, timeline.X, timeline.Right - 1, timeline.Y + timeline.Height / 2,
            new OverlayColor(48, 52, 66, 150));
    }

    private void DrawPcmVoicePanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        if (panel.Prepared.MainNotes.Length > 0 && _cameras[panel.Index] != null)
        {
            DrawPitchGrid(frame, panel, _cameras[panel.Index], currentSample, false);
            DrawPitchedPanel(frame, panel, currentSample, false);
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
        int first = LowerBoundPlayback(events, windowStart);
        for (int index = first; index < events.Length; index++)
        {
            SamplePlaybackEvent value = events[index];
            if (value.StartSample >= windowEnd)
                break;
            if (value.EndSample <= windowStart)
                continue;
            DrawSamplePlayback(frame, panel, value, lane, currentSample);
        }
    }

    private void DrawSamplePlayback(
        Span<byte> frame,
        PanelData panel,
        SamplePlaybackEvent value,
        OverlayRect lane,
        long currentSample)
    {
        double left = Math.Max(lane.X, _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        double right = Math.Min(lane.Right, _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
        if (right <= left)
            return;
        int height = Math.Max(12, lane.Height / 2);
        int y = lane.Y + (lane.Height - height) / 2;
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
}
