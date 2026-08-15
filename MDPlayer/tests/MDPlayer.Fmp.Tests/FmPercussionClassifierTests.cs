using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Phase 1 unit tests for the conservative FM percussion classifier (spec §5,
/// acceptance item 6). "Is percussive" and "which drum" are independent: a
/// bass with low pitch and "fm" naming must never classify; a fast-attack
/// decaying transient with no kick/snare/hat identity must return
/// IsPercussive=true, Role=Unknown at high confidence, never coerced.
/// </summary>
public sealed class FmPercussionClassifierTests
{
    private const int SampleRate = 44_100;

    private static FmOperatorDefinition Op(int ar, int sl, int sr, int rr, int tl = 30) =>
        new(ar, sr, sr, rr, sl, tl, 0, 1, 0, AmplitudeModulation: false, SsgEnvelope: 0);

    private static InstrumentDefinition Instrument(string id, int algorithm, params FmOperatorDefinition[] ops) =>
        new(id, "Fm", algorithm, Feedback: 0, Ams: null, Fms: null, ops);

    private static NoteEvent Note(string channelId, string instrumentId, long start, long end,
        bool isRetrigger, double midi = 60, VisualizationNoteMode mode = VisualizationNoteMode.Fm,
        IReadOnlyList<PitchChange>? pitch = null) =>
        new(channelId, start, end, 261.6, midi, instrumentId, mode, isRetrigger,
            pitch ?? Array.Empty<PitchChange>());

    [Fact]
    public void BassIsNotADrum_DespiteLowPitchAndFmNaming()
    {
        // Sustained envelope, slow attack, low repetition: a bass, whatever its
        // pitch or the "fm"/"bass" text says.
        InstrumentDefinition bass = Instrument("fm_bass_1", 0,
            Op(10, 12, 8, 12), Op(10, 12, 8, 12), Op(10, 12, 8, 12), Op(10, 12, 8, 12));
        NoteEvent note = Note("ym2608.0.fm.0", "fm_bass_1", 0, 22_050, isRetrigger: false, midi: 24);

        PercussionClassification result = FmPercussionClassifier.Classify(note, bass, SampleRate);

        Assert.False(result.IsPercussive);
        Assert.Equal(RhythmRole.Unknown, result.Role);
    }

    [Fact]
    public void PercussiveButUnknownRole_HighConfidence_NotCoerced()
    {
        // Fast attack, strong decay, repeated short attacks, no identity.
        InstrumentDefinition transient = Instrument("transient_4", 7,
            Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31));
        NoteEvent note = Note("ym2608.0.fm.4", "transient_4", 0, 1323, isRetrigger: true);

        PercussionClassification result = FmPercussionClassifier.Classify(note, transient, SampleRate);

        Assert.True(result.IsPercussive);
        Assert.Equal(RhythmRole.Unknown, result.Role);
        Assert.Equal(0.92, result.Confidence, precision: 6);
    }

    [Fact]
    public void Percussive_WithSharedKickVocabulary_KnownRoleHighConfidence()
    {
        InstrumentDefinition kick = Instrument("kick", 7,
            Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31));
        NoteEvent note = Note("ym2608.0.fm.1", "kick", 0, 1323, isRetrigger: true);

        PercussionClassification result = FmPercussionClassifier.Classify(note, kick, SampleRate);

        Assert.True(result.IsPercussive);
        Assert.Equal(RhythmRole.Bd, result.Role);
        Assert.True(result.Confidence >= 0.80, $"expected remap-gate confidence, got {result.Confidence}");
    }

    [Fact]
    public void SingleCarrierAlgorithm_EnvelopeComesFromCarrierOperator()
    {
        // Algorithms 0-3 have a single audible carrier (Op4). A snappy Op4 with
        // inert modulators must classify percussive (reuses the OPN carrier
        // helper; no OPN table duplication).
        InstrumentDefinition instrument = Instrument("single", 0,
            Op(0, 15, 0, 0), Op(0, 15, 0, 0), Op(0, 15, 0, 0), Op(31, 0, 31, 31));
        NoteEvent note = Note("ym2608.0.fm.2", "single", 0, 1323, isRetrigger: true);

        PercussionClassification result = FmPercussionClassifier.Classify(note, instrument, SampleRate);

        Assert.True(result.IsPercussive);
        Assert.Equal(RhythmRole.Unknown, result.Role);
    }

    [Fact]
    public void SustainedEnvelope_ShortGateAlone_IsNotPercussive()
    {
        InstrumentDefinition sustained = Instrument("pluck", 0,
            Op(31, 12, 8, 31), Op(31, 12, 8, 31), Op(31, 12, 8, 31), Op(31, 12, 8, 31));
        NoteEvent note = Note("ym2608.0.fm.5", "pluck", 0, 441, isRetrigger: false);

        PercussionClassification result = FmPercussionClassifier.Classify(note, sustained, SampleRate);

        // A pluck has a fast attack and fast release but sustains (SL=12): the
        // decay signal is absent, so envelope stays below the percussive bar.
        Assert.False(result.IsPercussive);
    }

    [Fact]
    public void DownwardPitchContour_IsSupportingSignal_NotAloneDecisive()
    {
        InstrumentDefinition mid = Instrument("sweep", 0,
            Op(31, 0, 31, 12), Op(31, 0, 31, 12), Op(31, 0, 31, 12), Op(31, 0, 31, 12));
        NoteEvent note = Note("ym2608.0.fm.6", "sweep", 0, 22_050, isRetrigger: false, midi: 50,
            pitch: [new PitchChange(441, 220.0, 46.0)]);

        PercussionClassification result = FmPercussionClassifier.Classify(note, mid, SampleRate);

        // Envelope = 0.7 (fast attack + strong decay, slow release), sustained
        // gate and no retrigger: the >= 4-semitone pitch drop provides the
        // remaining trigger signal.
        Assert.True(result.IsPercussive);
        Assert.Equal(RhythmRole.Unknown, result.Role);
    }

    [Fact]
    public void NonFmMode_IsNeverClassified()
    {
        InstrumentDefinition snappy = Instrument("kick", 7,
            Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31), Op(31, 0, 31, 31));
        NoteEvent note = Note("ay8910:0:ssg:3", "kick", 0, 1323, isRetrigger: true,
            mode: VisualizationNoteMode.SsgTone);

        PercussionClassification result = FmPercussionClassifier.Classify(note, snappy, SampleRate);

        Assert.False(result.IsPercussive);
        Assert.Equal(RhythmRole.Unknown, result.Role);
    }

    [Fact]
    public void NoInstrument_RepeatedShortNote_WeaklyPercussiveUnknownRole()
    {
        // Conservative signature fallback: repeated short attacks with no
        // envelope data are weakly percussive (below the 0.80 remap gate) and
        // never get a drum role.
        NoteEvent note = Note("ym2608.0.fm.7", "x", 0, 1323, isRetrigger: true);

        PercussionClassification result = FmPercussionClassifier.Classify(note, instrument: null, SampleRate);

        Assert.True(result.IsPercussive);
        Assert.Equal(RhythmRole.Unknown, result.Role);
        Assert.Equal(0.70, result.Confidence, precision: 6);
    }
}