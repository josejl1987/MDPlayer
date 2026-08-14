using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Xunit;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Unit tests for the voice-state normalization stage (INV3, INV5, INV6, INV7):
/// stage-level merge/suppress/bridge over synthetic timelines. The stage is a
/// pure function, so no corpus files or playback backends are needed.
/// </summary>
public sealed class VoiceStateNormalizationStageTests
{
    private const int Sr = 44_100;

    private static readonly string Voice = "ay8910.0.psg.1";
    private static readonly string Instrument = "ay8910:0:tone:0";

    private static NoteEvent Note(long start, long end, double initial, params PitchChange[] pitch)
        => new(Voice, start, end, 440, initial, Instrument, VisualizationNoteMode.SsgTone, false, pitch);

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        StartSample = 0,
        EndSample = 8_000_000,
        SampleRate = Sr,
        Notes = notes,
        Beats = new[] { new BeatEvent(0, 0) },
    };

    private static VisualizationTimeline Normalize(VisualizationTimeline timeline) =>
        VoiceStateNormalizationStage.Normalize(timeline);

    // ---- INV7: merge identical adjacent segments ----------------------------------

    [Fact]
    public void Merge_AdjacentIdenticalPitchSegments_Unconditional()
    {
        // Same channel/instrument/mode, pitch joins exactly, zero gap: one note.
        var result = Normalize(Timeline(
            Note(0, 1000, 60.0),
            Note(1000, 2000, 60.0),
            Note(2000, 3000, 60.0)));

        Assert.Single(result.Notes);
        Assert.Equal(0, result.Notes[0].StartSample);
        Assert.Equal(3000, result.Notes[0].EndSample);
    }

    [Fact]
    public void Merge_SubTickGap_IdenticalPitch_Merges()
    {
        // A short source-time gap with identical boundary pitch is one sustained
        // note; MIDI tick collisions are deliberately not consulted here.
        var result = Normalize(Timeline(
            Note(0, 1000, 74.0),
            Note(1030, 2030, 74.0)));

        Assert.Single(result.Notes);
        Assert.Equal(0, result.Notes[0].StartSample);
        Assert.Equal(2030, result.Notes[0].EndSample);
    }

    [Fact]
    public void Merge_DifferentPitch_DoesNotMerge()
    {
        var result = Normalize(Timeline(
            Note(0, 1000, 60.0),
            Note(1000, 2000, 61.0)));

        Assert.Equal(2, result.Notes.Count);
    }

    [Fact]
    public void Merge_TickSpanningGap_DoesNotMerge()
    {
        // 1500-sample gap = 1.5 ticks: a real separation, not a transient.
        var result = Normalize(Timeline(
            Note(0, 1000, 60.0),
            Note(2500, 3500, 60.0)));

        Assert.Equal(2, result.Notes.Count);
    }

    [Fact]
    public void Merge_DifferentInstrument_DoesNotMerge()
    {
        var a = Note(0, 1000, 60.0);
        var b = Note(1000, 2000, 60.0) with { InstrumentId = "other" };
        var result = Normalize(Timeline(a, b));

        Assert.Equal(2, result.Notes.Count);
    }

    [Fact]
    public void Merge_Retrigger_DoesNotMerge()
    {
        // A deliberate same-pitch retrigger (SCC frequency change while open) is
        // a real boundary, never merged (INV3).
        var a = Note(0, 1000, 60.0);
        var b = Note(1000, 2000, 60.0) with { IsRetrigger = true };
        var result = Normalize(Timeline(a, b));

        Assert.Equal(2, result.Notes.Count);
    }

    [Fact]
    public void Merge_AcrossLoopMarker_DoesNotMerge()
    {
        // A note must not silently span a loop pass.
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 8_000_000,
            SampleRate = Sr,
            Notes = new[] { Note(0, 1000, 60.0), Note(1000, 2000, 60.0) },
            Beats = new[] { new BeatEvent(0, 0) },
            LoopMarkers = new[] { new LoopMarker(1000, LoopMarkerKind.Restart, 1) },
        };

        var result = Normalize(timeline);
        Assert.Equal(2, result.Notes.Count);
    }

    // ---- INV5: out-of-domain pitch is suppressed, never clamped -------------------

    [Fact]
    public void Suppress_InitialAboveMidiDomain_NoNote()
    {
        var result = Normalize(Timeline(Note(0, 1000, 128.0)));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Suppress_InitialBelowMidiDomain_NoNote()
    {
        var result = Normalize(Timeline(Note(0, 1000, -1.0)));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Suppress_NonFiniteInitial_NoNote()
    {
        var result = Normalize(Timeline(Note(0, 1000, double.NaN)));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void Suppress_SustainedOutOfDomainChange_WholeNoteSuppressed()
    {
        // The pitch change to -1 is sustained for half the note (not a transient):
        // the note decodes outside [0, 127] and must not emit a melodic NoteOn.
        var result = Normalize(Timeline(Note(0, 4000, 60.0, new PitchChange(2000, 0, -1.0))));
        Assert.Empty(result.Notes);
    }

    [Fact]
    public void IntentionalUnpitchedNoise_NotSuppressed()
    {
        // SSG noise-only notes carry the intentional -1 sentinel and are excluded by
        // the exporter's own noise gate — the stage must leave them untouched.
        var noise = Note(0, 1000, -1.0) with { Mode = VisualizationNoteMode.SsgNoise };
        var result = Normalize(Timeline(noise));
        Assert.Single(result.Notes);
    }

    // ---- INV6: transient invalid pitch is bridged ----------------------------------

    [Fact]
    public void Bridge_TransientOutOfDomainChange_Dropped()
    {
        // A 20-sample invalid blip (<< 1 ms window = 44 samples) between valid
        // states: bridged, the note survives with the blip removed.
        var note = Note(0, 4000, 60.0,
            new PitchChange(1000, 0, -1.0),
            new PitchChange(1020, 0, 62.0));
        var result = Normalize(Timeline(note));

        Assert.Single(result.Notes);
        Assert.Equal(60.0, result.Notes[0].InitialMidiNote, 6);
        Assert.Single(result.Notes[0].Pitch);
        Assert.Equal(1020, result.Notes[0].Pitch[0].SamplePosition);
        Assert.Equal(62.0, result.Notes[0].Pitch[0].MidiNote, 6);
    }

    [Fact]
    public void Bridge_InvalidNoteBetweenSamePitchNotes_Merged()
    {
        // A short (30-sample) out-of-domain note sits between two valid notes of
        // the same pitch: it is suppressed and the flanks merge into one note.
        var result = Normalize(Timeline(
            Note(0, 1000, 60.0),
            Note(1000, 1030, 130.0),
            Note(1030, 2500, 60.0)));

        Assert.Single(result.Notes);
        Assert.Equal(0, result.Notes[0].StartSample);
        Assert.Equal(2500, result.Notes[0].EndSample);
    }

    [Fact]
    public void TrailingTransientOutOfDomain_Clipped()
    {
        // A sub-ms invalid tail is clipped; the note keeps its representable extent.
        var note = Note(0, 4000, 60.0, new PitchChange(3980, 0, -1.0));
        var result = Normalize(Timeline(note));

        Assert.Single(result.Notes);
        Assert.Equal(3980, result.Notes[0].EndSample);
        Assert.Empty(result.Notes[0].Pitch);
    }

    // ---- INV3: unchanged state leaves no boundary ----------------------------------

    [Fact]
    public void Inv3_UnchangedState_NoOffOnBoundary()
    {
        // A chain of same-pitch segments separated by gate cycles collapses to one
        // note, so no NoteOff/NoteOn boundary exists for unchanged melodic state.
        var result = Normalize(Timeline(
            Note(0, 500, 74.0),
            Note(517, 1000, 74.0),
            Note(1034, 1500, 74.0),
            Note(1500, 2000, 61.0)));

        Assert.Equal(2, result.Notes.Count);
        Assert.Equal(74.0, result.Notes[0].InitialMidiNote, 6);
        Assert.Equal(0, result.Notes[0].StartSample);
        Assert.Equal(1500, result.Notes[0].EndSample);
        Assert.Equal(1500, result.Notes[1].StartSample);
        Assert.Equal(61.0, result.Notes[1].InitialMidiNote, 6);
    }

    // ---- INV2: rhythm duplicates coalesce -------------------------------------------

    [Fact]
    public void Rhythm_DuplicateTimestampVoiceInstrument_Coalesced()
    {
        // Repeated sink observations of an unchanged active noise state collapse
        // to ONE trigger per (timestamp, voice, instrument) identity.
        var rhythm = new RhythmEvent("noise", "ay8910:0:noise:0", 1000, 0.8f, 0);
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 8_000_000,
            SampleRate = Sr,
            Notes = Array.Empty<NoteEvent>(),
            Beats = new[] { new BeatEvent(0, 0) },
            Rhythm = new[] { rhythm, rhythm, rhythm },
        };

        var result = Normalize(timeline);
        Assert.Single(result.Rhythm);
    }

    [Fact]
    public void Rhythm_DistinctTimestamps_NotCoalesced()
    {
        // Distinct hits at distinct timestamps are real drum hits — never merged.
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 8_000_000,
            SampleRate = Sr,
            Notes = Array.Empty<NoteEvent>(),
            Beats = new[] { new BeatEvent(0, 0) },
            Rhythm = new[]
            {
                new RhythmEvent("noise", "ay8910:0:noise:0", 1000, 0.8f, 0),
                new RhythmEvent("noise", "ay8910:0:noise:0", 5000, 0.9f, 0),
            },
        };

        var result = Normalize(timeline);
        Assert.Equal(2, result.Rhythm.Count);
    }
}
