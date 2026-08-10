using System.Text.RegularExpressions;
using Fmp.Core;
using Fmp.Core.Midi;
using Fmp.Core.Timing;
using Fmp.Core.Visualization;
using Melanchall.DryWetMidi.Core;
using VisualizationNoteEvent = Fmp.Core.Visualization.NoteEvent;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Patch 4 build provenance (FR-15, SC-17): exported MIDI metadata carries
/// mdplayer-version and, when the build resolved a git SHA, git-commit — the SHA
/// is OMITTED when unavailable (never "git-commit=unknown"). src-format and
/// sample-rate remain alongside.
/// </summary>
public sealed class BuildProvenanceTests
{
    private const int Sr = 44_100;
    private const int Ppq = 960;

    private static string[] ConductorTexts(byte[] bytes) =>
        MidiRoundTrip.TrackChunks(bytes)[0].Events
            .OfType<TextEvent>()
            .Select(t => t.Text)
            .ToArray();

    [Fact]
    public void Provenance_BuildMetadata_EmitsVersionAndGitCommit()
    {
        // In this git checkout the build-time metadata carries a real short SHA.
        Assert.Equal("1.0.0", BuildMetadata.Version);
        Assert.NotNull(BuildMetadata.GitCommit);
        Assert.Matches(new Regex("^[0-9a-f]{7,40}$"), BuildMetadata.GitCommit);

        var timeline = new VisualizationTimeline
        {
            StartSample = 0,
            EndSample = 8_000_000,
            SampleRate = Sr,
            Source = new TrackMetadata("vgz", "song", "chip", "song.vgz"),
            Notes = Array.Empty<VisualizationNoteEvent>(),
        };
        var build = MusicalTimeMapBuilder.Build(timeline, new MusicalTimeMapOptions { FixedBpm = 120 });
        MusicalMidiExportResult result = new MusicalMidiExporter(build.Map, Ppq).Export(timeline);

        string[] texts = ConductorTexts(result.Bytes);
        Assert.Contains(texts, t => t == "mdplayer-version 1.0.0");
        Assert.Contains(texts, t => t == $"git-commit {BuildMetadata.GitCommit}");
        Assert.Contains(texts, t => t.StartsWith("src-format ", StringComparison.Ordinal));
        Assert.Contains(texts, t => t == $"sample-rate {Sr}");
    }

    [Fact]
    public void Provenance_BuildMetadata_OmitsCommitWhenUnavailable()
    {
        // Omission path: when no SHA was resolved at build time, only
        // mdplayer-version is emitted — never "git-commit=unknown".
        var conductor = new List<MidiEventBase>();
        int order = 0;
        MusicalMidiExporter.AddBuildProvenance(conductor,
            evt => { evt.SourceOrder = order++; return evt; },
            version: "1.0.0",
            gitCommit: null);

        Assert.Single(conductor);
        var text = Assert.IsType<MidiMetaTextEvent>(conductor[0]);
        Assert.Equal("mdplayer-version 1.0.0", text.Text);
    }
}
