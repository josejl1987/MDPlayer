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
        Assert.Equal(original.Tools.CorrscopePath, restored.Tools.CorrscopePath);
        Assert.Equal(original.Tools.AnalysisForce, restored.Tools.AnalysisForce);
        Assert.Equal(original.IncludedTrackIds, restored.IncludedTrackIds);
        Assert.Equal(original.ExcludedTrackIds, restored.ExcludedTrackIds);
        Assert.Equal(60000, restored.FpsNumerator);
        Assert.Equal(1001, restored.FpsDenominator);
        Assert.Equal(VisualizationLayout.Hybrid, restored.Layout);
        Assert.Equal(VisualizationEffects.Cinematic, restored.Effects);
        Assert.Equal("My Song", restored.Title);
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
            TestRequests.Valid() with { Layout = VisualizationLayout.Hybrid, Encoder = VideoEncoder.Nvenc });
        using JsonDocument document = JsonDocument.Parse(json);
        Assert.Equal("Hybrid", document.RootElement.GetProperty("layout").GetString());
        Assert.Equal("Nvenc", document.RootElement.GetProperty("encoder").GetString());
    }
}
