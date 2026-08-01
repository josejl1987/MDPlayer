using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Decoding.SnesDsp;

/// <summary>
/// Behavioural tests for the managed SNES S-DSP timeline decoder, driven by
/// synthetic semantic events (§30.4). No native code, no file I/O.
/// </summary>
public class SnesDspTimelineDecoderTests
{
    private static SnesDspTimelineDecoder CreateDecoder(out TimelineBuilder timeline)
    {
        timeline = new TimelineBuilder(32_000);
        var decoder = new SnesDspTimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.SnesDsp(), timeline);
        return decoder;
    }

    [Fact]
    public void Initialize_AddsSnesDspDeviceAndEightVoices()
    {
        CreateDecoder(out TimelineBuilder timeline);

        VisualizationTimeline result = timeline.Build(0);

        DeviceDescriptor device = Assert.Single(result.Devices);
        Assert.Equal(ChipType.SnesDsp, device.Id.Type);
        Assert.Equal(8, result.Voices.Count);
        Assert.All(result.Voices, voice => Assert.Equal(ChipType.SnesDsp, voice.Id.Device.Type));
    }

    [Fact]
    public void SingleKeyOn_ClosedByVoiceEnd_CreatesOneNote()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(100, 2, sourceNumber: 3, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(500, 2));

        VisualizationTimeline result = timeline.Build(1000);
        NoteEvent note = Assert.Single(result.Notes);
        Assert.Equal(100L, note.StartSample);
        Assert.Equal(500L, note.EndSample);
        Assert.Equal("spc:src3", note.InstrumentId);
        Assert.Equal(VisualizationNoteMode.Pcm, note.Mode);
        Assert.False(note.IsRetrigger);
        Assert.Empty(note.Pitch);
    }

    [Fact]
    public void ReleaseStart_KeepsNoteOpenUntilVoiceEnd()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0));
        decoder.Process(SpcSemanticEvent.ReleaseStart(200, 0));

        // The note stays open: it is not emitted into the timeline yet.
        Assert.Empty(timeline.Build(1000).Notes);
        SpcVoiceState state = decoder.GetVoiceState(0);
        Assert.True(state.Active);
        Assert.True(state.Releasing);
        Assert.True(state.ReleaseStartSample.HasValue);
        Assert.Equal(200L, state.ReleaseStartSample.Value);

        decoder.Process(SpcSemanticEvent.VoiceEnd(400, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal(0L, note.StartSample);
        Assert.Equal(400L, note.EndSample);
    }

    [Fact]
    public void Retrigger_ClosesPreviousNoteAndReopensWithIsRetrigger()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, sourceNumber: 1));
        decoder.Process(SpcSemanticEvent.KeyOn(100, 0, sourceNumber: 1));
        decoder.Process(SpcSemanticEvent.VoiceEnd(300, 0));

        VisualizationTimeline result = timeline.Build(1000);
        Assert.Equal(2, result.Notes.Count);

        NoteEvent first = result.Notes[0];
        Assert.Equal(0L, first.StartSample);
        Assert.Equal(100L, first.EndSample);
        Assert.False(first.IsRetrigger);

        NoteEvent second = result.Notes[1];
        Assert.Equal(100L, second.StartSample);
        Assert.Equal(300L, second.EndSample);
        Assert.True(second.IsRetrigger);
    }

    [Fact]
    public void PitchChanges_AreMonotonic_AndDropOutOfOrderOrDuplicatePoints()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(100, 0, 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(200, 0, 0x1200));
        decoder.Process(SpcSemanticEvent.PitchChanged(150, 0, 0x1400)); // out of order -> dropped
        decoder.Process(SpcSemanticEvent.PitchChanged(200, 0, 0x1800)); // equal position -> dropped
        decoder.Process(SpcSemanticEvent.VoiceEnd(300, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal(2, note.Pitch.Count);
        Assert.Equal(100L, note.Pitch[0].SamplePosition);
        Assert.Equal(200L, note.Pitch[1].SamplePosition);
        for (int i = 1; i < note.Pitch.Count; i++)
            Assert.True(note.Pitch[i].SamplePosition > note.Pitch[i - 1].SamplePosition);
    }

    [Fact]
    public void Complete_ClosesAllOpenNotesAtEndSample()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(50, 1));
        decoder.Process(SpcSemanticEvent.KeyOn(75, 3));
        decoder.Process(SpcSemanticEvent.ReleaseStart(100, 1));
        decoder.Complete(1000);

        VisualizationTimeline result = timeline.Build(1000);
        Assert.Equal(2, result.Notes.Count);
        Assert.All(result.Notes, note => Assert.Equal(1000L, note.EndSample));
    }

    [Fact]
    public void SourceLatched_ResolvesInstrumentWhenKeyOnCarriesNoSource()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.SourceLatched(0, 4, 7));
        decoder.Process(SpcSemanticEvent.KeyOn(10, 4));
        decoder.Process(SpcSemanticEvent.VoiceEnd(60, 4));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal("spc:src7", note.InstrumentId);
    }

    [Theory]
    [InlineData(0x1000, 0.0)]
    [InlineData(0x2000, 12.0)]
    [InlineData(0x0800, -12.0)]
    public void PitchMapping_IsRelativeToNaturalSampleRate(int effectivePitch, double expectedSemitones)
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, effectivePitch: (ushort)effectivePitch));
        decoder.Process(SpcSemanticEvent.VoiceEnd(100, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal(expectedSemitones, note.InitialMidiNote, 3);
        double expectedHz = 440.0 * Math.Pow(2.0, expectedSemitones / 12.0);
        Assert.Equal(expectedHz, note.InitialFrequencyHz, 2);
    }

    [Fact]
    public void PitchChanged_UsesEffectivePitchForRelativeSemitones()
    {
        // PR 8 (§13.6): PITCH_CHANGED carries the EFFECTIVE pitch (register
        // pitch + PMON adjustment), so the pitch point must be computed from
        // that value, not from the register pitch.
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(100, 0, 0x1800));
        decoder.Process(SpcSemanticEvent.VoiceEnd(300, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        PitchChange point = Assert.Single(note.Pitch);
        // 12 * log2(0x1800 / 0x1000) = 12 * log2(1.5) ≈ +7.02 semitones.
        double expected = 12.0 * Math.Log2(0x1800 / (double)0x1000);
        Assert.Equal(expected, point.MidiNote, 3);
        Assert.Equal(100L, point.SamplePosition);
    }

    [Fact]
    public void PitchChanged_WithUnreportedEffectivePitch_FallsBackToNaturalRate()
    {
        // A PITCH_CHANGED with effective pitch not reported (0) falls back to
        // the natural sample rate: 0 relative semitones.
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(100, 0, 0));
        decoder.Process(SpcSemanticEvent.VoiceEnd(200, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal(0.0, Assert.Single(note.Pitch).MidiNote, 3);
    }

    [Fact]
    public void ReleaseThenKeyOn_StillWorksAndAnchorsNewNoteOnEffectivePitch()
    {
        // PR 8: a RELEASE/KEY_ON retrigger sequence must still work, and the
        // new note's initial pitch must be anchored on the effective pitch.
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 1, sourceNumber: 2, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.ReleaseStart(100, 1));
        decoder.Process(SpcSemanticEvent.KeyOn(200, 1, sourceNumber: 2, effectivePitch: 0x1800));
        decoder.Process(SpcSemanticEvent.VoiceEnd(400, 1));

        VisualizationTimeline result = timeline.Build(1000);
        Assert.Equal(2, result.Notes.Count);

        NoteEvent first = result.Notes[0];
        Assert.Equal(0L, first.StartSample);
        Assert.Equal(200L, first.EndSample);
        Assert.False(first.IsRetrigger);

        NoteEvent second = result.Notes[1];
        Assert.Equal(200L, second.StartSample);
        Assert.Equal(400L, second.EndSample);
        Assert.True(second.IsRetrigger);
        double expected = 12.0 * Math.Log2(0x1800 / (double)0x1000);
        Assert.Equal(expected, second.InitialMidiNote, 3);
    }

    [Fact]
    public void VoiceZero_EffectivePitchChanges_AreHonored()
    {
        // PR 8 (§19): hardware has no voice -1 to modulate voice 0, but the
        // decoder must follow the EFFECTIVE state reported by the native core
        // and must not assume voice 0 can never carry a pitch change.
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(50, 0, 0x1800));
        decoder.Process(SpcSemanticEvent.VoiceEnd(200, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        PitchChange point = Assert.Single(note.Pitch);
        Assert.Equal(50L, point.SamplePosition);
        double expected = 12.0 * Math.Log2(0x1800 / (double)0x1000);
        Assert.Equal(expected, point.MidiNote, 3);
    }

    [Fact]
    public void Process_ChipWrite_IsNotSupported()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);
        var write = new TimedChipWrite(0, new DeviceId(ChipType.SnesDsp, 0), 0, 0, 0);

        Assert.Throws<NotSupportedException>(() => decoder.Process(write));
    }
}
