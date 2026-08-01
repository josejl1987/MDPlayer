using Fmp.Core.Visualization;

namespace Fmp.Core.Analysis;

internal sealed record StructuralPitchRegion(
    long StartSample,
    long EndSample,
    double MidiPitch,
    double Stability);

internal sealed record StructuralPitchResult(
    double PrincipalPitch,
    IReadOnlyList<StructuralPitchRegion> Regions,
    bool Microtonal,
    bool Gliding,
    int PitchClass);

/// <summary>
/// Converts display pitch curves into a small number of stable structural
/// regions. The renderer keeps the original curve; analysis consumes this
/// intentionally conservative representation.
/// </summary>
internal static class StructuralPitchExtractor
{
    internal const double StableToleranceCents = 25;
    internal const double VibratoToleranceCents = 35;
    internal const int StableMinimumMs = 80;
    internal const int DestinationMinimumMs = 120;
    internal const double StructuralMoveCents = 70;

    public static StructuralPitchResult Extract(NoteEvent note, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(note);
        if (sampleRate <= 0 || note.EndSample <= note.StartSample)
            return Empty(note.InitialMidiNote);
        if (!double.IsFinite(note.InitialMidiNote) || note.InitialMidiNote < 0)
            return Empty(-1);

        IReadOnlyList<PitchChange> pitchChanges = note.Pitch ?? Array.Empty<PitchChange>();
        var points = new List<(long Sample, double Midi, int Order)>(pitchChanges.Count + 2)
        {
            (note.StartSample, note.InitialMidiNote, -1),
        };
        foreach ((PitchChange point, int order) in pitchChanges.Select((point, order) => (point, order)))
        {
            if (point.SamplePosition <= note.StartSample || point.SamplePosition >= note.EndSample
                || !double.IsFinite(point.MidiNote) || point.MidiNote < 0)
                continue;
            points.Add((point.SamplePosition, point.MidiNote, order));
        }
        points = points
            .OrderBy(point => point.Sample)
            .ThenBy(point => point.Order)
            .GroupBy(point => point.Sample)
            .Select(group => group.Last())
            .ToList();
        points.Add((note.EndSample, points[^1].Midi, int.MaxValue));
        bool continuousGlide = IsContinuousGlide(points);

        var regions = new List<StructuralPitchRegion>();
        long regionStart = points[0].Sample;
        double anchor = points[0].Midi;
        double deviationSum = 0;
        long deviationSamples = 0;
        int unstableMoves = 0;
        for (int index = 0; index < points.Count - 1; index++)
        {
            long start = points[index].Sample;
            long end = points[index + 1].Sample;
            double pitch = points[index].Midi;
            if (end <= start || !double.IsFinite(pitch) || pitch < 0)
                continue;

            double movement = Math.Abs((pitch - anchor) * 100);
            bool stableDestination = !continuousGlide
                && movement >= StructuralMoveCents
                && HasStableDestination(points, index, sampleRate);
            if (movement >= StructuralMoveCents && !stableDestination)
                unstableMoves++;
            if (stableDestination)
            {
                AddRegion(regions, regionStart, start, anchor, deviationSum, deviationSamples, sampleRate);
                regionStart = start;
                anchor = pitch;
                deviationSum = 0;
                deviationSamples = 0;
            }

            deviationSum += Math.Abs(pitch - anchor) * (end - start);
            deviationSamples += end - start;
        }

        AddRegion(regions, regionStart, note.EndSample, anchor, deviationSum, deviationSamples, sampleRate);
        if (regions.Count == 0)
        {
            double fallback = WeightedMedian(points, note.StartSample, note.EndSample);
            return Quantize(fallback, Array.Empty<StructuralPitchRegion>(), unstableMoves >= 2);
        }

        StructuralPitchRegion longest = regions
            .OrderByDescending(region => region.EndSample - region.StartSample)
            .ThenBy(region => region.StartSample)
            .First();
        long longestDuration = longest.EndSample - longest.StartSample;
        StructuralPitchRegion principal = regions
            .Where(region => region.EndSample - region.StartSample >= longestDuration * 0.90)
            .OrderBy(region => region.StartSample)
            .First();
        double selected = principal.MidiPitch;

        return Quantize(selected, regions, unstableMoves >= 2);
    }

