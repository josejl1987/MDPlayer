using Fmp.Core.Midi;
using Fmp.Core.Playback;
using Fmp.Core.Playback.Spc;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;
using NoteOnEvent = Melanchall.DryWetMidi.Core.NoteOnEvent;
using NoteOffEvent = Melanchall.DryWetMidi.Core.NoteOffEvent;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// The five-property serialized-bytes acceptance suite. The SMF produced by the
/// fixed-transport exporter (120 BPM / 960 PPQ) is read back byte-for-byte and
/// every property is measured against the SOURCE timeline:
///   P1 attack timing:  every NoteOn tick within 1 tick of the source attack
///   P2 release timing: every NoteOff tick within 1 tick of the source release
///   P3 attack pitch:   decoded attack pitch within 1 cent of the source
///   P4 continuous pitch: interior pitch states within their bend-step tolerance
///   P5 channel sanity: one voice = one channel = one bend range, no overlaps
/// Run on a REAL song (18 U.S.A. (Ken) I.vgz, captured through the real VGM
/// backend) and on a deterministic positive control. Exact numbers are printed
/// per property so the report can cite them.
/// </summary>
public sealed class MidiFivePropertyAcceptanceTests
{
    private const int SampleRate = 44_100;
    private const int Ppq = MidiTranscriber.DefaultPpq;

    [Fact]
    public void FiveProperties_RealSong_18UsaKen()
    {
        string fixture = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "corpus", "18-usa-ken.vgz");
        Assert.True(File.Exists(fixture), $"real fixture missing: {fixture}");

