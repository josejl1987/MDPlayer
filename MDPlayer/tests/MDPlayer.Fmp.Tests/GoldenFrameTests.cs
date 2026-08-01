using System.Security.Cryptography;
using System.Text;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// PR 10b: golden-frame visual regression suite (§23.2). Each scenario
/// builds a small timeline, renders a fixed frame, and compares a SHA256
/// determinism hash. When GOLDEN_WRITE=1 is set, it writes a human-reviewable
/// PNG reference under tests/VisualGoldens.
/// </summary>
public sealed class GoldenFrameTests
{
    private const int SampleRate = 55467;
    private const string InstrumentA = "ym2608:aaaaaa1111111111";

    private static NoteEvent Note(string channel, long start, long end, double midi,
        VisualizationNoteMode mode = VisualizationNoteMode.Fm,
        string instrument = InstrumentA,
        bool retrigger = false,
        PitchChange[] pitchChanges = null)
        => new(channel, start, end, 440.0 * Math.Pow(2, (midi - 69) / 12),
               midi, instrument, mode, retrigger, pitchChanges ?? Array.Empty<PitchChange>());

    private static VisualizationTimeline Timeline(
        List<NoteEvent> notes,
        List<RhythmEvent> rhythm = null,
        Ppz8Event[] ppz8 = null,
        AdpcmBEvent[] adpcmB = null)
        => new()
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate * 10,
            Instruments = [new InstrumentDefinition(InstrumentA, "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>())],
            Notes = notes?.ToArray() ?? Array.Empty<NoteEvent>(),
            Rhythm = rhythm?.ToArray() ?? Array.Empty<RhythmEvent>(),
            Ppz8 = ppz8 ?? Array.Empty<Ppz8Event>(),
            AdpcmB = adpcmB ?? Array.Empty<AdpcmBEvent>(),
        };

    private static PanelOverlayRenderer Renderer(VisualizationTimeline timeline)
        => new(timeline, new PanelOverlayRenderer.Options
        {
            Width = 960,
            Height = 540,
            FpsNumerator = 30,
            FpsDenominator = 1,
        });

    private static string HashFrame(byte[] frame)
    {
        byte[] hash = SHA256.HashData(frame);
        return Convert.ToHexString(hash)[..16];
    }

    // --- Scenario 1: FM bend and vibrato ---

    [Fact]
    public void Golden_FmBendAndVibrato()
    {
        long t = SampleRate;
        var notes = new List<NoteEvent>
        {
            Note("ym2608.0.fm.1", t, t * 4, 60, pitchChanges:
                [new PitchChange(t + SampleRate, 65, 62),
                 new PitchChange(t + SampleRate * 2, 58, 58)]),
        };
        var renderer = Renderer(Timeline(notes));
        byte[] frame = RenderFrame(renderer, 90); // ~3s in
        string hash = HashFrame(frame);
        AssertGoldenHash("FM_BEND_VIBRATO", hash, frame);
    }

    // --- Scenario 2: FM3 operator mode ---

    [Fact]
    public void Golden_Fm3OperatorMode()
    {
        long t = SampleRate;
        var notes = new List<NoteEvent>
        {
            Note("ym2608.0.fm.3", t, t * 4, 60),
            Note("ym2608.0.fm3.op.1", t, t * 3, 62, mode: VisualizationNoteMode.Fm3Operator),
            Note("ym2608.0.fm3.op.2", t, t * 3, 58, mode: VisualizationNoteMode.Fm3Operator),
        };
        var renderer = Renderer(Timeline(notes));
        byte[] frame = RenderFrame(renderer, 60);
        string hash = HashFrame(frame);
        AssertGoldenHash("FM3_OPERATOR", hash, frame);
    }

    // --- Scenario 3: SSG tone/noise/envelope modes ---

    [Fact]
    public void Golden_SsgModes()
    {
        long t = SampleRate;
        var notes = new List<NoteEvent>
        {
            Note("ym2608.0.ssg.1", t, t * 4, 60, mode: VisualizationNoteMode.SsgTone),
            Note("ym2608.0.ssg.2", t, t * 4, 64, mode: VisualizationNoteMode.SsgToneNoise),
            Note("ym2608.0.ssg.3", t, t * 4, 67, mode: VisualizationNoteMode.SsgEnvelopeTone),
        };
        var renderer = Renderer(Timeline(notes));
        byte[] frame = RenderFrame(renderer, 60);
        string hash = HashFrame(frame);
        AssertGoldenHash("SSG_MODES", hash, frame);
    }

    // --- Scenario 4: Rhythm impacts ---

    [Fact]
    public void Golden_RhythmImpacts()
    {
        long t = SampleRate;
        var rhythm = new List<RhythmEvent>();
        for (int i = 0; i < 20; i++)
        {
            rhythm.Add(new RhythmEvent(
                i % 2 == 0 ? "bd" : "sd",
                "ym2608.0.rhythm",
                t + i * SampleRate / 4,
                0.5f + (i % 3) * 0.2f,
                (i % 3 - 1) * 0.5f));
        }
        var renderer = Renderer(Timeline(new List<NoteEvent>(), rhythm));
        byte[] frame = RenderFrame(renderer, 35);
        string hash = HashFrame(frame);
        AssertGoldenHash("RHYTHM_IMPACTS", hash, frame);
    }

    // --- Scenario 5: Dense mixed passage ---

    [Fact]
    public void Golden_DenseMixedPassage()
    {
        long t = SampleRate;
        var notes = new List<NoteEvent>();
        for (int ch = 1; ch <= 6; ch++)
        {
            for (int n = 0; n < 10; n++)
            {
                notes.Add(Note($"ym2608.0.fm.{ch}",
                    t + n * SampleRate / 8,
                    t + n * SampleRate / 8 + SampleRate / 4,
                    55 + ch + n % 7));
            }
        }
        var renderer = Renderer(Timeline(notes));
        byte[] frame = RenderFrame(renderer, 35);
        string hash = HashFrame(frame);
        AssertGoldenHash("DENSE_MIXED", hash, frame);
    }

    // --- Scenario 6: Empty/silent panels ---

    [Fact]
    public void Golden_EmptySilentPanels()
    {
        var renderer = Renderer(Timeline(new List<NoteEvent>()));
        byte[] frame = RenderFrame(renderer, 10);
        string hash = HashFrame(frame);
        AssertGoldenHash("EMPTY_SILENT", hash, frame);
    }

    [Fact]
    public void Golden_Ppz8Events()
    {
        Ppz8Event[] events =
        [
            new(0, SampleRate, SampleRate * 3L, 1, 12, 22050, 57, 0.9f, -0.6f, false),
            new(3, SampleRate * 2L, SampleRate * 4L, 1, 37, null, null, 0.7f, 0.5f, true),
        ];
        var renderer = Renderer(Timeline(new List<NoteEvent>(), ppz8: events));
        byte[] frame = RenderFrame(renderer, 60);
        AssertGoldenHash("PPZ8", HashFrame(frame), frame);
    }

    [Fact]
    public void Golden_AdpcmEvents()
    {
        AdpcmBEvent[] events =
        [new(SampleRate, SampleRate * 4L, 0x100, 0x500, 0x240, null, 0.85f, 0, false)];
        var renderer = Renderer(Timeline(new List<NoteEvent>(), adpcmB: events));
        byte[] frame = RenderFrame(renderer, 60);
        AssertGoldenHash("ADPCM", HashFrame(frame), frame);
    }

    // --- Scenario 7: CJK title and credits ---

    [SkippableFact]
    public void Golden_CjkTitleAndCredits()
    {
        var timeline = new VisualizationTimeline
        {
            SampleRate = SampleRate,
            StartSample = 0,
            EndSample = SampleRate * 10,
            Instruments = [new InstrumentDefinition(InstrumentA, "fm", 4, 3, 0, 2, Array.Empty<FmOperatorDefinition>())],
            Notes = [Note("ym2608.0.fm.1", SampleRate, SampleRate * 5, 60)],
            Rhythm = Array.Empty<RhythmEvent>(),
        };
        var presentation = new VisualizationPresentation(
            Title: "東方幻想郷 ～ Lotus Land Story",
            Subtitle: "テストサウンドトラック",
            Credits: "© テスト作曲家 2024");
        try
        {
            // CJK goldens are byte-exact when a font is installed, but remain
            // intentionally skippable because font versions change rasterization.
            Skip.IfNot(
                UnicodeStaticTextRenderer.CanRender(presentation, null),
                "No CJK-capable font is available.");
        }
        catch (InvalidOperationException ex)
        {
            Skip.If(true, $"No usable CJK-capable font is available: {ex.Message}");
        }

        var renderer = new PanelOverlayRenderer(timeline, new PanelOverlayRenderer.Options
        {
            Width = 960,
            Height = 540,
            FpsNumerator = 30,
            FpsDenominator = 1,
            Presentation = presentation,
        });
        byte[] frame = RenderFrame(renderer, 10);
        string hash = HashFrame(frame);
        AssertGoldenHash("CJK_TITLE", hash, frame);
    }

    // --- helpers ---

    private static byte[] RenderFrame(PanelOverlayRenderer renderer, long frameIndex)
    {
        byte[] buffer = new byte[renderer.FrameByteCount];
        var scopeGrid = new byte[960 * renderer.Layout.CorrscopeGridHeight * 4];
        renderer.RenderCompositeFrame(frameIndex, scopeGrid, buffer);
        return buffer;
    }

    /// <summary>
    /// Asserts the determinism hash. GOLDEN_WRITE=1 additionally writes the
    /// human-reviewable PNG reference into tests/VisualGoldens.
    /// </summary>
    private static void AssertGoldenHash(string scenario, string actualHash, byte[] frame)
    {
        string goldenHash = GetGoldenHash(scenario);
        if (Environment.GetEnvironmentVariable("GOLDEN_WRITE") == "1")
        {
            string directory = GoldenDirectory();
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, GoldenFileName(scenario));
            using (Image<Rgba32> image = Image.LoadPixelData<Rgba32>(frame, 960, 540))
                image.SaveAsPng(path);
            Console.WriteLine($"GOLDEN_WRITE: {scenario} → {actualHash} (written to {path})");
            Console.WriteLine($"  Update GetGoldenHash(\"{scenario}\") to \"{actualHash}\"");
        }
        else
        {
            Assert.True(goldenHash == actualHash,
                $"Golden hash mismatch for {scenario}:\n  expected: {goldenHash}\n  actual:   {actualHash}\n" +
                "If this is a deliberate visual change, set GOLDEN_WRITE=1 and update the hash.");
            AssertVisualGolden(scenario, frame);
        }
    }

