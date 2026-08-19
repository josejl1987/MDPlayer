using Fmp.Application.Export;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using NoteEvent = Fmp.Core.Visualization.NoteEvent;
using System.Text.Json;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Verifies the public GUI-facing <see cref="MidiExportService"/> (Application
/// layer) which turns a persisted timeline into Format 1 MIDI bytes without
/// shelling to the CLI. The contract is raw fidelity: a fixed 120 BPM transport,
/// no tempo/meter/quantization/voice-projection options, and a report that
/// surfaces source-relative timing verbatim.
/// </summary>
public sealed class MidiExportServiceTests
{
    private const int Sr = 44_100;

    [Fact]
    public void ExportFromTimelinePath_WritesValidSmf()
    {
        VisualizationTimeline timeline = BuildTimeline();
        string timelinePath = WriteTimeline(timeline);

        var service = new MidiExportService();
        MidiExportResult result = service.ExportFromTimelinePath(timelinePath, new MidiExportRequest
        {
            Ppq = 960,
        });

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Bytes);
        Assert.True(result.Bytes.Length >= 14, "SMF header + at least one chunk");
        Assert.Equal((byte)'M', result.Bytes[0]);
        Assert.Equal((byte)'T', result.Bytes[1]);
        Assert.Equal((byte)'h', result.Bytes[2]);
        Assert.Equal((byte)'d', result.Bytes[3]);
        Assert.Equal(0, result.Bytes[8]);
        Assert.Equal(1, result.Bytes[9]);
        Assert.Contains(result.Report, line => line.Contains("midi-mode: raw-fidelity"));
        Assert.Contains(result.Report, line => line.Contains("transport: 120 BPM"));
    }

    [Fact]
    public void RequestContract_ContainsOnlyRawTranscriptionOptions()
    {
        // The musical vocabulary is not part of the contract. Anything beyond
        // PPQ + receipt hooks must be a compile error, enforced here by
        // reflection so a reintroduced option fails this test.
        string[] allowed = { "Ppq", "EnablePerformanceReceipts", "PerformanceFixture" };
        string[] actual = typeof(MidiExportRequest)
            .GetProperties()
            .Select(p => p.Name)
            .OrderBy(n => n)
            .ToArray();
        Assert.Equal(allowed.OrderBy(n => n), actual);
    }

    [Fact]
    public void PerformanceReceipts_EnabledEmitCompleteJsonAndHumanSummary()
    {
        string path = WriteTimeline(BuildTimeline());
        MidiExportResult result = new MidiExportService().ExportFromTimelinePath(path,
            new MidiExportRequest { EnablePerformanceReceipts = true, PerformanceFixture = "pinned-midi-fixture" });

        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Performance);
        using JsonDocument json = JsonDocument.Parse(result.Performance!.ToJson());
        Assert.Equal("pinned-midi-fixture", json.RootElement.GetProperty("Fixture").GetString());
        Assert.True(json.RootElement.TryGetProperty("Input", out _));
        Assert.True(json.RootElement.TryGetProperty("Configuration", out _));
        Assert.True(json.RootElement.TryGetProperty("Environment", out _));
        Assert.Equal("not-run", json.RootElement.GetProperty("Comparison").GetProperty("Status").GetString());
        Assert.Contains(result.Report, line => line.Contains("comparison-status=not-run"));
        Assert.Contains(result.Performance.ToHumanReadable(), line => line.Contains("raw-midi-transcription"));
    }

    [Fact]
    public void PerformanceReceipts_DisabledPreserveOutputEquivalence()
    {
        string path = WriteTimeline(BuildTimeline());
        var service = new MidiExportService();
        MidiExportResult disabled = service.ExportFromTimelinePath(path, new MidiExportRequest());
        MidiExportResult enabled = service.ExportFromTimelinePath(path,
            new MidiExportRequest { EnablePerformanceReceipts = true });

        Assert.True(disabled.Succeeded, disabled.Error);
        Assert.True(enabled.Succeeded, enabled.Error);
        Assert.Equal(disabled.Bytes, enabled.Bytes);
        Assert.Null(disabled.Performance);
    }

    [Fact]
    public void ExportFromTimelinePath_MissingFile_FailsGracefully()
    {
        var service = new MidiExportService();
        MidiExportResult result =
            service.ExportFromTimelinePath(Path.Combine(Path.GetTempPath(), "no-such-lease", "timeline.json"),
                new MidiExportRequest());

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrEmpty(result.Error));
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void BeatMetadata_DoesNotChangeRawMidiBytes()
    {
        // Raw fidelity is independent of the driver beat grid: tempo source and
        // phase inference are not part of this export. With or without beats the
        // bytes must be identical.
        string withBeats = WriteTimeline(BuildTimeline(withBeats: true));
        string withoutBeats = WriteTimeline(BuildTimeline(withBeats: false));

        var service = new MidiExportService();
        MidiExportResult a = service.ExportFromTimelinePath(withBeats, new MidiExportRequest());
        MidiExportResult b = service.ExportFromTimelinePath(withoutBeats, new MidiExportRequest());

        Assert.True(a.Succeeded, a.Error);
        Assert.True(b.Succeeded, b.Error);
        Assert.Equal(a.Bytes, b.Bytes);
    }

    [Fact]
    public void Export_RejectsInvalidPpq()
    {
        string path = WriteTimeline(BuildTimeline());
        MidiExportResult result = new MidiExportService().ExportFromTimelinePath(path,
            new MidiExportRequest { Ppq = 0 });

        Assert.False(result.Succeeded);
        Assert.Contains("PPQ", result.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(result.Bytes);
    }

    [Fact]
    public void Export_RejectsZeroSampleRate_WithActionableMessage()
    {
        // A timeline that lost its SampleRate must be rejected with the
        // transcriber's actionable message on the result — never a generic
        // JSON/argument error.
        string path = Path.Combine(Path.GetTempPath(), "mdplayer-midi-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        {"schemaVersion":2,"sampleRate":0,"startSample":0,"endSample":1000,
         "notes":[{"voiceId":"v","startSample":0,"endSample":100,"initialFrequencyHz":440,
                   "initialMidiNote":60,"instrumentId":"i","mode":0,"isRetrigger":false,"pitch":[]}]}
        """);
        try
        {
            MidiExportResult result = new MidiExportService().ExportFromTimelinePath(path, new MidiExportRequest());

            Assert.False(result.Succeeded);
            Assert.Contains("sample rate must be positive", result.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Null(result.Bytes);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void ExportFromTimelinePath_PreservesSerializedStartRangeAsRawOrigin()
    {
        // Raw origin is source-relative: the serialized [StartSample, EndSample]
        // range is preserved in the report, and note ticks are relative to the
        // serialized start — the origin is not reset to sample zero.
        var timeline = new VisualizationTimeline
        {
            SampleRate = Sr,
            StartSample = 5_000,
            EndSample = 30_000,
            Notes = new[]
            {
                new NoteEvent("v", 5_000, 27_050, 440, 60, "i", VisualizationNoteMode.Fm, false, []),
            },
        };
        string timelinePath = WriteTimeline(timeline);

        MidiExportResult result = new MidiExportService().ExportFromTimelinePath(timelinePath, new MidiExportRequest());

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains(result.Report, line => line.Contains("song: 5000-30000 samples @ 44100 Hz"));
        var ticks = MidiRoundTrip.TimedEvents(result.Bytes!, 1)
            .Where(pair => pair.Event is NoteOnEvent or NoteOffEvent)
            .Select(pair => pair.Tick)
            .ToArray();
        // 5000 samples = origin tick 0; 22050 more samples = one quarter @ 120 BPM = 960 ticks.
        Assert.Equal(new[] { 0L, 960L }, ticks);
    }

    /* ---- helpers ---- */

    private static VisualizationTimeline BuildTimeline(bool withBeats = true)
    {
        double spq = Sr * 60.0 / 120.0;
        var notes = Enumerable.Range(0, 8)
            .Select(i => new NoteEvent(
                ChannelId: "v",
                StartSample: (long)Math.Round(i * 4 * spq),
                EndSample: (long)Math.Round(i * 4 * spq) + 2000,
                InitialFrequencyHz: 440,
                InitialMidiNote: 60 + i,
                InstrumentId: "inst",
                Mode: VisualizationNoteMode.Fm,
                IsRetrigger: false,
                Pitch: Array.Empty<PitchChange>()))
            .ToArray();
        return new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = (long)Math.Round(8 * 4 * spq) + 20_000,
            SampleRate = Sr,
            Notes = notes,
            Beats = withBeats
                ? Enumerable.Range(0, 40)
                    .Select(i => new BeatEvent((long)Math.Round(i * spq), i))
                    .ToArray()
                : Array.Empty<BeatEvent>(),
            Source = new TrackMetadata("vgz", "song", "chip", "song.vgz"),
        };
    }

    private static string WriteTimeline(VisualizationTimeline timeline)
    {
        string path = Path.Combine(Path.GetTempPath(), "mdplayer-midi-" + Guid.NewGuid().ToString("N") + ".json");
        VisualizationJsonWriter.Write(path, timeline);
        return path;
    }
}