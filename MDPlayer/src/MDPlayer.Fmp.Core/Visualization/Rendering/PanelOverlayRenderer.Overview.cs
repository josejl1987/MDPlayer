namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Compact overview rendering for the fallback layout variants
/// (<see cref="VisualizationLayoutVariant.DiagnosticOverview"/> and
/// <see cref="VisualizationLayoutVariant.DeviceOverview"/>). These panels keep
/// the channel/device name, accent identity, current note/state and a compact
/// per-channel or aggregate waveform, but deliberately omit the piano roll,
/// pitch gutter, patch tokens, operator ribbons, time-grid labels and
/// instrument-change overlays that cannot fit.
/// </summary>
internal sealed partial class PanelOverlayRenderer
{
    private void DrawOverviewStaticPanel(Span<byte> frame, PanelData panel)
    {
        OverlayRect panelRect = _layout.GetPanelRect(panel.Index);
        OverlayRect header = _layout.GetHeaderRect(panel.Index);
        OverlayRect scope = _layout.GetScopeRect(panel.Index);

        FillRect(frame, panelRect, HeaderBackground);
        ClearRect(frame, scope);
        StrokeRect(frame, panelRect, Border, 1);
        StrokeRect(frame, scope, Border.WithAlpha(180), 1);

        OverlayColor accent = _panelAccents[panel.Index];
        FillRect(frame, new OverlayRect(header.X, header.Y, 4, header.Height), accent);
        if (header.Height > 0)
        {
            int labelScale = header.Height >= 20 ? 2 : 1;
            DrawText(
                frame,
                header.X + 10,
                header.Y + Math.Max(1, (header.Height - 7 * labelScale) / 2),
                panel.Label,
                BrightText,
                labelScale,
                header.Right - 8);
        }
    }

    private void DrawOverviewDynamicPanel(Span<byte> frame, PanelData panel, long currentSample)
    {
        OverlayRect panelRect = _layout.GetPanelRect(panel.Index);
        OverlayRect scope = _layout.GetScopeRect(panel.Index);

        string state = CurrentStateText(panel, currentSample);
        string activityLabel = ActivityLabel(panel, currentSample);

        // Compact current state in the header remainder (after the accent bar
        // and name gutter), faded for the device roll-up.
        OverlayRect header = _layout.GetHeaderRect(panel.Index);
        if (header.Height > 0 && !string.IsNullOrEmpty(state))
        {
            int scale = header.Height >= 20 ? 2 : 1;
            int textY = header.Y + Math.Max(1, (header.Height - 7 * scale) / 2);
            int left = header.X + 78;
            string compact = Ellipsize(state, scale, Math.Max(0, header.Right - left - 12));
            if (!string.IsNullOrEmpty(compact))
                DrawText(frame, left, textY, compact, BrightText, scale, header.Right - 8);
        }

        // Compact activity waveform / indicator inside the transparent scope
        // region.
        if (scope.Width > 0 && scope.Height > 0)
        {
            if (TryGetCurrentWaveform(panel, out WaveformDefinition waveform))
            {
                OverlayColor fill = panel.Prepared.Accent;
                WaveformPreviewRenderer.DrawPeriodicWaveform(
                    frame,
                    Width,
                    new OverlayRect(scope.X + 1, scope.Y + 1, Math.Max(1, scope.Width - 2), Math.Max(1, scope.Height - 2)),
                    waveform.Preview,
                    fill);
                DrawText(frame, scope.X + 3, scope.Y + Math.Max(1, scope.Height - 12),
                    HasCurrentActivity(panel, currentSample) ? "active" : "idle",
                    HasCurrentActivity(panel, currentSample) ? BrightText : MutedText,
                    1, scope.Right - 3);
            }
            else
            {
                // No discrete waveform — draw an activity bar driven by energy
                // (or contact activity), filling left-to-right with brightness.
                float energy = GetEnergy(panel.Index, currentSample);
                bool activeSpot = HasCurrentActivity(panel, currentSample);
                byte baseAlpha = (byte)Math.Clamp(60 + (int)(energy * 150), 60, 255);
                byte alpha = activeSpot ? (byte)Math.Max((int)baseAlpha, 220) : baseAlpha;
                OverlayColor fill = panel.Prepared.Accent.WithAlpha(alpha);
                int barWidth = Math.Max(1, (int)(scope.Width * Math.Clamp(energy, 0, 1)));
                FillRect(frame, new OverlayRect(scope.X + 1, scope.Y + 1, barWidth, Math.Max(1, scope.Height - 2)), fill);
                string label = _layout.Variant == VisualizationLayoutVariant.DeviceOverview
                    ? activityLabel
                    : activeSpot ? "active" : HasTrackAudio(panel.Index) ? "active" : "idle";
                DrawText(frame, scope.X + 3, scope.Y + Math.Max(1, scope.Height - 12),
                    label, activeSpot || HasTrackAudio(panel.Index) ? BrightText : MutedText,
                    1, scope.Right - 3);
            }
        }
    }