    private static string GoldenDirectory()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../VisualGoldens"));

    private static void AssertVisualGolden(string scenario, byte[] actual)
    {
        string path = Path.Combine(GoldenDirectory(), GoldenFileName(scenario));
        Assert.True(File.Exists(path), $"Missing visual golden image: {path}");

        using Image<Rgba32> expectedImage = Image.Load<Rgba32>(path);
        byte[] expected = new byte[actual.Length];
        expectedImage.CopyPixelDataTo(expected);
        int firstDifference = -1;
        for (int index = 0; index < actual.Length; index++)
        {
            if (actual[index] != expected[index])
            {
                firstDifference = index;
                break;
            }
        }
        if (firstDifference < 0)
            return;

        string failureDirectory = Path.Combine(
            AppContext.BaseDirectory, "VisualGoldenFailures", scenario);
        Directory.CreateDirectory(failureDirectory);
        File.WriteAllBytes(Path.Combine(failureDirectory, "actual.rgba"), actual);
        using (Image<Rgba32> actualImage = Image.LoadPixelData<Rgba32>(actual, 960, 540))
        {
            actualImage.SaveAsPng(Path.Combine(failureDirectory, "actual.png"));
            expectedImage.SaveAsPng(Path.Combine(failureDirectory, "expected.png"));
        }

        byte[] diff = new byte[actual.Length];
        for (int index = 0; index < diff.Length; index += 4)
        {
            diff[index] = (byte)Math.Min(255, Math.Abs(actual[index] - expected[index]) * 3);
            diff[index + 1] = (byte)Math.Min(255, Math.Abs(actual[index + 1] - expected[index + 1]) * 3);
            diff[index + 2] = (byte)Math.Min(255, Math.Abs(actual[index + 2] - expected[index + 2]) * 3);
            diff[index + 3] = 255;
        }
        using Image<Rgba32> diffImage = Image.LoadPixelData<Rgba32>(diff, 960, 540);
        diffImage.SaveAsPng(Path.Combine(failureDirectory, "diff.png"));

        Assert.True(false,
            $"Visual golden mismatch for {scenario} at byte {firstDifference}; " +
            $"artifacts: {failureDirectory}/expected.png, actual.png, diff.png");
    }

