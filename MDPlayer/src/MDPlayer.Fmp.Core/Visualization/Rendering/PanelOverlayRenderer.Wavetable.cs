namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private void DrawWavetablePanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);
        OverlayRect viewport = new(
            timeline.X + _layout.PitchLabelWidth + 4,
            timeline.Y + 3,
            Math.Max(1, timeline.Width - _layout.PitchLabelWidth - 8),
            Math.Max(12, timeline.Height / 3));
        WaveformChangeEvent[] changes = panel.Prepared.WaveformChanges;
        int currentIndex = UpperBound(changes, currentSample) - 1;
        WaveformDefinition current = currentIndex >= 0
            ? FindWaveform(panel.Prepared.WaveformsById, changes[currentIndex].WaveformId)
            : panel.Prepared.Waveforms.Length > 0 ? panel.Prepared.Waveforms[0] : null;
        if (currentIndex > 0 && current != null)
        {
            long age = currentSample - changes[currentIndex].SamplePosition;
            long fade = (long)Math.Round(0.150 * _timeline.SampleRate);
            if (age >= 0 && age < fade)
            {
                WaveformDefinition previous = FindWaveform(panel.Prepared.WaveformsById, changes[currentIndex - 1].WaveformId);
                if (previous != null)
                    WaveformPreviewRenderer.DrawPeriodicWaveform(frame, Width, viewport, previous.Preview,
                        IdentityColor(previous.Id, panel.Prepared.Accent).WithAlpha((byte)Math.Clamp(90 - age * 90 / Math.Max(1, fade), 0, 90)));
            }
        }
        if (current == null)
        {
            DrawText(frame, viewport.X + 4, viewport.Y + Math.Max(1, viewport.Height / 2 - 4),
                "NO TABLE DATA", MutedText, 1, viewport.Right - 4);
            return;
        }

        OverlayColor identity = IdentityColor(current.Id, panel.Prepared.Accent);
        WaveformPreviewRenderer.DrawPeriodicWaveform(frame, Width, viewport, current.Preview,
            identity.WithAlpha(230));
        long changeAge = currentIndex >= 0 ? currentSample - changes[currentIndex].SamplePosition : long.MaxValue;
        bool emphasized = changeAge >= 0 && changeAge < (long)Math.Round(0.150 * _timeline.SampleRate);
        StrokeRect(frame, viewport, identity.WithAlpha(emphasized ? (byte)255 : (byte)150), emphasized ? 2 : 1);
        string label = current.DisplayName ?? current.Id;
        DrawText(frame, viewport.X + 4, viewport.Bottom + 2, label, BrightText.WithAlpha(210), 1, viewport.Right - 4);
    }

    private static WaveformDefinition FindWaveform(
        IReadOnlyDictionary<string, WaveformDefinition> waveforms,
        string id) => waveforms.TryGetValue(id, out WaveformDefinition waveform) ? waveform : null;

    private static int UpperBound(WaveformChangeEvent[] changes, long sample)
    {
        int low = 0;
        int high = changes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (changes[middle].SamplePosition <= sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }
}
