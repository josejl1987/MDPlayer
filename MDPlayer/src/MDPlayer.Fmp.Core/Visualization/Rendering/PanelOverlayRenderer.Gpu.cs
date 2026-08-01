namespace Fmp.Core.Visualization.Rendering;

internal sealed partial class PanelOverlayRenderer
{
    private const int GpuPrimitiveWidth = 9;

    private int ComputeGpuPrimitiveCapacity()
    {
        long capacity = _panels.Length;
        foreach (PanelData panel in _panels)
        {
            PreparedPanel prepared = panel.Prepared;
            foreach (PreparedNote note in prepared.MainNotes)
                capacity += Math.Max(2, note.Pitch.Length + 2);
            foreach (PreparedNote[] operatorNotes in prepared.OperatorNotes)
            {
                foreach (PreparedNote note in operatorNotes)
                    capacity += Math.Max(2, note.Pitch.Length + 2);
            }
            capacity += prepared.Rhythm.Length;
            capacity += prepared.SamplePlayback.Length;
            capacity += prepared.Noise.Length;
            capacity += prepared.AggregateHits.Length;
        }

        return checked((int)Math.Max(1, Math.Min(int.MaxValue / GpuPrimitiveWidth, capacity)));
    }

    private void RenderGpuFrame(
        long frameIndex,
        ReadOnlySpan<byte> scopeGrid,
        Span<byte> destination)
    {
        lock (_gpuFrameGate)
        {
            ValidateFrame(frameIndex, destination);
            if (!scopeGrid.IsEmpty && scopeGrid.Length < ScopeFrameByteCount)
                throw new ArgumentException(
                    $"Scope grid requires at least {ScopeFrameByteCount} bytes, got {scopeGrid.Length}.",
                    nameof(scopeGrid));

            _staticFrame.AsSpan().CopyTo(_gpuFrame);
            if (!scopeGrid.IsEmpty)
                PlaceScopeRows(scopeGrid, _gpuFrame);

            // Chrome, pitch grid, playhead, and analysis remain CPU-rasterized;
            // semantic event bodies are intentionally omitted here and supplied as
            // primitives to the OpenCL kernel below.
            DrawDynamicCore(frameIndex, _gpuFrame, drawSemantic: false);
            int primitiveCount = BuildGpuPrimitives(frameIndex, _gpuPrimitiveData);
            _openClRenderer.Render(_gpuFrame, _gpuPrimitiveData, primitiveCount);
            _gpuFrame.AsSpan().CopyTo(destination);
        }
    }

    private int BuildGpuPrimitives(long frameIndex, int[] destination)
    {
        long relativeSample = OverlayLayout.FrameToSample(
            frameIndex,
            _timeline.SampleRate,
            FpsNumerator,
            FpsDenominator);
        long currentSample = Math.Min(_timeline.EndSample, _timeline.StartSample + relativeSample);
        long windowStart = _layout.WindowStartSample(currentSample, _timeline.SampleRate);
        long windowEnd = _layout.WindowEndSample(currentSample, _timeline.SampleRate);
        int count = 0;

        for (int panelIndex = 0; panelIndex < _panels.Length; panelIndex++)
        {
            PanelData panel = _panels[panelIndex];
            OverlayRect timeline = _layout.GetTimelineRect(panel.Index);
            if (timeline.Width <= 0 || timeline.Height <= 0)
                continue;

            switch (panel.TrackKind)
            {
                case VisualizationTrackKind.Pitched:
                case VisualizationTrackKind.FmOperatorGroup:
                case VisualizationTrackKind.WaveTable:
                    count = AddGpuNotes(
                        destination,
                        count,
                        panel,
                        currentSample,
                        windowStart,
                            windowEnd,
                            panel.TrackKind == VisualizationTrackKind.FmOperatorGroup);
                    if (panel.TrackKind == VisualizationTrackKind.FmOperatorGroup)
                    {
                        count = AddGpuOperatorNotes(
                            destination,
                            count,
                            panel,
                            currentSample,
                            windowStart,
                            windowEnd);
                    }
                    break;
                case VisualizationTrackKind.Sample:
                    if (panel.Prepared.MainNotes.Length > 0 && _cameras[panel.Index] != null)
                    {
                        count = AddGpuNotes(
                            destination,
                            count,
                            panel,
                            currentSample,
                            windowStart,
                            windowEnd,
                            reserveFm3OperatorRibbons: false);
                    }
                    count = AddGpuSampleEvents(destination, count, panel, currentSample, windowStart, windowEnd);
                    break;
                case VisualizationTrackKind.Percussion:
                    count = AddGpuRhythmEvents(destination, count, panel, currentSample, windowStart, windowEnd);
                    break;
                case VisualizationTrackKind.Noise:
                    count = AddGpuNoiseEvents(destination, count, panel, currentSample, windowStart, windowEnd);
                    break;
                case VisualizationTrackKind.AggregateActivity:
                    count = AddGpuAggregateEvents(destination, count, panel, currentSample, windowStart, windowEnd);
                    break;
            }

            // Restore the fixed playhead after primitive blending so a note body
            // cannot obscure the contact line.
            if (_layout.HasRoll)
            {
                OverlayRect lane = new(
                    timeline.X + _layout.PitchLabelWidth,
                    timeline.Y,
                    Math.Max(1, timeline.Width - _layout.PitchLabelWidth),
                    timeline.Height);
                int playheadX = _layout.GetPlayheadX(panel.Index);
                count = AddGpuRect(
                    destination,
                    count,
                    playheadX,
                    lane.Y,
                    playheadX + 1,
                    lane.Bottom,
                    Playhead);
            }
        }

        return count;
    }

