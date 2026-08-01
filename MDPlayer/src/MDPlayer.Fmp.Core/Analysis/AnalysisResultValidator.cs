using System.Text.Json;

namespace Fmp.Core.Analysis;

internal static class AnalysisOutputJson
{
    public static string Serialize(AnalysisOutput output, bool indented = true)
        => AnalysisJson.SerializeOutput(output, indented);

    public static AnalysisOutput Deserialize(string json)
        => AnalysisJson.Deserialize<AnalysisOutput>(json);
}

internal static class AnalysisResultValidator
{
    internal const string ExpectedMusic21Version = "10.5.0";
    internal const string ExpectedWorkerVersion = "1.0.2";
    private static readonly HashSet<string> Certainties = ["observed", "strong", "tentative", "withheld"];

    public static IReadOnlyList<string> Validate(AnalysisInput input, AnalysisOutput output)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        var errors = new List<string>();
        ValidateInput(input, errors);
        if (output.SchemaVersion != 1)
            errors.Add($"unsupported output schema version: {output.SchemaVersion}");
        if (!string.Equals(output.AnalysisId, input.AnalysisId, StringComparison.Ordinal))
            errors.Add("analysisId does not match the input");
        if (output.Engine is null || !string.Equals(
                output.Engine.Name, "mdplayer-music-analysis", StringComparison.Ordinal))
            errors.Add("unexpected analysis engine");
        if (output.Engine is not null && !string.Equals(
                output.Engine.Music21Version, ExpectedMusic21Version, StringComparison.Ordinal))
            errors.Add($"unexpected music21 version: {output.Engine.Music21Version}");
        if (output.Engine is not null && !string.Equals(
                output.Engine.Version, ExpectedWorkerVersion, StringComparison.Ordinal))
            errors.Add($"unexpected analysis engine version: {output.Engine.Version}");
        if (output.TimingMode is not ("seconds" or "beats"))
            errors.Add("unsupported timing mode");
        if (output.Global is null)
            errors.Add("global analysis is missing");
        else
        {
            if (output.Global.Key is null)
                errors.Add("global key analysis is missing");
            if (output.Global.Pitch is null)
                errors.Add("global pitch analysis is missing");
        }

        var knownChannels = (input.Channels ?? Array.Empty<AnalysisChannel>())
            .Where(channel => channel is not null)
            .Select(channel => channel.Id)
            .ToHashSet(StringComparer.Ordinal);
        ValidateConfidence(output.Global?.Key?.Confidence, "global.key.confidence", errors);
        if (output.Global?.Key?.Primary is not null)
            ValidateKey(output.Global.Key.Primary, "global.key.primary", errors);
        foreach (KeyCandidate candidate in output.Global?.Key?.Alternatives ?? Array.Empty<KeyCandidate>())
        {
            if (candidate is null)
                errors.Add("global.key.alternative is null");
            else
                ValidateKey(candidate, "global.key.alternative", errors);
        }
        foreach (KeyMethodResult method in output.Global?.Key?.Methods ?? Array.Empty<KeyMethodResult>())
        {
            if (method is null)
            {
                errors.Add("global.key.methods cannot contain null entries");
                continue;
            }
            ValidateFinite(method.Correlation, "global.key.method.correlation", errors);
            if (method.Correlation is < -1 or > 1)
                errors.Add("global.key.method.correlation is outside [-1,1]");
        }
        ValidateKeyEvidence(output.Global?.Key, errors);
        ValidatePitchDistribution(output.Global?.Pitch?.PitchClassDistribution, "global.pitch", errors);
        if (output.Global?.Pitch is not null)
        {
            ValidateFinite(output.Global.Pitch.MinMidiPitch, "global.pitch.minMidiPitch", errors);
            ValidateFinite(output.Global.Pitch.MaxMidiPitch, "global.pitch.maxMidiPitch", errors);
            ValidateFinite(output.Global.Pitch.Ambitus, "global.pitch.ambitus", errors);
            ValidateFinite(output.Global.Pitch.AverageNoteDuration, "global.pitch.averageNoteDuration", errors);
            ValidateFinite(output.Global.Pitch.NoteDensity, "global.pitch.noteDensity", errors);
            if (output.Global.Pitch.Ambitus < 0)
                errors.Add("global.pitch.ambitus is negative");
            if (output.Global.Pitch.AverageNoteDuration < 0)
                errors.Add("global.pitch.averageNoteDuration is negative");
            if (output.Global.Pitch.NoteDensity < 0)
                errors.Add("global.pitch.noteDensity is negative");
            ValidateJSymbolic(output.Global.Pitch.JSymbolic, errors);
        }

