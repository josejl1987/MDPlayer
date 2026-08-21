using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using Xunit;
using NoteOnEvent = Melanchall.DryWetMidi.Core.NoteOnEvent;
using NoteOffEvent = Melanchall.DryWetMidi.Core.NoteOffEvent;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Raw-fidelity transcription oracle. MidiTranscriber must preserve source
/// timing and pitch with a fixed 120 BPM transport and NO musical inference:
/// no tempo map, meter, downbeat, quantization, tuning normalization,
/// instrument splitting or drum role guessing. Channel 10 (0-based 9) carries
/// native rhythm hits verbatim.
/// </summary>
public sealed class MidiTranscriberFidelityTests
{
    private const int Sr = 44100;
    private const int Ppq = 960;
    private static readonly MidiTranscriber Transcriber = new(Ppq);

    private static VisualizationTimeline Timeline(params NoteEvent[] notes) => new()
    {
        SampleRate = Sr,
        StartSample = 0,
        EndSample = notes.Select(n => n.EndSample).DefaultIfEmpty(1).Max(),
        Notes = notes,
    };

    private static NoteEvent Note(
        string voice, long start, long end, double midiNote,
        IReadOnlyList<PitchChange>? pitch = null, bool retrigger = false)
    {
        var note = new NoteEvent(
            ChannelId: "voice-" + voice,
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 0,
            InitialMidiNote: midiNote,
            InstrumentId: "test",
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: retrigger,
            Pitch: pitch ?? Array.Empty<PitchChange>());
        return note with { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, int.Parse(voice)) };
    }

    private static PitchChange Pitch(long sample, double midiNote) =>
        new(sample, 0, midiNote);

    private static IReadOnlyList<(long Tick, MidiEvent Event)> Track(byte[] bytes, int trackIndex) =>
        MidiRoundTrip.TimedEvents(bytes, trackIndex);

    [Fact]
    public void Transport_FixedAt120Bpm_SingleTempoEventAtTickZero()
    {
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0))).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> conductor = Track(bytes, 0);
        Assert.Single(conductor, c => c.Event is SetTempoEvent);
        var tempo = conductor.Select(c => c.Event).OfType<SetTempoEvent>().Single();
        Assert.Equal(0, conductor.First(c => c.Event is SetTempoEvent).Tick);
        Assert.All(
            conductor.Where(c => c.Event is SetTempoEvent),
            c => Assert.Equal(0, c.Tick));
        Assert.Equal(500_000, tempo.MicrosecondsPerQuarterNote); // 120 BPM
        Assert.DoesNotContain(conductor, c => c.Event is TimeSignatureEvent);
        Assert.DoesNotContain(conductor, c => c.Event is MarkerEvent);
    }

    [Fact]
    public void OneSecond_AtFixedTransport_MapsToFourQuarters()
    {
        // 120 BPM fixed => 2 quarters per second; 1 s @ 44100 Hz with PPQ 960
        // must land exactly on tick 1920, independent of any timeline "tempo".
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0))).Bytes;
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);

        Assert.Equal(0, voice.First(e => e.Event is NoteOnEvent).Tick);
        Assert.Equal(1920, voice.First(e => e.Event is NoteOffEvent).Tick);
    }

    [Fact]
    public void SourceRelativeTick_IgnoresAbsoluteSampleZero()
    {
        // StartSample is 100000; a note 44100 samples after that still spans
        // exactly 1920 ticks. Fidelity is to the source-relative clock, not
        // sample-zero origin.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 100_000,
            EndSample = 200_000,
            Notes = new[] { Note("0", 100_000, 144_100, 60.0) },
        };
        byte[] bytes = Transcriber.Transcribe(timeline).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);
        Assert.Equal(0, voice.First(e => e.Event is NoteOnEvent).Tick);
        Assert.Equal(1920, voice.First(e => e.Event is NoteOffEvent).Tick);
    }

    [Fact]
    public void Attack_BendPrecedesNoteOn()
    {
        // The attack carries a pitch-bend to the exact initial pitch before the
        // NoteOn, at the same tick.
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.3))).Bytes;
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);

        var atAttack = voice.Where(e => e.Tick == 0).ToList();
        var channel = atAttack.Where(e => e.Event is ControlChangeEvent or PitchBendEvent or NoteOnEvent).ToList();
        // RPN bend range (5 CCs) then the attack bend then the note-on.
        Assert.Equal(7, channel.Count);
        Assert.IsType<PitchBendEvent>(channel[5].Event);
        Assert.IsType<NoteOnEvent>(channel[6].Event);
    }

    [Fact]
    public void ExactAttackPitch_BendIsZero()
    {
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0))).Bytes;
        var bend = (PitchBendEvent)Track(bytes, 1).First(e => e.Event is PitchBendEvent).Event;
        Assert.Equal(8192, bend.PitchValue); // 0 in signed 14-bit terms
    }

    [Fact]
    public void SameTickRetrigger_NoteOffPrecedesNoteOn()
    {
        // One-note voice retriggering exactly at its own boundary: at the shared
        // tick the old NoteOff must serialize before the new attack bend + NoteOn.
        long boundary = Sr / 2;
        byte[] bytes = Transcriber.Transcribe(Timeline(
            Note("0", 0, boundary, 60.0),
            Note("0", boundary, boundary + Sr, 62.0, retrigger: true))).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);
        long boundaryTick = Ppq; // 0.5 s @ 120 BPM = 1 quarter = 960 ticks
        var atTick = voice.Where(e => e.Tick == boundaryTick).ToList();
        var channel = atTick.Where(e => e.Event is NoteOffEvent or NoteOnEvent or PitchBendEvent).ToList();
        Assert.Equal(3, channel.Count);
        Assert.IsType<NoteOffEvent>(channel[0].Event);
        Assert.IsType<PitchBendEvent>(channel[1].Event);
        Assert.IsType<NoteOnEvent>(channel[2].Event);
    }

    [Fact]
    public void InteriorPitchWrites_EmitBendsAtTheirSourceTicks()
    {
        long glideStart = Sr / 4;
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0,
            pitch: new[]
            {
                Pitch(0, 60.0),
                Pitch(glideStart, 61.0),
                Pitch(Sr / 2, 62.0),
            }))).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);
        var bends = voice.Where(e => e.Event is PitchBendEvent).ToList();
        // Attack bend + one bend per interior state (tick 0, 960, 1920).
        Assert.Equal(3, bends.Count);
        Assert.Equal(0, bends[0].Tick);
        Assert.Equal(2 * Ppq / 4, bends[1].Tick);
        Assert.Equal(Ppq, bends[2].Tick);
    }

    [Fact]
    public void MultiplePitchWrites_AtSameSample_CollapseToFinalState()
    {
        // Two writes at the same interior sample are state transitions; only the
        // final one may emit a bend. A duplicate write at the attack sample is
        // folded into the attack entirely.
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0,
            pitch: new[]
            {
                Pitch(0, 59.0),  // folded into attack; attack wins
                Pitch(0, 60.0),
                Pitch(Sr / 2, 61.0),
                Pitch(Sr / 2, 62.0), // supersedes the 61.0 write
            }))).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);
        var bends = voice.Where(e => e.Event is PitchBendEvent).ToList();
        Assert.Equal(2, bends.Count); // attack + one interior, not two
        Assert.Equal(Ppq, bends[1].Tick);
        var midBend = (PitchBendEvent)bends[1].Event;
        Assert.True(midBend.PitchValue > 8192, "interior bend must reflect the FINAL state (62.0, not 61.0)");
    }

    [Fact]
    public void PitchStates_DecreasingSourceOrder_FailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0,
                pitch: new[]
                {
                    Pitch(Sr / 2, 61.0),
                    Pitch(Sr / 4, 60.0), // goes backwards in source time
                }))));
        Assert.Contains("source order", ex.Message);
    }

    [Fact]
    public void NonFiniteSourcePitch_FailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Transcriber.Transcribe(Timeline(Note("0", 0, Sr, double.NaN))));
        Assert.Contains("finite", ex.Message);
    }

    [Fact]
    public void SourceNoteBeforeTimelineStart_FailsLoudly()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 1000,
            EndSample = 1000 + Sr,
            Notes = new[] { Note("0", 500, 1500, 60.0) },
        };
        var ex = Assert.Throws<InvalidOperationException>(() => Transcriber.Transcribe(timeline));
        Assert.Contains("timeline", ex.Message);
    }

    [Fact]
    public void RequiredBendExcursion_BeyondMidiLimit_FailsLoudly()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 300.0))));
        Assert.Contains("127", ex.Message);
    }

    [Fact]
    public void Voices_SplitIntoPhysicalTracks_ChannelsAvoidPercussion()
    {
        byte[] bytes = Transcriber.Transcribe(Timeline(
            Note("0", 0, Sr, 60.0),
            Note("1", 0, Sr, 62.0),
            Note("2", 0, Sr, 64.0))).Bytes;

        Assert.Equal(4, MidiRoundTrip.TrackChunks(bytes).Count); // conductor + 3 voices
        for (int track = 1; track <= 3; track++)
        {
            var noteOn = (NoteOnEvent)Track(bytes, track).First(e => e.Event is NoteOnEvent).Event;
            Assert.NotEqual((Melanchall.DryWetMidi.Common.FourBitNumber)9, noteOn.Channel);
        }
        var first = (NoteOnEvent)Track(bytes, 1).First(e => e.Event is NoteOnEvent).Event;
        var second = (NoteOnEvent)Track(bytes, 2).First(e => e.Event is NoteOnEvent).Event;
        Assert.Equal(0, (int)first.Channel);
        Assert.Equal(1, (int)second.Channel);
    }

    [Fact]
    public void NativeRhythm_PreservedOnChannelTen()
    {
        byte[] bytes = Transcriber.Transcribe(new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            Rhythm = new[]
            {
                new RhythmEvent("rhythm", "rhythm", 0, 1.0f, 0.0f,
                    InstrumentId: "r0") { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) },
                new RhythmEvent("rhythm", "rhythm", Sr / 2, 0.5f, 0.0f,
                    InstrumentId: "r1") { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) },
            },
        }).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> rhythm = Track(bytes, 1);
        var ons = rhythm.Where(e => e.Event is NoteOnEvent).ToList();
        Assert.Equal(2, ons.Count);
        Assert.All(ons, o => Assert.Equal((Melanchall.DryWetMidi.Common.FourBitNumber)9, ((NoteOnEvent)o.Event).Channel));
        Assert.Equal(0, ons[0].Tick);
        Assert.Equal(Ppq, ons[1].Tick);
    }

    [Fact]
    public void NativeRhythm_VelocityScalesWithStrength()
    {
        byte[] bytes = Transcriber.Transcribe(new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            Rhythm = new[]
            {
                new RhythmEvent("rhythm", "rhythm", 0, 0.2f, 0.0f) { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) },
                new RhythmEvent("rhythm", "rhythm", Sr / 2, 1.0f, 0.0f) { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 0) },
            },
        }).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> rhythm = Track(bytes, 1);
        var ons = rhythm.Where(e => e.Event is NoteOnEvent).Select(e => (NoteOnEvent)e.Event).ToList();
        Assert.True(ons[1].Velocity > ons[0].Velocity,
            $"stronger hit must be louder (got {ons[0].Velocity} then {ons[1].Velocity})");
    }

    [Fact]
    public void ZeroLengthNote_ForcedToOneTick_CountedInDiagnostics()
    {
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(Note("0", 0, 0, 60.0)));
        Assert.Equal(1, result.Diagnostics.OneTickNotes);
        Assert.Equal(1, result.Diagnostics.SourceNoteCount);
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(result.Bytes, 1);
        long on = voice.First(e => e.Event is NoteOnEvent).Tick;
        long off = voice.First(e => e.Event is NoteOffEvent).Tick;
        Assert.Equal(1, off - on);
    }

    [Fact]
    public void SameTickAttackCollisions_CountedInDiagnostics()
    {
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(
            Note("0", 0, 0, 60.0),
            Note("0", 0, Sr, 62.0)));
        Assert.Equal(1, result.Diagnostics.SameTickAttackCollisions);
    }

    [Fact]
    public void NoteOnVelocity_IsDefaultAcrossAllVoices()
    {
        byte[] bytes = Transcriber.Transcribe(Timeline(
            Note("0", 0, Sr, 60.0),
            Note("1", 0, Sr, 62.0))).Bytes;
        for (int track = 1; track <= 2; track++)
        {
            var noteOn = (NoteOnEvent)Track(bytes, track).First(e => e.Event is NoteOnEvent).Event;
            Assert.Equal(96, (int)noteOn.Velocity);
        }
    }
    [Fact]
    public void SamplePlayback_UsesDeterministicIdentityBankAndExactTicks()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "sample-z", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr / 2, Sr, "sample-a", null, 1.0, 1.0f, 0, false, false),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(2, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(2, MidiRoundTrip.TrackChunks(result.Bytes).Count);
        IReadOnlyList<(long Tick, MidiEvent Event)> track = Track(result.Bytes, 1);
        var ons = track.Where(e => e.Event is NoteOnEvent).ToArray();
        Assert.Equal(2, ons.Length);
        Assert.Equal(0, ons[0].Tick);
        Assert.Equal(960, ons[1].Tick);
        Assert.Equal(1, ((NoteOnEvent)ons[0].Event).NoteNumber);
        Assert.Equal(0, ((NoteOnEvent)ons[1].Event).NoteNumber);
        Assert.Equal(0, ((ControlChangeEvent)track.First(e => e.Event is ControlChangeEvent).Event).ControlValue);
    }

    [Fact]
    public void SamplePlayback_RetriggerPlacesNoteOffBeforeNextIdentityAttack()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "sample-a", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr / 2, Sr, "sample-b", null, 1.0, 1.0f, 0, true, false),
            ],
        };

        IReadOnlyList<(long Tick, MidiEvent Event)> events =
            Track(Transcriber.Transcribe(timeline).Bytes, 1);
        var atBoundary = events.Where(e => e.Tick == Ppq).ToArray();
        Assert.IsType<NoteOffEvent>(atBoundary[0].Event);
        Assert.Contains(atBoundary, e => e.Event is NoteOnEvent);
    }

    [Fact]
    public void SamplePlayback_PitchedIdentityEmitsBend()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr, "sample-pitched", 60.5, 1.0, 1.0f, 0, false, false),
            ],
        };
        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Contains(Track(result.Bytes, 1), e => e.Event is PitchBendEvent);
    }
    [Fact]
    public void TonalPcmParallelViews_ShareIdentityAndEmitOneAttack()
    {
        var builder = new TimelineBuilder(Sr);
        builder.AddNote(
            new VoiceId(new DeviceId(ChipType.SnesDsp, 0), VoiceKind.PcmVoice, 0),
            0,
            Sr,
            60.0,
            0,
            "sample",
            VisualizationNoteMode.Pcm,
            false,
            Array.Empty<PitchChange>());

        VisualizationTimeline built = builder.Build(Sr);
        NoteEvent note = Assert.Single(built.Notes);
        SamplePlaybackEvent sample = Assert.Single(built.SamplePlayback);
        Assert.NotNull(note.SourceAttackId);
        Assert.Equal(note.SourceAttackId, sample.SourceAttackId);

        MidiTranscriptionResult result = Transcriber.Transcribe(built);
        Assert.Equal(0, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(1, Track(result.Bytes, 1).Count(e => e.Event is NoteOnEvent));
    }

    [Fact]
    public void UnownedSamplePlayback_EmitsOneAttack()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr, "sample-dac", null, 1.0, 1.0f, 0, false, false)
                {
                    SourceAttackId = "attack:dac",
                },
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(1, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(1, Track(result.Bytes, 1).Count(e => e.Event is NoteOnEvent));
    }

}