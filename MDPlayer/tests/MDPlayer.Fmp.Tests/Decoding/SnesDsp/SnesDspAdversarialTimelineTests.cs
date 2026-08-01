using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests.Decoding.SnesDsp;

/// <summary>
/// Adversarial managed-only coverage for state carried by native semantic
/// events. These tests deliberately avoid native audio and block timing.
/// </summary>
public sealed class SnesDspAdversarialTimelineTests
{
    [Fact]
    public void ExplicitSourceZeroWinsAfterNonzeroLatch_AndIsRecordedAtKeyOnSample()
    {
        (SnesDspTimelineDecoder decoder, TimelineBuilder timeline) = CreateDecoder();

        decoder.Process(SpcSemanticEvent.SourceLatched(10, 0, 7));
        decoder.Process(SpcSemanticEvent.KeyOn(123, 0, sourceNumber: 0));
        decoder.Process(SpcSemanticEvent.VoiceEnd(456, 0));

        VisualizationTimeline result = timeline.Build(1_000);
        NoteEvent note = Assert.Single(result.Notes);
        Assert.Equal("spc:src0", note.InstrumentId);

        SpcVoiceStateEvent source = Assert.Single(result.SpcVoiceStates,
            state => state.State == nameof(SpcSemanticEventKind.SourceLatched)
                && state.SamplePosition == 123);
        Assert.Equal(0, source.Value);
    }

    [Fact]
    public void PresentationStateTransitions_RetainPayloadAndSamplePositionPerVoice()
    {
        (SnesDspTimelineDecoder decoder, TimelineBuilder timeline) = CreateDecoder();

        decoder.Process(SpcSemanticEvent.KeyOn(0, 2, sourceNumber: 3));
        decoder.Process(SpcSemanticEvent.VolumeChanged(11, 2, -64, 63));
        decoder.Process(new SpcSemanticEvent(
            22, 2, SpcSemanticEventKind.NoiseChanged, Value: 1));
        decoder.Process(new SpcSemanticEvent(
            33, 2, SpcSemanticEventKind.PitchModChanged, Value: 1));
        decoder.Process(new SpcSemanticEvent(
            44, 2, SpcSemanticEventKind.EchoSendChanged, Value: 1));
        decoder.Process(SpcSemanticEvent.EnvelopeModeChanged(55, 2, 4));

        // Same state kinds on another voice must not be conflated with voice 2.
        decoder.Process(SpcSemanticEvent.VolumeChanged(66, 3, 12, 34));

        VisualizationTimeline result = timeline.Build(1_000);
        string voice2 = new VoiceId(
            VisualizationDeviceCatalog.SnesDsp().Id,
            VoiceKind.PcmVoice,
            2).ToString();
        string voice3 = new VoiceId(
            VisualizationDeviceCatalog.SnesDsp().Id,
            VoiceKind.PcmVoice,
            3).ToString();

        SpcVoiceStateEvent[] voice2States = result.SpcVoiceStates
            .Where(state => state.VoiceId == voice2)
            .OrderBy(state => state.SamplePosition)
            .ToArray();
        Assert.Equal(6, voice2States.Length);
        Assert.Equal(
            [
                nameof(SpcSemanticEventKind.SourceLatched),
                nameof(SpcSemanticEventKind.VolumeChanged),
                nameof(SpcSemanticEventKind.NoiseChanged),
                nameof(SpcSemanticEventKind.PitchModChanged),
                nameof(SpcSemanticEventKind.EchoSendChanged),
                nameof(SpcSemanticEventKind.EnvelopeModeChanged),
            ],
            voice2States.Select(state => state.State).ToArray());
        Assert.Equal([0L, 11L, 22L, 33L, 44L, 55L],
            voice2States.Select(state => state.SamplePosition).ToArray());
        Assert.Equal(3, voice2States[0].Value);
        Assert.Equal((-64, 63), (voice2States[1].Value, voice2States[1].Value2));
        Assert.Equal(1, voice2States[2].Value);
        Assert.Equal(1, voice2States[3].Value);
        Assert.Equal(1, voice2States[4].Value);
        Assert.Equal(4, voice2States[5].Value);

        SpcVoiceStateEvent otherVoiceVolume = Assert.Single(result.SpcVoiceStates,
            state => state.VoiceId == voice3
                && state.State == nameof(SpcSemanticEventKind.VolumeChanged));
        Assert.Equal(12, otherVoiceVolume.Value);
        Assert.Equal(34, otherVoiceVolume.Value2);
    }

    private static (SnesDspTimelineDecoder Decoder, TimelineBuilder Timeline) CreateDecoder()
    {
        TimelineBuilder timeline = new(32_000);
        SnesDspTimelineDecoder decoder = new();
        decoder.Initialize(VisualizationDeviceCatalog.SnesDsp(), timeline);
        return (decoder, timeline);
    }
}