    private static bool IsContinuousGlide(
        IReadOnlyList<(long Sample, double Midi, int Order)> points)
    {
        int direction = 0;
        int consecutiveMoves = 0;
        for (int index = 1; index < points.Count - 1; index++)
        {
            double delta = points[index].Midi - points[index - 1].Midi;
            if (Math.Abs(delta * 100) < StructuralMoveCents)
                continue;
            int currentDirection = Math.Sign(delta);
            if (currentDirection == direction)
                consecutiveMoves++;
            else
            {
                direction = currentDirection;
                consecutiveMoves = 1;
            }
            if (consecutiveMoves >= 3)
                return true;
        }
        return false;
    }

    private static bool HasStableDestination(
        IReadOnlyList<(long Sample, double Midi, int Order)> points,
        int index,
        int sampleRate)
    {
        double destination = points[index].Midi;
        long required = Math.Max(1, (long)Math.Round(sampleRate * DestinationMinimumMs / 1000.0));
        long stable = 0;
        for (int candidate = index; candidate < points.Count - 1; candidate++)
        {
            long length = points[candidate + 1].Sample - points[candidate].Sample;
            if (Math.Abs((points[candidate].Midi - destination) * 100) <= StableToleranceCents)
                stable += Math.Max(0, length);
            else
                stable = 0;
            if (stable >= required)
                return true;
        }
        return false;
    }

    private static void AddRegion(
        ICollection<StructuralPitchRegion> regions,
        long start,
        long end,
        double anchor,
        double deviationSum,
        long deviationSamples,
        int sampleRate)
    {
        long minimum = Math.Max(1, (long)Math.Round(sampleRate * StableMinimumMs / 1000.0));
        if (end - start < minimum || !double.IsFinite(anchor) || anchor < 0)
            return;
        double averageDeviation = deviationSamples > 0 ? deviationSum / deviationSamples : 0;
        double stability = Math.Clamp(1 - averageDeviation / (VibratoToleranceCents / 100), 0, 1);
        regions.Add(new StructuralPitchRegion(start, end, anchor, stability));
    }

    private static double WeightedMedian(
        IReadOnlyList<(long Sample, double Midi, int Order)> points,
        long start,
        long end)
    {
        var weighted = new List<(double Midi, long Weight)>();
        for (int index = 0; index < points.Count - 1; index++)
        {
            long length = Math.Max(0, Math.Min(end, points[index + 1].Sample)
                - Math.Max(start, points[index].Sample));
            if (length > 0 && double.IsFinite(points[index].Midi) && points[index].Midi >= 0)
                weighted.Add((points[index].Midi, length));
        }
        if (weighted.Count == 0)
            return 0;
        long target = Math.Max(1, weighted.Sum(value => value.Weight) / 2);
        long total = 0;
        foreach (var value in weighted.OrderBy(value => value.Midi))
        {
            total += value.Weight;
            if (total >= target)
                return value.Midi;
        }
        return weighted[^1].Midi;
    }

    private static StructuralPitchResult Quantize(
        double pitch,
        IReadOnlyList<StructuralPitchRegion> regions,
        bool gliding)
    {
        if (!double.IsFinite(pitch) || pitch < 0)
            return new StructuralPitchResult(-1, regions, false, gliding, -1);
        double nearest = Math.Round(pitch, MidpointRounding.AwayFromZero);
        bool microtonal = Math.Abs(pitch - nearest) * 100 > VibratoToleranceCents;
        int pitchClass = microtonal ? -1 : ((int)nearest % 12 + 12) % 12;
        return new StructuralPitchResult(pitch, regions, microtonal, gliding, pitchClass);
    }

    private static StructuralPitchResult Empty(double pitch)
        => Quantize(pitch, Array.Empty<StructuralPitchRegion>(), false);
}
