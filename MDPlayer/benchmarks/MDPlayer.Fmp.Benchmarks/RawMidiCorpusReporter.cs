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
/// the resulting SMF. Every source NoteEvent is matched to its physical decoded
/// attack/release and effective pitch (including pitch bend and RPN range), not
/// merely counted. Native rhythm, fixed transport, metadata absence, collision
/// accounting and determinism remain separate checks. Expectations live ONLY in
/// this harness (spec DoD).
/// Receipts are written to the gitignored artifacts location AND printed
/// inline; nothing is committed.
/// </summary>
internal static class RawMidiCorpusReporter
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    /// <summary>The eight-song raw corpus: the five acceptance songs plus the
    /// three additional tracked fixtures.</summary>
    private static readonly string[] Corpus =
    [
        "05 - Twilight Express.vgz",
        "26 - Robotnik.vgz",
        "28 - Smoking Head.vgz",
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
        Console.WriteLine("song | notes | rhythm | collisions | oneTick | melodicOn | percOn | tickMismatch | pitchMismatch | determinism | pass");
        foreach (SongReceipt row in rows)
        {
            Console.WriteLine($"{row.SongName} | {row.SourceNotes} | {row.RhythmHits} | {row.Collisions} | "
                + $"{row.OneTickNotes} | {row.MelodicNoteOns} | {row.PercussionNoteOns} | "
                + $"{row.NoteTickMismatches} | {row.NotePitchMismatches} | "
                + $"{(row.Deterministic ? "yes" : "NO")} | {(row.Pass ? "PASS" : "FAIL")}");
        }

        var maxima = new
        {
            notes = rows.Max(r => r.SourceNotes),
            rhythm = rows.Max(r => r.RhythmHits),
            sameTickAttackCollisions = rows.Max(r => r.Collisions),
            oneTickNotes = rows.Max(r => r.OneTickNotes),
        };
        var strictMaxima = new
        {
            noteTickMismatches = rows.Max(r => r.NoteTickMismatches),
            notePitchMismatches = rows.Max(r => r.NotePitchMismatches),
        };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "mdplayer.raw-midi-corpus/v1",
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
                    schema = "mdplayer.raw-midi-corpus/v1",
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
        int Collisions,
        int OneTickNotes,
        int MelodicNoteOns,
        int PercussionNoteOns,
        int NoteTickMismatches,
        int NotePitchMismatches,
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

            var transcriber = new MidiTranscriber(Ppq);
            MidiTranscriptionResult first = transcriber.Transcribe(timeline);
            MidiTranscriptionResult second = transcriber.Transcribe(timeline);
            bool deterministic = first.Bytes.SequenceEqual(second.Bytes);
            DecodeResult decoded = DecodeOnly(first.Bytes);

            int sourceNotes = timeline.Notes?.Count ?? 0;
            int rhythmHits = timeline.Rhythm?.Count ?? 0;

            var checks = new List<object>();
            bool pass = true;
            void Check(string name, bool ok, object expected, object actual, string? detail = null)
            {
                pass &= ok;
                checks.Add(new { name, expected, actual, pass = ok, detail });
            }

            NoteFidelity fidelity = CompareTimeline(timeline, decoded.Notes);
            Check("timeline-note-fidelity",
                fidelity.Pass,
                $"{sourceNotes} notes with exact start/end ticks and decoded pitch",
                $"decoded={fidelity.DecodedNotes}, startTickMismatch={fidelity.StartTickMismatches}, "
                    + $"endTickMismatch={fidelity.EndTickMismatches}, pitchMismatch={fidelity.PitchMismatches}",
                "compares every VisualizationTimeline.NoteEvent against its decoded SMF attack/release and effective pitch");
            Check("transport-single-120bpm",
                decoded.TempoCount == 1 && decoded.TempoMicroseconds == 500_000,
                "one Set Tempo, 500000us, at tick 0",
                $"{decoded.TempoCount} tempo(s), {decoded.TempoMicroseconds}us",
                "raw transport is fixed at 120 BPM; no tempo map inference");
            Check("no-musical-metadata-events",
                decoded.TimeSignatures == 0 && decoded.Markers == 0,
                "0 time signatures, 0 markers",
                $"{decoded.TimeSignatures} signature(s), {decoded.Markers} marker(s)",
                "meter/downbeat/marker inference is not part of raw transcription");
            Check("source-notes-preserved",
                decoded.MelodicNoteOns == sourceNotes,
                sourceNotes, decoded.MelodicNoteOns,
                "every source note emits exactly one attack");
            Check("native-rhythm-preserved-on-channel-10",
                decoded.PercussionNoteOns == rhythmHits,
                rhythmHits, decoded.PercussionNoteOns,
                "every native rhythm hit emits one channel-10 attack");
            Check("same-tick-collisions-consistent",
                decoded.SameTickCollisions <= first.Diagnostics.SameTickAttackCollisions,
                first.Diagnostics.SameTickAttackCollisions, decoded.SameTickCollisions,
                "decode observed collisions must not exceed the transcriber's count");
            Check("one-tick-notes-consistent",
                decoded.OffAtOnPlusOne >= first.Diagnostics.OneTickNotes,
                first.Diagnostics.OneTickNotes, decoded.OffAtOnPlusOne,
                "forced one-tick notes are a subset of observed off=on+1 notes");
            Check("export-deterministic", deterministic, "byte-identical on second export",
                deterministic ? "identical" : "differed",
                "same input + options must produce identical bytes");

            var receipt = new
            {
                schema = "mdplayer.raw-midi-receipt/v1",
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
                    sourceEvents = sourceNotes + rhythmHits,
                    durationSeconds = (timeline.EndSample - timeline.StartSample) / (double)timeline.SampleRate,
                    fresh = true,
                },
                fidelity = new
                {
                    sourceNotes,
                    rhythmHits,
                    melodicNoteOns = decoded.MelodicNoteOns,
                    percussionNoteOns = decoded.PercussionNoteOns,
                    noteTickMismatches = fidelity.StartTickMismatches + fidelity.EndTickMismatches,
                    notePitchMismatches = fidelity.PitchMismatches,
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
            return new SongReceipt(songName, sourceNotes, rhythmHits,
                first.Diagnostics.SameTickAttackCollisions, first.Diagnostics.OneTickNotes,
                decoded.MelodicNoteOns, decoded.PercussionNoteOns,
                fidelity.StartTickMismatches + fidelity.EndTickMismatches,
                fidelity.PitchMismatches, deterministic, pass, receipt);
        }
        catch (Exception ex)
        {
            var receipt = new
            {
                schema = "mdplayer.raw-midi-receipt/v1",
                song = songName,
                error = $"pipeline crashed: {ex.Message}",
                pass = false,
            };
            return new SongReceipt(songName, 0, 0, 0, 0, 0, 0, 0, 0, false, false, receipt);
        }
    }

    private sealed record DecodedNote(
        int Track, int Port, int Channel, int Note, long StartTick, long EndTick,
        double StartPitch, double EndPitch);

    private sealed record DecodeAttack(
        int Track, int Port, int Channel, int Note, int Velocity,
        long StartTick, double StartPitch);

    private sealed record DecodeResult(
        int TempoCount,
        int TempoMicroseconds,
        int TimeSignatures,
        int Markers,
        int MelodicNoteOns,
        int PercussionNoteOns,
        int SameTickCollisions,
        int OffAtOnPlusOne,
        IReadOnlyList<DecodedNote> Notes);

    /// <summary>
    /// Independent SMF decode. Besides transport/count checks, it reconstructs
    /// every note using the actual track port, channel, pitch-bend state and RPN
    /// range state at the event's position.
    /// </summary>
    private static DecodeResult DecodeOnly(byte[] bytes)
    {
        Melanchall.DryWetMidi.Core.MidiFile file = MidiFile.Read(new MemoryStream(bytes),
            new ReadingSettings { EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store });

        int tempoCount = 0, tempoUs = 0, sigs = 0, markers = 0;
        int melodicOn = 0, percOn = 0, offAtOnPlusOne = 0, collisions = 0;
        var decodedNotes = new List<DecodedNote>();
        var open = new Dictionary<(int Port, int Channel, int Note), Queue<DecodeAttack>>();
        var activeBend = new Dictionary<(int Port, int Channel), int>();
        var activeRange = new Dictionary<(int Port, int Channel), double>();
        var rpn = new Dictionary<(int Port, int Channel), (int Msb, int Lsb)>();
        TrackChunk[] chunks = file.GetTrackChunks().ToArray();

        for (int trackIndex = 0; trackIndex < chunks.Length; trackIndex++)
        {
            TrackChunk chunk = chunks[trackIndex];
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
                        var attack = new DecodeAttack(
                            trackIndex, port, channel, note, (int)on.Velocity, tick,
                            note + BendOffset(pitchValue, range));
                        var key = (port, channel, note);
                        if (!open.TryGetValue(key, out Queue<DecodeAttack>? queue))
                        {
                            queue = new Queue<DecodeAttack>();
                            open[key] = queue;
                        }
                        queue.Enqueue(attack);
                        if (channel == 9)
                            percOn++;
                        else
                            melodicOn++;
                        if (channel != 9)
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
                            attack.Track, attack.Port, attack.Channel, attack.Note,
                            attack.StartTick, tick, attack.StartPitch, endPitch));
                        if (tick - attack.StartTick == 1)
                            offAtOnPlusOne++;
                        break;
                    }
                    case PitchBendEvent bend:
                        activeBend[(port, (int)bend.Channel)] = bend.PitchValue;
                        break;
                    case ControlChangeEvent cc:
                    {
                        var endpoint = (port, (int)cc.Channel);
                        int control = (int)cc.ControlNumber;
                        if (control is 101 or 100)
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

        return new(tempoCount, tempoUs, sigs, markers, melodicOn, percOn,
            collisions, offAtOnPlusOne, decodedNotes);
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
                    || Math.Abs(decodedNote.StartPitch - expectedStartPitch) > 0.025
                    || Math.Abs(decodedNote.EndPitch - expectedEndPitch) > 0.025)
                {
                    pitchMismatches++;
                }
            }

            pitchMismatches += Math.Abs(expected.Length - actual.Length);
        }

        return new(source.Count, decodedCount, startMismatches, endMismatches, pitchMismatches);
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
        ((pitchValue - 8192) / 8192.0) * rangeSemitones;

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