using System.Text.Json;
using Fmp.Core.Timing;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Differential fuzz contract against the librosa 0.11 beat tracker.
///
/// The corpus (tests/Corpus/midi/librosa-differential-fuzz.json) was generated
/// offline by tests/Corpus/midi/generate-librosa-fuzz.py from the fixed seed
/// recorded inside the file: 10,000 pseudo-random sparse onset envelopes, each
/// tracked by librosa.beat.beat_track with a FIXED bpm (tightness 100). This
/// test replays every envelope through
/// <see cref="EllisBeatTracker.TrackFixedTempo"/> and requires ZERO beat-frame
/// mismatches. No Python is needed at test runtime.
/// </summary>
public sealed class EllisLibrosaDifferentialFuzzTests
{
    private const int SampleRate = 100;
    private const int HopSamples = 1;

    [Fact]
    public void FixedTempoTracker_MatchesLibrosaOnEveryFuzzCase()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi",
            "librosa-differential-fuzz.json");
        Assert.True(File.Exists(path), $"Differential fuzz corpus is missing: {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        Assert.Contains("librosa.beat.beat_track", root
            .GetProperty("reference").GetString(), StringComparison.Ordinal);
        // The seed is part of the contract: regenerating with a different seed
        // must be a deliberate act that updates this file.
        Assert.Equal(20260825, root.GetProperty("seed").GetInt32());
        Assert.Equal(10_000, root.GetProperty("caseCount").GetInt32());

        var cases = new List<(int FrameCount, double Bpm, bool Trim,
            (int Frame, double Strength)[] Onsets, int[] Expected)>();
        foreach (JsonElement item in root.GetProperty("cases").EnumerateArray())
        {
            int frameCount = item.GetProperty("frameCount").GetInt32();
            var onsets = item.GetProperty("onsets").EnumerateArray()
                .Select(onset => (onset[0].GetInt32(), onset[1].GetDouble()))
                .ToArray();
            int[] expected = item.GetProperty("expectedFrames").EnumerateArray()
                .Select(value => value.GetInt32()).ToArray();
            cases.Add((frameCount, item.GetProperty("bpm").GetDouble(),
                item.GetProperty("trim").GetBoolean(), onsets, expected));
        }

        var mismatches = new List<string>();
        Parallel.For(0, cases.Count, index =>
        {
            (int frameCount, double bpm, bool trim, var onsets, int[] expected) =
                cases[index];
            var envelope = new double[frameCount];
            foreach ((int frame, double strength) in onsets)
                envelope[frame] = strength;

            EllisReferenceBeatPath result = EllisBeatTracker.TrackFixedTempo(
                envelope, bpm, SampleRate, HopSamples, trim);
            int[] actual = result.Frames.ToArray();
            if (!expected.SequenceEqual(actual))
            {
                lock (mismatches)
                {
                    mismatches.Add(
                        $"case {index} (frames={frameCount}, bpm={bpm}, "
                        + $"trim={trim}): librosa=[{string.Join(", ", expected)}] "
                        + $"actual=[{string.Join(", ", actual)}]");
                }
            }
        });

        // The differential harness contract: ZERO beat-frame mismatches out of
        // 10,000 cases.
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} of {cases.Count} fuzz cases mismatched:\n"
            + string.Join("\n", mismatches.Take(10)));
    }
}
