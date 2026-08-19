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
/// timeline cache — then <see cref="MidiTranscriber"/>) and reports decode-only
/// fidelity maxima measured purely from the exported SMF bytes (Set Tempo,
/// tick deltas and note events only; no musical model involved): source notes,
/// native-rhythm hits on channel 10, same-tick attack collisions and one-tick
/// notes. The raw transport contract — exactly one 500000us Set Tempo at tick
/// 0, no time signatures, no markers, no velocity/instrument projection — is
/// asserted per song. Expectations live ONLY in this harness (spec DoD).
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
        Console.WriteLine("song | notes | rhythm | collisions | oneTick | melodicOn | percOn | determinism | pass");
        foreach (SongReceipt row in rows)
        {
            Console.WriteLine($"{row.SongName} | {row.SourceNotes} | {row.RhythmHits} | {row.Collisions} | "
                + $"{row.OneTickNotes} | {row.MelodicNoteOns} | {row.PercussionNoteOns} | "
                + $"{(row.Deterministic ? "yes" : "NO")} | {(row.Pass ? "PASS" : "FAIL")}");
        }

        var maxima = new
        {
            notes = rows.Max(r => r.SourceNotes),
            rhythm = rows.Max(r => r.RhythmHits),
            sameTickAttackCollisions = rows.Max(r => r.Collisions),
            oneTickNotes = rows.Max(r => r.OneTickNotes),
        };
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "mdplayer.raw-midi-corpus/v1",
            mapper = "MidiTranscriber",
            total = rows.Count,
            passed = rows.Count(r => r.Pass),
            failed = rows.Count(r => !r.Pass),
            decodeOnlyFidelityMaxima = maxima,
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
                decoded.MelodicNoteOns, decoded.PercussionNoteOns, deterministic, pass, receipt);
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
            return new SongReceipt(songName, 0, 0, 0, 0, 0, 0, false, false, receipt);
        }
    }

    private sealed record DecodeResult(
        int TempoCount,
        int TempoMicroseconds,
        int TimeSignatures,
        int Markers,
        int MelodicNoteOns,
        int PercussionNoteOns,
        int SameTickCollisions,
        int OffAtOnPlusOne);

    /// <summary>Decode-only measurement: tempo, time signature, marker and note
    /// counts plus per-channel same-tick attack collisions, all from the SMF
    /// bytes. No musical model is involved.</summary>
    private static DecodeResult DecodeOnly(byte[] bytes)
    {
        Melanchall.DryWetMidi.Core.MidiFile file = MidiFile.Read(new MemoryStream(bytes),
            new ReadingSettings { EndOfTrackStoringPolicy = EndOfTrackStoringPolicy.Store });

        int tempoCount = 0, tempoUs = 0, sigs = 0, markers = 0, melodicOn = 0, percOn = 0, offAtOnPlusOne = 0, collisions = 0;
        foreach (TrackChunk chunk in file.GetTrackChunks())
        {
            var attacksPerTick = new Dictionary<(int Channel, long Tick), int>();
            var open = new List<(int Channel, long OnTick)>();
            long tick = 0;
            foreach (MidiEvent evt in chunk.Events)
            {
                tick += evt.DeltaTime;
                switch (evt)
                {
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
                        if ((int)on.Channel == 9)
                            percOn++;
                        else
                            melodicOn++;
                        var key = ((int)on.Channel, tick);
                        attacksPerTick[key] = attacksPerTick.TryGetValue(key, out int count) ? count + 1 : 1;
                        open.Add(((int)on.Channel, tick));
                        break;
                    case NoteOffEvent off:
                        for (int index = open.Count - 1; index >= 0; index--)
                        {
                            if (open[index].Channel == (int)off.Channel)
                            {
                                if (tick - open[index].OnTick == 1)
                                    offAtOnPlusOne++;
                                open.RemoveAt(index);
                                break;
                            }
                        }
                        break;
                }
            }
            collisions += attacksPerTick.Values.Count(count => count > 1);
        }
        return new(tempoCount, tempoUs, sigs, markers, melodicOn, percOn, collisions, offAtOnPlusOne);
    }

    private static string Sanitize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit));

    private static string FileHash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>Gitignored raw-midi receipt artifacts directory, sibling of the
    /// legacy musical receipts.</summary>
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