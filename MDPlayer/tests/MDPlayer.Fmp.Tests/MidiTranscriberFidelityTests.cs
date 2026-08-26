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
        IReadOnlyList<PitchChange>? pitch = null, bool retrigger = false,
        string instrument = "test")
    {
        var note = new NoteEvent(
            ChannelId: "voice-" + voice,
            StartSample: start,
            EndSample: end,
            InitialFrequencyHz: 0,
            InitialMidiNote: midiNote,
            InstrumentId: instrument,
            Mode: VisualizationNoteMode.Fm,
            IsRetrigger: retrigger,
            Pitch: pitch ?? Array.Empty<PitchChange>());
        return note with { Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, int.Parse(voice)) };
    }

    private static PitchChange Pitch(long sample, double midiNote) =>
        new(sample, 0, midiNote);

    private static IReadOnlyList<(long Tick, MidiEvent Event)> Track(byte[] bytes, int trackIndex) =>
        MidiRoundTrip.TimedEvents(bytes, trackIndex);

    private static int NoteOnCount(MidiTranscriptionResult result) =>
        result.Tracks
            .SelectMany(track => track.Events)
            .OfType<MidiNoteEvent>()
            .Count(note => note.NoteOn);

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
        // RPN bend range (6 CCs, including Data Entry LSB) then the attack bend
        // then the note-on.
        Assert.Equal(8, channel.Count);
        Assert.IsType<PitchBendEvent>(channel[6].Event);
        Assert.IsType<NoteOnEvent>(channel[7].Event);
    }

    [Fact]
    public void ExactAttackPitch_EmitsNoPitchBendOrRange()
    {
        byte[] bytes = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.0))).Bytes;
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);
        Assert.DoesNotContain(voice, e => e.Event is PitchBendEvent);
        Assert.DoesNotContain(voice, e => e.Event is ControlChangeEvent cc && cc.ControlNumber == 6);
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
        Assert.Equal(2, channel.Count);
        Assert.IsType<NoteOffEvent>(channel[0].Event);
        Assert.IsType<NoteOnEvent>(channel[1].Event);
    }

    [Fact]
    public void InstrumentChange_UsesProgramChangeInsideTheSameDomainTrack()
    {
        long boundary = Sr / 2;
        byte[] bytes = Transcriber.Transcribe(Timeline(
            Note("0", 0, boundary, 60.0, instrument: "a"),
            Note("0", boundary, boundary + Sr, 62.0, instrument: "b"))).Bytes;

        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(bytes, 1);
        ProgramChangeEvent[] programs = voice
            .Select(entry => entry.Event)
            .OfType<ProgramChangeEvent>()
            .ToArray();
        Assert.Equal(2, programs.Length);
        Assert.Equal(new[] { 0L, (long)Ppq }, voice
            .Where(entry => entry.Event is ProgramChangeEvent)
            .Select(entry => entry.Tick));

        var boundaryEvents = voice.Where(entry => entry.Tick == Ppq).ToArray();
        Assert.IsType<NoteOffEvent>(boundaryEvents[0].Event);
        Assert.IsType<ProgramChangeEvent>(boundaryEvents[1].Event);
        Assert.IsType<NoteOnEvent>(boundaryEvents[2].Event);
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
    public void BaseNote_IsTheRoundedAttackPitch()
    {
        // Patch 2: the base note is the attack pitch rounded to the nearest
        // semitone (no minimax search). 60.25 -> base 60, with a bend range that
        // covers the whole curve (max excursion |61.75 - 60| = 1.75 -> range 2).
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(Note("0", 0, Sr, 60.25,
            pitch: new[] { Pitch(Sr / 2, 61.75) })));

        NoteOnEvent noteOn = (NoteOnEvent)Track(result.Bytes, 1)
            .First(e => e.Event is NoteOnEvent).Event;
        Assert.Equal(60, (int)noteOn.NoteNumber);
        ControlChangeEvent range = Track(result.Bytes, 1)
            .Select(e => e.Event)
            .OfType<ControlChangeEvent>()
            .Single(cc => cc.ControlNumber == 6);
        Assert.Equal(2, range.ControlValue);
    }

    [Fact]
    public void BendRange_IsOneGlobalValueAcrossTheSourceVoice()
    {
        // One physical voice = one channel = one bend range: the max excursion
        // over ALL its notes (|64.75 - 62| = 2.75 -> range 3), configured once.
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(
            Note("0", 0, Sr, 60.25,
                pitch: new[] { Pitch(Sr / 2, 60.75) }),
            Note("0", Sr, 2 * Sr, 62.25,
                pitch: new[] { Pitch(Sr + Sr / 2, 64.75) })));

        ControlChangeEvent[] ranges = Track(result.Bytes, 1)
            .Select(e => e.Event)
            .OfType<ControlChangeEvent>()
            .Where(cc => cc.ControlNumber == 6)
            .ToArray();
        Assert.Single(ranges);
        Assert.Equal(3, ranges[0].ControlValue);
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
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(
            Note("0", 0, Sr, 60.0),
            Note("1", 0, Sr, 62.0),
            Note("2", 0, Sr, 64.0)));
        byte[] bytes = result.Bytes;

        Assert.Equal(4, MidiRoundTrip.TrackChunks(bytes).Count); // conductor + 3 voices
        Assert.Equal(3, result.Tracks.Count);
        Assert.Equal(3, result.Tracks.Select(track => track.Endpoint.Channel).Distinct().Count());
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
    public void GridAlignedOnsets_KeepRequestedPpq_AndReportZeroQuantizationLoss()
    {
        // 8th-note grid at 120 BPM / 44.1 kHz: samples are multiples of
        // 11025, which 960 PPQ already represents exactly. Raise-first must
        // not change the PPQ, and the loss diagnostic must be zero.
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(
            Note("1", 0, 11025, 60),
            Note("1", 11025, 22050, 62),
            Note("2", 11025, 22050, 64),
            Note("2", 22050, 33075, 65)));

        Assert.Equal(Ppq, result.Diagnostics.EffectivePpq);
        Assert.Equal(0, result.Diagnostics.QuantizationLossyEventCount);
        Assert.Equal(0, result.Diagnostics.MaxQuantizationLossTicks);
    }

    [Fact]
    public void OffGridOnsets_KeepFixedPpq_AndReportQuantizationLoss()
    {
        // Patch 4: no PPQ raising. Onsets at samples 1000 and 2000 are not exact
        // ticks on the 960-PPQ grid, so they quantize (round-half-away) and the
        // loss is reported — never silently rescued by a different PPQ.
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(
            Note("1", 1000, 2000, 60),
            Note("2", 2000, 3000, 62)));

        Assert.Equal(Ppq, result.Diagnostics.EffectivePpq);
        // on(1000), off(2000), on(2000), off(3000): all four note events are
        // off the 960-PPQ grid, so all four are reported as lossy.
        Assert.Equal(4, result.Diagnostics.QuantizationLossyEventCount);
        Assert.True(result.Diagnostics.MaxQuantizationLossTicks > 0);
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
    public void SameTickAttacks_BothSurvive_AndAreCountedInDiagnostics()
    {
        // Two attacks quantizing to the same MIDI tick must NEVER cause a
        // source note to be deleted: both NoteOns survive, sequentially, at
        // that tick.
        VisualizationTimeline timeline = Timeline(
            Note("0", 0, 0, 60.0),
            Note("0", 0, Sr, 62.0));
        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);

        Assert.Equal(1, result.Diagnostics.SameTickAttackCollisions);
        Assert.Equal(2, result.Diagnostics.UniqueAudibleAttackCount);

        IndependentMidiPitchValidator.Validate(timeline, result);
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(result.Bytes, 1);
        Assert.Equal(2, voice.Count(e => e.Event is NoteOnEvent));
        Assert.Equal(2, voice.Count(e => e.Event is NoteOffEvent));
        Assert.All(
            voice.Where(e => e.Event is NoteOnEvent),
            e => Assert.Equal(0L, e.Tick));
    }

    [Fact]
    public void SameTickBoundaryRetrigger_PreservesBothAttacks_OffPrecedesOn_WithAttackBend()
    {
        // Adversarial: note A ends at source sample X and note B starts at
        // X+1, with A's release and B's attack quantizing to the same MIDI
        // tick. Assert ALL of: both NoteOns survive; NoteOff A precedes
        // NoteOn B at the shared tick; NoteOn B carries the correct attack
        // bend; source attack count == serialized NoteOn count.
        // A second voice repeats the literal same-tick ATTACK case (a sub-tick
        // blip A2 followed by B2 at the same sample): the old
        // collision-dropping behavior deleted one of these attacks; both must
        // now survive.
        long x = 30;   // tick(30) == 1
        long bx = 31;  // tick(31) == 1 (X + 1)
        long y = 45;   // tick(45) == 2
        VisualizationTimeline timeline = Timeline(
            Note("0", 0, x, 60.0),
            Note("0", bx, bx + Sr, 60.4, retrigger: true),
            Note("1", 40, y, 60.0),
            Note("1", y, y + Sr, 60.4, retrigger: true));
        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);

        // Strict attack conservation: unique source attacks == serialized NoteOns.
        Assert.Equal(4, result.Diagnostics.UniqueAudibleAttackCount);
        Assert.Equal(4, NoteOnCount(result));
        IndependentMidiPitchValidator.Validate(timeline, result);

        // Voice 0: A ends at sample 30 (tick 1), B starts at sample 31 (tick 1).
        IReadOnlyList<(long Tick, MidiEvent Event)> voice0 = Track(result.Bytes, 1);
        Assert.Equal(2, voice0.Count(e => e.Event is NoteOnEvent));
        Assert.Equal(2, voice0.Count(e => e.Event is NoteOffEvent));
        var boundary = voice0.Where(e => e.Tick == 1).ToList();
        Assert.Equal(3, boundary.Count); // NoteOff A, attack bend, NoteOn B
        Assert.IsType<NoteOffEvent>(boundary[0].Event); // NoteOff A first
        Assert.IsType<PitchBendEvent>(boundary[1].Event);
        Assert.IsType<NoteOnEvent>(boundary[2].Event); // then NoteOn B
        var noteOnB = (NoteOnEvent)boundary[2].Event;
        Assert.Equal(60, (int)noteOnB.NoteNumber);
        int bendRange = result.Tracks[0].Events
            .OfType<MidiBendRangeEvent>()
            .Select(evt => evt.Semitones)
            .Single();
        Assert.Equal(1, bendRange);
        double decoded = MidiPitchCompiler.DecodePitch(
            60, ((PitchBendEvent)boundary[1].Event).PitchValue, bendRange);
        Assert.InRange(decoded, 60.399, 60.401);

        // Voice 1: both attacks at tick 2 (A2 = [40, 45], B2 starts at 45).
        IReadOnlyList<(long Tick, MidiEvent Event)> voice1 = Track(result.Bytes, 2);
        Assert.Equal(2, voice1.Count(e => e.Event is NoteOnEvent));
        Assert.Equal(2, voice1.Count(e => e.Event is NoteOffEvent));
        var sameTickAttacks = voice1.Where(e => e.Tick == 2).ToList();
        // ProgramChange (first note of the voice) + attack bend + two NoteOns.
        Assert.Single(sameTickAttacks, e => e.Event is ProgramChangeEvent);
        Assert.Single(sameTickAttacks, e => e.Event is PitchBendEvent);
        Assert.Equal(2, sameTickAttacks.Count(e => e.Event is NoteOnEvent));
        Assert.Equal(1, voice1.Count(e => e.Tick == 3 && e.Event is NoteOffEvent)); // A2 off at on+1
    }

    [Fact]
    public void OneTickFloor_IsRepresentational_NotSourceReleaseFidelity()
    {
        // Sub-tick source note: the release quantizes to the SAME tick as the
        // attack (sourceOffTick == sourceOnTick), so the serialized off is
        // floored to sourceOnTick + 1. The floor is a MIDI representational
        // limit and is explicitly NOT an exact source->tick match.
        long start = 0, end = 5; // tick(0) == tick(5) == 0
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(Note("0", start, end, 60.0)));
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(result.Bytes, 1);
        long on = voice.First(e => e.Event is NoteOnEvent).Tick;
        long off = voice.First(e => e.Event is NoteOffEvent).Tick;
        long sourceOn = MidiTransportClock.SampleToTick(0, start, Sr, Ppq);
        long sourceOff = MidiTransportClock.SampleToTick(0, end, Sr, Ppq);
        Assert.Equal(0, sourceOn);
        Assert.Equal(0, sourceOff);
        Assert.Equal(sourceOff, sourceOn); // release quantizes to the attack tick
        Assert.Equal(on, sourceOn);
        Assert.Equal(sourceOn + 1, off);   // the one-tick representational floor
        Assert.NotEqual(sourceOff, off);   // NOT exact source release fidelity
    }

    [Fact]
    public void ExactSourceRelease_MapsExactlyToSourceTick()
    {
        // A note whose release quantizes strictly after its attack tick is
        // serialized at the exact source tick (sourceOffTick == midiOffTick).
        long start = 0, end = Sr;
        MidiTranscriptionResult result = Transcriber.Transcribe(Timeline(Note("0", start, end, 60.0)));
        IReadOnlyList<(long Tick, MidiEvent Event)> voice = Track(result.Bytes, 1);
        long on = voice.First(e => e.Event is NoteOnEvent).Tick;
        long off = voice.First(e => e.Event is NoteOffEvent).Tick;
        long sourceOn = MidiTransportClock.SampleToTick(0, start, Sr, Ppq);
        long sourceOff = MidiTransportClock.SampleToTick(0, end, Sr, Ppq);
        Assert.Equal(sourceOn, on);
        Assert.Equal(1920, off);
        Assert.Equal(sourceOff, off);
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
    public void SamplePlayback_DeterministicChannel10IdentityNotes_ExactTicks()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "dac:1", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr / 2, Sr, "dac:0", null, 1.0, 1.0f, 0, false, false),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(2, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(2, result.Diagnostics.SampleIdentityCount);
        Assert.Equal(2, MidiRoundTrip.TrackChunks(result.Bytes).Count);
        Assert.Single(result.Tracks);
        Assert.Equal(new MidiEndpoint(0, 9), result.Tracks[0].Endpoint);
        IReadOnlyList<(long Tick, MidiEvent Event)> track = Track(result.Bytes, 1);
        var ons = track.Where(e => e.Event is NoteOnEvent).ToArray();
        Assert.Equal(2, ons.Length);
        Assert.Equal(0, ons[0].Tick);
        Assert.Equal(960, ons[1].Tick);
        // dac:{catalogOrdinal} -> note = 36 + catalogOrdinal on channel 10.
        Assert.Equal(36, ((NoteOnEvent)ons[1].Event).NoteNumber);
        Assert.Equal(37, ((NoteOnEvent)ons[0].Event).NoteNumber);
        Assert.Equal(9, ((NoteOnEvent)ons[0].Event).Channel);
        Assert.Equal(9, ((NoteOnEvent)ons[1].Event).Channel);
        Assert.DoesNotContain(track, e => e.Event is ControlChangeEvent); // no bank select
    }

    [Fact]
    public void DacSample_SameIdentity_AlwaysSameMidiNote()
    {
        // The same kick played 500 times must map to one stable note. Three
        // playbacks of dac:0 at different offsets/times -> three NoteOns, all
        // note 36.
        var playbacks = new SamplePlaybackEvent[3];
        for (int i = 0; i < playbacks.Length; i++)
        {
            long start = i * Sr / 4;
            playbacks[i] = new SamplePlaybackEvent(
                "ym2612.0.pcm.dac", start, start + Sr / 8, "dac:0", null, 1.0, 1.0f, 0, false, false);
        }
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback = playbacks,
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(3, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(1, result.Diagnostics.SampleIdentityCount);
        var ons = Track(result.Bytes, 1).Where(e => e.Event is NoteOnEvent)
            .Select(e => (NoteOnEvent)e.Event).ToArray();
        Assert.Equal(3, ons.Length);
        Assert.All(ons, on => Assert.Equal(36, on.NoteNumber));
    }

    [Fact]
    public void DacSample_DifferentIdentity_DifferentMidiNote()
    {
        // dac:0, dac:1, dac:2 -> notes 36, 37, 38 in catalog ordinal order.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr * 3 / 2,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "dac:2", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr / 2, Sr, "dac:0", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr, Sr * 3 / 2, "dac:1", null, 1.0, 1.0f, 0, false, false),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(3, result.Diagnostics.SampleIdentityCount);
        var ons = Track(result.Bytes, 1).Where(e => e.Event is NoteOnEvent)
            .Select(e => (NoteOnEvent)e.Event).ToArray();
        Assert.Equal(new[] { 38, 36, 37 }, ons.Select(on => (int)on.NoteNumber));
    }

    [Fact]
    public void DacSample_PlaybackCountEqualsSerializedNoteOnCount()
    {
        // 500 playbacks of one identity plus 12 of another: every playback must
        // serialize exactly one NoteOn and one matching NoteOff.
        var playbacks = new List<SamplePlaybackEvent>();
        for (int i = 0; i < 500; i++)
        {
            long start = i * 80;
            playbacks.Add(new SamplePlaybackEvent(
                "ym2612.0.pcm.dac", start, start + 40, "dac:0", null, 1.0, 1.0f, 0, false, false));
        }
        for (int i = 0; i < 12; i++)
        {
            long start = 40_000 + i * 80;
            playbacks.Add(new SamplePlaybackEvent(
                "ym2612.0.pcm.dac", start, start + 40, "dac:1", null, 1.0, 1.0f, 0, false, false));
        }
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = 45_000,
            SamplePlayback = playbacks.ToArray(),
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(512, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(512, result.Diagnostics.UniqueAudibleAttackCount);
        var track = Track(result.Bytes, 1);
        Assert.Equal(512, track.Count(e => e.Event is NoteOnEvent));
        Assert.Equal(512, track.Count(e => e.Event is NoteOffEvent));
    }

    [Fact]
    public void DacContinuousPlayback_ExpandsToPerHitTriggers()
    {
        // A continuous DAC stream is NOT a single trigger: each inferred hit
        // must serialize as its own NoteOn. Regression for 01 Open Mind
        // (1 continuous playback, 226 hits -> 226 triggers).
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr, "dac:0", null, 1.0, 1.0f, 0, false, false),
            ],
            DacHits =
            [
                new DacHitEvent("ym2612.0.pcm.dac", 0, 1000, "dac:0", "kick-a", 0, 1000, DacHitClass.Kick, DacHitIdentityKind.Inferred, 0.9f, 0.5f),
                new DacHitEvent("ym2612.0.pcm.dac", 2000, 3000, "dac:0", "kick-a", 0, 1000, DacHitClass.Kick, DacHitIdentityKind.Inferred, 0.9f, 0.8f),
                new DacHitEvent("ym2612.0.pcm.dac", 4000, 5000, "dac:0", "kick-a", 0, 1000, DacHitClass.Kick, DacHitIdentityKind.Inferred, 0.9f, 0.3f),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(3, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(3, result.Diagnostics.UniqueAudibleAttackCount);
        Assert.Equal(1, result.Diagnostics.SampleIdentityCount); // byte-identical content -> ONE identity
        var track = Track(result.Bytes, 1);
        var ons = track.Where(e => e.Event is NoteOnEvent).ToArray();
        Assert.Equal(3, ons.Length);
        Assert.All(ons, e => Assert.Equal(36, ((NoteOnEvent)e.Event).NoteNumber)); // same content -> same note
        Assert.All(ons, e => Assert.Equal(9, ((NoteOnEvent)e.Event).Channel));     // channel 10
        // Peak amplitude maps to velocity; distinct peaks -> distinct velocities.
        int[] velocities = ons.Select(e => (int)((NoteOnEvent)e.Event).Velocity).ToArray();
        Assert.Equal(3, velocities.Distinct().Count());
        Assert.Equal(3, track.Count(e => e.Event is NoteOffEvent)); // every trigger has a release
    }

    [Fact]
    public void DacHits_DistinctContent_MapsToDistinctNotes()
    {
        // The MIDI identity is the hit CONTENT, not the stream asset id:
        // byte-distinct slices (kick vs snare) must map to different notes.
        // Regression for 01 Open Mind (226 hits, all previously note 36).
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr, "dac:0", null, 1.0, 1.0f, 0, false, false),
            ],
            DacHits =
            [
                new DacHitEvent("ym2612.0.pcm.dac", 0, 1000, "dac:0", "kick-slice", 0, 1000, DacHitClass.Kick, DacHitIdentityKind.Inferred, 0.9f, 0.5f),
                new DacHitEvent("ym2612.0.pcm.dac", 2000, 3000, "dac:0", "snare-slice", 0, 1000, DacHitClass.Snare, DacHitIdentityKind.Inferred, 0.9f, 0.5f),
                new DacHitEvent("ym2612.0.pcm.dac", 4000, 5000, "dac:0", "tom-slice", 0, 1000, DacHitClass.Tom, DacHitIdentityKind.Inferred, 0.9f, 0.5f),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(3, result.Diagnostics.SampleIdentityCount);
        var track = Track(result.Bytes, 1);
        int[] notes = track.Where(e => e.Event is NoteOnEvent)
            .Select(e => (int)((NoteOnEvent)e.Event).NoteNumber)
            .OrderBy(note => note)
            .ToArray();
        Assert.Equal(3, notes.Length);
        Assert.Equal(3, notes.Distinct().Count()); // distinct content -> distinct notes
    }

    [Fact]
    public void DacTrack_HasZeroPitchBendEvents()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "dac:0", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr / 2, Sr, "dac:1", 60.5, 1.0, 1.0f, 0, false, false),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        IReadOnlyList<(long Tick, MidiEvent Event)> track = Track(result.Bytes, 1);
        Assert.DoesNotContain(track, e => e.Event is PitchBendEvent);
        Assert.DoesNotContain(track, e => e.Event is ControlChangeEvent cc && cc.ControlNumber == 6);
        Assert.DoesNotContain(track, e => e.Event is ControlChangeEvent cc2 && (int)cc2.ControlNumber is 0 or 32);
    }

    [Fact]
    public void DacSample_DedupIsIdentityBased_NotInstanceBased()
    {
        // Two playback instances of the SAME identity at different volumes:
        // one note, different velocity. Instance count must never mint notes.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("nes.pcm.dpcm", 0, Sr / 2, "sample:nes-apu:dpcm", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("nes.pcm.dpcm", Sr / 2, Sr, "sample:nes-apu:dpcm", null, 1.0, 0.3f, 0, false, false),
            ],
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(2, result.Diagnostics.SamplePlaybackCount);
        Assert.Equal(1, result.Diagnostics.SampleIdentityCount);
        var ons = Track(result.Bytes, 1).Where(e => e.Event is NoteOnEvent)
            .Select(e => (NoteOnEvent)e.Event).ToArray();
        Assert.Equal(2, ons.Length);
        Assert.Equal(ons[0].NoteNumber, ons[1].NoteNumber);
        Assert.NotEqual(ons[0].Velocity, ons[1].Velocity);
    }

    [Fact]
    public void DacSample_ReservedRange36To95_ThenExtendsThenSpillsPort()
    {
        // Ordinals 0..59 land on the reserved 36..95. The mapping then extends
        // across the usable 0..127 range: ordinal 60 -> 96, ordinal 91 -> 127,
        // ordinal 92 -> 0. Once a lane holds 128 identities (ordinal 128), the
        // next identity spills to port 1 on the same channel 10.
        long step = 60;
        var playbacks = new List<SamplePlaybackEvent>();
        for (int ordinal = 0; ordinal <= 128; ordinal++)
        {
            long start = ordinal * step;
            playbacks.Add(new SamplePlaybackEvent(
                "ym2612.0.pcm.dac", start, start + 30, $"dac:{ordinal}", null, 1.0, 1.0f, 0, false, false));
        }
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = 129 * step,
            SamplePlayback = playbacks.ToArray(),
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(129, result.Diagnostics.SampleIdentityCount);
        Assert.Equal(2, result.Tracks.Count); // lane 0 on port 0, lane 1 on port 1
        Assert.Equal(new MidiEndpoint(0, 9), result.Tracks[0].Endpoint);
        Assert.Equal(new MidiEndpoint(1, 9), result.Tracks[1].Endpoint);

        var lane0 = Track(result.Bytes, 1).Where(e => e.Event is NoteOnEvent)
            .Select(e => (NoteOnEvent)e.Event).ToArray();
        var lane1 = Track(result.Bytes, 2).Where(e => e.Event is NoteOnEvent)
            .Select(e => (NoteOnEvent)e.Event).ToArray();
        Assert.Equal(128, lane0.Length);
        Assert.Single(lane1);
        Assert.Equal(36, lane0[0].NoteNumber);       // dac:0 -> 36
        Assert.Equal(95, lane0[59].NoteNumber);      // dac:59 -> 95 (reserved range end)
        Assert.Equal(96, lane0[60].NoteNumber);      // extend across 0..127
        Assert.Equal(127, lane0[91].NoteNumber);
        Assert.Equal(0, lane0[92].NoteNumber);       // wraps to the usable 0..35
        Assert.Equal(35, lane0[127].NoteNumber);     // lane 0 full
        Assert.Equal(36, lane1[0].NoteNumber);       // spill: port 1, ch 10, note 36
        Assert.All(lane1, on => Assert.Equal(9, on.Channel));
    }

    [Fact]
    public void DacSample_DacAndRhythmShareChannel10_OnDistinctPorts()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "dac:0", null, 1.0, 1.0f, 0, false, false),
            ],
            Rhythm = new[]
            {
                new RhythmEvent("kick", "kick", 0, 1.0f, 0),
            },
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(2, result.Tracks.Count);
        Assert.Equal(new MidiEndpoint(0, 9), result.Tracks[1].Endpoint); // native rhythm
        Assert.Equal(new MidiEndpoint(1, 9), result.Tracks[0].Endpoint); // DAC keeps ch 10, port 1
        var dacOn = Track(result.Bytes, 1).Single(e => e.Event is NoteOnEvent);
        Assert.Equal(9, ((NoteOnEvent)dacOn.Event).Channel);
        var rhythmOn = Track(result.Bytes, 2).Single(e => e.Event is NoteOnEvent);
        Assert.Equal(9, ((NoteOnEvent)rhythmOn.Event).Channel);
    }

    [Fact]
    public void DacSample_IdentityMapping_EmittedInDiagnostics()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 0,
            EndSample = Sr,
            SamplePlayback =
            [
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 2, "dac:0", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", Sr / 2, Sr, "dac:0", null, 1.0, 1.0f, 0, false, false),
                new SamplePlaybackEvent("ym2612.0.pcm.dac", 0, Sr / 3, "dac:3", null, 1.0, 1.0f, 0, false, false),
            ],
            Samples = new[]
            {
                new SampleDefinition("dac:0", "pcm", 64, null, null, null, SampleLoopMode.None,
                    [new WaveformEnvelopePoint(0, 0.5f)], "DAC S000"),
                new SampleDefinition("dac:3", "pcm", 64, null, null, null, SampleLoopMode.None,
                    [new WaveformEnvelopePoint(0, 0.5f)], "DAC S003"),
            },
        };

        MidiTranscriptionResult result = Transcriber.Transcribe(timeline);
        Assert.Equal(2, result.Diagnostics.SampleIdentityCount);
        string[] mappings = result.Diagnostics.SampleIdentityMappings.ToArray();
        Assert.Equal(2, mappings.Length);
        Assert.Contains(mappings, m => m.Contains("dac:0 -> port 0 ch 10 note 36 (DAC S000, ordinal 0, 2 playbacks)", StringComparison.Ordinal));
        Assert.Contains(mappings, m => m.Contains("dac:3 -> port 0 ch 10 note 39 (DAC S003, ordinal 3, 1 playbacks)", StringComparison.Ordinal));
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
    public void SamplePlayback_IgnoresSourcePitch()
    {
        // Patch 5: a DAC sample is triggered by its identity note only; the
        // source pitch of the sample event is deliberately not encoded.
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
        Assert.DoesNotContain(Track(result.Bytes, 1), e => e.Event is PitchBendEvent);
        Assert.DoesNotContain(Track(result.Bytes, 1),
            e => e.Event is ControlChangeEvent cc && cc.ControlNumber == 6);
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
