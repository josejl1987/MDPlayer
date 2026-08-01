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
    public void KeyOnReferencesGenericBrrSampleDefinition()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);
        byte[] encoded = new byte[9];
        encoded[0] = 0x01; // one terminal BRR block
        decoder.SetSamples([
            new SpcSampleEntry(
                "abcdef0123456789",
                "abcdef01",
                [3],
                0x2000,
                0x2000,
                false,
                encoded,
                "relative",
                null,
                0),
        ]);

        decoder.Process(SpcSemanticEvent.KeyOn(100, 2, sourceNumber: 3));
        decoder.Process(SpcSemanticEvent.VoiceEnd(500, 2));

        VisualizationTimeline result = timeline.Build(1_000);
        SampleDefinition sample = Assert.Single(result.Samples);
        SamplePlaybackEvent playback = Assert.Single(result.SamplePlayback);
        Assert.Equal("sample:abcdef01", sample.Id);
        Assert.Equal(sample.Id, playback.SampleId);
        Assert.Equal(16, sample.SourceLengthSamples);
        Assert.NotEmpty(sample.Preview);
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

    [Fact]
    public void KeyOn_SourceZero_DoesNotFallBackToPreviousLatchedSource()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.SourceLatched(0, 4, 7));
        decoder.Process(SpcSemanticEvent.KeyOn(10, 4, sourceNumber: 0));
        decoder.Process(SpcSemanticEvent.VoiceEnd(60, 4));

        VisualizationTimeline result = timeline.Build(1000);
        NoteEvent note = Assert.Single(result.Notes);
        Assert.Equal("spc:src0", note.InstrumentId);
        Assert.Contains(result.SpcVoiceStates, state =>
            state.State == nameof(SpcSemanticEventKind.SourceLatched)
            && state.Value == 0
            && state.SamplePosition == 10);
    }

    [Fact]
    public void AuxiliaryStateTransitions_AreRetainedForSpcPresentation()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, sourceNumber: 0));
        decoder.Process(SpcSemanticEvent.VolumeChanged(10, 0, -20, 40));
        decoder.Process(new SpcSemanticEvent(
            20, 0, SpcSemanticEventKind.NoiseChanged, Value: 1));
        decoder.Process(new SpcSemanticEvent(
            30, 0, SpcSemanticEventKind.PitchModChanged, Value: 1));
        decoder.Process(new SpcSemanticEvent(
            40, 0, SpcSemanticEventKind.EchoSendChanged, Value: 1));

        VisualizationTimeline result = timeline.Build(1000);
        Assert.Contains(result.SpcVoiceStates, state =>
            state.State == nameof(SpcSemanticEventKind.VolumeChanged)
            && state.Value == -20
            && state.Value2 == 40);
        Assert.Contains(result.SpcVoiceStates, state =>
            state.State == nameof(SpcSemanticEventKind.NoiseChanged)
            && state.Value == 1);
        Assert.Contains(result.SpcVoiceStates, state =>
            state.State == nameof(SpcSemanticEventKind.PitchModChanged)
            && state.Value == 1);
        Assert.Contains(result.SpcVoiceStates, state =>
            state.State == nameof(SpcSemanticEventKind.EchoSendChanged)
            && state.Value == 1);
    }

    [Theory]
    [InlineData(0x1000, 0.0)]
    [InlineData(0x2000, 12.0)]
    [InlineData(0x0800, -12.0)]
    public void PitchMapping_AnchorsRelativeSemitonesAtA4(int effectivePitch, double relativeSemitones)
    {
        // Without a root estimate the decoder anchors the sample's natural rate
        // at A4: midi = 69 + 12 * log2(pitch / 0x1000), and hz stays consistent
        // with that anchor (69 <-> 440 Hz).
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, effectivePitch: (ushort)effectivePitch));
        decoder.Process(SpcSemanticEvent.VoiceEnd(100, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal(69.0 + relativeSemitones, note.InitialMidiNote, 3);
        double expectedHz = 440.0 * Math.Pow(2.0, relativeSemitones / 12.0);
        Assert.Equal(expectedHz, note.InitialFrequencyHz, 2);
    }

    [Fact]
    public void SourceRoots_ShiftNotesByEstimatedRoot()
    {
        // §25.3: with an estimated BRR root the sounding pitch is root + relative.
        // A 400 Hz source played at unity pitch sounds at midi 69 + 12*log2(400/440).
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);
        decoder.SetSourceRoots(new[] { new SpcSourceRootInfo(3, 400.0, 0.98, "estimated") });

        decoder.Process(SpcSemanticEvent.KeyOn(100, 2, sourceNumber: 3, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(500, 2));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        double expectedMidi = 69.0 + 12.0 * Math.Log2(400.0 / 440.0);
        Assert.Equal(expectedMidi, note.InitialMidiNote, 3);
        Assert.Equal(400.0, note.InitialFrequencyHz, 1);
    }

    [Fact]
    public void SourceRoots_ApplyToPitchChanges()
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);
        decoder.SetSourceRoots(new[] { new SpcSourceRootInfo(0, 400.0, 0.98, "estimated") });

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, sourceNumber: 0, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.PitchChanged(100, 0, 0x2000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(200, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        double rootMidi = 69.0 + 12.0 * Math.Log2(400.0 / 440.0);
        Assert.Equal(rootMidi + 12.0, Assert.Single(note.Pitch).MidiNote, 3);
    }

    [Theory]
    [InlineData(7, true)]   // unpitched estimate (null Hz)
    [InlineData(8, false)]  // no estimate at all
    public void SourceRoots_MissingOrUnpitched_FallsBackToA4Anchor(int source, bool registerUnpitched)
    {
        SnesDspTimelineDecoder decoder = CreateDecoder(out TimelineBuilder timeline);
        if (registerUnpitched)
            decoder.SetSourceRoots(new[] { new SpcSourceRootInfo(source, null, 0, "unpitched") });

        decoder.Process(SpcSemanticEvent.KeyOn(0, 0, sourceNumber: source, effectivePitch: 0x1000));
        decoder.Process(SpcSemanticEvent.VoiceEnd(100, 0));

        NoteEvent note = Assert.Single(timeline.Build(1000).Notes);
        Assert.Equal(69.0, note.InitialMidiNote, 3);
        Assert.Equal(440.0, note.InitialFrequencyHz, 2);
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
        // 12 * log2(0x1800 / 0x1000) = 12 * log2(1.5) ≈ +7.02 semitones, A4-anchored.
        double expected = 69.0 + 12.0 * Math.Log2(0x1800 / (double)0x1000);
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
        Assert.Equal(69.0, Assert.Single(note.Pitch).MidiNote, 3);
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
        double expected = 69.0 + 12.0 * Math.Log2(0x1800 / (double)0x1000);
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
        double expected = 69.0 + 12.0 * Math.Log2(0x1800 / (double)0x1000);
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