    private int AddGpuNotes(
        int[] destination,
        int count,
        PanelData panel,
        long currentSample,
        long windowStart,
        long windowEnd,
        bool reserveFm3OperatorRibbons)
    {
        PreparedNote[] notes = panel.Prepared.MainNotes;
        if (notes.Length == 0 || _cameras[panel.Index] == null)
            return count;

        OverlayRect lane = _layout.GetPitchedLaneRect(panel.Index, reserveFm3OperatorRibbons);
        (double minimum, double maximum) = _cameras[panel.Index].GetPreciseRange(currentSample);
        return AddGpuNoteArray(
            destination,
            count,
            panel,
            notes,
            lane,
            currentSample,
            windowStart,
            windowEnd,
            minimum,
            maximum);
    }

    private int AddGpuOperatorNotes(
        int[] destination,
        int count,
        PanelData panel,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        PreparedNote[][] operatorNotes = panel.Prepared.OperatorNotes;
        if (operatorNotes.Length == 0)
            return count;

        OverlayRect ribbons = _layout.GetFm3OperatorRect(panel.Index);
        if (ribbons.Width <= 0 || ribbons.Height <= 0)
            return count;

        (double minimum, double maximum) = GetPitchRange(panel, currentSample);
        int rowHeight = Math.Max(1, ribbons.Height / 4);
        int rows = Math.Min(4, operatorNotes.Length);
        for (int op = 0; op < rows; op++)
        {
            OverlayRect row = new(
                ribbons.X,
                ribbons.Y + op * rowHeight,
                ribbons.Width,
                op == rows - 1 ? ribbons.Bottom - (ribbons.Y + op * rowHeight) : rowHeight);
            count = AddGpuNoteArray(
                destination,
                count,
                panel,
                operatorNotes[op],
                row,
                currentSample,
                windowStart,
                windowEnd,
                minimum,
                maximum,
                operatorRibbon: true);
        }
        return count;
    }

