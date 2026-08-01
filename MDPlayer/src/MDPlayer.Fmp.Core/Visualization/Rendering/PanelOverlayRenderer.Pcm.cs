namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private void DrawStaticPcmLanes(Span<byte> frame, PanelData panel, OverlayRect timeline)
    {
        FillRect(frame, new OverlayRect(timeline.X, timeline.Y, _layout.PitchLabelWidth, timeline.Height), HeaderBackground);
        if (!string.Equals(panel.Id, "ppz8.0", StringComparison.Ordinal))
            return;

        int rowHeight = Math.Max(1, timeline.Height / 8);
        for (int channel = 0; channel < 8; channel++)
        {
            int y = timeline.Y + channel * rowHeight;
            DrawHorizontalLine(frame, timeline.X + _layout.PitchLabelWidth, timeline.Right - 1, y, GridLine);
            DrawText(frame, timeline.X + 2, y + Math.Max(1, (rowHeight - 7) / 2),
                Ppz8ChannelLabels[channel], MutedText, 1, timeline.X + _layout.PitchLabelWidth - 2);
        }
    }

    private void DrawPcmPanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);
        OverlayRect lane = new(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            timeline.Width - _layout.PitchLabelWidth,
            timeline.Height);
        if (string.Equals(panel.Id, "ppz8.0", StringComparison.Ordinal))
        {
            int rowHeight = Math.Max(1, lane.Height / 8);
            long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
            int first = LowerBoundPpz8(panel.Prepared.Ppz8, windowStart);
            for (int index = first; index < panel.Prepared.Ppz8.Length; index++)
            {
                Ppz8Event value = panel.Prepared.Ppz8[index];
                if (value.StartSample >= _layout.WindowEndSample(currentSample, _timeline.SampleRate))
                    break;
                if (value.Channel is < 0 or >= 8)
                    continue;
                DrawPpz8Event(frame, panel, value, lane, rowHeight, currentSample);
            }
        }
        else
        {
            long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
            int first = LowerBoundAdpcmB(panel.Prepared.AdpcmB, windowStart);
            for (int index = first; index < panel.Prepared.AdpcmB.Length; index++)
            {
                AdpcmBEvent value = panel.Prepared.AdpcmB[index];
                if (value.StartSample >= _layout.WindowEndSample(currentSample, _timeline.SampleRate))
                    break;
                DrawAdpcmBEvent(frame, panel, value, lane, currentSample);
            }
        }
    }

    private void DrawPpz8Event(
        Span<byte> frame,
        PanelData panel,
        Ppz8Event value,
        OverlayRect lane,
        int rowHeight,
        long currentSample)
    {
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        if (value.StartSample >= windowEnd || value.EndSample <= windowStart)
            return;

        double leftX = _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane);
        double rightX = _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane);
        leftX = Math.Max(lane.X, leftX);
        rightX = Math.Min(lane.Right, rightX);
        if (rightX <= leftX)
            return;

        int y = lane.Y + value.Channel * rowHeight + Math.Max(1, rowHeight / 5);
        int height = Math.Max(6, rowHeight - 2);
        double temporal = PcmTemporalOpacity(value.StartSample, currentSample);
        double brightness = 0.85 + 0.15 * Math.Clamp(value.Volume, 0, 1);
        OverlayColor fill = panel.Prepared.Accent.Lighten(0.08 * brightness)
            .WithAlpha((byte)Math.Clamp(Math.Round(255 * temporal * brightness), 0, 255));
        if (rightX - leftX < 4)
        {
            double centre = (leftX + rightX) * 0.5;
            leftX = Math.Max(lane.X, centre - 2);
            rightX = Math.Min(lane.Right, centre + 2);
        }
        FillRectFractionalX(frame, leftX, rightX, y, height, fill);

        int startX = (int)Math.Round(_layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        if (startX >= lane.X && startX < lane.Right)
            DrawVerticalLine(frame, startX, y, Math.Min(lane.Bottom - 1, y + height - 1), panel.Prepared.Accent.Lighten(0.45));

        if (value.MidiNote is double midi)
        {
            int pitchY = y + height - 1 - (int)Math.Round(Math.Clamp(midi, 0, 127) / 127.0 * Math.Max(0, height - 1));
            DrawHorizontalLine(frame, Math.Max(lane.X, startX + 2), Math.Min(lane.Right - 1, startX + 7), pitchY,
                BrightText.WithAlpha(180));
        }

        if (value.SampleNumber is int sampleNumber
            && (uint)sampleNumber < (uint)PpzSampleLabels.Length
            && rightX - leftX >= 24)
        {
            DrawText(frame, Math.Max(lane.X, (int)Math.Round(leftX) + 3),
                y + Math.Max(1, (height - 7) / 2), PpzSampleLabels[sampleNumber],
                BrightText.WithAlpha(190), 1, Math.Min(lane.Right - 1, (int)Math.Round(rightX) - 2));
        }

        int panX = startX + (int)Math.Round(Math.Clamp(value.Pan, -1, 1) * 5);
        DrawHorizontalLine(frame, panX - 2, panX + 2, y + height / 2, BrightText.WithAlpha(190));

        double ageMs = (currentSample - value.StartSample) * 1000.0 / _timeline.SampleRate;
        if (_effects == EffectsMode.All && ageMs >= 0 && ageMs < RippleMs)
            DrawSegmentedPpz8Ripple(frame, startX, y + height / 2, lane, ageMs,
                panel.Prepared.Accent.Lighten(0.35));
    }

    private void DrawAdpcmBEvent(
        Span<byte> frame,
        PanelData panel,
        AdpcmBEvent value,
        OverlayRect lane,
        long currentSample)
    {
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        if (value.StartSample >= windowEnd || value.EndSample <= windowStart)
            return;

        double leftX = Math.Max(lane.X, _layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        double rightX = Math.Min(lane.Right, _layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
        if (rightX <= leftX)
            return;

        int height = Math.Max(8, lane.Height / 3);
        int y = lane.Y + (lane.Height - height) / 2;
        double temporal = PcmTemporalOpacity(value.StartSample, currentSample);
        double brightness = 0.85 + 0.15 * Math.Clamp(value.Level, 0, 1);
        OverlayColor fill = panel.Prepared.Accent.Lighten(0.12 * brightness)
            .WithAlpha((byte)Math.Clamp(Math.Round(255 * temporal * brightness), 0, 255));
        if (rightX - leftX < 4)
        {
            double centre = (leftX + rightX) * 0.5;
            leftX = Math.Max(lane.X, centre - 2);
            rightX = Math.Min(lane.Right, centre + 2);
        }
        FillRectFractionalX(frame, leftX, rightX, y, height, fill);

        int startX = (int)Math.Round(_layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
        if (startX >= lane.X && startX < lane.Right)
            DrawVerticalLine(frame, startX, y - 1, Math.Min(lane.Bottom - 1, y + height), panel.Prepared.Accent.Lighten(0.5));
        if (value.FrequencyHz is > 0 && value.FrequencyHz is double frequency)
        {
            double midi = 69 + 12 * Math.Log2(frequency / 440.0);
            int pitchY = y + height - 1 - (int)Math.Round(Math.Clamp(midi, 0, 127) / 127.0 * Math.Max(0, height - 1));
            DrawHorizontalLine(frame, Math.Max(lane.X, startX + 2), Math.Min(lane.Right - 1, startX + 7), pitchY,
                BrightText.WithAlpha(180));
        }
        int panX = startX + (int)Math.Round(Math.Clamp(value.Pan, -1, 1) * 6);
        DrawHorizontalLine(frame, panX - 2, panX + 2, y + height / 2, BrightText.WithAlpha(190));

        double ageMs = (currentSample - value.StartSample) * 1000.0 / _timeline.SampleRate;
        if (_effects == EffectsMode.All && ageMs >= 0 && ageMs < RippleMs)
            DrawAdpcmSoftRipple(frame, startX, y + height / 2, lane, ageMs,
                panel.Prepared.Accent.Lighten(0.30));
    }

    private double PcmTemporalOpacity(long startSample, long currentSample)
    {
        if (startSample > currentSample)
            return 0.50;
        double ageSeconds = Math.Max(0, (currentSample - startSample) / (double)_timeline.SampleRate);
        return 0.80 - 0.45 * Math.Clamp(ageSeconds / 0.40, 0, 1);
    }

    private static int LowerBoundPpz8(Ppz8Event[] events, long sample)
    {
        int low = 0, high = events.Length;
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

    private static int LowerBoundAdpcmB(AdpcmBEvent[] events, long sample)
    {
        int low = 0, high = events.Length;
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

    private void DrawSegmentedPpz8Ripple(
        Span<byte> frame,
        int cx,
        int cy,
        OverlayRect lane,
        double ageMs,
        OverlayColor accent)
    {
        double t = ageMs / RippleMs;
        int radius = (int)Math.Round(RippleInitialRadius
            + (Math.Clamp(12.0 * Height / 720.0, 8, 18) - RippleInitialRadius) * t);
        int alpha = (int)Math.Round(RippleInitialAlpha * Math.Pow(1.0 - t, 3));
        if (alpha <= 0)
            return;
        OverlayColor color = accent.WithAlpha((byte)Math.Clamp(alpha, 0, 255));
        int left = cx - radius;
        int right = cx + radius;
        int top = cy - radius;
        int bottom = cy + radius;
        for (int x = left; x <= right; x++)
        {
            if (((x - left) & 1) == 0)
            {
                SetPixelClipped(frame, x, top, color, lane);
                SetPixelClipped(frame, x, bottom, color, lane);
            }
        }
        for (int y = top; y <= bottom; y++)
        {
            if (((y - top) & 1) == 0)
            {
                SetPixelClipped(frame, left, y, color, lane);
                SetPixelClipped(frame, right, y, color, lane);
            }
        }
    }

    private void DrawAdpcmSoftRipple(
        Span<byte> frame,
        int cx,
        int cy,
        OverlayRect lane,
        double ageMs,
        OverlayColor accent)
    {
        double t = ageMs / RippleMs;
        int radius = (int)Math.Round(RippleInitialRadius
            + (Math.Clamp(14.0 * Height / 720.0, 10, 20) - RippleInitialRadius) * t);
        int alpha = (int)Math.Round(RippleInitialAlpha * Math.Pow(1.0 - t, 3));
        if (alpha <= 0)
            return;
        DrawRippleRing(frame, cx, cy, radius,
            accent.WithAlpha((byte)Math.Clamp(alpha / 2, 0, 255)), false, lane);
        if (radius > 3)
            DrawRippleRing(frame, cx, cy, radius - 2,
                accent.WithAlpha((byte)Math.Clamp(alpha, 0, 255)), false, lane);
    }
}
