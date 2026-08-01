using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Review;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MDPlayer.Fmp.Application.Tests;

public sealed class ReviewTests
{
    [Fact]
    public void ManifestReader_AcceptsNumericAndNamedMoments()
    {
        string path = WriteTemp("{\"files\":[{\"path\":\"spc/song.spc\",\"moments\":[8.0,{\"name\":\"dense\",\"time\":31.2}]}]}");
        try
        {
            ReviewManifest manifest = ReviewManifestReader.Read(path);
            Assert.Equal("moment-1", manifest.Files[0].Moments[0].Name);
            Assert.Equal(8.0, manifest.Files[0].Moments[0].TimeSeconds);
            Assert.Equal("dense", manifest.Files[0].Moments[1].Name);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ManifestReader_RejectsMissingPathDuplicateNamesAndNegativeTime()
    {
        string[] documents =
        [
            "{\"files\":[{\"moments\":[]}]}",
            "{\"files\":[{\"path\":\"a.spc\",\"moments\":[{\"name\":\"x\",\"time\":1},{\"name\":\"x\",\"time\":2}]}]}",
            "{\"files\":[{\"path\":\"a.spc\",\"moments\":[{\"name\":\"x\",\"time\":-1}]}]}",
        ];
        foreach (string document in documents)
        {
            string path = WriteTemp(document);
            try { Assert.Throws<ReviewManifestException>(() => ReviewManifestReader.Read(path)); }
            finally { File.Delete(path); }
        }
    }

    [Fact]
    public void ManifestReader_AcceptsAnEntryWithoutMoments()
    {
        string path = WriteTemp("{\"files\":[{\"path\":\"spc/song.spc\",\"moments\":[]}]}");
        try
        {
            ReviewManifest manifest = ReviewManifestReader.Read(path);
            Assert.Empty(manifest.Files[0].Moments);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CaseNaming_NormalizesUnicodeAndCompositionNames()
    {
        Assert.Equal("song-2", ReviewGenerator.Slug("Song (2)"));
        Assert.Equal("café-été", ReviewGenerator.Slug("Café_été"));
        Assert.Equal("diagnostic", ReviewGenerator.CompositionName(CompositionKind.Diagnostic));
    }

    [Fact]
    public void HtmlWriter_EmitsRenderedUnavailableFailedCardsAndEscapesMetadata()
    {
        string output = Directory.CreateTempSubdirectory("mdplayer-review-").FullName;
        var moment = new ReviewMoment { Name = "dense", TimeSeconds = 3.5 };
        var entry = new ReviewFileEntry
        {
            Path = "spc/<song>.spc",
            Label = "<unsafe>",
            Tags = ["fm"],
            Moments = [moment],
        };
        var source = new ReviewSourceResult
        {
            Id = "song",
            Entry = entry,
            ResolvedPath = "/missing/song.spc",
            DetectedChips = ["ym2608"],
            ActiveChips = ["ym2608"],
            Cases =
            [
                Case("song", moment, ReviewCaseStatus.Rendered, "files/song/dense/performance-720p.png"),
                Case("song", moment, ReviewCaseStatus.Unavailable, null),
                Case("song", moment, ReviewCaseStatus.Failed, null),
            ],
        };
        var result = new ReviewResult
        {
            OutputPath = output,
            Sources = [source],
            DetectedChips = ["ym2608"],
            CoveredChips = ["ym2608"],
        };

        string index = ReviewHtmlWriter.Write(result);
        string html = File.ReadAllText(index);
        Assert.Contains("Rendered", html);
        Assert.Contains("Unavailable", html);
        Assert.Contains("Failed", html);
        Assert.Contains("data-chip=\"ym2608\"", html);
        Assert.Contains("&lt;unsafe&gt;", html);
        Assert.DoesNotContain("<h2><unsafe>", html);
    }

    [Fact]
    public void ContactSheetWriter_SplitsRowsAndShowsUnavailableTiles()
    {
        string output = Directory.CreateTempSubdirectory("mdplayer-review-").FullName;
        var sources = Enumerable.Range(0, 13).Select(index => new ReviewSourceResult
        {
            Id = $"song-{index}",
            Entry = new ReviewFileEntry
            {
                Path = $"song-{index}.spc",
                Moments = [new ReviewMoment { Name = "representative", TimeSeconds = 1 }],
            },
            ResolvedPath = $"/song-{index}.spc",
            Cases = [Case($"song-{index}", new ReviewMoment { Name = "representative", TimeSeconds = 1 }, ReviewCaseStatus.Unavailable, null)],
        }).ToArray();
        var result = new ReviewResult { OutputPath = output, Sources = sources };

        ContactSheetWriter.WriteAll(result);

        Assert.True(File.Exists(Path.Combine(output, "contact-sheets", "overview-720p-1.png")));
        Assert.True(File.Exists(Path.Combine(output, "contact-sheets", "overview-720p-2.png")));
        using Image<Rgba32> image = Image.Load<Rgba32>(
            Path.Combine(output, "contact-sheets", "overview-720p-2.png"));
        // One composition remains, so each overview row is a single tile.
        Assert.Equal(480, image.Width);
        Assert.Equal(328, image.Height);
    }

    [Fact]
    public void ReviewDirectory_DoesNotReferenceSyntheticReviewInputs()
    {
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/MDPlayer.Fmp.Application/Review"));
        string[] forbidden =
        ["VisualizationTimelineFixture", "TimelineBuilder", "FakePlaybackBackend", "MockPlaybackBackend", "SyntheticAudioGenerator"];
        foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            string text = File.ReadAllText(file);
            foreach (string token in forbidden)
                Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
    }

    private static ReviewCaseResult Case(
        string sourceId,
        ReviewMoment moment,
        ReviewCaseStatus status,
        string? imagePath)
        => new()
        {
            SourceId = sourceId,
            SourceName = sourceId,
            Moment = moment,
            Composition = CompositionKind.Diagnostic,
            Resolution = ReviewResolution.P720,
            Status = status,
            ImagePath = imagePath,
            Reason = status == ReviewCaseStatus.Unavailable ? "No compatible tracks." : null,
        };

    private static string WriteTemp(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"review-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        return path;
    }
}
