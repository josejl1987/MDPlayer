namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private const double AggregateDecaySeconds = 0.180;

    private void DrawAggregatePanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect lane = _layout.GetTimelineRect(panel.Index);
        string[] subVoices = panel.Prepared.AggregateSubVoices;
        if (subVoices.Length == 0)
        {
            DrawText(frame, lane.X + 8, lane.Y + Math.Max(2, lane.Height / 2 - 4),
                panel.Prepared.HasTrackEvents ? "AGGREGATE" : "SILENT", MutedText, 1, lane.Right - 8);
            return;
        }

        int columns = 4;
        int rows = (subVoices.Length + columns - 1) / columns;
        int cellWidth = Math.Max(1, lane.Width / columns);
        int cellHeight = Math.Max(1, lane.Height / rows);
        for (int slot = 0; slot < subVoices.Length && slot < 16; slot++)
        {
            int column = slot % columns;
            int row = slot / columns;
            OverlayRect cell = new(lane.X + column * cellWidth, lane.Y + row * cellHeight,
                column == columns - 1 ? lane.Right - lane.X - column * cellWidth : cellWidth,
                row == rows - 1 ? lane.Bottom - lane.Y - row * cellHeight : cellHeight);
            AggregateHitEvent? active = FindRecentHit(panel.Prepared.AggregateHits, subVoices[slot], currentSample);
            double brightness = active is AggregateHitEvent hit
                ? Math.Clamp(1 - (currentSample - hit.SamplePosition) /
                    Math.Max(1.0, AggregateDecaySeconds * _timeline.SampleRate), 0, 1) * Math.Clamp(hit.Strength, 0, 1)
                : 0;
            OverlayColor fill = panel.Prepared.Accent.Lighten(0.10 + brightness * 0.45)
                .WithAlpha((byte)Math.Clamp(65 + brightness * 160, 0, 255));
            FillRect(frame, new OverlayRect(cell.X + 1, cell.Y + 1, Math.Max(1, cell.Width - 2), Math.Max(1, cell.Height - 2)), fill);
            StrokeRect(frame, cell, panel.Prepared.Accent.WithAlpha((byte)Math.Clamp(110 + brightness * 145, 0, 255)),
                brightness > 0 ? 2 : 1);
            string label = panel.Prepared.AggregateLabels.TryGetValue(subVoices[slot], out string preparedLabel)
                ? preparedLabel
                : subVoices[slot];
            DrawText(frame, cell.X + 3, cell.Y + Math.Max(1, cell.Height / 2 - 4), label,
                BrightText.WithAlpha((byte)Math.Clamp(130 + brightness * 125, 0, 255)), 1, cell.Right - 3);
            if (active is AggregateHitEvent hitWithPan)
            {
                int panX = cell.X + cell.Width / 2 + (int)Math.Round(Math.Clamp(hitWithPan.Pan, -1, 1) * Math.Max(1, cell.Width / 2 - 5));
                DrawVerticalLine(frame, panX, cell.Y + 2, cell.Bottom - 3, BrightText.WithAlpha(180));
            }
            DrawHitTrail(frame, cell, panel.Prepared.AggregateHits, subVoices[slot], currentSample);
        }
    }

    private void DrawHitTrail(
        Span<byte> frame,
        OverlayRect cell,
        AggregateHitEvent[] hits,
        string subVoiceId,
        long currentSample)
    {
        long historySamples = Math.Max(1, (long)Math.Round(1.5 * _timeline.SampleRate));
        long historyStart = currentSample - historySamples;
        int upper = UpperBound(hits, currentSample);
        int markers = 0;
        for (int index = upper - 1; index >= 0 && markers < 8; index--)
        {
            AggregateHitEvent hit = hits[index];
            if (hit.SamplePosition < historyStart)
                break;
            if (!string.Equals(hit.SubVoiceId, subVoiceId, StringComparison.Ordinal))
                continue;
            double age = (currentSample - hit.SamplePosition) / (double)historySamples;
            int x = cell.X + 3 + (int)Math.Round((1 - Math.Clamp(age, 0, 1)) * Math.Max(1, cell.Width - 7));
            byte alpha = (byte)Math.Clamp(70 + Math.Clamp(hit.Strength, 0, 1) * 150, 0, 255);
            DrawVerticalLine(frame, x, Math.Max(cell.Y + 2, cell.Bottom - 5), cell.Bottom - 2,
                BrightText.WithAlpha(alpha));
            markers++;
        }
    }

    private static AggregateHitEvent? FindRecentHit(
        AggregateHitEvent[] hits,
        string subVoiceId,
        long currentSample)
    {
        int high = UpperBound(hits, currentSample) - 1;
        for (int index = high; index >= 0; index--)
        {
            AggregateHitEvent hit = hits[index];
            if (hit.SubVoiceId == subVoiceId)
                return hit;
        }
        return null;
    }

    private static int UpperBound(AggregateHitEvent[] hits, long sample)
    {
        int low = 0;
        int high = hits.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (hits[middle].SamplePosition <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
