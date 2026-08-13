using System.Text.Json;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Xunit;

namespace Fmp.Application.Tests;

public class SerializerTests
{
    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        VisualizationRequest original = TestRequests.FullyPopulated();
        string json = VisualizationRequestSerializer.Serialize(original);
        VisualizationRequest restored = VisualizationRequestSerializer.Deserialize(json);

        // Lists deserialize as List<T> (reference-based record equality), so
        // assert canonical JSON stability plus targeted field checks.
        Assert.Equal(json, VisualizationRequestSerializer.Serialize(restored));

        Assert.Equal(CompositionKind.Diagnostic, restored.Composition);
        Assert.Equal(RenderQuality.Final, restored.Output.Quality);
        Assert.Equal(60000, restored.Output.FpsNumerator);
        Assert.Equal(1001, restored.Output.FpsDenominator);
        Assert.Equal(VideoEncoder.Nvenc, restored.Output.Encoder);
        Assert.True(restored.Output.Overwrite);

        Assert.Equal(TrackSelectionMode.Custom, restored.Tracks.Selection);
        Assert.Equal(original.Tracks.IncludedIds, restored.Tracks.IncludedIds);
        Assert.Equal(original.Tracks.ExcludedIds, restored.Tracks.ExcludedIds);
        Assert.True(restored.Tracks.IncludeInactiveDiagnosticTracks);

        Assert.Equal(0.4, restored.View.PastSeconds);
        Assert.Equal(1.6, restored.View.FutureSeconds);
        Assert.Equal(TimeGridMode.Analytical, restored.View.TimeGrid);
        Assert.Equal(StructureOverlayMode.Off, restored.View.Structure);
        Assert.Equal(30, restored.View.ScopeFps);

        Assert.Equal(VisualEffects.Cinematic, restored.Style.Effects);
        Assert.Equal(NoteColorMode.Channel, restored.Style.NoteColor);
        Assert.Equal(PaletteKind.Accessible, restored.Style.Palette);
        Assert.Equal(0.5, restored.Style.ScopeOpacity);

        Assert.Equal("My Song", restored.Presentation.Title);
        Assert.Equal("Sub", restored.Presentation.Subtitle);
        Assert.Equal("Cred", restored.Presentation.Credits);
        Assert.Equal("/fonts/noto.ttf", restored.Presentation.FontPath);

        Assert.Equal(4, restored.Playback.LoopCount);
        Assert.Equal(3.0, restored.Playback.FadeSeconds);
        Assert.Equal(1.0, restored.Playback.TailSeconds);
        Assert.Equal(120.0, restored.Playback.MaximumDurationSeconds);
        Assert.Equal(48_000, restored.Playback.SampleRate);
    }

    [Fact]
    public void Json_ContainsSchemaVersion()
    {
        string json = VisualizationRequestSerializer.Serialize(TestRequests.Valid());
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"inputPath\"", json);
        Assert.Contains("\"outputPath\"", json);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        string json = """
            {
              "schemaVersion": 1,
              "inputPath": "/tmp/x.vgz",
              "outputPath": "/tmp/x.visualization/v.mp4",
              "futureFeatureThatDoesNotExistYet": { "a": 1 }
            }
            """;
        VisualizationRequest request = VisualizationRequestSerializer.Deserialize(json);
        Assert.Equal("/tmp/x.vgz", request.InputPath);
        Assert.Equal(1, request.SchemaVersion);
    }

    [Fact]
    public void MissingSchema_DefaultsToOne()
    {
        string json = """
            {
              "inputPath": "/tmp/x.vgz",
              "outputPath": "/tmp/x.visualization/v.mp4"
            }
            """;
        Assert.Equal(1, VisualizationRequestSerializer.Deserialize(json).SchemaVersion);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OlderOrNegativeSchema_IsAccepted(int schemaVersion)
    {
        // The serializer rejects only schemas *newer* than MaxSchemaVersion.
        string json = $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "inputPath": "/tmp/x.vgz",
              "outputPath": "/tmp/x.visualization/v.mp4"
            }
            """;
        VisualizationRequest request = VisualizationRequestSerializer.Deserialize(json);
        Assert.Equal(schemaVersion, request.SchemaVersion);
    }

    [Fact]
    public void NewerSchema_Throws()
    {
        string json = """
            {
              "schemaVersion": 99,
              "inputPath": "/tmp/x.vgz",
              "outputPath": "/tmp/x.visualization/v.mp4"
            }
            """;
        VisualizationRequestException exception = Assert.Throws<VisualizationRequestException>(
            () => VisualizationRequestSerializer.Deserialize(json));
        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MalformedJson_Throws()
    {
        Assert.Throws<VisualizationRequestException>(
            () => VisualizationRequestSerializer.Deserialize("{ not json"));
    }

    [Fact]
    public void WriteAndReadFile_RoundTrips()
    {
        string path = Path.Combine(Path.GetTempPath(), $"req-{Guid.NewGuid():N}.json");
        try
        {
            VisualizationRequest original = TestRequests.FullyPopulated();
            VisualizationRequestSerializer.WriteToFile(original, path);
            VisualizationRequest restored = VisualizationRequestSerializer.ReadFromFile(path);
            Assert.Equal(
                VisualizationRequestSerializer.Serialize(original),
                VisualizationRequestSerializer.Serialize(restored));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EnumJson_UsesStringNames()
    {
        string json = VisualizationRequestSerializer.Serialize(
            TestRequests.Valid() with
            {
                Composition = CompositionKind.Diagnostic,
                Output = new OutputSettings { Encoder = VideoEncoder.Nvenc },
                Tracks = new TrackSettings { Selection = TrackSelectionMode.All },
                View = new ViewSettings { TimeGrid = TimeGridMode.Authoritative },
                Style = new StyleSettings
                {
                    Effects = VisualEffects.Cinematic,
                    NoteColor = NoteColorMode.PitchClass,
                    Palette = PaletteKind.Monochrome,
                },
            });
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("Diagnostic", document.RootElement.GetProperty("composition").GetString());
        Assert.Equal("Nvenc", document.RootElement.GetProperty("output").GetProperty("encoder").GetString());
        Assert.Equal("All", document.RootElement.GetProperty("tracks").GetProperty("selection").GetString());
        Assert.Equal("Authoritative", document.RootElement.GetProperty("view").GetProperty("timeGrid").GetString());
        Assert.Equal("Cinematic", document.RootElement.GetProperty("style").GetProperty("effects").GetString());
        Assert.Equal("PitchClass", document.RootElement.GetProperty("style").GetProperty("noteColor").GetString());
        Assert.Equal("Monochrome", document.RootElement.GetProperty("style").GetProperty("palette").GetString());
    }
}
