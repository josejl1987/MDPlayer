using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Unit tests for the AY-3-8910 decoder's source-fidelity voice-state model:
/// tone-enabled voices produce pitched notes, while shared noise is emitted only
/// as unpitched NoiseStateEvent metadata. Register writes must not speculate
/// melodic attacks.
/// </summary>
public sealed class Ay8910TimelineDecoderTests
{
    private const long ClockHz = 1_789_773;

    /// <summary>Mixer value: noise disabled, all tone channels enabled.</summary>
    private const int MixerToneOnly = 0b0011_1000;

    /// <summary>Mixer value: all tone disabled, noise enabled.</summary>
    private const int MixerNoiseOnly = 0b0000_0111;

    private static (Ay8910TimelineDecoder Decoder, TimelineBuilder Builder) NewDecoder()
    {
        var timeline = new TimelineBuilder(44_100);
        var decoder = new Ay8910TimelineDecoder();
        decoder.Initialize(VisualizationDeviceCatalog.Ay8910(0, ClockHz), timeline);
        return (decoder, timeline);
    }

    private static TimedChipWrite W(long sample, int address, int data) =>
        new(sample, new DeviceId(ChipType.Ay8910, 0), 0, address, data);

    private static void WritePeriod(Ay8910TimelineDecoder decoder, int channel, int period, long sample = 0)
    {
        decoder.Process(W(sample, channel * 2, period & 0xFF));
        decoder.Process(W(sample, channel * 2 + 1, (period >> 8) & 0x0F));
    }

