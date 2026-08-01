namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private void DrawNoisePanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect lane = _layout.GetTimelineRect(panel.Index);
        OverlayRect band = new(lane.X + 4, lane.Y + 5, Math.Max(1, lane.Width - 8), Math.Max(12, lane.Height - 24));
        DrawHorizontalLine(frame, band.X, band.Right - 1, band.Y + band.Height / 2,
            new OverlayColor(90, 96, 118, 120));

        NoiseStateEvent[] events = panel.Prepared.Noise;
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        int first = LowerBoundNoise(events, windowStart);
        if (first > 0)
            first--;
        bool active = false;
        int activeIndex = -1;
        for (int index = first; index < events.Length; index++)
        {
            NoiseStateEvent value = events[index];
            if (value.EndSample <= windowStart)
                continue;
            if (value.StartSample >= windowEnd)
                break;
            long age = currentSample - value.StartSample;
            double visualLevel = Math.Clamp(value.Level, 0, 1);
            if (age < 0)
            {
                visualLevel = 0;
            }
            else
            {
                double attack = Math.Clamp(age / Math.Max(1.0, _timeline.SampleRate * 0.030), 0, 1);
                long remaining = value.EndSample - currentSample;
                double release = Math.Clamp(remaining / Math.Max(1.0, _timeline.SampleRate * 0.080), 0, 1);
                visualLevel *= Math.Min(attack, release);
            }
            DrawNoiseTexture(frame, band, panel.Id, value.StartSample, currentSample, visualLevel, value.Mode, value.CentreFrequencyHz);
            if (value.StartSample <= currentSample && currentSample < value.EndSample)
            {
                active = true;
                activeIndex = index;
            }
        }

        string state = active && activeIndex >= 0
            ? panel.Prepared.NoiseLabels[activeIndex]
            : panel.Prepared.HasTrackEvents ? "NOISE" : "SILENT";
        DrawText(frame, lane.X + 6, lane.Bottom - 14, state, active ? BrightText : MutedText, 1, lane.Right - 6);
    }

    private static int LowerBoundNoise(NoiseStateEvent[] events, long sample)
    {
        int low = 0;
        int high = events.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (events[middle].StartSample < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private void DrawNoiseTexture(
        Span<byte> frame,
        OverlayRect band,
        string voiceId,
        long eventStart,
        long currentSample,
        double level,
        NoiseMode mode,
        double? frequency)
    {
        if (level <= 0)
            return;
        int density = mode == NoiseMode.Periodic ? 3 : 2;
        uint seed = StableSeed(voiceId, eventStart, currentSample);
        for (int x = 0; x < band.Width; x += density)
        {
            seed = seed * 1664525u + 1013904223u;
            double value = ((seed >> 8) & 0xFFFF) / 65535.0;
            if (frequency is > 0 and double hz)
            {
                double bandPosition = Math.Clamp(Math.Log10(Math.Max(20, hz) / 20.0) / 3.0, 0, 1);
                double distance = Math.Abs(value - bandPosition);
                value = Math.Max(value, 1 - distance * 2);
            }
            int height = Math.Max(1, (int)Math.Round((0.25 + value * 0.75) * band.Height * level));
            int centre = band.Y + band.Height / 2;
            OverlayColor color = new(120, 156, 230, (byte)Math.Clamp(40 + level * 180, 0, 220));
            for (int y = centre - height / 2; y <= centre + height / 2; y++)
                SetPixel(frame, band.X + x, y, color);
        }
    }

    private static uint StableSeed(string voiceId, long startSample, long currentSample)
    {
        uint hash = 2166136261;
        for (int index = 0; index < voiceId.Length; index++)
            hash = (hash ^ voiceId[index]) * 16777619;
        hash ^= unchecked((uint)startSample);
        hash *= 16777619;
        hash ^= unchecked((uint)currentSample);
        return hash;
    }
}