    private string ActivityLabel(PanelData panel, long currentSample)
    {
        // Device overview: "N active" where N counts the active channels.
        int activeCount = CountActiveChannels(panel, currentSample);
        string cached = _activityLabelCache[panel.Index];
        if (_activityLabelCounts[panel.Index] == activeCount && cached is not null)
            return cached;

        int total = panel.Prepared.MainNotes.Length > 0 || panel.Prepared.Rhythm.Length > 0
            ? Math.Max(activeCount, 1)
            : activeCount;
        string label = activeCount > 0
            ? $"{activeCount} active"
            : total > 0 ? "idle" : "silent";
        _activityLabelCounts[panel.Index] = activeCount;
        _activityLabelCache[panel.Index] = label;
        return label;
    }

    private int CountActiveChannels(PanelData panel, long currentSample)
    {
        PreparedPanel prepared = panel.Prepared;
        int count = 0;
        if (ContainsRecentNote(prepared.MainNotes,
                panel.MainNoteStreamId,
                currentSample - (long)Math.Round(0.5 * _timeline.SampleRate), currentSample))
            count++;
        int operatorCount = Math.Min(prepared.OperatorNotes.Length, panel.OperatorNoteStreamIds.Length);
        for (int operatorIndex = 0; operatorIndex < operatorCount; operatorIndex++)
        {
            if (ContainsRecentNote(prepared.OperatorNotes[operatorIndex],
                    panel.OperatorNoteStreamIds[operatorIndex],
                    currentSample - (long)Math.Round(0.5 * _timeline.SampleRate), currentSample))
                count++;
        }
        long recentStart = currentSample - (long)Math.Round(0.5 * _timeline.SampleRate);
        int rhythmFirst;
        if (!(_activeSequentialState?.TryGetRhythmFirst(
                panel.RhythmStreamId, prepared.Rhythm, recentStart, out rhythmFirst) ?? false))
            rhythmFirst = LowerBoundRhythm(prepared.Rhythm, recentStart);
        if (rhythmFirst < prepared.Rhythm.Length && prepared.Rhythm[rhythmFirst].SamplePosition <= currentSample)
            count++;
        return count;
    }

    private bool HasTrackAudio(int panelIndex) => HasEnergyEnvelope(panelIndex) && HasAudioEnergy(panelIndex);

    private bool TryGetCurrentWaveform(PanelData panel, out WaveformDefinition waveform)
    {
        // Compact overview: use the panel's first available waveform. Precise
        // waveform-change selection is not meaningful at overview scale.
        if (panel.Prepared.Waveforms.Length > 0)
        {
            waveform = panel.Prepared.Waveforms[0];
            return true;
        }
        waveform = null;
        return false;
    }

    private bool HasCurrentActivity(PanelData panel, long currentSample)
    {
        if (HasRecentActivity(panel.Index, currentSample))
            return true;
        if (HasPanelContactActivity(panel, currentSample))
            return true;
        return FindActive(panel.Prepared.MainNotes, panel.MainNoteStreamId, currentSample) != null;
    }

    private string CurrentStateText(PanelData panel, long currentSample)
    {
        PreparedNote active = FindActive(panel.Prepared.MainNotes, panel.MainNoteStreamId, currentSample);
        if (active == null && panel.TrackKind == VisualizationTrackKind.FmOperatorGroup)
        {
            int operatorCount = Math.Min(
                panel.Prepared.OperatorNotes.Length, panel.OperatorNoteStreamIds.Length);
            for (int operatorIndex = 0; operatorIndex < operatorCount; operatorIndex++)
            {
                active = FindActive(
                    panel.Prepared.OperatorNotes[operatorIndex],
                    panel.OperatorNoteStreamIds[operatorIndex],
                    currentSample);
                if (active != null)
                    break;
            }
        }

        if (active != null && IsSsgMode(active.Mode)
            && currentSample >= active.StartSample
            && currentSample - active.StartSample <= (long)Math.Round(0.600 * _timeline.SampleRate))
        {
            return SsgModeToken(active.Mode);
        }
        if (active == null)
        {
            if (HasCurrentActivity(panel, currentSample))
                return "active";
            PreparedRhythmEvent[] rhythm = panel.Prepared.Rhythm;
            if (rhythm.Length > 0)
                return "percussion";
            if (panel.Prepared.HasTrackEvents)
                return "armed";
            return "idle";
        }
        return active.Mode is VisualizationNoteMode.SsgNoise or VisualizationNoteMode.SsgEnvelopeNoise
            ? "NOISE"
            : FormatPitchWithCents(PitchContour.PitchAtSample(active, currentSample, _samplesPerFrame));
    }
}
