using System.Text.Json;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class MidiAdversarialCorpusTests
{
    private static readonly IReadOnlyDictionary<string, string> FixtureLinks =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["21 Master Ninja.vgz"] = "master-ninja.vgz",
            ["120 Smash Up.spc"] = "smash-up.spc",
            ["02 Stranger ~ Wandering Swordsman.vgz"] = "corpus/02-stranger.vgz",
            ["05 - Twilight Express.vgz"] = "corpus/05-twilight.vgz",
            ["18 U.S.A. (Ken) I.vgz"] = "corpus/18-usa-ken.vgz",
            ["20 Ninja Yashiki ~ Their Secrets Die With Them.vgz"] = "corpus/20-ninja-yashiki.vgz",
            ["28 - Smoking Head.vgz"] = "corpus/28-smoking-head.vgz",
            ["26 - Robotnik.vgz"] = "robotnik-dac.vgz",
            ["32 Arctic Wind.vgz"] = "corpus/32-arctic-wind.vgz",
        };

    [Fact]
    public void Manifest_ContainsRealFixturesAndReviewableSemanticSidecars()
    {
        string path = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "midi", "adversarial-manifest.json");
        Assert.True(File.Exists(path), $"MIDI adversarial manifest is missing: {path}");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement entries = document.RootElement.GetProperty("entries");
        Assert.Equal(FixtureLinks.Count, entries.GetArrayLength());

        foreach (JsonElement entry in entries.EnumerateArray())
        {
            string source = entry.GetProperty("source").GetString()!;
            Assert.True(FixtureLinks.TryGetValue(source, out string? linked), source);
            string fixture = Path.Combine(
                AppContext.BaseDirectory, "testfixtures", linked.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(fixture), $"Corpus fixture is missing: {source} -> {fixture}");

            string status = entry.GetProperty("reviewStatus").GetString()!;
            JsonElement expected = entry.GetProperty("expected");
            if (status == "reviewed")
            {
                AssertReviewedField(entry, expected, source, "tempo", "allowUnresolvedTempo");
                AssertReviewedField(entry, expected, source, "meter", "allowUnresolvedMeter");
                AssertReviewedField(entry, expected, source, "downbeat", "allowUnresolvedDownbeat");
            }
            else if (status == "unresolved")
            {
                Assert.True(
                    entry.TryGetProperty("reviewNotes", out JsonElement notes)
                    && notes.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(notes.GetString()),
                    $"Unresolved fixture {source} must record the evidence for abstention.");
                Assert.True(entry.GetProperty("allowUnresolvedTempo").GetBoolean(),
                    $"Unresolved fixture {source} must explicitly allow unresolved tempo.");
                Assert.True(entry.GetProperty("allowUnresolvedMeter").GetBoolean(),
                    $"Unresolved fixture {source} must explicitly allow unresolved meter.");
                Assert.True(entry.GetProperty("allowUnresolvedDownbeat").GetBoolean(),
                    $"Unresolved fixture {source} must explicitly allow unresolved downbeat.");
            }
            else
            {
                Assert.Equal("pending", status);
            }
        }
    }

    private static void AssertReviewedField(
        JsonElement entry,
        JsonElement expected,
        string source,
        string field,
        string unresolvedFlag)
    {
        if (expected.GetProperty(field).ValueKind == JsonValueKind.Null)
        {
            Assert.True(entry.GetProperty(unresolvedFlag).GetBoolean(),
                $"Reviewed fixture {source} has unresolved {field} without {unresolvedFlag}=true.");
        }
    }
}
