using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class VisualizationJsonWriterTests
{
    [Fact]
    public void Serialize_IsDeterministicAndUsesStringEnums()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = 44_100,
            EndSample = 100,
            Notes =
            [
                new NoteEvent(
                    "ym2608.0.fm.1",
                    10,
                    90,
                    440,
                    69,
                    "ym2608:abc",
                    VisualizationNoteMode.Fm,
                    false,
                    Array.Empty<PitchChange>()),
            ],
        };

        string first = VisualizationJsonWriter.Serialize(timeline);
        string second = VisualizationJsonWriter.Serialize(timeline);

        Assert.Equal(first, second);
        Assert.Contains("\"mode\": \"fm\"", first);
        Assert.Contains("\"sampleRate\": 44100", first);
        Assert.Contains("\"voiceId\": \"ym2608.0.fm.1\"", first);
        Assert.DoesNotContain("\"channelId\"", first);
    }
}
