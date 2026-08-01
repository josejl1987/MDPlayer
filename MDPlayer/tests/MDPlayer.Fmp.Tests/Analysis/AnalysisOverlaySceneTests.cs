using Fmp.Core.Analysis;
using Fmp.Core.Visualization;
using MDPlayer.Fmp.Tests.Fixtures;
using Xunit;

namespace MDPlayer.Fmp.Tests.Analysis;

public sealed class AnalysisOverlaySceneTests
{
    [Fact]
    public void MinimalDisplaysOnlyAStrongGlobalKey()
    {
        AnalysisOverlayScene scene = AnalysisOverlaySceneBuilder.Build(
            Output("C", "major", 0.8, "strong"),
            VisualizationTimelineFixture.Create(),
            AnalysisOverlayMode.Minimal);

        Assert.Equal("KEY C major", scene.KeyLabel);
        Assert.Empty(scene.Harmony);
        Assert.Empty(scene.Sections);
        Assert.Empty(scene.MotifMarkers);
        Assert.Empty(scene.Relationships);
    }

    [Fact]
    public void StandardMayDisplayAnExplicitlyPermittedTentativeKeyButNoOtherCategory()
    {
        AnalysisOutput output = Output("C", "major", 0.6, "tentative", [new HarmonySegment
        {
            StartSample = 500,
            EndSample = 1_500,
            Symbol = "false-positive",
            Confidence = new AnalysisConfidence { Score = 0.99, Certainty = "strong" },
        }]);

        AnalysisOverlayScene scene = AnalysisOverlaySceneBuilder.Build(
            output,
            VisualizationTimelineFixture.Create(),
            AnalysisOverlayMode.Standard);

        Assert.Equal("KEY C major", scene.KeyLabel);
        Assert.Empty(scene.Harmony);
    }

    [Fact]
    public void NoneAndWithheldKeyProduceAnEmptyScene()
    {
        VisualizationTimeline timeline = VisualizationTimelineFixture.Create();
        Assert.Same(
            AnalysisOverlayScene.Empty,
            AnalysisOverlaySceneBuilder.Build(Output("C", "major", 0.8, "strong"), timeline, AnalysisOverlayMode.None));
        Assert.Same(
            AnalysisOverlayScene.Empty,
            AnalysisOverlaySceneBuilder.Build(Output("C", "major", 0.3, "withheld"), timeline, AnalysisOverlayMode.Standard));
    }

    private static AnalysisOutput Output(
        string tonic,
        string mode,
        double score,
        string certainty,
        IReadOnlyList<HarmonySegment> harmony = null)
        => new()
        {
            Global = new AnalysisGlobal
            {
                Key = new KeyInterpretation
                {
                    Primary = new KeyCandidate { TonicPitchClass = 0, Tonic = tonic, Mode = mode },
                    Confidence = new AnalysisConfidence { Score = score, Certainty = certainty },
                },
            },
            Harmony = harmony ?? Array.Empty<HarmonySegment>(),
        };
}