    private int AddGpuNoteArray(
        int[] destination,
        int count,
        PanelData panel,
        PreparedNote[] notes,
        OverlayRect lane,
        long currentSample,
        long windowStart,
        long windowEnd,
        double minimum,
        double maximum,
        bool operatorRibbon = false)
    {
        if (notes.Length == 0 || lane.Width <= 0 || lane.Height <= 0)
            return count;

        int first = LowerBoundGpuNotes(notes, windowStart);
        if (first > 0)
            first--;
        for (int index = first; index < notes.Length; index++)
        {
            PreparedNote note = notes[index];
            if (note.StartSample >= windowEnd)
                break;
            if (note.EndSample <= windowStart
                || note.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise
                || (!operatorRibbon && note.InitialMidiNote < 0))
                continue;

            OverlayColor fill = note.StartSample > currentSample
                ? note.Fill.WithAlpha((byte)Math.Max(1, note.Fill.A / 2))
                : currentSample < note.EndSample ? note.ActiveFill : note.Fill.WithAlpha(100);
            if (operatorRibbon)
                fill = fill.WithAlpha((byte)Math.Clamp((int)Math.Round(fill.A * 0.6), 1, 255));
            int radius = Math.Clamp(lane.Height / 120, 2, 4);
            long segmentStart = Math.Max(note.StartSample, windowStart);
            long segmentEnd = Math.Min(note.EndSample, windowEnd);
            int previousX = (int)Math.Round(_layout.SampleToX(
                segmentStart, currentSample, _timeline.SampleRate, lane));
            int previousY = MidiToY(
                PitchContour.PitchAtSample(note, segmentStart, _samplesPerFrame),
                operatorRibbon ? Math.Round(note.InitialMidiNote) - 2 : minimum,
                operatorRibbon ? Math.Round(note.InitialMidiNote) + 2 : maximum,
                lane);

            foreach (PreparedPitchPoint point in note.Pitch)
            {
                if (point.SamplePosition <= segmentStart || point.SamplePosition >= segmentEnd)
                    continue;
                int pointX = (int)Math.Round(_layout.SampleToX(
                    point.SamplePosition, currentSample, _timeline.SampleRate, lane));
                int pointY = MidiToY(
                    point.MidiNote,
                    operatorRibbon ? Math.Round(note.InitialMidiNote) - 2 : minimum,
                    operatorRibbon ? Math.Round(note.InitialMidiNote) + 2 : maximum,
                    lane);
                count = AddGpuLine(
                    destination,
                    count,
                    previousX,
                    previousY,
                    pointX,
                    pointY,
                    radius,
                    fill);
                previousX = pointX;
                previousY = pointY;
            }

            int endX = (int)Math.Round(_layout.SampleToX(
                segmentEnd, currentSample, _timeline.SampleRate, lane));
            int endY = MidiToY(
                PitchContour.PitchAtSample(note, segmentEnd, _samplesPerFrame),
                operatorRibbon ? Math.Round(note.InitialMidiNote) - 2 : minimum,
                operatorRibbon ? Math.Round(note.InitialMidiNote) + 2 : maximum,
                lane);
            count = AddGpuLine(
                destination,
                count,
                previousX,
                previousY,
                endX,
                endY,
                radius,
                fill);

            int capX = (int)Math.Round(_layout.SampleToX(
                note.StartSample, currentSample, _timeline.SampleRate, lane));
            count = AddGpuRect(
                destination,
                count,
                capX - 1,
                Math.Clamp(previousY - radius, lane.Y, lane.Bottom),
                capX + 2,
                Math.Clamp(previousY + radius + 1, lane.Y, lane.Bottom),
                note.CapFill);
            if (count >= destination.Length / GpuPrimitiveWidth)
                break;
        }
        return count;
    }

    private int AddGpuRhythmEvents(
        int[] destination,
        int count,
        PanelData panel,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        OverlayRect lane = _layout.GetTimelineRect(panel.Index);
        int rows = Math.Max(1, panel.Prepared.Rows.Length);
        int rowHeight = Math.Max(1, lane.Height / rows);
        foreach (PreparedRhythmEvent value in panel.Prepared.Rhythm)
        {
            if (value.SamplePosition < windowStart)
                continue;
            if (value.SamplePosition >= windowEnd)
                break;
            int row = 0;
            for (; row < panel.Prepared.Rows.Length; row++)
            {
                if (string.Equals(panel.Prepared.Rows[row].Id, value.Voice, StringComparison.Ordinal)
                    || string.Equals(panel.Prepared.Rows[row].Label, value.Voice, StringComparison.Ordinal))
                    break;
            }
            row = Math.Min(row, rows - 1);
            int x = (int)Math.Round(_layout.SampleToX(
                value.SamplePosition,
                currentSample,
                _timeline.SampleRate,
                lane));
            OverlayColor color = panel.Prepared.Accent.WithAlpha(
                (byte)Math.Clamp(80 + value.Strength * 175, 0, 255));
            count = AddGpuRect(
                destination,
                count,
                x - 2,
                lane.Y + row * rowHeight + 2,
                x + 3,
                Math.Min(lane.Bottom, lane.Y + (row + 1) * rowHeight - 2),
                color);
            if (count >= destination.Length / GpuPrimitiveWidth)
                break;
        }
        return count;
    }

