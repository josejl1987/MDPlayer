namespace Fmp.Core.Visualization.Rendering;

// Rhythm lane drawing seam. Row identity comes from the topology descriptor.
internal sealed partial class PanelOverlayRenderer
{
    private void DrawRhythmPanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect timeline = _layout.GetTimelineRect(panel.Index);
        OverlayRect lane = new(
            timeline.X + _layout.PitchLabelWidth,
            timeline.Y,
            timeline.Width - _layout.PitchLabelWidth,
            timeline.Height);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        PreparedRhythmEvent[] rhythm = panel.Prepared.Rhythm;
        int first = LowerBoundRhythm(rhythm, windowStart);
        PanelRowDefinition[] rows = panel.Prepared.Rows;
        if (rows.Length == 0)
            return;
        int rowHeight = Math.Max(1, lane.Height / rows.Length);
        OverlayColor accent = _panelAccents[panel.Index];

        for (int index = first; index < rhythm.Length; index++)
        {
            PreparedRhythmEvent evt = rhythm[index];
            if (evt.SamplePosition >= windowEnd)
                break;
            int voice = Array.FindIndex(rows, row => string.Equals(row.Id, evt.Voice, StringComparison.Ordinal));
            if (voice < 0)
            {
                // Legacy captures can contain a percussion identity that was
                // not known when the fixed diagnostic rows were prepared.
                // Preserve the hit in the final stable row rather than
                // silently discarding semantic data.
                voice = rows.Length - 1;
            }

            double xFrac = _layout.SampleToX(evt.SamplePosition, currentSample, _timeline.SampleRate, lane);
            int x = (int)Math.Round(xFrac);
            if (x < lane.X || x >= lane.Right)
                continue;

            float strength = Math.Clamp(evt.Strength, 0, 1);
            float energy = GetEnergy(panel.Index, currentSample);
            int pulseWidth = 4 + (int)Math.Round(strength * 12);
            int pulseHeight = Math.Max(3, rowHeight - 3);
            int y = lane.Y + voice * rowHeight + Math.Max(1, (rowHeight - pulseHeight) / 2);
            double pulseLeft = Math.Max(lane.X, xFrac - pulseWidth / 2.0);
            double pulseRight = Math.Min(lane.Right, xFrac + pulseWidth / 2.0);

            // §15.2 decaying horizontal trail: extends right from the onset,
            // holding full brightness for the strong phase then fading to zero
            // alpha by the total decay. Uses absolute event age (§21).
            double ageMs = (currentSample - evt.SamplePosition) * 1000.0 / _timeline.SampleRate;
            if (ageMs >= 0 && ageMs < RhythmTotalDecayMs)
            {
                double decayFraction = ageMs < RhythmStrongPhaseMs
                    ? 1.0
                    : 1.0 - (ageMs - RhythmStrongPhaseMs) / (RhythmTotalDecayMs - RhythmStrongPhaseMs);
                int trailAlpha = (int)Math.Round(110 * strength * decayFraction * (0.85 + energy * 0.15));
                if (trailAlpha > 0)
                {
                    double trailEndX = _layout.SampleToX(
                        evt.SamplePosition + (long)Math.Round(RhythmTotalDecayMs * _timeline.SampleRate / 1000.0),
                        currentSample, _timeline.SampleRate, lane);
                    double trailLeft = Math.Max(lane.X, xFrac + pulseWidth / 2.0);
                    double trailRight = Math.Min(lane.Right, trailEndX);
                    int trailHeight = Math.Max(2, pulseHeight / 2);
                    int trailY = y + (pulseHeight - trailHeight) / 2;
                    if (trailRight > trailLeft)
                    {
                        FillRectFractionalX(frame, trailLeft, trailRight, trailY, trailHeight,
                            accent.WithAlpha((byte)Math.Clamp(trailAlpha, 0, 255)));
                    }
                }
            }

            // Onset impact block (drawn on top of the trail so the attack stays
            // crisp). Alpha scales with strength.
            OverlayColor impact = AdjustForEnergy(accent.Lighten(0.12), energy)
                .WithAlpha((byte)Math.Clamp(Math.Round((150 + strength * 105) * (0.85 + energy * 0.15)), 0, 255));
            FillRectFractionalX(frame, pulseLeft, pulseRight, y, pulseHeight, impact);

            // §15.3 pan tick: a short horizontal mark offset from the voice-row
            // centre by the pan value. The impact block never moves — only the
            // tick indicates left/centre/right.
            int rowCentreY = lane.Y + voice * rowHeight + rowHeight / 2;
            int tickX = x + (int)Math.Round(evt.Pan * RhythmPanTickOffset);
            int tickHalf = 2;
            DrawHorizontalLine(frame, tickX - tickHalf, tickX + tickHalf, rowCentreY,
                BrightText.WithAlpha(200));
        }
    }

}
