using Fmp.Core.Visualization;
using Xunit;

namespace MDPlayer.Fmp.Tests;

public sealed class DacSampleExporterTests
{
    private const int Src = 5;

    [Fact]
    public void Export_DeduplicatesToOneWavPerAsset()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            byte[] a = [0x10, 0x20, 0x30];
            Play(ops, 10, a);
            Play(ops, 40, a);            // same payload -> one asset
            Play(ops, 70, [0xAA, 0xBB]); // different payload -> second asset
        });

        DacSampleExporterResult result = new DacSampleExporter().Export(report);

        Assert.Equal(2, result.Assets.Count);           // deduplicated
        Assert.Equal("dac_s000.wav", result.Assets[0].FileName);
        Assert.Equal("dac_s001.wav", result.Assets[1].FileName);
        Assert.Equal(2, report.Assets[0].TriggerCount); // two triggers, one file
    }

    [Fact]
    public void Export_ProducesValidWavHeader()
    {
        DacAnalysisReport report = MakeReport(ops => Play(ops, 10, [0x11, 0x22, 0x33]));
        DacSampleExporterResult result = new DacSampleExporter().Export(report);

        DacSampleExportFile file = Assert.Single(result.Assets);
        byte[] wav = file.WavBytes;
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal("fmt ", System.Text.Encoding.ASCII.GetString(wav, 12, 4));
        Assert.Equal("data", System.Text.Encoding.ASCII.GetString(wav, 36, 4));
        // 44-byte header + 3 payload bytes.
        Assert.Equal(44 + 3, wav.Length);
    }

    [Fact]
    public void Export_ManifestContainsAssetMetadata()
    {
        DacAnalysisReport report = MakeReport(ops =>
        {
            Play(ops, 10, [0x10, 0x20]);
            Play(ops, 40, [0xAA, 0xBB]);
        });
        DacSampleExporterResult result = new DacSampleExporter().Export(report);

        string json = System.Text.Encoding.UTF8.GetString(result.ManifestUtf8);
        Assert.Contains("dac_s000.wav", json);
        Assert.Contains("dac_s001.wav", json);
        Assert.Contains("\"encoding\": \"pcm\"", json);
        Assert.Contains("\"bitsPerSample\": 8", json);
    }

    private static DacAnalysisReport MakeReport(Action<List<DacOperation>> build)
    {
        var ops = new List<DacOperation>();
        build(ops);
        var tracker = new DacPlaybackTracker();
        foreach (DacOperation op in ops)
            tracker.Add(op);
        tracker.Complete(20_000);
        return DacAnalysisReport.From(tracker);
    }

    private static void Play(List<DacOperation> ops, long start, byte[] payload)
    {
        ops.Add(new DacOperation.DacPlaybackStarted(start, Src, 0, null, null));
        for (int i = 0; i < payload.Length; i++)
            ops.Add(new DacOperation.DacByteConsumed(start + i, Src, i, payload[i]));
        ops.Add(new DacOperation.DacPlaybackStopped(start + 10, DacStopReason.ExplicitStop));
    }
}