    /// <summary>
    private static string GoldenFileName(string scenario) => scenario switch
    {
        "FM_BEND_VIBRATO" => "fm-bend.png",
        "FM3_OPERATOR" => "fm3-operators.png",
        "SSG_MODES" => "ssg-modes.png",
        "RHYTHM_IMPACTS" => "rhythm.png",
        "PPZ8" => "ppz8.png",
        "ADPCM" => "adpcm.png",
        "DENSE_MIXED" => "dense-mix.png",
        "CJK_TITLE" => "cjk-title.png",
        _ => "empty-silent.png",
    };

    /// Approved hashes are determinism tests; visual correctness is reviewed
    /// from the committed PNG files.
    /// </summary>
    private static string GetGoldenHash(string scenario) => scenario switch
    {
        "FM_BEND_VIBRATO" => "9B180B3B68F4ADA7",
        "FM3_OPERATOR" => "056DBFDFBB70E287",
        "SSG_MODES" => "429252CE8C0C3837",
        "RHYTHM_IMPACTS" => "0CFF4422986C42CA",
        "DENSE_MIXED" => "E86801DAB4AD96FF",
        "EMPTY_SILENT" => "ED4786A69DC4BE77",
        "CJK_TITLE" => "C263BDFB94293997",
        "PPZ8" => "1021F914440F77B0",
        "ADPCM" => "685C654BB45780E6",
        _ => "UNKNOWN",
    };
}