    private int AddGpuSampleEvents(
        int[] destination,
        int count,
        PanelData panel,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        OverlayRect lane = _layout.GetTimelineRect(panel.Index);
        foreach (SamplePlaybackEvent value in panel.Prepared.SamplePlayback)
        {
            if (value.StartSample >= windowEnd)
                break;
            if (value.EndSample <= windowStart)
                continue;
            int left = (int)Math.Round(_layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
            int right = (int)Math.Round(_layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
            count = AddGpuRect(
                destination,
                count,
                Math.Clamp(left, lane.X, lane.Right),
                lane.Y + Math.Max(1, lane.Height / 4),
                Math.Clamp(Math.Max(left + 2, right), lane.X, lane.Right),
                lane.Y + Math.Max(2, lane.Height * 3 / 4),
                panel.Prepared.Accent.WithAlpha(150));
            if (count >= destination.Length / GpuPrimitiveWidth)
                break;
        }
        return count;
    }

    private int AddGpuNoiseEvents(
        int[] destination,
        int count,
        PanelData panel,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        OverlayRect lane = _layout.GetTimelineRect(panel.Index);
        foreach (NoiseStateEvent value in panel.Prepared.Noise)
        {
            if (value.StartSample >= windowEnd)
                break;
            if (value.EndSample <= windowStart)
                continue;
            int left = (int)Math.Round(_layout.SampleToX(value.StartSample, currentSample, _timeline.SampleRate, lane));
            int right = (int)Math.Round(_layout.SampleToX(value.EndSample, currentSample, _timeline.SampleRate, lane));
            count = AddGpuRect(
                destination,
                count,
                Math.Clamp(left, lane.X, lane.Right),
                lane.Y + Math.Max(1, lane.Height / 3),
                Math.Clamp(Math.Max(left + 2, right), lane.X, lane.Right),
                lane.Y + Math.Max(2, lane.Height * 2 / 3),
                panel.Prepared.Accent.WithAlpha(130));
            if (count >= destination.Length / GpuPrimitiveWidth)
                break;
        }
        return count;
    }

    private int AddGpuAggregateEvents(
        int[] destination,
        int count,
        PanelData panel,
        long currentSample,
        long windowStart,
        long windowEnd)
    {
        OverlayRect lane = _layout.GetTimelineRect(panel.Index);
        foreach (AggregateHitEvent value in panel.Prepared.AggregateHits)
        {
            if (value.SamplePosition < windowStart)
                continue;
            if (value.SamplePosition >= windowEnd)
                break;
            int x = (int)Math.Round(_layout.SampleToX(value.SamplePosition, currentSample, _timeline.SampleRate, lane));
            count = AddGpuRect(
                destination,
                count,
                x - 2,
                lane.Y + 2,
                x + 3,
                lane.Bottom - 2,
                panel.Prepared.Accent.WithAlpha((byte)Math.Clamp(75 + value.Strength * 160, 0, 255)));
            if (count >= destination.Length / GpuPrimitiveWidth)
                break;
        }
        return count;
    }

    private static int LowerBoundGpuNotes(PreparedNote[] notes, long sample)
    {
        int low = 0;
        int high = notes.Length;
        while (low < high)
        {
            int middle = low + (high - low) / 2;
            if (notes[middle].StartSample < sample)
                low = middle + 1;
            else
                high = middle;
        }
        return low;
    }

    private int AddGpuRect(
        int[] destination,
        int count,
        int left,
        int top,
        int right,
        int bottom,
        OverlayColor color)
    {
        if (count >= destination.Length / GpuPrimitiveWidth)
            return count;
        left = Math.Clamp(left, 0, Width);
        right = Math.Clamp(right, 0, Width);
        top = Math.Clamp(top, 0, Height);
        bottom = Math.Clamp(bottom, 0, Height);
        if (right <= left || bottom <= top || color.A == 0)
            return count;
        int offset = count * GpuPrimitiveWidth;
        destination[offset] = left;
        destination[offset + 1] = top;
        destination[offset + 2] = right;
        destination[offset + 3] = bottom;
        destination[offset + 4] = color.R;
        destination[offset + 5] = color.G;
        destination[offset + 6] = color.B;
        destination[offset + 7] = color.A;
        destination[offset + 8] = 0;
        return count + 1;
    }

    private int AddGpuLine(
        int[] destination,
        int count,
        int x0,
        int y0,
        int x1,
        int y1,
        int radius,
        OverlayColor color)
    {
        if (count >= destination.Length / GpuPrimitiveWidth || color.A == 0)
            return count;
        x0 = Math.Clamp(x0, 0, Width - 1);
        x1 = Math.Clamp(x1, 0, Width - 1);
        y0 = Math.Clamp(y0, 0, Height - 1);
        y1 = Math.Clamp(y1, 0, Height - 1);
        int offset = count * GpuPrimitiveWidth;
        destination[offset] = x0;
        destination[offset + 1] = y0;
        destination[offset + 2] = x1;
        destination[offset + 3] = y1;
        destination[offset + 4] = color.R;
        destination[offset + 5] = color.G;
        destination[offset + 6] = color.B;
        destination[offset + 7] = color.A;
        destination[offset + 8] = Math.Max(1, radius);
        return count + 1;
    }
}
