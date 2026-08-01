namespace Fmp.Core.Visualization.Rendering;

// ScopeStage composition: the compact synchronized activity strip shown
// below the scope mosaic. One lane per active channel, drawn along the same
// sliding time window and playhead as the scopes above. Pitch is
// deliberately absent — the strip is an activity map; the scope wall carries
// the waveform detail.
internal sealed partial class PanelOverlayRenderer
{
    private void DrawScopeStageStrip(Span<byte> frame, long currentSample)
    {
        OverlayRect strip = _layout.ScopeStageStripRect;
        if (strip.Width <= 0 || strip.Height <= 0)
            return;

        FillRect(frame, strip, TimelineBackground);

        int panelCount = Math.Max(1, _panels.Length);
        int laneHeight = Math.Max(2, strip.Height / panelCount);
        int laneGap = Math.Max(1, laneHeight / 8);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        int labelWidth = _layout.PitchLabelWidth;

        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PanelData panel = _panels[panelIndex];
            int laneTop = strip.Y + panelIndex * laneHeight;
            int laneBottom = Math.Min(strip.Bottom, laneTop + laneHeight);
            if (laneBottom - laneTop < 1)
                break;
            int tokenTop = laneTop + laneGap;
            int tokenHeight = Math.Max(1, (laneBottom - laneTop) - laneGap * 2);

            // Lane content region starts after the compact label column.
            var lane = new OverlayRect(strip.X + labelWidth, laneTop,
                Math.Max(0, strip.Width - labelWidth), laneHeight);

            DrawLaneLabel(frame, panel, strip.X, laneTop, labelWidth, laneHeight);

            // Notes: horizontal bars spanning note duration at identity color.
            foreach (PreparedNote note in panel.Prepared.MainNotes)
            {
                if (note.EndSample <= windowStart || note.StartSample >= windowEnd)
                    continue;
                double leftX = _layout.SampleToX(note.StartSample, currentSample, _timeline.SampleRate, lane);
                double rightX = _layout.SampleToX(note.EndSample, currentSample, _timeline.SampleRate, lane);
                FillRectFractionalX(
                    frame,
                    Math.Max(lane.X, leftX),
                    Math.Min(lane.Right, rightX),
                    tokenTop,
                    tokenHeight,
                    note.Fill);
            }

            // Rhythm hits: narrow ticks at onset position.
            foreach (PreparedRhythmEvent rhythm in panel.Prepared.Rhythm)
            {
                if (rhythm.SamplePosition < windowStart || rhythm.SamplePosition >= windowEnd)
                    continue;
                double x = _layout.SampleToX(rhythm.SamplePosition, currentSample, _timeline.SampleRate, lane);
                DrawTick(frame, x, laneTop, laneBottom, panel.Prepared.Accent);
            }

            // Noise states: continuous activity band while the channel runs.
            foreach (NoiseStateEvent noise in panel.Prepared.Noise)
            {
                if (noise.EndSample <= windowStart || noise.StartSample >= windowEnd)
                    continue;
                double leftX = _layout.SampleToX(noise.StartSample, currentSample, _timeline.SampleRate, lane);
                double rightX = _layout.SampleToX(noise.EndSample, currentSample, _timeline.SampleRate, lane);
                FillRectFractionalX(
                    frame,
                    Math.Max(lane.X, leftX),
                    Math.Min(lane.Right, rightX),
                    laneTop + laneHeight / 2,
                    Math.Max(1, laneHeight / 2),
                    panel.Prepared.Accent.WithAlpha(150));
            }

            // Sample playback: solid block while the sample sounds.
            foreach (SamplePlaybackEvent sample in panel.Prepared.SamplePlayback)
            {
                if (sample.EndSample <= windowStart || sample.StartSample >= windowEnd)
                    continue;
                double leftX = _layout.SampleToX(sample.StartSample, currentSample, _timeline.SampleRate, lane);
                double rightX = _layout.SampleToX(sample.EndSample, currentSample, _timeline.SampleRate, lane);
                FillRectFractionalX(
                    frame,
                    Math.Max(lane.X, leftX),
                    Math.Min(lane.Right, rightX),
                    laneTop + laneHeight / 4,
                    Math.Max(1, laneHeight / 2),
                    panel.Prepared.Accent);
            }

            // Aggregate hits: ticks at onset position.
            foreach (AggregateHitEvent hit in panel.Prepared.AggregateHits)
            {
                if (hit.SamplePosition < windowStart || hit.SamplePosition >= windowEnd)
                    continue;
                double x = _layout.SampleToX(hit.SamplePosition, currentSample, _timeline.SampleRate, lane);
                DrawTick(frame, x, laneTop, laneBottom, panel.Prepared.Accent);
            }

            // Subtle lane separator so dense strips stay readable.
            if (panelIndex > 0)
                DrawHorizontalLine(frame, lane.X, laneTop, lane.Right, Border.WithAlpha(60));
        }

        // One shared playhead across the whole strip, aligned with the mosaic.
        int playheadX = strip.X + labelWidth
            + (int)Math.Round(Math.Max(0, strip.Width - labelWidth) * _layout.PlayheadFraction);
        DrawVerticalLine(frame, playheadX, strip.Y, strip.Bottom - 1, Playhead);
        if (playheadX + 1 < strip.Right)
            DrawVerticalLine(frame, playheadX + 1, strip.Y, strip.Bottom - 1, Playhead.WithAlpha(55));
    }

    private void DrawLaneLabel(
        Span<byte> frame,
        PanelData panel,
        int x,
        int y,
        int width,
        int laneHeight)
    {
        if (width < 24 || laneHeight < 5)
            return;
        DrawText(
            frame,
            x + 6,
            y + Math.Max(1, (laneHeight - 7) / 2),
            ShortLaneLabel(panel.Label),
            MutedText,
            1,
            x + width - 4);
    }

    private static string ShortLaneLabel(string label)
    {
        // Channel labels already carry the chip/instance prefix; the strip is
        // 60-ish px tall total, so keep each lane label to the meaningful tail.
        string trimmed = label.Trim();
        if (trimmed.Length <= 8)
            return trimmed;
        // Prefer the last whitespace-separated token (e.g. "FM1 CH1" -> "CH1")
        // so the full channel set stays recognizable at a glance.
        int lastSpace = trimmed.LastIndexOf(' ');
        return lastSpace > 0 ? trimmed[(lastSpace + 1)..] : trimmed[..8];
    }

    private void DrawTick(Span<byte> frame, double x, int y1, int y2, OverlayColor color)
    {
        int left = (int)Math.Round(x);
        if (left >= 0 && left < Width)
        {
            DrawVerticalLine(frame, left, y1, y2 - 1, color);
            if (left + 1 < Width)
                DrawVerticalLine(frame, left + 1, y1, y2 - 1, color.WithAlpha(90));
        }
    }
}