        var outputChannelIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (ChannelAnalysis channel in output.Channels ?? Array.Empty<ChannelAnalysis>())
        {
            if (channel is null)
            {
                errors.Add("channels cannot contain null entries");
                continue;
            }
            if (!knownChannels.Contains(channel.ChannelId))
                errors.Add($"unknown channel: {channel.ChannelId}");
            if (string.IsNullOrWhiteSpace(channel.ChannelId) || !outputChannelIds.Add(channel.ChannelId))
                errors.Add($"output channel id is missing or duplicated: {channel.ChannelId}");
            ValidateFinite(channel.MinMidiPitch, $"channel {channel.ChannelId}.minMidiPitch", errors);
            ValidateFinite(channel.MaxMidiPitch, $"channel {channel.ChannelId}.maxMidiPitch", errors);
            ValidateFinite(channel.MedianMidiPitch, $"channel {channel.ChannelId}.medianMidiPitch", errors);
            ValidateFinite(channel.StepRatio, $"channel {channel.ChannelId}.stepRatio", errors);
            ValidateFinite(channel.RepetitionRatio, $"channel {channel.ChannelId}.repetitionRatio", errors);
            ValidateFinite(channel.IntervalDiversity, $"channel {channel.ChannelId}.intervalDiversity", errors);
            if (channel.NoteCount < 0)
                errors.Add($"channel {channel.ChannelId}.noteCount is negative");
            if (channel.Ambitus < 0 || !double.IsFinite(channel.Ambitus))
                errors.Add($"channel {channel.ChannelId}.ambitus is invalid");
            ValidateRatio(channel.StepRatio, $"channel {channel.ChannelId}.stepRatio", errors);
            ValidateRatio(channel.RepetitionRatio, $"channel {channel.ChannelId}.repetitionRatio", errors);
            ValidateRatio(channel.IntervalDiversity, $"channel {channel.ChannelId}.intervalDiversity", errors);
            ValidatePitchDistribution(channel.PitchClassDistribution, $"channel {channel.ChannelId}", errors);
            if (channel.MinMidiPitch > channel.MaxMidiPitch && channel.NoteCount > 0)
                errors.Add($"channel {channel.ChannelId}.pitch range is inverted");
        }

