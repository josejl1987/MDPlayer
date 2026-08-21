using Fmp.Application.Contracts;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Avalonia;
using Avalonia.Headless.XUnit;
using Xunit;

namespace Fmp.Gui.Tests;

/// <summary>
/// PR6 workflow tests driving a real MainWindowViewModel against a recording
/// fake preview session: open input, composition changes refresh preview, output
/// changes do not refresh, rapid visual changes coalesce, and the GUI exposes
/// every composition kind.
/// </summary>
public sealed class MainWindowViewModelTests
{
    [AvaloniaFact]
    public async Task OpeningInputCreatesRequestPlanAndPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            Assert.True(h.VM.HasInput);
            Assert.NotNull(h.VM.Request);
            Assert.True(h.PlanCalls >= 1);
            Assert.True(h.FrameCalls >= 1);
            Assert.NotNull(h.VM.Preview.CurrentImage);
            Assert.Equal(GuiState.Ready, h.VM.State);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ExportMidi_CommandWiresCanExecuteChangedAndEnablesAfterOpen()
    {
        Harness h = Harness.Create();
        try
        {
            Assert.NotNull(h.VM.ExportMidiCommand);
            Assert.False(h.VM.ExportMidiCommand.CanExecute(null));

            // Avalonia re-queries CanExecute only when the command raises
            // CanExecuteChanged (e.g. from SetState on input open). Capture the
            // event to prove the button will actually refresh instead of staying
            // greyed out.
            int changedEvents = 0;
            h.VM.ExportMidiCommand.CanExecuteChanged += (_, _) => changedEvents++;

            await h.OpenAsync();

            Assert.True(h.VM.ExportMidiCommand.CanExecute(null));
            Assert.True(changedEvents > 0,
                "ExportMidiCommand must raise CanExecuteChanged when a file opens; " +
                "otherwise Avalonia leaves the MIDI button disabled.");
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task MidiExportOptions_ExposeOnlyRawTranscriptionResolution()
    {
        Harness h = Harness.Create();
        try
        {
            Assert.Equal(960, h.VM.MidiPpq);
            Assert.Contains(960, h.VM.MidiPpqOptions);

            // The musical vocabulary was removed from the surface. Only PPQ
            // survives; everything tied to tempo/meter/quantization/voice
            // projection is gone and must not be resolvable.
            Assert.DoesNotContain("MidiTempoSource", h.VM.GetType().GetProperties().Select(p => p.Name));
            Assert.DoesNotContain("MidiMeter", h.VM.GetType().GetProperties().Select(p => p.Name));
            Assert.DoesNotContain("MidiQuantize", h.VM.GetType().GetProperties().Select(p => p.Name));
            Assert.DoesNotContain("MidiVoiceOptions", h.VM.GetType().GetProperties().Select(p => p.Name));
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OpenInputAsync_RunsSinglePlanThenInitialFrameAndRetainsImage()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            RecordingPreviewSession session = h.Factory.LastSession;

            // Call order: a single plan, followed by a single timeline-only
            // initial still. Plan is not called twice.
            Assert.Equal(1, session.PlanCalls);
            Assert.Equal(PreviewFidelity.TimelineStill, session.FrameFidelities[0]);
            Assert.Equal(1,
                session.FrameFidelities.Count(f => f == PreviewFidelity.TimelineStill));
            Assert.True(session.FrameFidelities.Count >= 1);

            // CurrentImage is populated by the initial frame.
            Assert.NotNull(h.VM.Preview.CurrentImage);

            // A later normal refresh (scrub/visual) must not clear the existing
            // preview image.
            h.VM.CommitScrub();
            await h.VM.WaitForPreviewRefreshAsync();
            Assert.NotNull(h.VM.Preview.CurrentImage);
            Assert.False(h.VM.HasError);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task SelectingSameComposition_DoesNotSchedulePreviewRefresh()
    {
        // CompositionOptionViewModel is a value record, so assigning the
        // already-selected composition is a no-op and must NOT schedule a
        // preview refresh. This remains true when the GUI default is Performance.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            CompositionOptionViewModel selected = h.VM.Settings.Basic.SelectedComposition!;
            h.VM.Settings.Basic.SelectedComposition = selected;

            // No debounce tick fires for a no-op assignment.
            await Task.Delay(400);
            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);

            // An explicit refresh still renders the selected composition.
            await h.VM.RefreshPreviewManuallyAsync();
            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(selected.Value, h.LastRequest!.Composition);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingOutputPathDoesNotRefreshPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.SetOutputPath("/tmp/new-output.mp4");

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingOverwriteDoesNotRefreshPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Advanced.Overwrite = true;

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingEncoderDoesNotRefreshPreview()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Advanced.SelectedEncoder = "Nvenc";

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RapidVisualChangesProduceOneRefresh()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Style.PastSeconds = 0.7m;
            h.VM.Settings.Style.PastSeconds = 0.8m;
            h.VM.Settings.Style.PastSeconds = 0.9m;

            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task CommittedSeekRendersFrameWithoutPlanning()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.PreviewScrubTime = 12;
            h.VM.CommitScrub();
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(12, h.Factory.LastSession.LastFrameTime);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RapidSeeksCoalesceToOneFrame()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.PreviewScrubTime = 10;
            h.VM.CommitScrub();
            h.VM.PreviewScrubTime = 11;
            h.VM.CommitScrub();
            h.VM.PreviewScrubTime = 12;
            h.VM.CommitScrub();

            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(12, h.Factory.LastSession.LastFrameTime);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task SeekThenVisualChange_ProducesOnePlanAndFrame()
    {
        // A seek followed by a visual change must coalesce into a single
        // plan+frame refresh: the pending seek must never downgrade the
        // stronger plan refresh.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.PreviewScrubTime = 5;
            h.VM.CommitScrub();
            h.VM.Settings.Style.PastSeconds = 0.5m;

            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task VisualChangeThenSeek_ProducesOnePlanAndFrame()
    {
        // A visual change followed by a seek must coalesce into a single
        // plan+frame refresh for the newer time.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            h.VM.Settings.Style.PastSeconds = 0.5m;
            h.VM.PreviewScrubTime = 5;
            h.VM.CommitScrub();

            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task WaitingWithoutPendingWorkIsNoOp()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(0, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task StaleFrameIsNotApplied()
    {
        // A superseded refresh must never overwrite the newest frame. Frame A is
        // held in flight (gated) while a newer refresh B starts; releasing A
        // first must not change CurrentImage, and only B's frame is applied.
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            // No ResetCalls here: the per-frame PNG identity is derived from the
            // session's monotonic frame-call index. Opening produces the
            // timeline still (index 0 = 1x1), then the initial refinement
            // replaces it (index 1 = 2x2) so the displayed image is 2x2.
            // refresh A = index 2, refresh B = index 3 (both 3x1).
            Assert.True(h.VM.Preview.IsRefined, "the initial refinement should have completed during open");
            Assert.Equal(new PixelSize(2, 2), h.VM.Preview.CurrentImage!.PixelSize);

            h.Factory.LastSession.GateFrames = true;

            Task refreshA = h.VM.RefreshPreviewManuallyAsync();
            Assert.Single(h.Factory.LastSession.FrameGates);

            Task refreshB = h.VM.RefreshPreviewManuallyAsync();
            Assert.Equal(2, h.Factory.LastSession.FrameGates.Count);

            // Release A's gate first; A is superseded by B so its frame must not
            // replace the refined image currently displayed.
            h.Factory.LastSession.FrameGates[0].SetResult();
            await refreshA;
            Assert.Equal(new PixelSize(2, 2), h.VM.Preview.CurrentImage!.PixelSize);

            // Release B's gate; only B's 3x1 frame becomes CurrentImage.
            h.Factory.LastSession.FrameGates[1].SetResult();
            await refreshB;
            Assert.Equal(new PixelSize(3, 1), h.VM.Preview.CurrentImage!.PixelSize);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public void GuiExposesEveryComposition()
    {
        CompositionKind[] expected = Enum.GetValues<CompositionKind>();

        CompositionKind[] actual = SettingsViewModel.CreateCompositionOptions()
            .Select(option => option.Value)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [AvaloniaFact]
    public async Task FailedOpen_ShowsErrorAndReturnsToUsableState()
    {
        // No prior input: a failed open must not leave the window stuck in LoadingInput.
        Harness h = Harness.Create();
        try
        {
            h.Factory.FailNextOpen = new InvalidOperationException("boom");
            await h.VM.OpenInputAsync(h.InputPath);

            Assert.Equal(GuiState.Empty, h.VM.State);
            Assert.NotNull(h.VM.Error);
        }
        finally
        {
            await h.DisposeAsync();
        }

        // With a previously opened input, a failed re-open must be
        // transactional: the old request stays active with its session intact.
        // (The previous version never created the "second file", so
        // OpenInputAsync exited at the File.Exists check and never exercised
        // FailNextOpen at all.)
        Harness h2 = Harness.Create();
        await h2.OpenAsync();
        try
        {
            string secondPath = Path.Combine(Path.GetTempPath(), "other-" + Guid.NewGuid() + ".vgz");
            await File.WriteAllTextAsync(secondPath, "not really vgz");
            try
            {
                h2.Factory.FailNextOpen = new InvalidOperationException("boom");
                await h2.VM.OpenInputAsync(secondPath);

                Assert.Equal(GuiState.Ready, h2.VM.State);
                Assert.NotNull(h2.VM.Error);
                // The previous request remains fully active...
                Assert.NotNull(h2.VM.Request);
                Assert.Equal(Path.GetFullPath(h2.InputPath), h2.VM.Request!.InputPath);

                // ...and its session is still usable: a preview refresh still works.
                h2.ResetCalls();
                await h2.VM.RefreshPreviewManuallyAsync();
                Assert.Equal(1, h2.PlanCalls);
            }
            finally
            {
                File.Delete(secondPath);
            }
        }
        finally
        {
            await h2.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ExportSettingChangeDuringInFlightPreview_ClearsLoading()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            // Gate the next refresh so it stays in flight while an export-only
            // setting changes.
            h.Factory.LastSession.PlanGate = new TaskCompletionSource();
            Task refreshTask = h.VM.RefreshPreviewManuallyAsync();

            // PlanAsync runs synchronously up to the gate, so the refresh is in flight now.
            Assert.True(h.PlanCalls >= 1);

            h.VM.SetOutputPath("/tmp/in-flight-output.mp4");

            h.Factory.LastSession.PlanGate.SetResult();
            await refreshTask;

            Assert.False(h.VM.Preview.IsLoading, "an export-only change must not leave the preview spinner stuck");
            Assert.Equal("/tmp/in-flight-output.mp4", h.VM.Request!.OutputPath);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OpeningInputProducesTimelineStillThenRefinedFrame()
    {
        // Progressive first paint: opening captures the semantic timeline and
        // renders a timeline-only still first, then the background refinement
        // replaces it with the interactive still.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();

            Assert.Equal(
                new[] { PreviewFidelity.TimelineStill, PreviewFidelity.InteractiveStill },
                h.Factory.LastSession.FrameFidelities);
            Assert.Equal(PreviewFidelity.InteractiveStill, h.VM.Preview.CurrentFidelity);
            Assert.True(h.VM.Preview.IsRefined);
            Assert.Equal("Refined", h.VM.Preview.BadgeText);
            Assert.NotNull(h.VM.Preview.CurrentImage);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task TimelineStillFidelityIsBadgedAsQuickPreview()
    {
        // While refinement is pending (interactive frames fail), the displayed
        // frame stays a TimelineStill and is badged "Quick" — never a
        // hard error dialog, never a cleared first frame.
        Harness h = Harness.Create();
        try
        {
            h.Factory.ConfigureSession = s => s.FailInteractiveFrames = true;
            await h.OpenAsync();

            Assert.Equal(PreviewFidelity.TimelineStill, h.VM.Preview.CurrentFidelity);
            Assert.False(h.VM.Preview.IsRefined);
            Assert.Equal("Quick", h.VM.Preview.BadgeText);
            Assert.True(h.VM.Preview.HasWarning);
            Assert.False(h.VM.Preview.HasError);
            Assert.NotNull(h.VM.Preview.CurrentImage);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task RenderOnlyChangeDuringRefinement_StartsNewInteractiveWaiterWithoutHardFailing()
    {
        // A palette change while interactive preparation is pending must cancel
        // only the obsolete waiter, keep the session-owned stem task alive, and
        // eventually replace the image with a refined frame. Here the fake
        // succeeds, so the refinement completes.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();

            h.VM.ApplyVisualSetting(r => r with
            {
                Style = r.Style with { Palette = PaletteKind.Monochrome },
            });

            await h.VM.WaitForPreviewRefreshAsync();

            // Palette is a frame-style change: no re-plan; the interactive still
            // renders a fresh frame because the preview is already interactive.
            Assert.Equal(0, h.Factory.LastSession.PlanCalls);
            Assert.True(h.Factory.LastSession.FrameCalls >= 1);
            Assert.Equal(PreviewFidelity.InteractiveStill, h.VM.Preview.CurrentFidelity);
            Assert.True(h.VM.Preview.IsRefined);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ViewportResize_SchedulesReplanAtViewportDimensions()
    {
        Harness h = Harness.Create();
        await h.OpenAsync();
        try
        {
            h.ResetCalls();

            // A small viewport: the preview should be requested at (roughly) that
            // logical size rather than the full configured maximum, so the layout
            // resolver can pick overview/full grammar for the displayed size.
            h.VM.SchedulePreviewResize(480, 270);
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.True(h.Factory.LastSession.FrameSizes.Count > 0,
                "RenderFrameAsync should receive concrete preview dimensions");
            (int width, int height) = h.Factory.LastSession.FrameSizes[^1];
            Assert.Equal(480, width);
            Assert.Equal(270, height);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task AccurateFrameCommand_AppliesAccurateStill()
    {
        // One-shot accurate frame: the command renders exactly one AccurateStill
        // frame, and the displayed frame reflects it.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();

            await h.VM.RenderAccuratePreviewAsync();

            Assert.Contains(
                PreviewFidelity.AccurateStill,
                h.Factory.LastSession.FrameFidelities);
            Assert.Equal(PreviewFidelity.AccurateStill, h.VM.Preview.CurrentFidelity);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task SeekAfterAccurate_UsesInteractiveStill()
    {
        // The accurate frame is one-shot: after it applies, a seek requests the
        // normal interactive still because refinement is already ready.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            await h.VM.RenderAccuratePreviewAsync();
            Assert.Equal(PreviewFidelity.AccurateStill, h.VM.Preview.CurrentFidelity);

            h.ResetCalls();
            h.VM.PreviewScrubTime = 12;
            h.VM.CommitScrub();
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(PreviewFidelity.InteractiveStill, h.VM.Preview.CurrentFidelity);
            Assert.True(h.Factory.LastSession.FrameFidelities.Count > 0);
            Assert.All(
                h.Factory.LastSession.FrameFidelities,
                f => Assert.NotEqual(PreviewFidelity.AccurateStill, f));
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task VisualEditAfterAccurate_DoesNotReuseAccurateStill()
    {
        // An accurate frame is never sticky: a subsequent visual edit re-plans and
        // requests the interactive still, not another accurate frame.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            await h.VM.RenderAccuratePreviewAsync();
            Assert.Equal(PreviewFidelity.AccurateStill, h.VM.Preview.CurrentFidelity);

            h.ResetCalls();
            h.VM.ApplyVisualSetting(r => r with
            {
                Style = r.Style with { Palette = PaletteKind.Monochrome },
            });
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(PreviewFidelity.InteractiveStill, h.VM.Preview.CurrentFidelity);
            Assert.All(
                h.Factory.LastSession.FrameFidelities,
                f => Assert.NotEqual(PreviewFidelity.AccurateStill, f));
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task StyleAndPresentationChange_OneFrameZeroPlans()
    {
        // Palette and title are frame-style changes: they render one fresh frame
        // and never rebuild the plan.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();

            h.VM.ApplyVisualSetting(r => r with
            {
                Style = r.Style with { Palette = PaletteKind.Monochrome },
                Presentation = r.Presentation with { Title = "New Title" },
            });
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(0, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
            Assert.Equal(PreviewFidelity.InteractiveStill, h.VM.Preview.CurrentFidelity);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ResizeViewChange_OnePlanOneFrame()
    {
        // View-window change alters the plan, so it re-plans and renders one frame.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();

            h.VM.ApplyVisualSetting(r => r with
            {
                View = r.View with { PastSeconds = 3.0 },
            });
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task TrackSelectionChange_OnePlanOneFrame()
    {
        // Track selection changes scope/projection identity: one plan and one frame.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();

            h.VM.ApplyVisualSetting(r => r with
            {
                Tracks = r.Tracks with { IncludedIds = ["track-1"] },
            });
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.Equal(1, h.FrameCalls);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task PlaybackChange_OnePlanOneFrameAndImmediateTimelineStill()
    {
        // A playback change is a timeline-capture change: the interactive preview
        // is marked stale, so the immediately rendered frame is a TimelineStill.
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();

            h.VM.ApplyVisualSetting(r => r with
            {
                Playback = r.Playback with { LoopCount = 3 },
            });
            await h.VM.WaitForPreviewRefreshAsync();

            Assert.Equal(1, h.PlanCalls);
            Assert.True(h.Factory.LastSession.FrameFidelities.Count >= 1);
            Assert.Equal(
                PreviewFidelity.TimelineStill,
                h.Factory.LastSession.FrameFidelities[0]);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OpnaBackend_Default_IsMdsound()
    {
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            // Default (both a fresh request and the view-model property) is MDSound.
            Assert.Equal(FmpOpnaBackend.Mdsound, h.LastRequest!.Playback.OpnaBackend);
            Assert.Equal(FmpOpnaBackend.Mdsound, h.VM.Settings.Advanced.SelectedOpnaBackend);
            Assert.Equal(0, h.VM.Settings.Advanced.SelectedOpnaBackendIndex);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task SelectingNativeAudio_NotifiesAndRoutesIntoRequest()
    {
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            bool notified = false;
            h.VM.Settings.Advanced.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(AdvancedSettingsViewModel.SelectedOpnaBackend)
                    || e.PropertyName == nameof(AdvancedSettingsViewModel.SelectedOpnaBackendIndex))
                    notified = true;
            };

            h.VM.Settings.Advanced.SelectedOpnaBackendIndex = 1;

            Assert.True(notified, "property notification must occur");
            Assert.Equal(FmpOpnaBackend.NativeAudio, h.VM.Settings.Advanced.SelectedOpnaBackend);
            Assert.Equal(1, h.VM.Settings.Advanced.SelectedOpnaBackendIndex);
            // The authoritative request (the one preview/export read) carries NativeAudio.
            Assert.Equal(FmpOpnaBackend.NativeAudio, h.VM.Request!.Playback.OpnaBackend);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ChangingOpnaBackend_InvalidatesTimelineCapturePreview()
    {
        Harness h = Harness.Create();
        try
        {
            await h.OpenAsync();
            h.ResetCalls();
            h.VM.ApplyVisualSetting(r => r with
            {
                Playback = r.Playback with { OpnaBackend = FmpOpnaBackend.NativeAudio },
            });
            await h.VM.WaitForPreviewRefreshAsync();

            // A backend change is a capture-affecting change: the interactive
            // preview is stale, so the next frame is a timelineStill.
            Assert.Equal(1, h.PlanCalls);
            Assert.True(h.Factory.LastSession.FrameFidelities.Count >= 1);
            Assert.Equal(PreviewFidelity.TimelineStill, h.Factory.LastSession.FrameFidelities[0]);
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task OpnaBackend_Selector_VisibleForFmpInputsHiddenForNonFmp()
    {
        Harness fmp = Harness.CreateFmp("ovi");
        try
        {
            await fmp.OpenAsync();
            Assert.True(fmp.VM.Settings.Advanced.ShowOpnaBackend);
        }
        finally
        {
            await fmp.DisposeAsync();
        }

        Harness nonFmp = Harness.Create(); // `.vgz`
        try
        {
            await nonFmp.OpenAsync();
            Assert.False(nonFmp.VM.Settings.Advanced.ShowOpnaBackend);
        }
        finally
        {
            await nonFmp.DisposeAsync();
        }
    }

    private sealed class Harness
    {
        public MainWindowViewModel VM { get; private set; } = null!;
        public RecordingPreviewFactory Factory { get; private set; } = null!;
        public IReadOnlyList<CompositionOptionViewModel> Compositions { get; private set; } = null!;
        private string _inputPath = "";
        private string _settingsPath = "";

        public int PlanCalls => Factory.PlanCalls;
        public int FrameCalls => Factory.FrameCalls;
        public VisualizationRequest? LastRequest => Factory.LastRequest;
        public string InputPath => _inputPath;

        public static Harness CreateFmp(string extension)
        {
            string inputPath = Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-test-" + Guid.NewGuid() + "." + extension);
            File.WriteAllBytes(inputPath, new byte[] { 0x4f, 0x56, 0x4d });
            string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");
            return CreateCore(new GuiSettingsStore(settingsPath), inputPath, settingsPath);
        }

        public static Harness Create()
        {
            string inputPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-test-" + Guid.NewGuid() + ".vgz");
            File.WriteAllBytes(inputPath, new byte[] { 0x56, 0x67, 0x6d });
            string settingsPath = Path.Combine(Path.GetTempPath(), "mdplayer-gui-settings-" + Guid.NewGuid() + ".json");
            return CreateCore(new GuiSettingsStore(settingsPath), inputPath, settingsPath);
        }

        private static Harness CreateCore(GuiSettingsStore settings, string inputPath, string settingsPath)
        {
            var factory = new RecordingPreviewFactory();
            var vm = new MainWindowViewModel(
                settings,
                new FileDialogService(),
                new ClipboardService(),
                new ExportProcessService(null),
                factory,
                initialInputPath: null);

            return new Harness
            {
                VM = vm,
                Factory = factory,
                Compositions = SettingsViewModel.CreateCompositionOptions(),
                _inputPath = inputPath,
                _settingsPath = settingsPath,
            };
        }

        public async Task OpenAsync()
        {
            await VM.OpenInputAsync(_inputPath);
            await VM.WaitForPreviewRefreshAsync();
        }

        public void ResetCalls() => Factory.ResetCalls();

        public async Task DisposeAsync()
        {
            await VM.ShutdownAsync();
            try { File.Delete(_inputPath); } catch { }
            try { File.Delete(_settingsPath); } catch { }
        }
    }
}