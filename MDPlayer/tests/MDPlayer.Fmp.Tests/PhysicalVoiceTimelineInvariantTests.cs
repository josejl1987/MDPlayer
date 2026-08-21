using System.Text.Json;
using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Xunit;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

public sealed class PhysicalVoiceTimelineInvariantTests
{
    private const int SampleRate = 44_100;
    private static readonly MidiTranscriber Transcriber = new();
    private static readonly SourceDomainKey Voice =
        new(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, 0);

    [Fact]
    public void OverlappingNotesOnOnePhysicalVoiceAreRejectedBeforePlanning()
    {
        VisualizationTimeline timeline = Timeline(
            Note(0, 100, 60),
            Note(50, 150, 62));

        var exception = Assert.Throws<InvalidOperationException>(
            () => Transcriber.Transcribe(timeline));

        Assert.Contains("overlapping", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidatorRejectsOverlappingNotesOnOnePhysicalVoice()
    {
        VisualizationTimeline timeline = Timeline(
            Note(0, 100, 60),
            Note(50, 150, 62));

        var exception = Assert.Throws<JsonException>(
            () => VisualizationTimelineValidator.Validate(timeline));

        Assert.Contains("overlapping", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExactBoundaryRetriggerIsAccepted()
    {
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(
            Note(0, 100, 60),
            Note(100, 200, 62)));

        Assert.Equal(2, result.Diagnostics.UniqueAudibleAttackCount);
        Assert.Equal(2, NoteOnCount(result));
    }

    [Fact]
    public void NoteAndMatchingSampleViewSerializeOneAttack()
    {
        const string attackId = "attack:shared";
        NoteEvent note = Note(0, SampleRate, 60) with { SourceAttackId = attackId };
        VisualizationTimeline timeline = new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate,
            Notes = new[] { note },
            SamplePlayback = new[] { Sample(0, SampleRate, attackId) },
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);

        Assert.Equal(0, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(1, result.Diagnostics.UniqueAudibleAttackCount);
        Assert.Equal(1, NoteOnCount(result));
    }

    [Fact]
    public void SampleOnlyViewWithNullAttackIdOwnsOneLocalAttack()
    {
        VisualizationTimeline timeline = new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate,
            SamplePlayback = new[] { Sample(0, SampleRate, null) },
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);

        Assert.Equal(1, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(1, result.Diagnostics.UniqueAudibleAttackCount);
        Assert.Equal(1, NoteOnCount(result));
    }

    [Fact]
    public void PitchChangesDoNotIncreaseAttackCount()
    {
        MidiTranscriptionResult plain = Transcriber.Transcribe(
            Timeline(Note(0, SampleRate, 60)));
        MidiTranscriptionResult pitched = Transcriber.Transcribe(
            Timeline(Note(0, SampleRate, 60, new[]
            {
                new PitchChange(0, 0, 60),
                new PitchChange(SampleRate / 2, 0, 62),
            })));

        Assert.Equal(plain.Diagnostics.UniqueAudibleAttackCount,
            pitched.Diagnostics.UniqueAudibleAttackCount);
        Assert.Equal(NoteOnCount(plain), NoteOnCount(pitched));
    }

    [Fact]
    public void DuplicateSameFamilyAttackIdsFailLoudly()
    {
        VisualizationTimeline timeline = Timeline(
            Note(0, 100, 60, attackId: "attack:duplicate", domain: Voice with { Index = 0 }),
            Note(100, 200, 62, attackId: "attack:duplicate", domain: Voice with { Index = 1 }));

        var exception = Assert.Throws<InvalidOperationException>(
            () => Transcriber.Transcribe(timeline));

        Assert.Contains("Duplicate Note SourceAttackId", exception.Message);
    }

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        SampleRate = SampleRate,
        StartSample = 0,
        EndSample = notes.Select(note => note.EndSample).DefaultIfEmpty(1).Max(),
        Notes = notes,
    };

    private static NoteEvent Note(
        long start,
        long end,
        double midiNote,
        IReadOnlyList<PitchChange>? pitch = null,
        string? attackId = null,
        SourceDomainKey? domain = null) =>
        new(
            ChannelId: "voice-0",
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 0,
            InitialMidiNote: midiNote,
            InstrumentId: "test",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: false,
            Pitch: pitch ?? Array.Empty<PitchChange>())
        {
            Domain = domain ?? Voice,
            SourceAttackId = attackId,
        };

    private static SamplePlaybackEvent Sample(long start, long end, string? attackId) =>
        new(
            VoiceId: "ym2612.0.pcm.dac",
            StartSample: start,
            EndSample: end,
            SampleId: "sample-dac",
            MidiPitch: null,
            PlaybackRate: 1.0,
            Gain: 1.0f,
            Pan: 0,
            Retrigger: false,
            Looping: false)
        {
            SourceAttackId = attackId,
        };

    private static int NoteOnCount(MidiTranscriptionResult result) =>
        result.Tracks
            .SelectMany(track => track.Events)
            .OfType<MidiNoteEvent>()
            .Count(note => note.NoteOn);
}
