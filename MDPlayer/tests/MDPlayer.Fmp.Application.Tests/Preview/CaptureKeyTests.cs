using Fmp.Application.Contracts;
using Fmp.Application.Preview;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>
/// Complete capture-fingerprint tests. The capture key must change for every
/// playback, capture or capture-affecting track setting, and must ignore every
/// visual-only setting. Reordering ID collections must not change the key.
/// </summary>
public sealed class CaptureKeyTests
{
    private const string InputPath = "/music/song.vgz";
    private static readonly DateTime LastWrite = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Key(VisualizationRequest? request = null, long length = 100)
        => PreviewCacheKey.CaptureKey(
            InputPath,
            length,
            LastWrite,
            request ?? TestRequests.Valid());

    private static void AssertCaptureChanges(Func<VisualizationRequest, VisualizationRequest> transform)
    {
        Assert.NotEqual(
            Key(),
            Key(transform(TestRequests.Valid())));
    }

    private static void AssertCaptureUnchanged(Func<VisualizationRequest, VisualizationRequest> transform)
    {
        Assert.Equal(
            Key(),
            Key(transform(TestRequests.Valid())));
    }

    [Fact]
    public void Key_IsStableForIdenticalInputs()
        => Assert.Equal(Key(), Key());

    [Fact]
    public void Key_PrefixAndLength_AreDirectorySafe()
    {
        string key = Key();
        Assert.StartsWith("capture-v2-", key);
        // 8-char prefix + 64-hex SHA-256.
        Assert.Equal("capture-v2-".Length + 64, key.Length);
        Assert.Equal(key, key.ToLowerInvariant());
    }

    // ---- Input identity ---------------------------------------------------

    [Fact]
    public void Key_ChangesWithInputLength()
    {
        Assert.NotEqual(Key(length: 100), Key(length: 101));
    }

    [Fact]
    public void Key_ChangesWithInputLastWriteTime()
    {
        Assert.NotEqual(
            PreviewCacheKey.CaptureKey(InputPath, 100, LastWrite, TestRequests.Valid()),
            PreviewCacheKey.CaptureKey(InputPath, 100, LastWrite.AddDays(1), TestRequests.Valid()));
    }

    // ---- Playback settings --------------------------------------------------

    [Fact]
    public void Key_ChangesWithLoopCount()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { LoopCount = 3 } });

    [Fact]
    public void Key_ChangesWithFadeSeconds()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { FadeSeconds = 7.0 } });

    [Fact]
    public void Key_ChangesWithTailSeconds()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { TailSeconds = 2.0 } });

    [Fact]
    public void Key_ChangesWithMaximumDurationSeconds()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { MaximumDurationSeconds = 200 } });

    [Fact]
    public void Key_ChangesWithSampleRate()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { SampleRate = 44_100 } });

    [Fact]
    public void Key_ChangesWithSsgGainDb()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { SsgGainDb = 6.0 } });

    [Fact]
    public void Key_ChangesWithSpcPitch()
        => AssertCaptureChanges(r => r with { Playback = r.Playback with { SpcPitch = SpcPitchInterpretation.Relative } });

    // ---- Track settings ------------------------------------------------------

    [Fact]
    public void Key_ChangesWithTrackSelectionMode()
        => AssertCaptureChanges(r => r with { Tracks = r.Tracks with { Selection = TrackSelectionMode.Custom } });

    [Fact]
    public void Key_ChangesWithIncludedIds()
        => AssertCaptureChanges(
            r => r with { Tracks = r.Tracks with
            {
                Selection = TrackSelectionMode.Custom,
                IncludedIds = new[] { "ym2608.0.fm.1" },
            } });

    [Fact]
    public void Key_ChangesWithExcludedIds()
        => AssertCaptureChanges(
            r => r with { Tracks = r.Tracks with
            {
                Selection = TrackSelectionMode.Custom,
                ExcludedIds = new[] { "ym2608.0.rhythm.1" },
            } });

    [Fact]
    public void Key_ChangesWithIncludeInactiveDiagnosticTracks()
        => AssertCaptureChanges(
            r => r with { Tracks = r.Tracks with { IncludeInactiveDiagnosticTracks = true } });

    [Fact]
    public void Key_IgnoresIdReordering()
    {
        string a = PreviewCacheKey.CaptureKey(InputPath, 100, LastWrite,
            TestRequests.Valid() with
            {
                Tracks = TestRequests.Valid().Tracks with
                {
                    Selection = TrackSelectionMode.Custom,
                    IncludedIds = new[] { "b", "a", "c" },
                    ExcludedIds = new[] { "y", "x" },
                },
            });
        string b = PreviewCacheKey.CaptureKey(InputPath, 100, LastWrite,
            TestRequests.Valid() with
            {
                Tracks = TestRequests.Valid().Tracks with
                {
                    Selection = TrackSelectionMode.Custom,
                    IncludedIds = new[] { "c", "a", "b" },
                    ExcludedIds = new[] { "x", "y" },
                },
            });
        Assert.Equal(a, b);
    }

    // ---- Visual-only settings (must NOT change the key) -----------------------

    [Fact]
    public void Key_IgnoresOutputPathAndEncoder()
    {
        VisualizationRequest r = TestRequests.Valid() with
        {
            OutputPath = "/elsewhere/out.mp4",
            Output = new OutputSettings { Encoder = VideoEncoder.Nvenc, Overwrite = true },
        };
        Assert.Equal(Key(), Key(r));
    }

    [Fact]
    public void Key_IgnoresWidthAndHeight()
        => AssertCaptureUnchanged(r => r with { Output = r.Output with { Width = 1280, Height = 720 } });

    [Fact]
    public void Key_IgnoresFps()
        => AssertCaptureUnchanged(r => r with { Output = r.Output with { FpsNumerator = 30, FpsDenominator = 1 } });

    [Fact]
    public void Key_IgnoresQualityAndOverwrite()
        => AssertCaptureUnchanged(r => r with
        {
            Output = r.Output with { Quality = RenderQuality.Final, Overwrite = true },
        });

    [Fact]
    public void Key_IgnoresComposition()
        => AssertCaptureUnchanged(r => r with { Composition = CompositionKind.Diagnostic });

    [Fact]
    public void Key_IgnoresPastAndFutureSeconds()
        => AssertCaptureUnchanged(r => r with { View = r.View with { PastSeconds = 0.4, FutureSeconds = 1.6 } });

    [Fact]
    public void Key_IgnoresEffectsNoteColorAndPalette()
        => AssertCaptureUnchanged(r => r with
        {
            Style = r.Style with
            {
                Effects = VisualEffects.Cinematic,
                NoteColor = NoteColorMode.Channel,
                Palette = PaletteKind.Accessible,
            },
        });

    [Fact]
    public void Key_IgnoresPresentationTextAndFont()
        => AssertCaptureUnchanged(r => r with
        {
            Presentation = r.Presentation with
            {
                Title = "Title",
                Subtitle = "Sub",
                Credits = "Cred",
                FontPath = "/fonts/noto.ttf",
            },
        });
}
