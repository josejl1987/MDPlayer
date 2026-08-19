using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Cli;
using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;

namespace Fmp.Benchmarks;

/// <summary>
/// Raw-fidelity MIDI corpus oracle. Runs the eight tracked songs through the
/// production pipeline (TimelineCaptureService capture — always fresh, never a
/// timeline cache — then <see cref="MidiTranscriber"/>) and independently decodes
/// the resulting SMF. It compares every source NoteEvent, RhythmEvent and
/// SamplePlaybackEvent for exact timing, physical identity and effective pitch;
/// it also validates every interior melodic pitch-state bend. Counts alone never
/// pass the oracle. Expectations live ONLY in this harness (spec DoD).
/// Receipts are written to the gitignored artifacts location AND printed
/// inline; nothing is committed.
/// </summary>
internal static class RawMidiCorpusReporter
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    /// <summary>The tracked raw corpus, including the OKIM6295 ownership
    /// regression fixture and the broader acceptance fixtures.</summary>
    private static readonly string[] Corpus =
    [
        "05 - Twilight Express.vgz",
        "26 - Robotnik.vgz",
        "28 - Smoking Head.vgz",
        "18 U.S.A. (Ken) I.vgz",
        "XA2020.OVI",
        "53 Triumphal Arch.vgz",
        "21 Master Ninja.vgz",
        "02 Stranger ~ Wandering Swordsman.vgz",
        "XA2021.OVI",
    ];

    public static int Run(string[] args)
    {
        string root = MidiFixtureResolver.FindRepositoryRoot(Environment.CurrentDirectory);
        string? requested = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal) ? args[1] : null;

        string[] selected = requested is null
            ? Corpus
            : Corpus.Where(name =>
                name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(requested).Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (selected.Length == 0)
        {
            Console.Error.WriteLine($"raw-corpus: fixture not found or unsupported: {requested}");
            return 2;
        }

        var rows = new List<SongReceipt>();
        bool allPass = true;
        foreach (string fixtureName in selected)
        {
            string fixture = Path.Combine(root, fixtureName);
            if (!File.Exists(fixture))
            {
                Console.Error.WriteLine($"raw-corpus: fixture not found: {fixtureName}");
                allPass = false;
                continue;
            }
            SongReceipt receipt = RunSong(root, fixture);
            rows.Add(receipt);
            allPass &= receipt.Pass;
        }

        Console.WriteLine("MDPlayer raw-fidelity MIDI corpus (fresh capture, MidiTranscriber)");
        Console.WriteLine("song | notes | rhythm | samples | collisions | oneTick | melodicOn | percOn | sampleOn | "
            + "noteTick | notePitch | rhythmTick | rhythmId | sampleTick | sampleId | samplePitch | trajectory | ownership | determinism | pass");
        foreach (SongReceipt row in rows)
        {
            Console.WriteLine($"{row.SongName} | {row.SourceNotes} | {row.RhythmHits} | {row.SamplePlaybackCount} | "
                + $"{row.Collisions} | {row.OneTickNotes} | {row.MelodicNoteOns} | {row.PercussionNoteOns} | "
                + $"{row.SampleNoteOns} | {row.NoteTickMismatches} | {row.NotePitchMismatches} | "
                + $"{row.RhythmTickMismatches} | {row.RhythmIdentityMismatches} | "
                + $"{row.SampleTickMismatches} | {row.SampleIdentityMismatches} | {row.SamplePitchMismatches} | "
                + $"{row.TrajectoryMismatches} | {row.OwnershipCollisions} | "
                + $"{(row.Deterministic ? "yes" : "NO")} | {(row.Pass ? "PASS" : "FAIL")}");
        }

        var maxima = new
        {
            notes = rows.Max(r => r.SourceNotes),
            rhythm = rows.Max(r => r.RhythmHits),
            samplePlayback = rows.Max(r => r.SamplePlaybackCount),
            sameTickAttackCollisions = rows.Max(r => r.Collisions),
            oneTickNotes = rows.Max(r => r.OneTickNotes),
        };
        var strictMaxima = new
        {
            noteTickMismatches = rows.Max(r => r.NoteTickMismatches),
            notePitchMismatches = rows.Max(r => r.NotePitchMismatches),
            rhythmTickMismatches = rows.Max(r => r.RhythmTickMismatches),
            rhythmIdentityMismatches = rows.Max(r => r.RhythmIdentityMismatches),
            sampleTickMismatches = rows.Max(r => r.SampleTickMismatches),
            sampleIdentityMismatches = rows.Max(r => r.SampleIdentityMismatches),
            samplePitchMismatches = rows.Max(r => r.SamplePitchMismatches),
            sampleSourceDuplicates = rows.Max(r => r.SampleSourceDuplicates),
            sampleDecodedDuplicates = rows.Max(r => r.SampleDecodedDuplicates),
            trajectoryMismatches = rows.Max(r => r.TrajectoryMismatches),
            ownershipCollisions = rows.Max(r => r.OwnershipCollisions),
        };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "mdplayer.raw-midi-corpus/v2",
            mapper = "MidiTranscriber",
            total = rows.Count,
            passed = rows.Count(r => r.Pass),
            failed = rows.Count(r => !r.Pass),
            decodeOnlyFidelityMaxima = maxima,
            timelineSmfFidelityMaxima = strictMaxima,
            songs = rows.Select(r => r.Receipt).ToArray(),
        }, new JsonSerializerOptions { WriteIndented = true }));

        string receiptsDir = RawMidiArtifactsDir(root);
        try
        {
            Directory.CreateDirectory(receiptsDir);
            foreach (SongReceipt row in rows)
                File.WriteAllText(Path.Combine(receiptsDir, Sanitize(row.SongName) + ".raw-midi.receipt.json"),
                    JsonSerializer.Serialize(row.Receipt, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(receiptsDir, "summary.json"),
                JsonSerializer.Serialize(new
                {
                    schema = "mdplayer.raw-midi-corpus/v2",
                    total = rows.Count,
                    passed = rows.Count(r => r.Pass),
                    generatedAtUtc = DateTime.UtcNow,
                }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine($"raw-corpus: receipts written to {receiptsDir} (gitignored, not committed)");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"raw-corpus: could not persist receipts to {receiptsDir}: {ex.Message}");
        }

        Console.Error.WriteLine(allPass
            ? "raw-corpus: ALL EXPECTATIONS PASS"
            : "raw-corpus: FAILURES PRESENT (see checks)");
        return allPass ? 0 : 1;
    }

    private sealed record SongReceipt(
        string SongName,
        int SourceNotes,
        int RhythmHits,
        int SamplePlaybackCount,
        int Collisions,
        int OneTickNotes,
        int MelodicNoteOns,
        int PercussionNoteOns,
        int SampleNoteOns,
        int NoteTickMismatches,
        int NotePitchMismatches,
        int RhythmTickMismatches,
        int RhythmIdentityMismatches,
        int SampleTickMismatches,
        int SampleIdentityMismatches,
        int SamplePitchMismatches,
        int SampleSourceDuplicates,
        int SampleDecodedDuplicates,
        int TrajectoryMismatches,
        int OwnershipCollisions,
        bool Deterministic,
        bool Pass,
        object Receipt);

    private static SongReceipt RunSong(string root, string fixture)
    {
        string songName = Path.GetFileName(fixture);
        try
        {
            var settings = new BatchRenderSettings
            {
                AssetsDir = root,
                Loops = 2,
                MaxDuration = 300,
                Timeout = 120,
                SampleRate = Sr,
            };
            settings.ValidateCommon();
            var inputInfo = new FileInfo(fixture);

            // Fresh capture every run: the raw oracle measures the pipeline, so a
            // cached timeline would hide capture regressions.
            VisualizationTimeline timeline = TimelineCaptureService.Capture(fixture, null, settings);
            MidiTranscriptionResult first = new MidiTranscriber(Ppq).Transcribe(timeline);
            MidiTranscriptionResult second = new MidiTranscriber(Ppq).Transcribe(timeline);
            bool deterministic = first.Bytes.SequenceEqual(second.Bytes);
            DecodeResult decoded = DecodeOnly(first.Bytes);

            int sourceNotes = timeline.Notes?.Count ?? 0;
            int rhythmHits = timeline.Rhythm?.Count ?? 0;
            int samplePlayback = AuthoritativeSamples(timeline).Length;

            var checks = new List<object>();
            bool pass = true;
            void Check(string name, bool ok, object expected, object actual, string? detail = null)
            {
                pass &= ok;
                checks.Add(new { name, expected, actual, pass = ok, detail });
            }

            NoteFidelity noteFidelity = CompareTimeline(timeline, decoded.Notes);
            RhythmFidelity rhythmFidelity = CompareRhythm(timeline, decoded.Notes);
            SampleFidelity sampleFidelity = CompareSamples(timeline, decoded.Notes);
            int trajectoryMismatches = ComparePitchTrajectory(timeline, decoded.Bends);
            int ownershipCollisions = CountOwnershipCollisions(timeline);
            int uniqueSourceAttacks = sourceNotes + rhythmHits + samplePlayback - ownershipCollisions;
            int decodedAttacks = decoded.MelodicNoteOns + decoded.PercussionNoteOns + decoded.SampleNoteOns;
            Check("timeline-note-fidelity", noteFidelity.Pass,
                $"{sourceNotes} notes with exact start/end ticks and decoded pitch",
                $"decoded={noteFidelity.DecodedNotes}, startTickMismatch={noteFidelity.StartTickMismatches}, "
                    + $"endTickMismatch={noteFidelity.EndTickMismatches}, pitchMismatch={noteFidelity.PitchMismatches}");
            Check("timeline-rhythm-fidelity", rhythmFidelity.Pass, rhythmHits,
                $"decoded={rhythmFidelity.Decoded}, tickMismatch={rhythmFidelity.TickMismatches}, "
                    + $"identityMismatch={rhythmFidelity.IdentityMismatches}");
            Check("timeline-sample-fidelity", sampleFidelity.Pass, samplePlayback,
                $"decoded={sampleFidelity.Decoded}, tickMismatch={sampleFidelity.TickMismatches}, "
                    + $"identityMismatch={sampleFidelity.IdentityMismatches}, pitchMismatch={sampleFidelity.PitchMismatches}");
            Check("melodic-pitch-trajectory", trajectoryMismatches == 0, 0, trajectoryMismatches);
            Check("sample-rhythm-ownership", ownershipCollisions == 0, 0, ownershipCollisions,
                "a PCM-owned source hit must not also be emitted as native rhythm");
            Check("unique-source-attacks", decodedAttacks == uniqueSourceAttacks,
                uniqueSourceAttacks, decodedAttacks,
                "parallel timeline views must not become duplicate MIDI attacks");
            Check("transport-single-120bpm",
                decoded.TempoCount == 1 && decoded.TempoMicroseconds == 500_000,
                "one Set Tempo, 500000us, at tick 0", $"{decoded.TempoCount} tempo(s), {decoded.TempoMicroseconds}us");
            Check("no-musical-metadata-events",
                decoded.TimeSignatures == 0 && decoded.Markers == 0,
                "0 time signatures, 0 markers",
                $"{decoded.TimeSignatures} signature(s), {decoded.Markers} marker(s)");
            Check("source-notes-preserved",
                decoded.MelodicNoteOns == sourceNotes, sourceNotes, decoded.MelodicNoteOns);
            Check("native-rhythm-preserved-on-channel-10",
                rhythmFidelity.Pass, rhythmHits, decoded.PercussionNoteOns);
            Check("sample-playback-preserved",
                sampleFidelity.Pass, samplePlayback, decoded.SampleNoteOns);
            Check("same-tick-collisions-consistent",
                decoded.SameTickCollisions <= first.Diagnostics.SameTickAttackCollisions,
                first.Diagnostics.SameTickAttackCollisions, decoded.SameTickCollisions);
            Check("one-tick-notes-consistent",
                decoded.OffAtOnPlusOne >= first.Diagnostics.OneTickNotes,
                first.Diagnostics.OneTickNotes, decoded.OffAtOnPlusOne);
            Check("export-deterministic", deterministic, "byte-identical on second export",
                deterministic ? "identical" : "differed");

            var receipt = new
            {
                schema = "mdplayer.raw-midi-receipt/v2",
                song = songName,
                input = new
                {
                    path = Path.GetRelativePath(root, fixture),
                    sha256 = FileHash(fixture),
                    sizeBytes = inputInfo.Length,
                },
                provenance = new
                {
                    gitSha = GitSha(root),
                    branch = GitBranch(root),
                },
                capture = new
                {
                    sourceEvents = sourceNotes + rhythmHits + samplePlayback,
                    durationSeconds = (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate,
                    fresh = true,
                },
                fidelity = new
                {
                    sourceNotes,
                    rhythmHits,
                    samplePlayback,
                    melodicNoteOns = decoded.MelodicNoteOns,
                    percussionNoteOns = decoded.PercussionNoteOns,
                    sampleNoteOns = decoded.SampleNoteOns,
                    sampleTickMismatches = sampleFidelity.TickMismatches,
                    sampleIdentityMismatches = sampleFidelity.IdentityMismatches,
                    samplePitchMismatches = sampleFidelity.PitchMismatches,
                    sampleSourceDuplicates = sampleFidelity.SourceDuplicates,
                    sampleDecodedDuplicates = sampleFidelity.DecodedDuplicates,
                    sampleIds = (timeline.SamplePlayback ?? Array.Empty<SamplePlaybackEvent>())
                        .Select(value => value.SampleId)
                        .Distinct(StringComparer.Ordinal)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToArray(),
                    trajectoryMismatches,
                    ownershipCollisions,
                    sameTickAttackCollisions = first.Diagnostics.SameTickAttackCollisions,
                    oneTickNotes = first.Diagnostics.OneTickNotes,
                    transportMicrosecondsPerQuarter = decoded.TempoMicroseconds,
                    outputSha256 = Convert.ToHexString(SHA256.HashData(first.Bytes)).ToLowerInvariant(),
                    outputBytes = first.Bytes.Length,
                    tracks = first.Tracks.Count,
                },
                checks,
                pass,
            };
            return new SongReceipt(songName, sourceNotes, rhythmHits, samplePlayback,
                first.Diagnostics.SameTickAttackCollisions, first.Diagnostics.OneTickNotes,
                decoded.MelodicNoteOns, decoded.PercussionNoteOns, decoded.SampleNoteOns,
                noteFidelity.StartTickMismatches + noteFidelity.EndTickMismatches,
                noteFidelity.PitchMismatches, rhythmFidelity.TickMismatches,
                rhythmFidelity.IdentityMismatches, sampleFidelity.TickMismatches,
                sampleFidelity.IdentityMismatches, sampleFidelity.PitchMismatches,
                sampleFidelity.SourceDuplicates, sampleFidelity.DecodedDuplicates,
                trajectoryMismatches, ownershipCollisions, deterministic, pass, receipt);
        }
        catch (Exception ex)
        {
            var receipt = new
            {
                schema = "mdplayer.raw-midi-receipt/v2",
                song = songName,
                error = $"pipeline crashed: {ex.Message}",
                pass = false,
            };
            return new SongReceipt(
                SongName: songName,
                SourceNotes: 0,
                RhythmHits: 0,
                SamplePlaybackCount: 0,
                Collisions: 0,
                OneTickNotes: 0,
                MelodicNoteOns: 0,
                PercussionNoteOns: 0,
                SampleNoteOns: 0,
                NoteTickMismatches: 0,
                NotePitchMismatches: 0,
                RhythmTickMismatches: 0,
                RhythmIdentityMismatches: 0,
                SampleTickMismatches: 0,
                SampleIdentityMismatches: 0,
                SamplePitchMismatches: 0,
                SampleSourceDuplicates: 0,
                SampleDecodedDuplicates: 0,
                TrajectoryMismatches: 0,
                OwnershipCollisions: 0,
                Deterministic: false,
                Pass: false,
                Receipt: receipt);
        }
    }

    private sealed record DecodedNote(
        int Track, int Port, int Channel, int Note, int Bank, bool IsSampleTrack,
        long StartTick, long EndTick, double StartPitch, double EndPitch);

    private sealed record DecodeAttack(
        int Track, int Port, int Channel, int Note, int Bank, bool IsSampleTrack,
        int Velocity, long StartTick, double StartPitch);

    private sealed record DecodedBend(
        int Track, int Port, int Channel, long Tick, int Value, double Range);

    private sealed record DecodeResult(
        int TempoCount,
        int TempoMicroseconds,
        int TimeSignatures,
        int Markers,
        int MelodicNoteOns,
        int PercussionNoteOns,
        int SampleNoteOns,
        int SameTickCollisions,
        int OffAtOnPlusOne,
        IReadOnlyList<DecodedNote> Notes,
        IReadOnlyList<DecodedBend> Bends);

    /// <summary>
    /// Independent SMF decode with endpoint bank/RPN state and track identity.
    /// </summary>
    private static DecodeResult DecodeOnly(byte[] bytes)
    {
        Melanchall.DryWetMidi.Core.MidiFile file = MidiFile.Read(new MemoryStream(bytes),
            new ReadingSettings { EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store });

        int tempoCount = 0, tempoUs = 0, sigs = 0, markers = 0;
        int melodicOn = 0, percOn = 0, sampleOn = 0, offAtOnPlusOne = 0, collisions = 0;
        var decodedNotes = new List<DecodedNote>();
        var decodedBends = new List<DecodedBend>();
        var open = new Dictionary<(int Port, int Channel, int Note), Queue<DecodeAttack>>();
        var activeBend = new Dictionary<(int Port, int Channel), int>();
        var activeRange = new Dictionary<(int Port, int Channel), double>();
        var banks = new Dictionary<(int Port, int Channel), int>();
        var rpn = new Dictionary<(int Port, int Channel), (int Msb, int Lsb)>();
        TrackChunk[] chunks = file.GetTrackChunks().ToArray();

        for (int trackIndex = 0; trackIndex < chunks.Length; trackIndex++)
        {
            TrackChunk chunk = chunks[trackIndex];
            string? trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text;
            bool isSampleTrack = trackName?.StartsWith("Sample ", StringComparison.Ordinal) == true;
            var attacksPerTick = new Dictionary<(int Channel, long Tick), int>();
            int port = 0;
            long tick = 0;
            foreach (MidiEvent evt in chunk.Events)
            {
                tick += evt.DeltaTime;
                switch (evt)
                {
                    case PortPrefixEvent prefix:
                        port = prefix.Port;
                        break;
                    case SetTempoEvent tempo:
                        tempoCount++;
                        if (tempoCount == 1)
                            tempoUs = (int)tempo.MicrosecondsPerQuarterNote;
                        break;
                    case TimeSignatureEvent:
                        sigs++;
                        break;
                    case MarkerEvent:
                        markers++;
                        break;
                    case NoteOnEvent on when (int)on.Velocity > 0:
                    {
                        int channel = (int)on.Channel;
                        int note = (int)on.NoteNumber;
                        var endpoint = (port, channel);
                        double range = activeRange.TryGetValue(endpoint, out double knownRange)
                            ? knownRange
                            : 2.0;
                        int pitchValue = activeBend.TryGetValue(endpoint, out int knownBend)
                            ? knownBend
                            : 8192;
                        int currentBank = banks.TryGetValue(endpoint, out int knownBank) ? knownBank : 0;
                        var attack = new DecodeAttack(
                            trackIndex, port, channel, note, currentBank, isSampleTrack,
                            (int)on.Velocity, tick, note + BendOffset(pitchValue, range));
                        var key = (port, channel, note);
                        if (!open.TryGetValue(key, out Queue<DecodeAttack>? queue))
                        {
                            queue = new Queue<DecodeAttack>();
                            open[key] = queue;
                        }
                        queue.Enqueue(attack);
                        if (isSampleTrack)
                            sampleOn++;
                        else if (channel == 9)
                            percOn++;
                        else
                            melodicOn++;
                        if (!isSampleTrack && channel != 9)
                        {
                            var collisionKey = (channel, tick);
                            attacksPerTick[collisionKey] =
                                attacksPerTick.TryGetValue(collisionKey, out int count) ? count + 1 : 1;
                        }
                        break;
                    }
                    case NoteOnEvent:
                    case NoteOffEvent:
                    {
                        int channel;
                        int note;
                        if (evt is NoteOnEvent zeroOn)
                        {
                            channel = (int)zeroOn.Channel;
                            note = (int)zeroOn.NoteNumber;
                        }
                        else
                        {
                            NoteOffEvent zeroOff = (NoteOffEvent)evt;
                            channel = (int)zeroOff.Channel;
                            note = (int)zeroOff.NoteNumber;
                        }
                        var key = (port, channel, note);
                        if (!open.TryGetValue(key, out Queue<DecodeAttack>? queue) || queue.Count == 0)
                            break;

                        DecodeAttack attack = queue.Dequeue();
                        var endpoint = (port, channel);
                        double range = activeRange.TryGetValue(endpoint, out double knownRange)
                            ? knownRange
                            : 2.0;
                        int pitchValue = activeBend.TryGetValue(endpoint, out int knownBend)
                            ? knownBend
                            : 8192;
                        double endPitch = note + BendOffset(pitchValue, range);
                        decodedNotes.Add(new DecodedNote(
                            attack.Track, attack.Port, attack.Channel, attack.Note, attack.Bank,
                            attack.IsSampleTrack, attack.StartTick, tick,
                            attack.StartPitch, endPitch));
                        if (tick - attack.StartTick == 1)
                            offAtOnPlusOne++;
                        break;
                    }
                    case PitchBendEvent bend:
                    {
                        var endpoint = (port, (int)bend.Channel);
                        activeBend[endpoint] = bend.PitchValue;
                        double range = activeRange.TryGetValue(endpoint, out double knownRange)
                            ? knownRange
                            : 2.0;
                        decodedBends.Add(new DecodedBend(
                            trackIndex, port, (int)bend.Channel, tick, bend.PitchValue, range));
                        break;
                    }
                    case ControlChangeEvent cc:
                    {
                        var endpoint = (port, (int)cc.Channel);
                        int control = (int)cc.ControlNumber;
                        if (control == 0)
                        {
                            banks[endpoint] = (int)cc.ControlValue;
                        }
                        else if (control is 101 or 100)
                        {
                            rpn.TryGetValue(endpoint, out var state);
                            rpn[endpoint] = control == 101
                                ? (cc.ControlValue, state.Lsb)
                                : (state.Msb, cc.ControlValue);
                        }
                        else if (control == 6
                            && rpn.TryGetValue(endpoint, out var state)
                            && state.Msb == 0
                            && state.Lsb == 0)
                        {
                            activeRange[endpoint] = Math.Max(1, (int)cc.ControlValue);
                        }
                        break;
                    }
                }
            }
            collisions += attacksPerTick.Values.Count(count => count > 1);
        }
        return new(tempoCount, tempoUs, sigs, markers, melodicOn, percOn, sampleOn,
            collisions, offAtOnPlusOne, decodedNotes, decodedBends);
    }

    private sealed record NoteFidelity(
        int ExpectedNotes,
        int DecodedNotes,
        int StartTickMismatches,
        int EndTickMismatches,
        int PitchMismatches)
    {
        public bool Pass =>
            ExpectedNotes == DecodedNotes
            && StartTickMismatches == 0
            && EndTickMismatches == 0
            && PitchMismatches == 0;
    }

    private static NoteFidelity CompareTimeline(
        VisualizationTimeline timeline,
        IReadOnlyList<DecodedNote> decoded)
    {
        IReadOnlyList<Fmp.Core.Visualization.NoteEvent> source =
            timeline.Notes ?? Array.Empty<Fmp.Core.Visualization.NoteEvent>();
        var voices = source
            .Select((note, index) => (note, index, voice: VoiceId(note)))
            .GroupBy(item => item.voice, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();

        int startMismatches = 0;
        int endMismatches = 0;
        int pitchMismatches = 0;
        int decodedCount = 0;
        for (int voiceIndex = 0; voiceIndex < voices.Length; voiceIndex++)
        {
            int port = voiceIndex / 15;
            int slot = voiceIndex % 15;
            int channel = slot < 9 ? slot : slot + 1;
            int track = voiceIndex + 1; // track 0 is the conductor.
            int bendRange = BendRange(voices[voiceIndex].Select(item => item.note).ToArray());
            double pitchTolerance = bendRange / 8191.0 + 0.001;
            var expected = voices[voiceIndex]
                .OrderBy(item => ToTick(timeline, item.note.StartSample))
                .ThenByDescending(item => ToTick(timeline, item.note.EndSample))
                .ThenBy(item => BaseNote(item.note))
                .ThenBy(item => item.index)
                .ToArray();
            var actual = decoded
                .Where(note => note.Track == track && note.Port == port && note.Channel == channel)
                .OrderBy(note => note.StartTick)
                .ThenByDescending(note => note.EndTick)
                .ThenBy(note => note.Note)
                .ToArray();
            decodedCount += actual.Length;

            int compared = Math.Min(expected.Length, actual.Length);
            for (int index = 0; index < compared; index++)
            {
                Fmp.Core.Visualization.NoteEvent sourceNote = expected[index].note;
                DecodedNote decodedNote = actual[index];
                long expectedStart = ToTick(timeline, sourceNote.StartSample);
                long expectedEnd = ToTick(timeline, sourceNote.EndSample);
                if (expectedEnd <= expectedStart)
                    expectedEnd = checked(expectedStart + 1);
                if (decodedNote.StartTick != expectedStart)
                    startMismatches++;
                if (decodedNote.EndTick != expectedEnd)
                    endMismatches++;

                double expectedStartPitch = SourcePitchAt(sourceNote, sourceNote.StartSample);
                double expectedEndPitch = SourcePitchAt(sourceNote, sourceNote.EndSample);
                if (decodedNote.Note != BaseNote(sourceNote)
                    || Math.Abs(decodedNote.StartPitch - expectedStartPitch) > pitchTolerance
                    || Math.Abs(decodedNote.EndPitch - expectedEndPitch) > pitchTolerance)
                {
                    pitchMismatches++;
                }
            }

            pitchMismatches += Math.Abs(expected.Length - actual.Length);
        }

        return new(source.Count, decodedCount, startMismatches, endMismatches, pitchMismatches);
    }

    private sealed record RhythmFidelity(int Decoded, int TickMismatches, int IdentityMismatches)
    {
        public bool Pass => Decoded >= 0 && TickMismatches == 0 && IdentityMismatches == 0;
    }

    private sealed record SampleFidelity(
        int Decoded,
        int TickMismatches,
        int IdentityMismatches,
        int PitchMismatches,
        int SourceDuplicates,
        int DecodedDuplicates)
    {
        public bool Pass =>
            Decoded >= 0
            && TickMismatches == 0
            && IdentityMismatches == 0
            && PitchMismatches == 0
            && SourceDuplicates == DecodedDuplicates;
    }

    private static RhythmFidelity CompareRhythm(
        VisualizationTimeline timeline, IReadOnlyList<DecodedNote> decoded)
    {
        RhythmEvent[] expected = (timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .Select((value, index) => (value, index))
            .OrderBy(item => ToTick(timeline, item.value.SamplePosition))
            .ThenBy(item => item.index)
            .Select(item => item.value)
            .ToArray();
        DecodedNote[] actual = decoded
            .Where(note => !note.IsSampleTrack && note.Channel == 9)
            .OrderBy(note => note.StartTick)
            .ThenBy(note => note.EndTick)
            .ToArray();
        int tickMismatches = 0;
        int identityMismatches = 0;
        int compared = Math.Min(expected.Length, actual.Length);
        for (int index = 0; index < compared; index++)
        {
            RhythmEvent hit = expected[index];
            DecodedNote note = actual[index];
            long tick = ToTick(timeline, hit.SamplePosition);
            if (note.StartTick != tick || note.EndTick != tick + 1)
                tickMismatches++;
            int expectedNote = GeneralMidiDrumMapper.TryMap(hit, out int mapped)
                ? mapped
                : 60;
            if (note.Note != expectedNote)
                identityMismatches++;
        }
        int missing = Math.Abs(expected.Length - actual.Length);
        return new(actual.Length, tickMismatches + missing, identityMismatches + missing);
    }

    private static SampleFidelity CompareSamples(
        VisualizationTimeline timeline, IReadOnlyList<DecodedNote> decoded)
    {
        SamplePlaybackEvent[] source = AuthoritativeSamples(timeline);
        string[] ids = source.Select(value => value.SampleId)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var assignments = ids
            .Select((id, ordinal) => (id, assignment: new DacNoteAssignment(ordinal / 128, ordinal % 128)))
            .ToDictionary(value => value.id, value => value.assignment, StringComparer.Ordinal);
        var voices = source
            .Select((value, index) => (value, index))
            .GroupBy(item => item.value.VoiceId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        int melodicVoices = (timeline.Notes ?? Array.Empty<Fmp.Core.Visualization.NoteEvent>())
            .GroupBy(item => VoiceId(item), StringComparer.Ordinal)
            .Count();
        int decodedCount = 0;
        int tickMismatches = 0;
        int identityMismatches = 0;
        int pitchMismatches = 0;
        for (int voiceIndex = 0; voiceIndex < voices.Length; voiceIndex++)
        {
            MidiEndpointData endpoint = Endpoint(melodicVoices + voiceIndex);
            int track = 1 + melodicVoices + voiceIndex;
            SamplePlaybackEvent[] expected = voices[voiceIndex]
                .OrderBy(item => ToTick(timeline, item.value.StartSample))
                .ThenByDescending(item => ToTick(timeline, item.value.EndSample))
                .ThenBy(item => item.index)
                .Select(item => item.value)
                .ToArray();
            DecodedNote[] actual = decoded
                .Where(note => note.IsSampleTrack && note.Track == track
                    && note.Port == endpoint.Port && note.Channel == endpoint.Channel)
                .OrderBy(note => note.StartTick)
                .ThenByDescending(note => note.EndTick)
                .ThenBy(note => note.Note)
                .ToArray();
            decodedCount += actual.Length;
            int range = SampleBendRange(expected, assignments);
            int compared = Math.Min(expected.Length, actual.Length);
            for (int index = 0; index < compared; index++)
            {
                SamplePlaybackEvent sample = expected[index];
                DecodedNote note = actual[index];
                long start = ToTick(timeline, sample.StartSample);
                long end = ToTick(timeline, sample.EndSample);
                if (end <= start) end = checked(start + 1);
                if (note.StartTick != start || note.EndTick != end)
                    tickMismatches++;
                DacNoteAssignment assignment = assignments[sample.SampleId];
                if (note.Bank != assignment.Bank || note.Note != assignment.Note)
                    identityMismatches++;
                if (sample.MidiPitch is double pitch)
                {
                    double tolerance = range > 0 ? range / 8191.0 + 0.001 : 0.001;
                    if (Math.Abs(note.StartPitch - pitch) > tolerance)
                        pitchMismatches++;
                }
            }
            int missing = Math.Abs(expected.Length - actual.Length);
            tickMismatches += missing;
            identityMismatches += missing;
        }
        int sourceDuplicates = source
            .GroupBy(value => (value.VoiceId, Tick: ToTick(timeline, value.StartSample), value.SampleId))
            .Sum(group => Math.Max(0, group.Count() - 1));
        int decodedDuplicates = decoded
            .Where(value => value.IsSampleTrack)
            .GroupBy(value => (value.Track, value.StartTick, value.Bank, value.Note))
            .Sum(group => Math.Max(0, group.Count() - 1));
        return new(decodedCount, tickMismatches, identityMismatches, pitchMismatches,
            sourceDuplicates, decodedDuplicates);
    }

    private static int SampleBendRange(
        IReadOnlyList<SamplePlaybackEvent> samples,
        IReadOnlyDictionary<string, DacNoteAssignment> assignments)
    {
        double maximum = 0;
        foreach (SamplePlaybackEvent sample in samples)
            if (sample.MidiPitch is double pitch)
                maximum = Math.Max(maximum, Math.Abs(pitch - assignments[sample.SampleId].Note));
        return maximum <= 0 ? 0 : Math.Clamp((int)Math.Ceiling(maximum), 2, 127);
    }

    private static int ComparePitchTrajectory(
        VisualizationTimeline timeline, IReadOnlyList<DecodedBend> decoded)
    {
        var voices = (timeline.Notes ?? Array.Empty<Fmp.Core.Visualization.NoteEvent>())
            .Select((note, index) => (note, index, voice: VoiceId(note)))
            .GroupBy(item => item.voice, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        int mismatches = 0;
        for (int voiceIndex = 0; voiceIndex < voices.Length; voiceIndex++)
        {
            int range = BendRange(voices[voiceIndex].Select(item => item.note).ToArray());
            var expected = new List<(long Tick, double Pitch, long Sample, int Source, int Order, int Base)>();
            foreach (var item in voices[voiceIndex])
            {
                int baseNote = BaseNote(item.note);
                expected.Add((ToTick(timeline, item.note.StartSample),
                    SourcePitchAt(item.note, item.note.StartSample),
                    item.note.StartSample, item.index, 0, baseNote));
                int order = 0;
                foreach (PitchChange change in PitchStates(item.note))
                {
                    if (change.SamplePosition <= item.note.StartSample
                        || change.SamplePosition >= item.note.EndSample)
                        continue;
                    expected.Add((ToTick(timeline, change.SamplePosition), change.MidiNote,
                        change.SamplePosition, item.index, ++order, baseNote));
                }
            }
            expected = expected.OrderBy(value => value.Tick)
                .ThenBy(value => value.Sample)
                .ThenBy(value => value.Source)
                .ThenBy(value => value.Order)
                .ToList();
            DecodedBend[] actual = decoded
                .Where(value => value.Track == voiceIndex + 1)
                .OrderBy(value => value.Tick)
                .ToArray();
            int compared = Math.Min(expected.Count, actual.Length);
            double tolerance = range / 8191.0 + 0.001;
            for (int index = 0; index < compared; index++)
            {
                DecodedBend bend = actual[index];
                var target = expected[index];
                if (bend.Tick != target.Tick
                    || Math.Abs(target.Base + BendOffset(bend.Value, bend.Range) - target.Pitch) > tolerance)
                {
                    mismatches++;
                }
            }
            mismatches += Math.Abs(expected.Count - actual.Length);
        }
        return mismatches;
    }

    private static IEnumerable<PitchChange> PitchStates(Fmp.Core.Visualization.NoteEvent note)
    {
        IReadOnlyList<PitchChange> changes = note.Pitch ?? Array.Empty<PitchChange>();
        for (int index = 0; index < changes.Count; index++)
        {
            if (index > 0 && changes[index].SamplePosition < changes[index - 1].SamplePosition)
                throw new InvalidOperationException("Pitch changes are not in source order.");
            if (index + 1 < changes.Count
                && changes[index + 1].SamplePosition == changes[index].SamplePosition)
                continue;
            yield return changes[index];
        }
    }

    private static int BendRange(IReadOnlyList<Fmp.Core.Visualization.NoteEvent> notes)
    {
        double maximum = 0;
        foreach (Fmp.Core.Visualization.NoteEvent note in notes)
        {
            double initial = SourcePitchAt(note, note.StartSample);
            int baseNote = (int)Math.Round(initial, MidpointRounding.AwayFromZero);
            maximum = Math.Max(maximum, Math.Abs(initial - baseNote));
            foreach (PitchChange change in PitchStates(note))
                if (change.SamplePosition > note.StartSample && change.SamplePosition < note.EndSample)
                    maximum = Math.Max(maximum, Math.Abs(change.MidiNote - baseNote));
        }
        return maximum <= 0 ? 2 : Math.Clamp((int)Math.Ceiling(maximum), 2, 127);
    }

    private readonly record struct MidiEndpointData(int Port, int Channel);

    private static MidiEndpointData Endpoint(int voiceIndex)
    {
        int slot = voiceIndex % 15;
        return new(voiceIndex / 15, slot < 9 ? slot : slot + 1);
    }

    private static SamplePlaybackEvent[] AuthoritativeSamples(VisualizationTimeline timeline) =>
        (timeline.SamplePlayback ?? Array.Empty<SamplePlaybackEvent>())
            .Where(value => string.Equals(value.VoiceId, "ym2612.0.pcm.dac", StringComparison.Ordinal))
            .ToArray();

    private static int CountOwnershipCollisions(VisualizationTimeline timeline)
    {
        var sampleOwned = AuthoritativeSamples(timeline)
            .Select(value => (value.VoiceId, value.StartSample))
            .ToHashSet();
        return (timeline.Rhythm ?? Array.Empty<RhythmEvent>())
            .Count(value => sampleOwned.Contains((value.ChannelId, value.SamplePosition)));
    }

    private static string VoiceId(Fmp.Core.Visualization.NoteEvent note)
    {
        if (note.Domain is SourceDomainKey domain)
            return "domain:" + domain;
        if (!string.IsNullOrWhiteSpace(note.ChannelId))
            return "channel:" + note.ChannelId;
        throw new InvalidOperationException("Source note has no physical voice identity.");
    }

    private static long ToTick(VisualizationTimeline timeline, long sample)
    {
        long delta = checked(sample - timeline.StartSample);
        if (delta < 0)
            throw new InvalidOperationException("Source sample precedes timeline start.");
        decimal ticks = (decimal)delta * (2m * Ppq) / timeline.SampleRate;
        return decimal.ToInt64(decimal.Round(ticks, 0, MidpointRounding.AwayFromZero));
    }

    private static int BaseNote(Fmp.Core.Visualization.NoteEvent note) =>
        (int)Math.Clamp(
            checked((long)Math.Round(SourcePitchAt(note, note.StartSample), MidpointRounding.AwayFromZero)),
            0L, 127L);

    private static double SourcePitchAt(Fmp.Core.Visualization.NoteEvent note, long sample)
    {
        double pitch = note.InitialMidiNote;
        foreach (PitchChange change in note.Pitch ?? Array.Empty<PitchChange>())
        {
            if (change.SamplePosition > sample)
                break;
            if (change.SamplePosition == note.EndSample && sample == note.EndSample)
                continue;
            pitch = change.MidiNote;
        }
        return pitch;
    }

    private static double BendOffset(int pitchValue, double rangeSemitones) =>
        pitchValue < 8192
            ? ((pitchValue - 8192) / 8192.0) * rangeSemitones
            : ((pitchValue - 8192) / 8191.0) * rangeSemitones;

    private static string Sanitize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit));

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>Gitignored raw-MIDI receipt artifacts directory, sibling of other
    /// receipt artifacts.</summary>
    private static string RawMidiArtifactsDir(string root)
    {
        string[] candidates =
        [
            Path.Combine(root, "MDPlayer", "tests", "Corpus", "artifacts", "linux-baseline", "raw-midi-receipts"),
            Path.Combine(root, "tests", "Corpus", "artifacts", "linux-baseline", "raw-midi-receipts"),
        ];
        foreach (string candidate in candidates)
        {
            if (Directory.Exists(candidate) || candidate == candidates[0])
                return candidate;
        }
        return candidates[0];
    }

    private static string GitSha(string root)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("HEAD");
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }

    private static string GitBranch(string root)
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root,
            };
            startInfo.ArgumentList.Add("branch");
            startInfo.ArgumentList.Add("--show-current");
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 && output.Length > 0 ? output : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}