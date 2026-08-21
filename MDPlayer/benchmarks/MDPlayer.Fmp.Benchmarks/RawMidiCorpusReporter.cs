using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fmp.Cli;
using Fmp.Core.Midi;
using Fmp.Core.Visualization;
using System.Buffers.Binary;

namespace Fmp.Benchmarks;

/// <summary>
/// Raw-fidelity MIDI corpus oracle. Runs the ten real source fixtures through
/// the production pipeline (TimelineCaptureService capture — always fresh, never
/// a timeline cache — then <see cref="MidiTranscriber"/>) and independently
/// decodes the serialized SMF bytes. The decoder is intentionally local and
/// byte-oriented and does not use production writer or parser helpers.
/// Receipts compare explicit source attack ownership with decoded audible
/// attacks, exact endpoint/tick/pitch/trajectory/identity fidelity, metadata
/// conformance, physical overlaps, and byte determinism. Receipts are written
/// to the gitignored artifacts location AND printed inline; nothing is committed.
///
/// </summary>
internal static class RawMidiCorpusReporter
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    /// <summary>The ten real raw corpus inputs already present in the repository.
    /// This list contains source paths only; it never adds or commits copyrighted
    /// fixture files.</summary>
    private static readonly string[] Corpus =
    [
        "02 Stranger ~ Wandering Swordsman.vgz",
        "05 - Twilight Express.vgz",
        "10 First Attack.vgz",
        "18 U.S.A. (Ken) I.vgz",
        "20 Ninja Yashiki ~ Their Secrets Die With Them.vgz",
        "21 Master Ninja.vgz",
        "26 - Robotnik.vgz",
        "28 - Smoking Head.vgz",
        "32 Arctic Wind.vgz",
        "53 Triumphal Arch.vgz",
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

        Console.WriteLine("MDPlayer raw-fidelity MIDI corpus v3 (fresh capture, local SMF decoder)");
        Console.WriteLine("song | noteViews/sourceNoteAttacks | rhythmViews/sourceRhythmAttacks | "
            + "samplePlaybackViews/sourceSampleOnlyAttacks | uniqueSourceAttacks | "
            + "melodic/rhythm/sample | duplicate/missing | mismatches | metadata | deterministic | pass");
        foreach (SongReceipt row in rows)
        {
            Console.WriteLine($"{row.SongName} | {row.NoteViews}/{row.SourceNoteAttacks} | "
                + $"{row.RhythmViews}/{row.SourceRhythmAttacks} | "
                + $"{row.SamplePlaybackViews}/{row.SourceSampleOnlyAttacks} | "
                + $"{row.UniqueSourceAttacks} | {row.MidiMelodicAttacks}/{row.MidiRhythmAttacks}/{row.MidiSampleAttacks} | "
                + $"{row.DuplicateAudibleAttacks}/{row.MissingAudibleAttacks} | "
                + $"{row.TotalMismatches} | {row.ForbiddenMetadataCount} | "
                + $"{(row.Deterministic ? "yes" : "NO")} | {(row.Pass ? "PASS" : "FAIL")}");
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "mdplayer.raw-midi-corpus/v3",
            mapper = "MidiTranscriber",
            decoder = "local-serialized-smf",
            total = rows.Count,
            passed = rows.Count(r => r.Pass),
            failed = rows.Count(r => !r.Pass),
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
                    schema = "mdplayer.raw-midi-corpus/v3",
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
        int NoteViews,
        int SourceNoteAttacks,
        int RhythmViews,
        int SourceRhythmAttacks,
        int SamplePlaybackViews,
        int SourceSampleOnlyAttacks,
        int UniqueSourceAttacks,
        int MidiMelodicAttacks,
        int MidiRhythmAttacks,
        int MidiSampleAttacks,
        int DuplicateAudibleAttacks,
        int MissingAudibleAttacks,
        int NoteStartTickMismatches,
        int NoteEndTickMismatches,
        int NotePitchMismatches,
        int PitchTrajectoryMismatches,
        int RhythmTickMismatches,
        int RhythmIdentityMismatches,
        int SampleTickMismatches,
        int SampleIdentityMismatches,
        int SamplePitchMismatches,
        int PhysicalVoiceOverlapCount,
        int TempoEventCount,
        long[] TempoEventTicks,
        int TempoMicrosecondsPerQuarter,
        int TimeSignatureCount,
        int KeySignatureCount,
        int MarkerCount,
        int CueCount,
        int LyricsCount,
        int TextCount,
        int SequencerSpecificCount,
        bool Deterministic,
        bool Pass,
        object Receipt)
    {
        public int ForbiddenMetadataCount =>
            TimeSignatureCount + KeySignatureCount + MarkerCount + CueCount + LyricsCount
            + TextCount + SequencerSpecificCount;
        public int TotalMismatches =>
            NoteStartTickMismatches + NoteEndTickMismatches + NotePitchMismatches
            + PitchTrajectoryMismatches + RhythmTickMismatches + RhythmIdentityMismatches
            + SampleTickMismatches + SampleIdentityMismatches + SamplePitchMismatches
            + DuplicateAudibleAttacks + MissingAudibleAttacks + PhysicalVoiceOverlapCount;
    }

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
            SourceOwnership ownership = AnalyzeOwnership(timeline);

            NoteFidelity noteFidelity = CompareTimeline(timeline, decoded.Notes);
            RhythmFidelity rhythmFidelity = CompareRhythm(timeline, decoded.Notes);
            SampleFidelity sampleFidelity = CompareSamples(timeline, decoded.Notes);
            int trajectoryMismatches = ComparePitchTrajectory(timeline, decoded.Bends);
            int physicalVoiceOverlapCount = CountPhysicalVoiceOverlaps(timeline);
            int decodedAttacks = decoded.MelodicNoteOns + decoded.PercussionNoteOns + decoded.SampleNoteOns;
            int duplicateAudibleAttacks = Math.Max(0, decodedAttacks - ownership.UniqueSourceAttacks);
            int missingAudibleAttacks = Math.Max(0, ownership.UniqueSourceAttacks - decodedAttacks);

            var checks = new List<object>();
            void Check(string name, bool ok, object expected, object actual, string? detail = null) =>
                checks.Add(new { name, expected, actual, pass = ok, detail });

            bool decoderPass = decoded.DecoderConformance.Valid;
            bool transcriptionPass =
                noteFidelity.StartTickMismatches == 0
                && noteFidelity.EndTickMismatches == 0
                && noteFidelity.PitchMismatches == 0
                && trajectoryMismatches == 0
                && rhythmFidelity.TickMismatches == 0
                && rhythmFidelity.IdentityMismatches == 0
                && sampleFidelity.TickMismatches == 0
                && sampleFidelity.IdentityMismatches == 0
                && sampleFidelity.PitchMismatches == 0
                && ownership.MissingSourceAttackIds == 0
                && duplicateAudibleAttacks == 0
                && missingAudibleAttacks == 0
                && decoded.TempoCount == 1
                && decoded.TempoTicks.SequenceEqual(new[] { 0L })
                && decoded.TempoMicroseconds == 500_000
                && decoded.TimeSignatures == 0
                && decoded.KeySignatures == 0
                && decoded.Markers == 0
                && decoded.Cues == 0
                && decoded.Lyrics == 0
                && decoded.Text == 0
                && decoded.SequencerSpecific == 0
                && decoded.SmpteOffsets == 0
                && decodedAttacks == ownership.UniqueSourceAttacks
                && deterministic;
            Check("source-attack-ids", ownership.MissingSourceAttackIds == 0, 0,
                ownership.MissingSourceAttackIds);
            Check("unique-source-attacks", decodedAttacks == ownership.UniqueSourceAttacks,
                ownership.UniqueSourceAttacks, decodedAttacks,
                "decoded audible attacks must equal explicit SourceAttackId ownership");
            Check("audible-attack-duplicates", duplicateAudibleAttacks == 0, 0, duplicateAudibleAttacks);
            Check("audible-attack-missing", missingAudibleAttacks == 0, 0, missingAudibleAttacks);
            Check("note-fidelity", noteFidelity.Pass, ownership.NoteViews, noteFidelity.DecodedNotes);
            Check("rhythm-fidelity", rhythmFidelity.Pass, ownership.RhythmViews,
                rhythmFidelity.Decoded);
            Check("sample-fidelity", sampleFidelity.Pass, ownership.SamplePlaybackViews,
                sampleFidelity.Decoded);
            Check("pitch-trajectory", trajectoryMismatches == 0, 0, trajectoryMismatches);
            Check("physical-voice-overlap", physicalVoiceOverlapCount == 0, 0, physicalVoiceOverlapCount);
            Check("tempo", decoded.TempoCount == 1 && decoded.TempoTicks.SequenceEqual(new[] { 0L })
                && decoded.TempoMicroseconds == 500_000,
                "one tempo at tick 0 with 500000 microseconds", decoded.TempoTicks);
            Check("forbidden-metadata", decoded.ForbiddenMetadataCount == 0, 0,
                decoded.ForbiddenMetadataCount);
            Check("export-deterministic", deterministic, "byte-identical on second export",
                deterministic ? "identical" : "differed");

            bool pass = decoderPass && transcriptionPass;
            var receipt = new
            {
                schema = "mdplayer.raw-midi-receipt/v3",
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
                durationSamples = timeline.EndSample - timeline.StartSample,
                noteViews = ownership.NoteViews,
                rhythmViews = ownership.RhythmViews,
                samplePlaybackViews = ownership.SamplePlaybackViews,
                uniqueSourceAttacks = ownership.UniqueSourceAttacks,
                sourceNoteAttacks = ownership.SourceNoteAttacks,
                sourceRhythmAttacks = ownership.SourceRhythmAttacks,
                sourceSampleOnlyAttacks = ownership.SourceSampleOnlyAttacks,
                midiMelodicAttacks = decoded.MelodicNoteOns,
                midiRhythmAttacks = decoded.PercussionNoteOns,
                midiSampleAttacks = decoded.SampleNoteOns,
                duplicateAudibleAttacks,
                missingAudibleAttacks,
                noteStartTickMismatches = noteFidelity.StartTickMismatches,
                noteEndTickMismatches = noteFidelity.EndTickMismatches,
                notePitchMismatches = noteFidelity.PitchMismatches,
                pitchTrajectoryMismatches = trajectoryMismatches,
                rhythmTickMismatches = rhythmFidelity.TickMismatches,
                rhythmIdentityMismatches = rhythmFidelity.IdentityMismatches,
                sampleTickMismatches = sampleFidelity.TickMismatches,
                sampleIdentityMismatches = sampleFidelity.IdentityMismatches,
                samplePitchMismatches = sampleFidelity.PitchMismatches,
                physicalVoiceOverlapCount,
                tempoEventCount = decoded.TempoCount,
                tempoEventTicks = decoded.TempoTicks,
                tempoMicrosecondsPerQuarter = decoded.TempoMicroseconds,
                timeSignatureCount = decoded.TimeSignatures,
                keySignatureCount = decoded.KeySignatures,
                markerCount = decoded.Markers,
                cueCount = decoded.Cues,
                lyricsCount = decoded.Lyrics,
                textCount = decoded.Text,
                sequencerSpecificCount = decoded.SequencerSpecific,
                smpteOffsetCount = decoded.SmpteOffsets,
                deterministic,
                decoderConformance = decoded.DecoderConformance,
                smfTranscriptionConformance = new
                {
                    pass = transcriptionPass,
                    decodedAttacks,
                    checks,
                },
                pass,
            };
            return new SongReceipt(
                songName, ownership.NoteViews, ownership.SourceNoteAttacks,
                ownership.RhythmViews, ownership.SourceRhythmAttacks,
                ownership.SamplePlaybackViews, ownership.SourceSampleOnlyAttacks,
                ownership.UniqueSourceAttacks, decoded.MelodicNoteOns, decoded.PercussionNoteOns,
                decoded.SampleNoteOns, duplicateAudibleAttacks, missingAudibleAttacks,
                noteFidelity.StartTickMismatches, noteFidelity.EndTickMismatches,
                noteFidelity.PitchMismatches, trajectoryMismatches,
                rhythmFidelity.TickMismatches, rhythmFidelity.IdentityMismatches,
                sampleFidelity.TickMismatches, sampleFidelity.IdentityMismatches,
                sampleFidelity.PitchMismatches, physicalVoiceOverlapCount,
                decoded.TempoCount, decoded.TempoTicks.ToArray(),
                decoded.TempoMicroseconds, decoded.TimeSignatures, decoded.KeySignatures,
                decoded.Markers, decoded.Cues, decoded.Lyrics, decoded.Text,
                decoded.SequencerSpecific, deterministic, pass, receipt);
        }
        catch (Exception ex)
        {
            return FailedReceipt(songName, $"pipeline crashed: {ex.Message}");
        }
    }

    private static SongReceipt FailedReceipt(string songName, string error)
    {
        var receipt = new
        {
            schema = "mdplayer.raw-midi-receipt/v3",
            song = songName,
            error,
            decoderConformance = new { valid = false, error },
            smfTranscriptionConformance = new { pass = false },
            pass = false,
        };
        return new SongReceipt(
            SongName: songName,
            NoteViews: 0,
            SourceNoteAttacks: 0,
            RhythmViews: 0,
            SourceRhythmAttacks: 0,
            SamplePlaybackViews: 0,
            SourceSampleOnlyAttacks: 0,
            UniqueSourceAttacks: 0,
            MidiMelodicAttacks: 0,
            MidiRhythmAttacks: 0,
            MidiSampleAttacks: 0,
            DuplicateAudibleAttacks: 0,
            MissingAudibleAttacks: 0,
            NoteStartTickMismatches: 0,
            NoteEndTickMismatches: 0,
            NotePitchMismatches: 0,
            PitchTrajectoryMismatches: 0,
            RhythmTickMismatches: 0,
            RhythmIdentityMismatches: 0,
            SampleTickMismatches: 0,
            SampleIdentityMismatches: 0,
            SamplePitchMismatches: 0,
            PhysicalVoiceOverlapCount: 0,
            TempoEventCount: 0,
            TempoEventTicks: Array.Empty<long>(),
            TempoMicrosecondsPerQuarter: 0,
            TimeSignatureCount: 0,
            KeySignatureCount: 0,
            MarkerCount: 0,
            CueCount: 0,
            LyricsCount: 0,
            TextCount: 0,
            SequencerSpecificCount: 0,
            Deterministic: false,
            Pass: false,
            Receipt: receipt);
    }

    private sealed record SourceOwnership(
        int NoteViews,
        int SourceNoteAttacks,
        int RhythmViews,
        int SourceRhythmAttacks,
        int SamplePlaybackViews,
        int SourceSampleOnlyAttacks,
        int UniqueSourceAttacks,
        int MissingSourceAttackIds,
        IReadOnlySet<string> NoteAttackIds,
        IReadOnlySet<string> SampleOnlyAttackIds);

    private sealed record DecodedNote(
        int Track, int Port, int Channel, int Note, int Bank, bool IsSampleTrack,
        long StartTick, long EndTick, double StartPitch, double EndPitch, int Ordinal);

    private sealed record DecodeAttack(
        int Track, int Port, int Channel, int Note, int Bank, bool IsSampleTrack,
        int Velocity, long StartTick, double StartPitch, int Bend, double BendRange, int Ordinal)
    {
        public int EndBend { get; set; } = Bend;
        public double EndBendRange { get; set; } = BendRange;
    }

    private sealed record DecodedBend(
        int Track, int Port, int Channel, long Tick, int Value, double Range);

    private sealed record DecoderConformance(
        bool Valid,
        int Format,
        int Division,
        int TrackCount,
        int EndOfTrackCount,
        int PortPrefixEvents,
        int MidiChannelEvents,
        int NoteOnEvents,
        int NoteOffEvents,
        int PitchBendEvents,
        int RpnBendRangeEvents,
        int BankSelectEvents,
        int SmpteOffsetEvents,
        int UnexpectedMetaEvents,
        int StatusDataErrors,
        string? Error);

    private sealed class DecodeResult
    {
        public int TempoCount { get; init; }
        public List<long> TempoTicks { get; } = [];
        public int TempoMicroseconds { get; init; }
        public int TimeSignatures { get; init; }
        public int KeySignatures { get; init; }
        public int Markers { get; init; }
        public int Cues { get; init; }
        public int Lyrics { get; init; }
        public int Text { get; init; }
        public int SequencerSpecific { get; init; }
        public int SmpteOffsets { get; init; }
        public int MelodicNoteOns { get; init; }
        public int PercussionNoteOns { get; init; }
        public int SampleNoteOns { get; init; }
        public int SameTickCollisions { get; init; }
        public int OffAtOnPlusOne { get; init; }
        public List<DecodedNote> Notes { get; init; } = [];
        public List<DecodedBend> Bends { get; init; } = [];
        public DecoderConformance DecoderConformance { get; init; } =
            new(
                Valid: false, Format: 0, Division: 0, TrackCount: 0, EndOfTrackCount: 0,
                PortPrefixEvents: 0, MidiChannelEvents: 0, NoteOnEvents: 0, NoteOffEvents: 0,
                PitchBendEvents: 0, RpnBendRangeEvents: 0, BankSelectEvents: 0,
                SmpteOffsetEvents: 0, UnexpectedMetaEvents: 0, StatusDataErrors: 0,
                Error: "not decoded");
        public int ForbiddenMetadataCount =>
            TimeSignatures + KeySignatures + Markers + Cues + Lyrics + Text + SequencerSpecific + SmpteOffsets;

        public static DecodeResult Invalid(string error) => new()
        {
            DecoderConformance = new(
                Valid: false, Format: 0, Division: 0, TrackCount: 0, EndOfTrackCount: 0,
                PortPrefixEvents: 0, MidiChannelEvents: 0, NoteOnEvents: 0, NoteOffEvents: 0,
                PitchBendEvents: 0, RpnBendRangeEvents: 0, BankSelectEvents: 0,
                SmpteOffsetEvents: 0, UnexpectedMetaEvents: 0, StatusDataErrors: 1,
                Error: error),
        };
    }

    private sealed class SmfReader
    {
        private readonly byte[] _bytes;
        public int Position { get; private set; }
        public int End { get; }

        public SmfReader(byte[] bytes, int start, int end)
        {
            _bytes = bytes;
            Position = start;
            End = end;
        }

        public bool AtEnd => Position >= End;

        public byte ReadByte()
        {
            if (Position >= End)
                throw new InvalidDataException("SMF event exceeds its track chunk.");
            return _bytes[Position++];
        }

        public byte[] ReadBytes(int count)
        {
            if (count < 0 || Position > End - count)
                throw new InvalidDataException("SMF payload exceeds its track chunk.");
            byte[] result = _bytes[Position..(Position + count)];
            Position += count;
            return result;
        }

        public ushort ReadU16()
        {
            if (Position > End - 2)
                throw new InvalidDataException("SMF header is truncated.");
            ushort value = BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(Position, 2));
            Position += 2;
            return value;
        }

        public uint ReadU32()
        {
            if (Position > End - 4)
                throw new InvalidDataException("SMF chunk header is truncated.");
            uint value = BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(Position, 4));
            Position += 4;
            return value;
        }

        public int ReadVlq()
        {
            int value = 0;
            for (int index = 0; index < 4; index++)
            {
                byte next = ReadByte();
                value = checked((value << 7) | (next & 0x7F));
                if ((next & 0x80) == 0)
                    return value;
            }
            throw new InvalidDataException("SMF variable-length quantity exceeds four bytes.");
        }
    }

    /// <summary>Independent byte decoder for serialized SMF bytes. It reads
    /// absolute ticks, raw status/data bytes, running status, and all metadata
    /// relevant to the v3 receipt without external MIDI parser helpers.</summary>
    private static DecodeResult DecodeOnly(byte[] bytes)
    {
        try
        {
            var reader = new SmfReader(bytes, 0, bytes.Length);
            if (!reader.ReadBytes(4).SequenceEqual("MThd"u8.ToArray()))
                throw new InvalidDataException("SMF header chunk is missing.");
            uint headerLength = reader.ReadU32();
            if (headerLength != 6)
                throw new InvalidDataException($"SMF header length {headerLength} is not six.");
            int format = reader.ReadU16();
            int trackCount = reader.ReadU16();
            int division = reader.ReadU16();
            if (trackCount <= 0)
                throw new InvalidDataException("SMF contains no tracks.");

            var result = new DecodeResult { };
            var notes = new List<DecodedNote>();
            var bends = new List<DecodedBend>();
            var open = new Dictionary<(int Port, int Channel, int Note), Queue<DecodeAttack>>();
            var activeBend = new Dictionary<(int Port, int Channel), int>();
            var activeRange = new Dictionary<(int Port, int Channel), double>();
            var banks = new Dictionary<(int Port, int Channel), int>();
            var rpn = new Dictionary<(int Port, int Channel), (int Msb, int Lsb)>();
            int tempoCount = 0, tempoUs = 0, sigs = 0, keys = 0, markers = 0;
            int cues = 0, lyrics = 0, text = 0, sequencerSpecific = 0, smpte = 0;
            int melodicOn = 0, percussionOn = 0, sampleOn = 0, collisions = 0, offAtOnPlusOne = 0;
            int portPrefixes = 0, channelEvents = 0, noteOns = 0, noteOffs = 0, pitchBends = 0;
            int rpnBends = 0, bankSelects = 0, eotCount = 0, unexpectedMeta = 0, statusErrors = 0;
            int attackOrdinal = 0;
            var tempoTicks = new List<long>();
            var ports = new HashSet<int>();
            var midiChannels = new HashSet<int>();

            for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
            {
                if (!reader.ReadBytes(4).SequenceEqual("MTrk"u8.ToArray()))
                    throw new InvalidDataException("SMF track chunk is missing.");
                int trackLength = checked((int)reader.ReadU32());
                int trackEnd = checked(reader.Position + trackLength);
                if (trackEnd > reader.End)
                    throw new InvalidDataException("SMF track chunk exceeds file length.");
                var track = new SmfReader(bytes, reader.Position, trackEnd);
                long tick = 0;
                byte runningStatus = 0;
                int port = 0;
                string? trackName = null;
                var attacksPerTick = new Dictionary<(int Port, int Channel, long Tick), int>();

                while (!track.AtEnd)
                {
                    tick = checked(tick + track.ReadVlq());
                    byte first = track.ReadByte();
                    byte status;
                    byte firstData = 0;
                    bool hasFirstData = false;
                    if (first < 0x80)
                    {
                        if (runningStatus < 0x80 || runningStatus >= 0xF0)
                        {
                            statusErrors++;
                            throw new InvalidDataException("SMF uses running status without a channel status.");
                        }
                        status = runningStatus;
                        firstData = first;
                        hasFirstData = true;
                    }
                    else
                    {
                        status = first;
                        if (status < 0xF0)
                            runningStatus = status;
                        else
                            runningStatus = 0;
                    }

                    if (status == 0xFF)
                    {
                        byte type = track.ReadByte();
                        int length = track.ReadVlq();
                        byte[] data = track.ReadBytes(length);
                        switch (type)
                        {
                            case 0x00:
                                break;
                            case 0x01: text++; break;
                            case 0x03:
                                trackName = Encoding.UTF8.GetString(data);
                                break;
                            case 0x05: lyrics++; break;
                            case 0x06: markers++; break;
                            case 0x07: cues++; break;
                            case 0x21:
                                if (length != 1)
                                    throw new InvalidDataException("MIDI Port Prefix must contain one byte.");
                                port = data[0];
                                ports.Add(port);
                                portPrefixes++;
                                break;
                            case 0x2F:
                                if (length != 0)
                                    throw new InvalidDataException("EndOfTrack must have zero-length data.");
                                eotCount++;
                                if (!track.AtEnd)
                                    throw new InvalidDataException("SMF has data after EndOfTrack.");
                                break;
                            case 0x51:
                                if (length != 3)
                                    throw new InvalidDataException("Set Tempo must contain three bytes.");
                                tempoCount++;
                                tempoUs = (data[0] << 16) | (data[1] << 8) | data[2];
                                tempoTicks.Add(tick);
                                break;
                            case 0x54: smpte++; break;
                            case 0x58: sigs++; break;
                            case 0x59: keys++; break;
                            case 0x7F: sequencerSpecific++; break;
                            default:
                                unexpectedMeta++;
                                break;
                        }
                        continue;
                    }

                    if (status is 0xF0 or 0xF7)
                    {
                        int length = track.ReadVlq();
                        _ = track.ReadBytes(length);
                        continue;
                    }
                    if (status < 0x80 || status >= 0xF0)
                    {
                        statusErrors++;
                        throw new InvalidDataException($"Unsupported SMF status 0x{status:X2}.");
                    }

                    channelEvents++;
                    int channel = status & 0x0F;
                    int command = status & 0xF0;
                    midiChannels.Add(channel);
                    int dataLength = command is 0xC0 or 0xD0 ? 1 : 2;
                    byte data1 = hasFirstData ? firstData : track.ReadByte();
                    byte data2 = dataLength == 2 ? track.ReadByte() : (byte)0;
                    if (data1 > 0x7F || data2 > 0x7F)
                    {
                        statusErrors++;
                        throw new InvalidDataException("SMF channel data byte has its high bit set.");
                    }

                    var endpoint = (port, channel);
                    switch (command)
                    {
                        case 0x80:
                            noteOffs++;
                            CloseDecodedNote(trackIndex, port, channel, data1, tick, open, notes,
                                ref offAtOnPlusOne);
                            break;
                        case 0x90:
                            noteOns++;
                            if (data2 == 0)
                            {
                                noteOffs++;
                                CloseDecodedNote(trackIndex, port, channel, data1, tick, open, notes,
                                    ref offAtOnPlusOne);
                                break;
                            }
                            double range = activeRange.TryGetValue(endpoint, out double knownRange) ? knownRange : 2.0;
                            int bend = activeBend.TryGetValue(endpoint, out int knownBend) ? knownBend : 8192;
                            int bank = banks.TryGetValue(endpoint, out int knownBank) ? knownBank : 0;
                            bool sampleTrack = trackName?.StartsWith("Sample ", StringComparison.Ordinal) == true;
                            var attack = new DecodeAttack(trackIndex, port, channel, data1, bank, sampleTrack,
                                data2, tick, data1 + BendOffset(bend, range), bend, range, attackOrdinal++);
                            var key = (port, channel, (int)data1);
                            if (!open.TryGetValue(key, out Queue<DecodeAttack>? queue))
                            {
                                queue = new Queue<DecodeAttack>();
                                open[key] = queue;
                            }
                            queue.Enqueue(attack);
                            if (sampleTrack)
                                sampleOn++;
                            else if (channel == 9)
                                percussionOn++;
                            else
                                melodicOn++;
                            var collisionKey = (port, channel, tick);
                            attacksPerTick[collisionKey] =
                                attacksPerTick.TryGetValue(collisionKey, out int count) ? count + 1 : 1;
                            break;
                        case 0xB0:
                            if (data1 is 0 or 32)
                            {
                                bankSelects++;
                                if (data1 == 0)
                                    banks[endpoint] = data2;
                            }
                            else if (data1 is 101 or 100)
                            {
                                rpn.TryGetValue(endpoint, out var state);
                                rpn[endpoint] = data1 == 101 ? (data2, state.Lsb) : (state.Msb, data2);
                            }
                            else if (data1 == 6
                                && rpn.TryGetValue(endpoint, out var selected)
                                && selected.Msb == 0 && selected.Lsb == 0)
                            {
                                activeRange[endpoint] = Math.Max(1, (int)data2);
                                UpdateOpenAttackState(open, port, channel,
                                    bendRange: activeRange[endpoint]);
                                rpnBends++;
                            }
                            break;
                        case 0xE0:
                            int pitchValue = data1 | (data2 << 7);
                            activeBend[endpoint] = pitchValue;
                            double bendRange = activeRange.TryGetValue(endpoint, out double currentRange)
                                ? currentRange : 2.0;
                            pitchBends++;
                            bends.Add(new DecodedBend(trackIndex, port, channel, tick, pitchValue, bendRange));
                            UpdateOpenAttackState(open, port, channel, bend: pitchValue);
                            break;
                    }
                }
                collisions += attacksPerTick.Values.Count(count => count > 1);
                reader.ReadBytes(trackLength);
            }

            if (!reader.AtEnd)
                throw new InvalidDataException("SMF has trailing bytes after its track chunks.");
            result = new DecodeResult
            {
                TempoCount = tempoCount,
                TempoMicroseconds = tempoUs,
                TimeSignatures = sigs,
                KeySignatures = keys,
                Cues = cues,
                Lyrics = lyrics,
                Text = text,
                SequencerSpecific = sequencerSpecific,
                SmpteOffsets = smpte,
                MelodicNoteOns = melodicOn,
                PercussionNoteOns = percussionOn,
                SampleNoteOns = sampleOn,
                SameTickCollisions = collisions,
                OffAtOnPlusOne = offAtOnPlusOne,
                Notes = notes,
                Bends = bends,
                DecoderConformance = new(
                    Valid: format == 1 && division == Ppq && eotCount == trackCount
                        && unexpectedMeta == 0 && statusErrors == 0,
                    Format: format,
                    Division: division,
                    TrackCount: trackCount,
                    EndOfTrackCount: eotCount,
                    PortPrefixEvents: portPrefixes,
                    MidiChannelEvents: channelEvents,
                    NoteOnEvents: noteOns,
                    NoteOffEvents: noteOffs,
                    PitchBendEvents: pitchBends,
                    RpnBendRangeEvents: rpnBends,
                    BankSelectEvents: bankSelects,
                    SmpteOffsetEvents: smpte,
                    UnexpectedMetaEvents: unexpectedMeta,
                    StatusDataErrors: statusErrors,
                    Error: null),
            };
            result.TempoTicks.AddRange(tempoTicks);
            return result;
        }
        catch (Exception ex)
        {
            return DecodeResult.Invalid(ex.Message);
        }
    }

    private static void UpdateOpenAttackState(
        Dictionary<(int Port, int Channel, int Note), Queue<DecodeAttack>> open,
        int port,
        int channel,
        int? bend = null,
        double? bendRange = null)
    {
        foreach (KeyValuePair<(int Port, int Channel, int Note), Queue<DecodeAttack>> entry in open)
        {
            if (entry.Key.Port != port || entry.Key.Channel != channel)
                continue;
            foreach (DecodeAttack attack in entry.Value)
            {
                if (bend.HasValue)
                    attack.EndBend = bend.Value;
                if (bendRange.HasValue)
                    attack.EndBendRange = bendRange.Value;
            }
        }
    }

    private static void CloseDecodedNote(
        int trackIndex,
        int port,
        int channel,
        int note,
        long tick,
        Dictionary<(int Port, int Channel, int Note), Queue<DecodeAttack>> open,
        List<DecodedNote> notes,
        ref int offAtOnPlusOne)
    {
        var key = (port, channel, note);
        if (!open.TryGetValue(key, out Queue<DecodeAttack>? queue) || queue.Count == 0)
            return;
        DecodeAttack attack = queue.Dequeue();
        notes.Add(new DecodedNote(trackIndex, port, channel, note, attack.Bank, attack.IsSampleTrack,
            attack.StartTick, tick, attack.StartPitch,
            note + BendOffset(attack.EndBend, attack.EndBendRange), attack.Ordinal));
        if (tick - attack.StartTick == 1)
            offAtOnPlusOne++;
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
                .ThenBy(item => item.note.StartSample)
                .ThenBy(item => item.index)
                .ToArray();
            var actual = decoded
                .Where(note => note.Track == track && note.Port == port && note.Channel == channel)
                .OrderBy(note => note.StartTick)
                .ThenBy(note => note.Ordinal)
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
                bool serializedOneTickWithoutInteriorPitch =
                    expectedEnd == expectedStart + 1
                    && decodedNote.EndTick == decodedNote.StartTick + 1
                    && !HasInteriorSourcePitchState(sourceNote);
                double decodedEndPitch = serializedOneTickWithoutInteriorPitch
                    ? decodedNote.StartPitch
                    : decodedNote.EndPitch;
                if (decodedNote.Note != BaseNote(sourceNote)
                    || Math.Abs(decodedNote.StartPitch - expectedStartPitch) > pitchTolerance
                    || Math.Abs(decodedEndPitch - expectedEndPitch) > pitchTolerance)
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
        var noteAttackIds = (timeline.Notes ?? Array.Empty<NoteEvent>())
            .Select(value => value.SourceAttackId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        SamplePlaybackEvent[] source = SampleOnlySamples(timeline, noteAttackIds);
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
    private static bool HasInteriorSourcePitchState(Fmp.Core.Visualization.NoteEvent note) =>
        PitchStates(note).Any(change =>
            change.SamplePosition > note.StartSample
            && change.SamplePosition < note.EndSample);

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
        (timeline.SamplePlayback ?? Array.Empty<SamplePlaybackEvent>()).ToArray();

    private static SamplePlaybackEvent[] SampleOnlySamples(
        VisualizationTimeline timeline,
        IReadOnlySet<string> noteAttackIds) =>
        AuthoritativeSamples(timeline)
            .Where(value => value.SourceAttackId is null
                || !noteAttackIds.Contains(value.SourceAttackId))
            .ToArray();

    private static SourceOwnership AnalyzeOwnership(VisualizationTimeline timeline)
    {
        NoteEvent[] notes = (timeline.Notes ?? Array.Empty<NoteEvent>()).ToArray();
        RhythmEvent[] rhythm = (timeline.Rhythm ?? Array.Empty<RhythmEvent>()).ToArray();
        var noteIds = notes
            .Select(value => value.SourceAttackId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        var rhythmIds = rhythm
            .Select(value => value.SourceAttackId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        SamplePlaybackEvent[] sampleOnly = SampleOnlySamples(timeline, noteIds);
        var sampleOnlyIds = sampleOnly
            .Select(value => value.SourceAttackId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToHashSet(StringComparer.Ordinal);
        var unique = noteIds.Union(rhythmIds, StringComparer.Ordinal)
            .Union(sampleOnlyIds, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        int missingIds = notes.Count(value => string.IsNullOrWhiteSpace(value.SourceAttackId))
            + rhythm.Count(value => string.IsNullOrWhiteSpace(value.SourceAttackId))
            + sampleOnly.Count(value => string.IsNullOrWhiteSpace(value.SourceAttackId));
        return new SourceOwnership(
            notes.Length, noteIds.Count, rhythm.Length, rhythmIds.Count,
            AuthoritativeSamples(timeline).Length, sampleOnlyIds.Count, unique.Count, missingIds,
            noteIds, sampleOnlyIds);
    }

    private static int CountPhysicalVoiceOverlaps(VisualizationTimeline timeline)
    {
        var intervals = new List<(string Family, string Voice, long Start, long End)>();
        foreach (NoteEvent note in timeline.Notes ?? Array.Empty<NoteEvent>())
        {
            long start = note.StartSample;
            long end = note.EndSample;
            intervals.Add(("pitched", VoiceId(note), start, end <= start ? start + 1 : end));
        }

        int overlaps = 0;
        foreach (IGrouping<(string Family, string Voice), (string Family, string Voice, long Start, long End)> group
            in intervals.GroupBy(value => (value.Family, value.Voice)))
        {
            long activeEnd = long.MinValue;
            foreach (var item in group.OrderBy(value => value.Start).ThenBy(value => value.End))
            {
                if (item.Start < activeEnd)
                    overlaps++;
                activeEnd = Math.Max(activeEnd, item.End);
            }
        }
        return overlaps;
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