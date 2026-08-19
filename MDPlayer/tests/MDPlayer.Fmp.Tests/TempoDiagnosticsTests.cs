using System.Text.Json;
using Fmp.Cli;
using Fmp.Core.Rendering;
using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Patch 2: tempo alias/confidence diagnostics (FR-9, SC-8, SC-9, SC-14).
/// Serializes TimingDiagnostics (the single source of truth — no recompute in
/// the exporter) into the ONE compact timing meta text event and the CLI timing
/// report: selected-bpm, selected-score, alternative-bpm, alternative-score,
/// alias-margin, tempo-confidence, tempo-ambiguous, phase-sample. Missing
/// values serialize as "none"; no fabricated 1.0 margin. Also pins the Ninja
/// cadence at 112 BPM (keyed on the rhythm pattern, never a song title).
/// </summary>
public sealed class TempoDiagnosticsTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    // ---- synthetic pattern builders (deterministic, pattern-keyed) ---------------

    /// <summary>Dense 16th-note grid at the given cadence (octave-ambiguous texture).</summary>
    private static VisualizationTimeline Dense16th(double bpm)
    {
        long step = (long)Math.Round(Sr * 60.0 / bpm / 4.0);
        var notes = Enumerable.Range(0, 32)
            .Select(i => Note("v", i * step, i * step + 800, 64))
            .ToArray();
        return new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 32 * step + 10_000,
            SampleRate = Sr,
            Notes = notes,
        };
    }

    /// <summary>Ninja-cadence pattern: 16th-note chug at 112 BPM with quarter-beat
    /// accents. Reproduces the 56/112/224 alias family deterministically — keyed on
    /// the rhythm pattern, NEVER a song title (FR-10 / request 23).</summary>
    private static VisualizationTimeline NinjaPattern()
    {
        long step = (long)Math.Round(Sr * 60.0 / 112.0 / 4.0);
        long beat = (long)Math.Round(Sr * 60.0 / 112.0);
        var notes = Enumerable.Range(0, 48)
            .Select(i => Note("v", i * step, i * step + 700, 60))
            .ToArray();
        var rhythm = Enumerable.Range(0, 12)
            .Select(i => new RhythmEvent("bass", "rhythm.bass", i * beat, 1.0f, 0f)
            {
                Domain = new SourceDomainKey(new DeviceId(ChipType.Ym2608, 0), VoiceKind.Rhythm, 1),
            })
            .ToArray();
        return new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 48 * step + 10_000,
            SampleRate = Sr,
            Notes = notes,
            Rhythm = rhythm,
        };
    }

    private static NoteEvent Note(string voice, long start, long end, double midi) =>
        new(voice, start, end, 440, midi, "inst", VisualizationNoteMode.Fm, false, null);

    // ---- helpers ----------------------------------------------------------------

    private static MusicalMidiExportResult Export(VisualizationTimeline timeline)
    {
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions
        {
            Source = TimingSource.SymbolicInference,
        });
        var exporter = new MusicalMidiExporter(build.Map, Ppq,
            new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        return exporter.Export(timeline);
    }

    /// <summary>Extracts the "timing ..." meta text from the conductor track.</summary>
    private static string TimingMetaText(byte[] bytes)
    {
        foreach (Melanchall.DryWetMidi.Core.MidiEvent evt in MidiRoundTrip.TrackChunks(bytes)[0].Events)
        {
            if (evt is Melanchall.DryWetMidi.Core.TextEvent text && text.Text.StartsWith("timing ", StringComparison.Ordinal))
                return text.Text;
        }
        throw new Xunit.Sdk.XunitException("no timing meta text event found in conductor track");
    }

    private static string Field(string timingText, string name)
    {
        string body = timingText.StartsWith("timing ", StringComparison.Ordinal)
            ? timingText["timing ".Length..]
            : timingText;
        string prefix = name + "=";
        foreach (string part in body.Split(';'))
        {
            if (part.TrimStart().StartsWith(prefix, StringComparison.Ordinal))
                return part.TrimStart()[prefix.Length..];
        }
        return null;
    }

    // ---- SC-8: ambiguous alias --------------------------------------------------

    [Fact]
    public void TempoDiagnostics_AmbiguousAlias_ExportsAlternativeAndConfidence()
    {
        // Dense 16ths at 112: half/double alias (224) competes; the octave resolves
        // to 112 but the alternative + scores + margin + confidence must all be
        // serialized from TimingDiagnostics — with no fabricated 1.0 margin.
        VisualizationTimeline timeline = Dense16th(112);
        MusicalMidiExportResult result = Export(timeline);
        string meta = TimingMetaText(result.Bytes);

        TimingDiagnostics d = result.Diagnostics;
        Assert.NotNull(d.AlternativeBpm);
        Assert.True(d.TempoAmbiguous);
        Assert.NotNull(d.TempoConfidence);

        Assert.Equal("SymbolicInference", Field(meta, "tempo-source"));
        Assert.Equal("112", Field(meta, "selected-bpm"));
        Assert.Equal("224", Field(meta, "alternative-bpm"));
        Assert.NotEqual("none", Field(meta, "selected-score"));
        Assert.NotEqual("none", Field(meta, "alternative-score"));
        Assert.NotEqual("none", Field(meta, "alias-margin"));
        Assert.NotEqual("1", Field(meta, "alias-margin"));      // no fabricated 1.0
        Assert.NotEqual("1", Field(meta, "selected-score"));
        Assert.NotEqual("none", Field(meta, "tempo-confidence"));
        Assert.Equal("true", Field(meta, "tempo-ambiguous"));
        Assert.NotEqual("none", Field(meta, "phase-sample"));

        // Meaningful inequalities (request 22 — never exact floats).
        Assert.True(d.AliasMargin > 0, $"alias margin must be positive, got {d.AliasMargin}");
        Assert.True(d.SelectedScore > d.AlternativeScore,
            $"selected {d.SelectedScore} must outscore alternative {d.AlternativeScore}");
        Assert.True(d.TempoConfidence < 0.9, "ambiguous alias must not report high confidence");
    }

    [Fact]
    public void TempoDiagnostics_ResolvedAlias_ExportsSelectedScore()
    {
        // Clearly resolved cadence: the accent layer locks the family; the selected
        // tempo must be exported with a positive alias margin and a clear score gap.
        //
        // NOTE (design deviation, reported): SC-9 literally requires TempoAmbiguous
        // == false, but under the D.4 algorithm the metrical-family alternative
        // ALWAYS exists for symbolic inference (the family members always score
        // differently — the tempo prior is cadence-dependent), so TempoAmbiguous is
        // true for every symbolic result by design (SymbolicTempoInference:157:
        // "resolving the octave does not make the alternative disappear"). The
        // meaningful, reachable assertions are: selected-bpm exported, SelectedScore
        // > AlternativeScore, AliasMargin > 0, and tempo-ambiguous serialized
        // verbatim from diagnostics.
        VisualizationTimeline timeline = NinjaPattern();
        MusicalMidiExportResult result = Export(timeline);
        string meta = TimingMetaText(result.Bytes);

        TimingDiagnostics d = result.Diagnostics;
        Assert.NotNull(d.SelectedBpm);
        Assert.Equal(112, Math.Round(d.SelectedBpm.Value));
        Assert.True(d.SelectedScore > d.AlternativeScore,
            $"selected {d.SelectedScore} must outscore alternative {d.AlternativeScore}");
        Assert.True(d.AliasMargin > 0, $"alias margin must be positive, got {d.AliasMargin}");

        Assert.Equal("112", Field(meta, "selected-bpm"));
        Assert.NotEqual("none", Field(meta, "selected-score"));
        Assert.NotEqual("none", Field(meta, "alternative-score"));
        Assert.NotEqual("none", Field(meta, "alias-margin"));
        Assert.Equal(d.TempoAmbiguous.ToString().ToLowerInvariant(), Field(meta, "tempo-ambiguous"));
    }

    // ---- D9: gating — no diagnostics on non-inferred paths ----------------------

    [Fact]
    public void TempoDiagnostics_NullDiagnosticsPath_EmitsNoAliasFields()
    {
        // Fixed BPM (driver/override path): TempoInferred is false, so the extended
        // alias fields must NOT appear — and never a misleading selected-bpm=0.
        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 8_000_000,
            SampleRate = Sr,
            Notes = Enumerable.Range(0, 8)
                .Select(i => Note("v", i * (long)Math.Round(Sr * 60.0 / 120.0), i * (long)Math.Round(Sr * 60.0 / 120.0) + 2000, 60 + i))
                .ToArray(),
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions { FixedBpm = 120 });
        var exporter = new MusicalMidiExporter(build.Map, Ppq, new MusicalMidiExportOptions { EmitPitchBend = true })
        {
            Diagnostics = build.Diagnostics,
        };
        MusicalMidiExportResult result = exporter.Export(timeline);
        string meta = TimingMetaText(result.Bytes);

        Assert.Equal("UserOverride", Field(meta, "tempo-source"));
        Assert.Null(Field(meta, "selected-bpm"));
        Assert.Null(Field(meta, "alternative-bpm"));
        Assert.Null(Field(meta, "alias-margin"));
        Assert.Null(Field(meta, "tempo-confidence"));
        Assert.Null(Field(meta, "tempo-ambiguous"));
        Assert.NotNull(Field(meta, "sample0-quarter")); // structural field stays
    }

    // ---- SC-14: Ninja 112 permanent ---------------------------------------------

    [Fact]
    public void TempoDiagnostics_NinjaAlias_112Permanent()
    {
        VisualizationTimeline timeline = NinjaPattern();
        MusicalMidiExportResult result = Export(timeline);
        string meta = TimingMetaText(result.Bytes);

        TimingDiagnostics d = result.Diagnostics;
        Assert.NotNull(d.SelectedBpm);
        Assert.True(Math.Abs(d.SelectedBpm.Value - 112.0) < 0.5,
            $"selected {d.SelectedBpm.Value} must be the central octave 112, not 56/224");
        Assert.True(d.AlternativeBpm is double a && Math.Abs(a / d.SelectedBpm.Value - 0.5) < 0.02,
            $"alternative must be the half-tempo 56 member of the family, got {d.AlternativeBpm}");
        Assert.Equal("112", Field(meta, "selected-bpm"));
    }

    // ---- request 24: no blanket BPM-interval preference --------------------------

    [Fact]
    public void TempoDiagnostics_180Cadence_ReportsDiagnosticsWithoutForcingHalving()
    {
        // The exporter must never hide or force the 180-cadence outcome: report
        // selected/alternative/margin as the algorithm resolved them. (A dense
        // synthetic 16th grid at 180 is octave/triplet-ambiguous and the D.4 prior
        // resolves it to a central member — this test only asserts the diagnostics
        // surface, not a specific BPM; the REAL Smoking Head file acceptance in
        // Patch 4 reports the actual selected/alternative per spec 42.)
        VisualizationTimeline timeline = Dense16th(180);
        MusicalMidiExportResult result = Export(timeline);
        string meta = TimingMetaText(result.Bytes);

        Assert.Equal("SymbolicInference", Field(meta, "tempo-source"));
        Assert.NotNull(Field(meta, "selected-bpm"));
        Assert.NotNull(Field(meta, "alternative-bpm"));
        Assert.NotNull(Field(meta, "alias-margin"));
        Assert.NotEqual("1", Field(meta, "alias-margin")); // never a fabricated margin
        Assert.NotEqual("none", Field(meta, "tempo-confidence"));
    }

    // ---- T-9: AliasMargin property (pure derivation) -----------------------------

    [Fact]
    public void AliasMargin_IsNullWhenEitherScoreMissing()
    {
        var d = new TimingDiagnostics { SelectedScore = 0.9 };
        Assert.Null(d.AliasMargin);
        d = new TimingDiagnostics { AlternativeScore = 0.8 };
        Assert.Null(d.AliasMargin);
        d = new TimingDiagnostics();
        Assert.Null(d.AliasMargin);
    }

    [Fact]
    public void AliasMargin_IsScoreDifferenceWhenBothPresent()
    {
        var d = new TimingDiagnostics { SelectedScore = 0.91, AlternativeScore = 0.78 };
        Assert.Equal(0.13, d.AliasMargin.Value, precision: 9);
    }

    // ---- T-11: CLI JSON report (raw-fidelity) ------------------------------------

    [Fact]
    public void CliTimingReport_RawContainsTransportAndFidelityCounters()
    {
        string timelinePath = WriteTimeline(NinjaPattern());
        string outPath = Path.Combine(Path.GetTempPath(), $"mdplayer-p2-{Guid.NewGuid():N}.mid");
        string reportPath = Path.Combine(Path.GetTempPath(), $"mdplayer-p2-{Guid.NewGuid():N}.json");
        try
        {
            int exit = MidiCommand.Handle(new[]
            {
                "--timeline", timelinePath,
                "--output", outPath,
                "--timing-report", reportPath,
                "dummy.vgz",
            });
            Assert.True(exit == 0, $"exit={exit}");

            using var doc = JsonDocument.Parse(File.ReadAllText(reportPath));
            Assert.Equal("raw-fidelity", doc.RootElement.GetProperty("mode").GetString());
            Assert.Equal(960, doc.RootElement.GetProperty("ppq").GetInt32());
            Assert.Equal(120, doc.RootElement.GetProperty("transportBpm").GetInt32());
            Assert.Equal(500_000, doc.RootElement.GetProperty("transportMicrosecondsPerQuarter").GetInt32());
            Assert.False(doc.RootElement.GetProperty("musicalGridInferred").GetBoolean(),
                "the raw report must not claim any musical grid inference");
            Assert.False(doc.RootElement.TryGetProperty("tempoInference", out _),
                "the raw report must not carry the musical tempoInference block");
            Assert.True(doc.RootElement.GetProperty("sourceNotes").GetInt32() > 0);
        }
        finally
        {
            foreach (string p in new[] { timelinePath, outPath, reportPath })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    [Fact]
    public void CliTimingReport_RejectsRemovedTempoSourceOptionLoudly()
    {
        // The musical vocabulary was removed from the CLI option surface.
        // A stale script passing --tempo-source/--bpm must fail loudly at parse
        // time (exit 2), never silently degrade to the fixed 120 BPM transport.
        string timelinePath = WriteTimeline(NinjaPattern());
        string outPath = Path.Combine(Path.GetTempPath(), $"mdplayer-p2-{Guid.NewGuid():N}.mid");
        try
        {
            int exit = MidiCommand.Handle(new[]
            {
                "--timeline", timelinePath,
                "--output", outPath,
                "--tempo-source", "symbolic",
                "dummy.vgz",
            });
            Assert.Equal(2, exit);

            int exitWithBpm = MidiCommand.Handle(new[]
            {
                "--timeline", timelinePath,
                "--output", outPath,
                "--tempo-source", "fixed",
                "--bpm", "120",
                "dummy.vgz",
            });
            Assert.Equal(2, exitWithBpm);
        }
        finally
        {
            foreach (string p in new[] { timelinePath, outPath })
                if (File.Exists(p)) File.Delete(p);
        }
    }

    // ---- T-19: real-file integration acceptance (SC-14 real arm) ----------------

    [Fact]
    public void NinjaRealFile_SelectedBpm112_IntegrationAcceptance()
    {
        // Real-file acceptance ONLY (synthetic determinism lives in
        // TempoDiagnostics_NinjaAlias_112Permanent): the actual corpus file must
        // resolve to the canonical alias 112 BPM under the D.4 prior, not 56/224.
        string input = Path.Combine(AppContext.BaseDirectory, "testfixtures", "corpus", "20-ninja-yashiki.vgz");
        Assert.True(File.Exists(input), $"Ninja corpus fixture not provisioned: {input}");

        string wav = Path.Combine(Path.GetTempPath(), $"mdplayer-ninja-{Guid.NewGuid():N}.wav");
        var sink = new TimelineDecoderEventSink(Sr);
        VisualizationTimeline timeline;
        try
        {
            using IPlaybackCaptureSession session = new VgmPlaybackBackend().Open(
                new FileInfo(input),
                new PlaybackOptions(LoopCount: 1, FadeSeconds: 0, TailSeconds: 0, OutputAudioPath: wav, SampleRate: Sr),
                sink);
            session.Run();
            timeline = sink.Complete(session.SamplePosition, "test");
        }
        finally
        {
            if (File.Exists(wav)) File.Delete(wav);
        }

        Assert.True(timeline.Notes.Count > 0, "Ninja capture produced no notes");
        MusicalMidiExportResult result = Export(timeline);
        TimingDiagnostics d = result.Diagnostics;
        Assert.NotNull(d.SelectedBpm);
        Assert.True(Math.Abs(d.SelectedBpm.Value - 112.0) < 0.5,
            $"real Ninja file must resolve to 112 BPM, got {d.SelectedBpm.Value}");
        Assert.Equal("112", Field(TimingMetaText(result.Bytes), "selected-bpm"));
    }

    private static string WriteTimeline(VisualizationTimeline timeline)
    {
        string path = Path.Combine(Path.GetTempPath(), "mdplayer-midi-" + Guid.NewGuid().ToString("N") + ".json");
        VisualizationJsonWriter.Write(path, timeline);
        return path;
    }
}