    [Fact]
    public void Tone_UltrasonicPeriod_ProducesNoNote()
    {
        // INV5: period 2 at 1.79 MHz decodes to ~55.9 kHz — an ultrasonic
        // initialization/glitch state, never a musical pitch. It must not become
        // a MIDI note even with tone enabled and volume up.
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 2);
        decoder.Process(W(0, 7, MixerToneOnly));
        decoder.Process(W(0, 8, 0x0F));
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void Tone_RepresentablePeriod_CreatesNote()
    {
        // Period 200 → 1_789_773 / 16 / 200 ≈ 559 Hz (MIDI ≈ 73.2): a normal
        // audible tone; with tone enabled and volume up a note is created.
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, MixerToneOnly));
        decoder.Process(W(0, 8, 0x0F));
        decoder.Complete(10_000);

        NoteEvent note = Assert.Single(timeline.Build(10_000).Notes);
        Assert.Equal("ay8910.0.psg.1", note.ChannelId);
        Assert.Equal(VisualizationNoteMode.SsgTone, note.Mode);
        Assert.Equal(0, note.StartSample);
        Assert.Equal(10_000, note.EndSample);
        Assert.InRange(note.InitialMidiNote, 72.0, 75.0);
    }

    [Fact]
    public void Tone_MixerDisabled_ProducesNoNote()
    {
        // Tone channel disabled via the mixer bit: silence, even with volume up.
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, MixerToneOnly | 0b0000_0001)); // tone A disabled
        decoder.Process(W(0, 8, 0x0F));
        decoder.Complete(10_000);

        Assert.Empty(timeline.Build(10_000).Notes);
    }

    [Fact]
    public void Tone_VolumeZero_ClosesNote()
    {
        // Volume is the gate: dropping to 0 closes the segment; raising it again
        // opens a new segment (the normalization stage merges unchanged adjacent
        // segments — INV3 — but the decoder itself must show the gate).
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, MixerToneOnly));
        decoder.Process(W(0, 8, 0x0F));
        decoder.Process(W(1_000, 8, 0x00));
        decoder.Process(W(1_500, 8, 0x0F));
        decoder.Complete(10_000);

        var notes = timeline.Build(10_000).Notes;
        Assert.Equal(2, notes.Count);
        Assert.Equal(0, notes[0].StartSample);
        Assert.Equal(1_000, notes[0].EndSample);
        Assert.Equal(1_500, notes[1].StartSample);
        Assert.Equal(10_000, notes[1].EndSample);
    }

    [Fact]
    public void Tone_VolumeChangeDoesNotRetrigger()
    {
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, MixerToneOnly));
        decoder.Process(W(0, 8, 0x08));
        decoder.Process(W(500, 8, 0x0A));
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        Assert.Equal(0, note.StartSample);
        Assert.Equal(1_000, note.EndSample);
    }

    [Fact]
    public void TonePeriodChangeWhileGateOpen_AddsPitchChangeWithoutRetrigger()
    {
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, MixerToneOnly));
        decoder.Process(W(0, 8, 0x0F));
        WritePeriod(decoder, 0, 240, 500);
        decoder.Process(W(900, 8, 0x00));
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        PitchChange change = Assert.Single(note.Pitch);
        Assert.Equal(500, change.SamplePosition);
        Assert.NotEqual(note.InitialMidiNote, change.MidiNote);
    }


    [Fact]
    public void Tone_EnvelopeEnabledZeroDirectVolumeRemainsAudible()
    {
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, MixerToneOnly));
        decoder.Process(W(0, 8, 0x10));
        decoder.Process(W(100, 11, 0x20));
        decoder.Process(W(200, 12, 0x01));
        decoder.Process(W(300, 13, 0x09));
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        Assert.Equal(0, note.StartSample);
        Assert.Equal(1_000, note.EndSample);
        Assert.Equal(VisualizationNoteMode.SsgEnvelopeTone, note.Mode);
    }

    [Fact]
    public void ToneAndNoise_CreateOnePitchedNoteAndNoiseMetadata()
    {
        var (decoder, timeline) = NewDecoder();
        WritePeriod(decoder, 0, 200);
        decoder.Process(W(0, 7, 0x30)); // tone A and noise A enabled
        decoder.Process(W(0, 8, 0x0F));
        decoder.Complete(1_000);

        NoteEvent note = Assert.Single(timeline.Build(1_000).Notes);
        Assert.Equal(VisualizationNoteMode.SsgToneNoise, note.Mode);
        Assert.Single(timeline.Build(1_000).NoiseStates);
        Assert.Empty(timeline.Build(1_000).Rhythm);
    }

    [Fact]
    public void NoiseOnly_EmitsNoiseMetadataAndNoMelodicNote()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(W(0, 7, MixerNoiseOnly));
        decoder.Process(W(0, 8, 0x03));
        decoder.Process(W(100, 8, 0x05));
        decoder.Process(W(200, 8, 0x00));
        decoder.Process(W(300, 8, 0x04));
        decoder.Complete(1_000);

        VisualizationTimeline result = timeline.Build(1_000);
        Assert.Empty(result.Notes);
        Assert.Equal(2, result.NoiseStates.Length);
        Assert.Empty(result.Rhythm);
        Assert.Equal(3 / 15.0f, result.NoiseStates[0].Level, 3);
    }

    [Fact]
    public void Noise_EnvelopeEnabledZeroDirectVolumeEmitsFullLevelMetadata()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(W(0, 7, MixerNoiseOnly));
        decoder.Process(W(0, 8, 0x10));
        decoder.Complete(1_000);

        NoiseStateEvent noise = Assert.Single(timeline.Build(1_000).NoiseStates);
        Assert.Equal(1.0f, noise.Level);
        Assert.Empty(timeline.Build(1_000).Notes);
    }

    [Fact]
    public void Noise_VolumeZeroAndMixerDisabledAreSilent()
    {
        var (decoder, timeline) = NewDecoder();
        decoder.Process(W(0, 7, MixerNoiseOnly));
        decoder.Process(W(0, 8, 0x00));
        decoder.Process(W(100, 7, MixerToneOnly));
        decoder.Process(W(100, 8, 0x0F));
        decoder.Complete(1_000);

        VisualizationTimeline result = timeline.Build(1_000);
        Assert.Empty(result.Notes);
        Assert.Empty(result.NoiseStates);
    }
}
