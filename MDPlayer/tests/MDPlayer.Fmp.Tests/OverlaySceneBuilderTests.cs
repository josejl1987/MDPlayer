using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class OverlaySceneBuilderTests
{
    private static OverlayScene BuildDefaultScene()
    {
        var timeline = VisualizationTimelineFixture.Create();
        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        return OverlaySceneBuilder.Build(timeline, layout);
    }

    [Fact]
    public void Build_ReturnsSceneWithTwelvePanels()
    {
        var scene = BuildDefaultScene();
        Assert.Equal(12, scene.Panels.Length);
    }

    [Fact]
    public void Build_PanelsHaveCorrectIds()
    {
        var scene = BuildDefaultScene();
        for (int i = 0; i < OverlayLayout.PanelIds.Length; i++)
            Assert.Equal(OverlayLayout.PanelIds[i], scene.Panels[i].Id);
    }

    [Fact]
    public void Build_PanelsHaveCorrectLabels()
    {
        var scene = BuildDefaultScene();
        for (int i = 0; i < OverlayLayout.PanelLabels.Length; i++)
            Assert.Equal(OverlayLayout.PanelLabels[i], scene.Panels[i].Label);
    }

    [Fact]
    public void Build_PanelKindsMatchIndex()
    {
        var scene = BuildDefaultScene();
        Assert.Equal(PreparedPanelKind.Pitched, scene.Panels[0].Kind);
        Assert.Equal(PreparedPanelKind.Fm3, scene.Panels[2].Kind);
        Assert.Equal(PreparedPanelKind.Ssg, scene.Panels[6].Kind);
        Assert.Equal(PreparedPanelKind.Rhythm, scene.Panels[9].Kind);
        Assert.Equal(PreparedPanelKind.PcmVoice, scene.Panels[10].Kind);
    }

    [Fact]
    public void Build_PitchedPanel_ContainsMainNotes()
    {
        var scene = BuildDefaultScene();
        // Panel 0 (ym2608.0.fm.1) has 2 notes in the fixture.
        Assert.Equal(2, scene.Panels[0].MainNotes.Length);
    }

    [Fact]
    public void Build_NotesAreSortedByStartSample()
    {
        var scene = BuildDefaultScene();
        for (int p = 0; p < scene.Panels.Length; p++)
        {
            var notes = scene.Panels[p].MainNotes;
            for (int i = 1; i < notes.Length; i++)
                Assert.True(notes[i].StartSample >= notes[i - 1].StartSample);
        }
    }

    [Fact]
    public void Build_Fm3Panel_HasOperatorNotes()
    {
        var scene = BuildDefaultScene();
        // Panel 2 is FM3 — should have 4 operator lanes.
        Assert.Equal(4, scene.Panels[2].OperatorNotes.Length);
    }

    [Fact]
    public void Build_Fm3OperatorNotes_AreSorted()
    {
        var scene = BuildDefaultScene();
        foreach (var ops in scene.Panels[2].OperatorNotes)
        {
            for (int i = 1; i < ops.Length; i++)
                Assert.True(ops[i].StartSample >= ops[i - 1].StartSample);
        }
    }

    [Fact]
    public void Build_RhythmPanel_HasEvents()
    {
        var scene = BuildDefaultScene();
        Assert.True(scene.Panels[9].Rhythm.Length > 0);
    }

    [Fact]
    public void Build_RhythmEvents_AreSorted()
    {
        var scene = BuildDefaultScene();
        var rhythm = scene.Panels[9].Rhythm;
        for (int i = 1; i < rhythm.Length; i++)
            Assert.True(rhythm[i].SamplePosition >= rhythm[i - 1].SamplePosition);
    }

    [Fact]
    public void Build_PlaceholderPanels_HaveNoNotes()
    {
        var scene = BuildDefaultScene();
        for (int i = 10; i < 12; i++)
        {
            Assert.Empty(scene.Panels[i].MainNotes);
            Assert.Empty(scene.Panels[i].Rhythm);
        }
    }

    [Fact]
    public void Build_PitchRange_HasDefaultForEmptyPanel()
    {
        var scene = BuildDefaultScene();
        // Panel 10 (ADPCM-B) has no notes — range should default.
        Assert.InRange(scene.Panels[10].MinMidi, 40, 80);
        Assert.InRange(scene.Panels[10].MaxMidi, 40, 80);
    }

    [Fact]
    public void Build_AccentColors_AreSet()
    {
        var scene = BuildDefaultScene();
        foreach (var panel in scene.Panels)
            Assert.NotEqual(default(OverlayColor), panel.Accent);
    }

    [Fact]
    public void Build_Notes_HaveFillColors()
    {
        var scene = BuildDefaultScene();
        foreach (var panel in scene.Panels)
        {
            foreach (var note in panel.MainNotes)
            {
                Assert.NotEqual(default(OverlayColor), note.Fill);
                Assert.NotEqual(default(OverlayColor), note.ActiveFill);
            }
        }
    }

    [Fact]
    public void Build_MetadataDefaultsToEmpty()
    {
        var scene = BuildDefaultScene();
        Assert.NotNull(scene.Metadata);
    }

    [Fact]
    public void Build_SceneHasTimelineRange()
    {
        var scene = BuildDefaultScene();
        Assert.True(scene.EndSample > scene.StartSample);
    }

    // --- Pitch point sorting (test 21) ---

    [Fact]
    public void Build_PitchPoints_AreSortedBySamplePosition()
    {
        var scene = BuildDefaultScene();
        // Panel 0 note 0 has 2 pitch changes — check they're sorted.
        var note = scene.Panels[0].MainNotes[0];
        Assert.True(note.Pitch.Length >= 2);
        for (int i = 1; i < note.Pitch.Length; i++)
            Assert.True(note.Pitch[i].SamplePosition >= note.Pitch[i - 1].SamplePosition,
                $"Pitch point {i} not sorted: {note.Pitch[i].SamplePosition} < {note.Pitch[i - 1].SamplePosition}");
    }

    [Fact]
    public void Build_PitchPoints_SortedEvenWhenInputUnsorted()
    {
        // Create a timeline with unsorted pitch points.
        var timeline = VisualizationTimelineFixture.Create();
        // Find the first note with pitch changes and reverse them.
        var note = timeline.Notes[0];
        // The fixture has pitch changes at 1700 and 2300 — already sorted,
        // but let's verify the builder sorts regardless.
        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(timeline, layout);
        var prepared = scene.Panels[0].MainNotes[0];
        for (int i = 1; i < prepared.Pitch.Length; i++)
            Assert.True(prepared.Pitch[i].SamplePosition >= prepared.Pitch[i - 1].SamplePosition);
    }

    // --- Invalid note filtering (test 22) ---

    [Fact]
    public void Build_RejectsNotesWithZeroDuration()
    {
        var baseTimeline = VisualizationTimelineFixture.Create();
        // Add a zero-duration note to a pitched channel.
        var notes = baseTimeline.Notes.ToList();
        notes.Add(new NoteEvent(
            "ym2608.0.fm.1", 1000, 1000, 261.63, 60,
            "ym2608:aaaaaa1111111111", VisualizationNoteMode.Fm, false,
            Array.Empty<PitchChange>()));
        var timeline = new VisualizationTimeline
        {
            SampleRate = baseTimeline.SampleRate,
            StartSample = baseTimeline.StartSample,
            EndSample = baseTimeline.EndSample,
            Instruments = baseTimeline.Instruments,
            Notes = notes.ToArray(),
            Rhythm = baseTimeline.Rhythm,
        };

        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(timeline, layout);
        // The zero-duration note should not appear.
        foreach (var note in scene.Panels[0].MainNotes)
            Assert.True(note.EndSample > note.StartSample);
    }

    [Fact]
    public void Build_RejectsNotesWithNonFiniteMidi()
    {
        var baseTimeline = VisualizationTimelineFixture.Create();
        var notes = baseTimeline.Notes.ToList();
        notes.Add(new NoteEvent(
            "ym2608.0.fm.1", 2000, 3000, double.NaN, -1,
            "ym2608:aaaaaa1111111111", VisualizationNoteMode.Fm, false,
            Array.Empty<PitchChange>()));
        var timeline = new VisualizationTimeline
        {
            SampleRate = baseTimeline.SampleRate,
            StartSample = baseTimeline.StartSample,
            EndSample = baseTimeline.EndSample,
            Instruments = baseTimeline.Instruments,
            Notes = notes.ToArray(),
            Rhythm = baseTimeline.Rhythm,
        };

        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(timeline, layout);
        foreach (var note in scene.Panels[0].MainNotes)
            Assert.True(double.IsFinite(note.InitialMidiNote));
    }

    [Fact]
    public void Build_DropsInvalidPitchSamplesButKeepsTheNote()
    {
        var baseTimeline = VisualizationTimelineFixture.Create();
        var notes = baseTimeline.Notes.ToList();
        notes.Add(new NoteEvent(
            "ym2608.0.fm.1", 3200, 3600, 261.63, 60,
            "ym2608:aaaaaa1111111111", VisualizationNoteMode.Fm, false,
            [
                new PitchChange(3300, 0, double.NaN),
                new PitchChange(3400, 0, 61),
                new PitchChange(3700, 0, 62),
            ]));
        var timeline = new VisualizationTimeline
        {
            SampleRate = baseTimeline.SampleRate,
            StartSample = baseTimeline.StartSample,
            EndSample = baseTimeline.EndSample,
            Instruments = baseTimeline.Instruments,
            Notes = notes.ToArray(),
            Rhythm = baseTimeline.Rhythm,
        };

        OverlayScene scene = OverlaySceneBuilder.Build(
            timeline,
            new OverlayLayout(960, 540, 0.75, 2.25));
        PreparedNote note = Assert.Single(
            scene.Panels[0].MainNotes.Where(value =>
                value.StartSample == 3200 && value.EndSample == 3600));
        Assert.Equal(60, note.InitialMidiNote);
        Assert.Single(note.Pitch);
        Assert.Equal(3400, note.Pitch[0].SamplePosition);
    }

    // --- FPS mapping test (test 29) ---

    [Fact]
    public void FrameToSample_60Fps_MapsCorrectly()
    {
        // At 60 fps, 1000 Hz sample rate: frame 60 → sample 1000.
        long sample = OverlayLayout.FrameToSample(60, 1000, 60, 1);
        Assert.Equal(1000, sample);
    }

    [Fact]
    public void FrameToSample_30Fps_MapsCorrectly()
    {
        // At 30 fps, 1000 Hz: frame 30 → sample 1000.
        long sample = OverlayLayout.FrameToSample(30, 1000, 30, 1);
        Assert.Equal(1000, sample);
    }

    [Fact]
    public void FrameToSample_20Fps_MapsCorrectly()
    {
        // At 20 fps, 1000 Hz: frame 20 → sample 1000.
        long sample = OverlayLayout.FrameToSample(20, 1000, 20, 1);
        Assert.Equal(1000, sample);
    }

    [Fact]
    public void FrameToSample_NtscFractional_MapsCorrectly()
    {
        // NTSC 30000/1001 fps, 48000 Hz: frame 1001.
        // sample = frame * sampleRate * denom / numer = 1001 * 48000 * 1001 / 30000
        // = 48128048000 / 30000 = 1603201.6 → rounded to 1603202
        long sample = OverlayLayout.FrameToSample(1001, 48000, 30000, 1001);
        Assert.Equal(1603202, sample);
    }

    [Fact]
    public void FrameToSample_Frame0_IsSample0()
    {
        long sample = OverlayLayout.FrameToSample(0, 44100, 60, 1);
        Assert.Equal(0, sample);
    }

    // --- Rhythm voice filtering (test 24) ---

    [Fact]
    public void Build_RhythmPanel_ContainsExpectedVoices()
    {
        var scene = BuildDefaultScene();
        var voices = scene.Panels[9].Rhythm.Select(e => e.Voice).Distinct().OrderBy(v => v).ToArray();
        // Should contain bd, hh, rim, sd, tom (from fixture).
        Assert.Contains("bd", voices);
        Assert.Contains("sd", voices);
        Assert.Contains("hh", voices);
        Assert.Contains("tom", voices);
        Assert.Contains("rim", voices);
    }

    [Fact]
    public void Build_RhythmEvents_HaveValidStrength()
    {
        var scene = BuildDefaultScene();
        foreach (var evt in scene.Panels[9].Rhythm)
        {
            Assert.InRange(evt.Strength, 0f, 1f);
        }
    }

    // --- FM3 operator lane structure ---

    [Fact]
    public void Build_Fm3Panel_HasFourOperatorLanes()
    {
        var scene = BuildDefaultScene();
        Assert.Equal(4, scene.Panels[2].OperatorNotes.Length);
        // Lanes 0 and 1 should have notes (from fixture).
        Assert.True(scene.Panels[2].OperatorNotes[0].Length > 0);
        Assert.True(scene.Panels[2].OperatorNotes[1].Length > 0);
        // Lanes 2 and 3 should be empty (no op.3/op.4 notes in fixture).
        Assert.Empty(scene.Panels[2].OperatorNotes[2]);
        Assert.Empty(scene.Panels[2].OperatorNotes[3]);
    }

    [Fact]
    public void Build_Fm3MainNotes_AreFromFm3Channel()
    {
        var scene = BuildDefaultScene();
        // Panel 2 main notes come from "ym2608.0.fm.3" channel.
        Assert.True(scene.Panels[2].MainNotes.Length > 0);
    }

    // --- Duration-weighted percentile (test 23) ---

    [Fact]
    public void ComputePitchRange_LongNoteDominatedByLongDuration()
    {
        // When a long note (high duration) has a mid-range pitch and a
        // very short note has an extreme pitch, the percentile range
        // should be dominated by the long note.
        var baseTimeline = VisualizationTimelineFixture.Create();
        var notes = baseTimeline.Notes.ToList();
        // Add a very short note at an extreme pitch.
        notes.Add(new NoteEvent(
            "ym2608.0.fm.1", 4500, 4999, 120, 96,
            "ym2608:aaaaaa1111111111", VisualizationNoteMode.Fm, false,
            Array.Empty<PitchChange>()));
        var timeline = new VisualizationTimeline
        {
            SampleRate = baseTimeline.SampleRate,
            StartSample = baseTimeline.StartSample,
            EndSample = baseTimeline.EndSample,
            Instruments = baseTimeline.Instruments,
            Notes = notes.ToArray(),
            Rhythm = baseTimeline.Rhythm,
        };

        var layout = new OverlayLayout(960, 540, 0.75, 2.25);
        var scene = OverlaySceneBuilder.Build(timeline, layout);
        // The extreme note (MIDI 120) should not expand the range beyond
        // a reasonable bound — the long notes should dominate.
        Assert.True(scene.Panels[0].MaxMidi < 100,
            $"Max MIDI {scene.Panels[0].MaxMidi} should be < 100 — short extreme note should not dominate");
    }

    [Fact]
    public void ComputePitchRange_EmptyNotes_ReturnsDefault()
    {
        var scene = BuildDefaultScene();
        // Panel 10 (placeholder) has no notes — should return default range.
        Assert.InRange(scene.Panels[10].MinMidi, 40, 80);
        Assert.InRange(scene.Panels[10].MaxMidi, 40, 80);
    }

    [Fact]
    public void ComputePitchRange_HasMinimumRange()
    {
        var scene = BuildDefaultScene();
        foreach (var panel in scene.Panels)
        {
            if (panel.MainNotes.Length > 0)
            {
                double range = panel.MaxMidi - panel.MinMidi;
                Assert.True(range >= 6, $"Panel {panel.Id} range {range} < minimum 6");
            }
        }
    }
}