        foreach (KeyRegion key in output.Keys ?? Array.Empty<KeyRegion>())
        {
            if (key is null)
            {
                errors.Add("keys cannot contain null entries");
                continue;
            }
            ValidateRange(key.StartSample, key.EndSample, input, "key", errors);
            ValidateKey(key, "key", errors);
            ValidateConfidence(key.Confidence, "key.confidence", errors);
        }
        foreach (HarmonySegment harmony in output.Harmony ?? Array.Empty<HarmonySegment>())
        {
            if (harmony is null)
            {
                errors.Add("harmony cannot contain null entries");
                continue;
            }
            ValidateStatus(harmony.Status, "harmony.status", errors);
            ValidateRange(harmony.StartSample, harmony.EndSample, input, "harmony", errors);
            foreach (int pitchClass in harmony.PitchClasses ?? Array.Empty<int>())
                ValidatePitchClass(pitchClass, "harmony.pitchClasses", errors);
            if (harmony.BassPitchClass < -1 || harmony.BassPitchClass > 11)
                errors.Add("harmony.bassPitchClass is outside -1..11");
            else if (harmony.BassPitchClass >= 0)
                ValidatePitchClass(harmony.BassPitchClass, "harmony.bassPitchClass", errors);
            ValidateConfidence(harmony.Confidence, "harmony.confidence", errors);
            if (harmony.Roman is not null)
                ValidateConfidence(harmony.RomanConfidence, "harmony.romanConfidence", errors);
        }
        foreach (MotifOccurrence motif in output.Motifs ?? Array.Empty<MotifOccurrence>())
        {
            if (motif is null)
            {
                errors.Add("motifs cannot contain null entries");
                continue;
            }
            ValidateStatus(motif.Status, "motif.status", errors);
            if (!knownChannels.Contains(motif.ChannelId))
                errors.Add($"motif references unknown channel: {motif.ChannelId}");
            ValidateRange(motif.StartSample, motif.EndSample, input, "motif", errors);
            if (string.IsNullOrWhiteSpace(motif.MotifId))
                errors.Add("motif.motifId is required");
            ValidateFinite(motif.Similarity, "motif.similarity", errors);
            if (motif.Similarity is < 0 or > 1)
                errors.Add("motif similarity is outside [0,1]");
        }
        foreach (ChannelRelationship relationship in output.Relationships ?? Array.Empty<ChannelRelationship>())
        {
            if (relationship is null)
            {
                errors.Add("relationships cannot contain null entries");
                continue;
            }
            ValidateStatus(relationship.Status, "relationship.status", errors);
            if (!knownChannels.Contains(relationship.SourceChannelId)
                || !knownChannels.Contains(relationship.TargetChannelId))
                errors.Add("relationship references an unknown channel");
            ValidateFinite(relationship.Coverage, "relationship.coverage", errors);
            if (relationship.Coverage is < 0 or > 1)
                errors.Add("relationship coverage is outside [0,1]");
            ValidateConfidence(relationship.Confidence, "relationship.confidence", errors);
        }
        foreach (PedalToneCandidate pedal in output.PedalTones ?? Array.Empty<PedalToneCandidate>())
        {
            if (pedal is null)
            {
                errors.Add("pedalTones cannot contain null entries");
                continue;
            }
            ValidateStatus(pedal.Status, "pedalTone.status", errors);
            if (!knownChannels.Contains(pedal.ChannelId))
                errors.Add($"pedal tone references unknown channel: {pedal.ChannelId}");
            ValidatePitchClass(pedal.PitchClass, "pedal tone.pitchClass", errors);
            ValidateRange(pedal.StartSample, pedal.EndSample, input, "pedal tone", errors);
            ValidateRatio(pedal.Coverage, "pedal tone.coverage", errors);
            ValidateConfidence(pedal.Confidence, "pedal tone.confidence", errors);
        }
        foreach (OstinatoCandidate ostinato in output.Ostinatos ?? Array.Empty<OstinatoCandidate>())
        {
            if (ostinato is null)
            {
                errors.Add("ostinatos cannot contain null entries");
                continue;
            }
            ValidateStatus(ostinato.Status, "ostinato.status", errors);
            if (!knownChannels.Contains(ostinato.ChannelId))
                errors.Add($"ostinato references unknown channel: {ostinato.ChannelId}");
            foreach (int pitchClass in ostinato.PitchClasses ?? Array.Empty<int>())
                ValidatePitchClass(pitchClass, "ostinato.pitchClasses", errors);
            if (ostinato.Occurrences < 3)
                errors.Add("ostinato occurrences must be at least 3");
            ValidateRange(ostinato.StartSample, ostinato.EndSample, input, "ostinato", errors);
            ValidateRatio(ostinato.Regularity, "ostinato.regularity", errors);
            ValidateConfidence(ostinato.Confidence, "ostinato.confidence", errors);
        }
        foreach (AnalysisBoundary boundary in output.Boundaries ?? Array.Empty<AnalysisBoundary>())
        {
            if (boundary is null)
            {
                errors.Add("boundaries cannot contain null entries");
                continue;
            }
            ValidateStatus(boundary.Status, "boundary.status", errors);
            ValidateRange(boundary.Sample, boundary.Sample, input, "boundary", errors);
            ValidateConfidence(boundary.Confidence, "boundary.confidence", errors);
        }
        foreach (AnalysisWarning warning in output.Warnings ?? Array.Empty<AnalysisWarning>())
        {
            if (warning is null)
            {
                errors.Add("warnings cannot contain null entries");
                continue;
            }
            if (string.IsNullOrWhiteSpace(warning.Code) || string.IsNullOrWhiteSpace(warning.Message))
                errors.Add("warnings require code and message");
        }
        return errors;
    }

    private static void ValidateInput(AnalysisInput input, ICollection<string> errors)
    {
        if (input.SchemaVersion != 1)
            errors.Add($"unsupported input schema version: {input.SchemaVersion}");
        if (input.SampleRate <= 0)
            errors.Add("input sampleRate must be positive");
        if (input.StartSample < 0 || input.EndSample < input.StartSample)
            errors.Add("input sample range is invalid");
        if (input.Track is null || input.Timing is null)
            errors.Add("input track and timing are required");
        if (input.Timing is not null)
        {
            if (input.Timing.TimingMode is not ("seconds" or "beats"))
                errors.Add("input timing mode is unsupported");
            BeatPosition[] beats = (input.Timing.Beats ?? Array.Empty<BeatPosition>()).ToArray();
            for (int index = 0; index < beats.Length; index++)
            {
                BeatPosition beat = beats[index];
                if (beat.Sample < input.StartSample || beat.Sample > input.EndSample
                    || !double.IsFinite(beat.Beat))
                    errors.Add("input beat is outside the source range or non-finite");
                if (index > 0 && (beat.Sample <= beats[index - 1].Sample
                    || beat.Beat <= beats[index - 1].Beat))
                    errors.Add("input beats must increase in sample and beat order");
            }
            if (input.Timing.TimingMode == "beats" && beats.Length < 2)
                errors.Add("beat timing requires at least two beat events");
        }

        var channels = input.Channels ?? Array.Empty<AnalysisChannel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (AnalysisChannel channel in channels)
        {
            if (channel is null)
            {
                errors.Add("input channels cannot contain null entries");
                continue;
            }
            if (string.IsNullOrWhiteSpace(channel.Id) || !ids.Add(channel.Id))
                errors.Add($"input channel id is missing or duplicated: {channel.Id}");
            ValidateFinite(channel.AnalysisWeight, $"input channel {channel.Id}.analysisWeight", errors);
            if (channel.AnalysisWeight is < 0 or > 1)
                errors.Add($"input channel {channel.Id}.analysisWeight is outside [0,1]");
            foreach (AnalysisNote note in channel.Notes ?? Array.Empty<AnalysisNote>())
            {
                if (note is null)
                {
                    errors.Add($"input channel {channel.Id} contains a null note");
                    continue;
                }
                ValidateRange(note.StartSample, note.EndSample, input, "input note", errors);
                if (note.PitchClass < -1 || note.PitchClass > 11)
                    errors.Add("input note.pitchClass is outside -1..11");
                else if (note.PitchClass >= 0)
                    ValidatePitchClass(note.PitchClass, "input note.pitchClass", errors);
                ValidateFinite(note.StructuralMidiPitch, "input note.structuralMidiPitch", errors);
                if (note.IsNoise && note.IsPitched)
                    errors.Add("input note cannot be both noise and pitched");
                foreach (AnalysisPitchRegion region in note.PitchRegions ?? Array.Empty<AnalysisPitchRegion>())
                {
                    if (region is null)
                    {
                        errors.Add("input note contains a null pitch region");
                        continue;
                    }
                    ValidateRange(region.StartSample, region.EndSample, input, "input pitch region", errors);
                    if (region.StartSample < note.StartSample || region.EndSample > note.EndSample)
                        errors.Add("input pitch region is outside its note");
                    ValidateFinite(region.MidiPitch, "input pitch region.midiPitch", errors);
                    ValidateRatio(region.Stability, "input pitch region.stability", errors);
                }
            }
        }

        foreach (ArpeggioEvidence evidence in input.ArpeggioEvidence ?? Array.Empty<ArpeggioEvidence>())
        {
            if (evidence is null)
            {
                errors.Add("input arpeggio evidence cannot contain null entries");
                continue;
            }
            if (!ids.Contains(evidence.ChannelId))
                errors.Add($"arpeggio evidence references unknown channel: {evidence.ChannelId}");
            ValidateRange(evidence.StartSample, evidence.EndSample, input, "arpeggio evidence", errors);
            if (evidence.PeriodSamples <= 0)
                errors.Add("arpeggio evidence period must be positive");
            foreach (int pitchClass in evidence.PitchClasses ?? Array.Empty<int>())
                ValidatePitchClass(pitchClass, "arpeggio evidence.pitchClasses", errors);
            ValidateRatio(evidence.Regularity, "arpeggio evidence.regularity", errors);
            ValidateRatio(evidence.Weight, "arpeggio evidence.weight", errors);
        }
    }

    public static void EnsureValid(AnalysisInput input, AnalysisOutput output)
    {
        IReadOnlyList<string> errors = Validate(input, output);
        if (errors.Count > 0)
            throw new InvalidDataException("Invalid analysis output: " + string.Join("; ", errors));
    }

    public static AnalysisOutput ReadAndValidate(string path, AnalysisInput input)
    {
        string json = File.ReadAllText(path);
        AnalysisOutput output;
        try { output = AnalysisOutputJson.Deserialize(json); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid analysis JSON.", ex); }
        EnsureValid(input, output);
        return output;
    }

    private static void ValidateKey(KeyCandidate key, string name, ICollection<string> errors)
    {
        if (key.TonicPitchClass is < 0 or > 11)
            errors.Add($"{name}.tonicPitchClass is outside 0-11");
        ValidateFinite(key.Confidence, $"{name}.confidence", errors);
        if (key.Confidence is < 0 or > 1)
            errors.Add($"{name}.confidence is outside [0,1]");
    }

    private static void ValidateKey(KeyRegion key, string name, ICollection<string> errors)
    {
        if (key.TonicPitchClass is < 0 or > 11)
            errors.Add($"{name}.tonicPitchClass is outside 0-11");
    }

    private static void ValidateKeyEvidence(KeyInterpretation key, ICollection<string> errors)
    {
        foreach ((string name, int votes) in key?.WinnerVotes ?? new Dictionary<string, int>())
        {
            if (string.IsNullOrWhiteSpace(name) || votes < 1)
                errors.Add("global.key.winnerVotes contains an invalid entry");
        }
        ValidateCorrelationMap(key?.WinnerCorrelations, "global.key.winnerCorrelations", errors);
        ValidateCorrelationMap(key?.AlternativeCorrelations, "global.key.alternativeCorrelations", errors);
    }

    private static void ValidateCorrelationMap(
        IReadOnlyDictionary<string, IReadOnlyList<double>> values,
        string name,
        ICollection<string> errors)
    {
        foreach ((string candidate, IReadOnlyList<double> correlations) in values ??
            new Dictionary<string, IReadOnlyList<double>>())
        {
            if (string.IsNullOrWhiteSpace(candidate) || correlations is null || correlations.Count == 0)
            {
                errors.Add($"{name} contains an invalid entry");
                continue;
            }
            foreach (double correlation in correlations)
            {
                ValidateFinite(correlation, name + ".correlation", errors);
                if (correlation is < -1 or > 1)
                    errors.Add(name + ".correlation is outside [-1,1]");
            }
        }
    }

    private static void ValidateConfidence(AnalysisConfidence confidence, string name, ICollection<string> errors)
    {
        if (confidence is null)
        {
            errors.Add($"{name} is missing");
            return;
        }
        ValidateFinite(confidence.Score, name + ".score", errors);
        if (confidence.Score is < 0 or > 1)
            errors.Add($"{name}.score is outside [0,1]");
        if (!Certainties.Contains(confidence.Certainty ?? ""))
            errors.Add($"{name}.certainty is unsupported");
    }

    private static void ValidateRange(long start, long end, AnalysisInput input, string name, ICollection<string> errors)
    {
        if (start < input.StartSample || end > input.EndSample || end < start)
            errors.Add($"{name} sample range is outside the input");
    }

    private static void ValidatePitchDistribution(IReadOnlyList<double> values, string name, ICollection<string> errors)
    {
        if (values is null || values.Count != 12)
            errors.Add($"{name}.pitchClassDistribution must contain 12 values");
        foreach (double value in values ?? Array.Empty<double>())
        {
            ValidateFinite(value, name + ".pitchClassDistribution", errors);
            if (value is < 0 or > 1)
                errors.Add($"{name}.pitchClassDistribution contains a value outside [0,1]");
        }
    }

    private static void ValidateJSymbolic(JSymbolicFeatures features, ICollection<string> errors)
    {
        if (features is null)
        {
            errors.Add("global.pitch.jSymbolic is missing");
            return;
        }
        ValidatePitchDistribution(features.PitchClassDistribution, "global.pitch.jSymbolic", errors);
        if (features.MelodicIntervalHistogram is null || features.MelodicIntervalHistogram.Count == 0)
            errors.Add("global.pitch.jSymbolic.melodicIntervalHistogram is empty");
        foreach (double value in features.MelodicIntervalHistogram ?? Array.Empty<double>())
            ValidateFinite(value, "global.pitch.jSymbolic.melodicIntervalHistogram", errors);
        ValidateRatioOrPositive(features.NoteDensity, "global.pitch.jSymbolic.noteDensity", errors);
        ValidateRatioOrPositive(features.AverageNoteDuration, "global.pitch.jSymbolic.averageNoteDuration", errors);
        ValidateRatioOrPositive(features.PitchVariety, "global.pitch.jSymbolic.pitchVariety", errors);
        ValidateRatioOrPositive(features.PitchClassVariety, "global.pitch.jSymbolic.pitchClassVariety", errors);
        ValidateRatio(features.MostCommonPitchClassPrevalence, "global.pitch.jSymbolic.mostCommonPitchClassPrevalence", errors);
        ValidateFinite(features.RelativeStrengthOfTopPitchClasses, "global.pitch.jSymbolic.relativeStrengthOfTopPitchClasses", errors);
        if (features.RelativeStrengthOfTopPitchClasses < 0)
            errors.Add("global.pitch.jSymbolic.relativeStrengthOfTopPitchClasses is negative");
    }

    private static void ValidateRatioOrPositive(double value, string name, ICollection<string> errors)
    {
        ValidateFinite(value, name, errors);
        if (value < 0)
            errors.Add($"{name} is negative");
    }

    private static void ValidatePitchClass(int value, string name, ICollection<string> errors)
    {
        if (value is < 0 or > 11)
            errors.Add($"{name} contains pitch class outside 0-11");
    }

    private static void ValidateFinite(double value, string name, ICollection<string> errors)
    {
        if (!double.IsFinite(value))
            errors.Add($"{name} is not finite");
    }

    private static void ValidateStatus(string status, string name, ICollection<string> errors)
    {
        if (!string.IsNullOrEmpty(status) && status != "experimental")
            errors.Add($"{name} must be empty or experimental");
    }

    private static void ValidateRatio(double value, string name, ICollection<string> errors)
    {
        ValidateFinite(value, name, errors);
        if (value is < 0 or > 1)
            errors.Add($"{name} is outside [0,1]");
    }
}