        VisualizationTimeline timeline = Capture(fixture);
        MidiTranscriptionResult export = new MidiTranscriber().Transcribe(timeline);
        Properties properties = Measure(export, timeline);
        Console.WriteLine(
            $"FIVE-PROPERTY real-song 18-usa-ken: notes={export.Diagnostics.SourceNoteCount} " +
            $"rhythm={export.Diagnostics.NativeRhythmHitCount} samples={export.Diagnostics.SamplePlaybackCount} " +
            $"| P1 attacks={properties.Attacks} maxΔt={properties.MaxAttackTickDelta} tick " +
            $"| P2 releases={properties.Releases} maxΔt={properties.MaxReleaseTickDelta} tick " +
            $"| P3 attacks={properties.Attacks} maxErr={properties.MaxAttackPitchCents:0.###} cent " +
            $"| P4 validator={properties.ContinuousPitchPassed} " +
            $"| P5 tracks={properties.Tracks} ranges={properties.BendRangeConfigs} overlaps={properties.Overlaps}");
        AssertProperties(properties);
    }

    [Fact]
    public void FiveProperties_DeterministicPositiveControl()
    {
        // Exact-grid song at 120 BPM: every onset is an exact tick, so P1/P2
        // must be ZERO ticks and P3 must be zero cents for the flat notes.
        double spq = SampleRate * 60.0 / 120.0;
        var notes = new[]
        {
            Note("v0", 0, (long)(4 * spq), 60.25, new[] { new PitchChange((long)(2 * spq), 0, 62.0) }),
            Note("v1", 0, (long)(2 * spq), 64.0, Array.Empty<PitchChange>()),
            Note("v0", (long)(4 * spq), (long)(8 * spq), 57.0, Array.Empty<PitchChange>()),
        };
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = (long)(8 * spq),
            Notes = notes,
            Rhythm = new[]
            {
                new RhythmEvent("kick", "kick", 0, 1.0f, 0),
                new RhythmEvent("hat", "hat", (long)(2 * spq), 0.5f, 0),
            },
        };

        MidiTranscriptionResult export = new MidiTranscriber().Transcribe(timeline);
        Properties properties = Measure(export, timeline);
        Console.WriteLine(
            $"FIVE-PROPERTY deterministic positive-control: " +
            $"| P1 maxΔt={properties.MaxAttackTickDelta} tick | P2 maxΔt={properties.MaxReleaseTickDelta} tick " +
            $"| P3 maxErr={properties.MaxAttackPitchCents:0.###} cent | P4 passes={properties.ContinuousPitchPassed} " +
            $"| P5 ranges={properties.BendRangeConfigs} overlaps={properties.Overlaps}");
        AssertProperties(properties);
        Assert.Equal(0, properties.MaxAttackTickDelta);
        Assert.Equal(0, properties.MaxReleaseTickDelta);
        // The 60.25 attack encodes through the 14-bit bend word, whose
        // quantization floor is ~0.003 cents here — well under the 1-cent bound.
        Assert.InRange(properties.MaxAttackPitchCents, 0, 0.01);
    }

    private static VisualizationTimeline Capture(string fixture)
    {
        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-five-property-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(SampleRate);
        try
        {
            IPlaybackBackend backend = Path.GetExtension(fixture)
                .Equals(".spc", StringComparison.OrdinalIgnoreCase)
                ? new SpcPlaybackBackend()
                : new VgmPlaybackBackend();
            using IPlaybackCaptureSession session = backend.Open(
                new FileInfo(fixture),
                new PlaybackOptions(
                    LoopCount: 1,
                    FadeSeconds: 0,
                    TailSeconds: 0,
                    MaxDurationSeconds: 60,
                    OutputAudioPath: wav,
                    SampleRate: SampleRate),
                sink);
            session.Run();
            return sink.Complete(session.SamplePosition, "five-property-acceptance");
        }
        finally
        {
            if (File.Exists(wav))
                File.Delete(wav);
            if (File.Exists(wav + ".tmp"))
                File.Delete(wav + ".tmp");
        }
    }

    private static Properties Measure(MidiTranscriptionResult export, VisualizationTimeline timeline)
    {
        long SourceTick(long sample) => MidiTransportClock.SampleToTick(
            timeline.StartSample, sample, timeline.SampleRate, Ppq);

        var properties = new Properties();
        int trackIndex = 0;
        foreach (MidiTrack track in export.Tracks)
        {
            IReadOnlyList<SourcePitchNote>? sources = track.SourceNotes;
            var parsed = MidiRoundTrip.TimedEvents(export.Bytes, trackIndex + 1)
                .Where(e => e.Event is NoteOnEvent or NoteOffEvent or PitchBendEvent
                    || e.Event is ControlChangeEvent cc2 && cc2.ControlNumber == 6)
                .Select(e => (e.Tick, e.Event))
                .ToList();
            int rangeSetCount = parsed.Count(e => e.Event is ControlChangeEvent);
            if (rangeSetCount > 1)
                properties.BendRangeConfigs += rangeSetCount;

            if (sources is null)
            {
                // Sample/rhythm tracks: verify note-on/off pairing only.
                int ons = parsed.Count(e => e.Event is NoteOnEvent);
                int offs = parsed.Count(e => e.Event is NoteOffEvent);
                properties.Overlaps += Math.Abs(ons - offs);
                continue;
            }

            properties.Tracks++;
            properties.BendRangeConfigs += rangeSetCount;

            // Merge the serialized channel stream with the source notes in order.
            int sourceIndex = 0;
            int bendBeforeNoteOn = 0;
            SourcePitchNote? active = null;
            foreach ((long tick, MidiEvent evt) in parsed)
            {
                switch (evt)
                {
                    case PitchBendEvent bend:
                        bendBeforeNoteOn = bend.PitchValue - 8192;
                        break;
                    case NoteOnEvent noteOn when noteOn.Velocity != 0:
                        SourcePitchNote source = sources[sourceIndex++];
                        long expectedOn = SourceTick(source.StartSample);
                        properties.Attacks++;
                        properties.MaxAttackTickDelta = Math.Max(
                            properties.MaxAttackTickDelta, Math.Abs(expectedOn - tick));
                        properties.MaxAttackPitchCents = Math.Max(
                            properties.MaxAttackPitchCents,
                            Math.Abs(DecodeAttackPitch(noteOn.NoteNumber, bendBeforeNoteOn, track, export) - source.InitialMidiNote) * 100.0);
                        active = source;
                        break;
                    case NoteOffEvent noteOff when active is not null:
                        properties.Releases++;
                        long expectedRelease = Math.Max(
                            SourceTick(active.EndSample), SourceTick(active.StartSample) + 1);
                        properties.MaxReleaseTickDelta = Math.Max(
                            properties.MaxReleaseTickDelta,
                            Math.Abs(expectedRelease - tick));
                        active = null;
                        break;
                }
            }

            trackIndex++;
        }

        // P4 continuous pitch and P5 channel-state integrity come from the
        // independent validator (it fails loudly on any violation).
        try
        {
            IndependentMidiPitchValidator.Validate(timeline, export);
            IndependentMidiPitchValidator.ValidateAbsoluteTiming(timeline, export);
            properties.ContinuousPitchPassed = 1;
        }
        catch (Exception error)
        {
            throw new Xunit.Sdk.XunitException($"P4/P5 validator failed: {error.Message}");
        }
        return properties;
    }

    private static double DecodeAttackPitch(
        int baseNote,
        int signedBend,
        MidiTrack track,
        MidiTranscriptionResult export)
    {
        int bendRange = track.Events
            .OfType<MidiBendRangeEvent>()
            .Select(evt => evt.Semitones)
            .SingleOrDefault();
        double denominator = signedBend < 0 ? 8192.0 : 8191.0;
        return baseNote + signedBend / denominator * bendRange;
    }

    private static void AssertProperties(Properties properties)
    {
        Assert.InRange(properties.MaxAttackTickDelta, 0, 1);
        Assert.InRange(properties.MaxReleaseTickDelta, 0, 1);
        Assert.InRange(properties.MaxAttackPitchCents, 0, 1.0 + 1e-9);
        Assert.Equal(1, properties.ContinuousPitchPassed);
        Assert.Equal(0, properties.Overlaps);
    }

    private static NoteEvent Note(string voice, long start, long end, double pitch, PitchChange[] changes)
    {
        var note = new NoteEvent(
            "five-property-" + voice,
            start,
            end,
            0,
            pitch,
            "five-property",
            VisualizationNoteMode.Fm,
            false,
            changes);
        return note with
        {
            Domain = new SourceDomainKey(
                new DeviceId(ChipType.Ym2608, 0), VoiceKind.Fm, int.Parse(voice[1..])),
        };
    }

    private sealed class Properties
    {
        public int Attacks;
        public int Releases;
        public int ContinuousPitchPassed;
        public long MaxAttackTickDelta;
        public long MaxReleaseTickDelta;
        public double MaxAttackPitchCents;
        public int Tracks;
        public int BendRangeConfigs;
        public int Overlaps;
    }
}