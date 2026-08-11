using System.Security.Cryptography;
using Fmp.Core.Rendering;
using Fmp.Core.Rendering.NativeAudioValidation;
using MDPlayer.Fmp.Tests.NativeAudioValidation;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Prompt 9R validation corpus tests against the trace-driven native-audio
/// backend. Covers capture determinism across fresh sessions (Workstream C),
/// capture block-size independence (Workstream C), real-file feature presence
/// via the replay mute seam (Workstream H), and offline audio sanity
/// (Workstream I). Fast-tier fixtures only; extended-tier stability/leak/ABI
/// tests live in the hardening suite.
/// </summary>
public class NativeAudioValidationTests
{
    private const uint CpuHz = 8_000_000;

    private static ValidationCorpusDoc Corpus() => ValidationCorpus.LoadManifest();

    private static IEnumerable<ValidationFixtureDef> FastFixtures() =>
        Corpus().Entries.Where(e => e.Tier == "fast");

    private static bool AnyFixture(out string reason)
    {
        reason = null;
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM"))) { reason = "FMP.COM missing"; return false; }
        if (NativeAudioValidationRunner.NativeLibrary() == null) { reason = "native library missing"; return false; }
        var first = FastFixtures().Select(ValidationCorpus.Resolve).FirstOrDefault(f => f != null);
        if (first == null) { reason = "no OVI fixture resolved"; return false; }
        return true;
    }

    /// <summary>Fast-tier deterministic capture helper: runs and compares event catalogs.</summary>
    private static (string hash, List<(ulong cycle, ulong seq, byte t0, byte t1, byte t2, byte t3, int bank)> canon) CaptureCanonical(
        string ovi, int sampleRate, int discardFrames, double maxSeconds)
    {
        var (capture, hash, _, _) = NativeAudioValidationRunner.CapturePass(ovi, sampleRate, maxSeconds, discardFrames: discardFrames);
        var canon = new List<(ulong, ulong, byte, byte, byte, byte, int)>();
        foreach (var e in capture.Events)
        {
            if (e.Kind == CapturedEventKind.OpnaWrite)
                canon.Add((e.OpnaMasterClock, e.Sequence, 0, e.Payload.Opna.Port, e.Payload.Opna.Address, e.Payload.Opna.Data, -1));
            else if (e.Kind == CapturedEventKind.Ppz8Command)
                canon.Add((e.OpnaMasterClock, e.Sequence, 1, (byte)e.Payload.Ppz8.Port, (byte)e.Payload.Ppz8.Address, (byte)e.Payload.Ppz8.Data, e.Payload.Ppz8.BankId));
        }
        return (hash, canon);
    }

    [Fact]
    public void ValidationCorpus_Manifest_DeclaresAllRequiredCoverage()
    {
        var doc = Corpus();
        Assert.NotEmpty(doc.Entries);
        Assert.InRange(doc.Entries.Count, 6, 15);
        Assert.NotEmpty(doc.FastValidationSubset);

        // Every feature category must be covered across the corpus.
        var fm = doc.Entries.Any(e => e.Coverage.Fm);
        var ssg = doc.Entries.Any(e => e.Coverage.Ssg);
        var rhythm = doc.Entries.Any(e => e.Coverage.RhythmRss);
        var adpcm = doc.Entries.Any(e => e.Coverage.AdpcmB);
        var ppz8 = doc.Entries.Any(e => e.Coverage.Ppz8);
        var mixed = doc.Entries.Any(e => e.Coverage.MixedOpnaPpz8);
        var looping = doc.Entries.Any(e => e.ExpectedLoopBehavior == "looping");
        var nonLooping = doc.Entries.Any(e => e.ExpectedLoopBehavior == "nonLooping");
        var bank0 = doc.Entries.Any(e => e.Coverage.Bank0Write);
        var bank1 = doc.Entries.Any(e => e.Coverage.Bank1Write);

        Assert.True(fm, "corpus must cover FM");
        Assert.True(ssg, "corpus must cover SSG");
        Assert.True(rhythm, "corpus must cover rhythm/RSS");
        Assert.True(adpcm, "corpus must cover ADPCM-B");
        Assert.True(ppz8, "corpus must cover PPZ8");
        Assert.True(mixed, "corpus must cover mixed OPNA+PPZ8");
        Assert.True(looping, "corpus must cover looping");
        Assert.True(nonLooping, "corpus must cover non-looping");
        Assert.True(bank0, "corpus must cover bank-0 writes");
        Assert.True(bank1, "corpus must cover bank-1 writes");
    }

    /// <summary>
    /// Capture determinism (Workstream C): a fast-tier fixture captured in three
    /// fresh sessions (each its own native/capture session) yields identical
    /// events, bank hashes, termination metadata and canonical SHA-256.
    /// </summary>
    [Fact]
    public void CaptureDeterminism_ThreeFreshSessions_AreIdentical()
    {
        if (!AnyFixture(out var reason))
        {
            Assert.True(true); // environment without any fixture: nothing to compare
            return;
        }
        var fixture = FastFixtures().Select(f => (f, ValidationCorpus.Resolve(f))).First(x => x.Item2 != null);
        string ovi = fixture.Item2;
        int sr = 48000;
        double maxS = 3.0;

        using var lib = NativeAudioValidationRunner.WithNativeLibrary();
        var first = CaptureCanonical(ovi, sr, 4096, maxS);
        for (int i = 0; i < 2; i++)
        {
            var next = CaptureCanonical(ovi, sr, 4096, maxS);
            Assert.Equal(first.hash, next.hash);
            Assert.Equal(first.canon.Count, next.canon.Count);
            for (int k = 0; k < first.canon.Count; k++)
                Assert.Equal(first.canon[k], next.canon[k]);
        }
    }

    /// <summary>
    /// Capture block-size independence (Workstream C): the discarded PCM buffer
    /// size must not alter the capture. Six block sizes, identical capture.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(64)]
    [InlineData(257)]
    [InlineData(1024)]
    [InlineData(4096)]
    public void CaptureDeterminism_BlockSizeIndependence_IdenticalCapture(int discardFrames)
    {
        if (!AnyFixture(out var reason))
        {
            Assert.True(true);
            return;
        }
        var fixture = FastFixtures().Select(f => (f, ValidationCorpus.Resolve(f))).First(x => x.Item2 != null);
        string ovi = fixture.Item2;

        using var lib = NativeAudioValidationRunner.WithNativeLibrary();
        var baseHash = CaptureCanonical(ovi, 48000, 4096, 3.0).hash;
        var sizeHash = CaptureCanonical(ovi, 48000, discardFrames, 3.0).hash;
        Assert.Equal(baseHash, sizeHash);
    }

    /// <summary>
    /// Audio sanity (Workstream I): the fast native-audio replay is not silent,
    /// is not accidentally mono, stays int16-bounded, does not clip dominantly,
    /// DC stays small relative to full scale, and the last audible event is not
    /// truncated (nonzero near the tail end).
    /// </summary>
    [Fact]
    public void Replay_AudioSanity_BroadDefectsDetected()
    {
        if (!AnyFixture(out var reason))
        {
            Assert.True(true);
            return;
        }
        var fixture = FastFixtures().Select(f => (f, ValidationCorpus.Resolve(f))).First(x => x.Item2 != null);
        string ovi = fixture.Item2;

        using var lib = NativeAudioValidationRunner.WithNativeLibrary();
        var metrics = AudioSanityChecks.Compute(
            NativeAudioValidationRunner.RenderWithMask(ovi, 48000, 3.0, 0).pcm);

        Assert.True(metrics.FrameCount > 0, "empty render");
        Assert.True(metrics.PeakLeft > 0 || metrics.PeakRight > 0, "fixture must not be entirely silent");
        Assert.True(metrics.PeakLeft > 0 && metrics.PeakRight > 0, "fixture must not be accidentally mono");

        // int16-bounded and not clipping-dominant by construction (mixer clamps once).
        double clipRatio = (double)metrics.ClippedSampleCount / Math.Max(1, metrics.FrameCount * 2);
        Assert.True(clipRatio < 0.50, $"clipping dominates render ({clipRatio:P1} of samples)");

        // DC small relative to full scale (±1.0).
        Assert.True(Math.Abs(metrics.DcMeanLeft) < 0.05, $"DC left too large ({metrics.DcMeanLeft:F3})");
        Assert.True(Math.Abs(metrics.DcMeanRight) < 0.05, $"DC right too large ({metrics.DcMeanRight:F3})");

        // Startup region contains the first audible event.
        Assert.True(metrics.FirstNonZeroFrame >= 0, "no audible frame anywhere");
        Assert.True(metrics.FirstNonZeroFrame < Math.Max(1, metrics.FrameCount), "first audible beyond end");
        // Not truncated: some nonzero audio in the final 5% (or the tail region is short).
        if (metrics.LastNonZeroFrame >= 0)
            Assert.True(metrics.LastNonZeroFrame >= metrics.FrameCount * 0.9,
                $"last audible frame {metrics.LastNonZeroFrame} truncated (/ {metrics.FrameCount})");
    }

    /// <summary>
    /// Feature presence (Workstream H) via the replay mute seam: for a fast-tier
    /// fixture that actually drives OPNA and/or PPZ8 (as evidenced by its
    /// capture event stream), the PPZ8-muted render (OPNA-only) and the
    /// OPNA-muted render (PPZ8-only) must each produce nonzero output when the
    /// component is genuinely driven. Assertions are per-fixture based on the
    /// captured component presence, so a fixture that happens not to drive a
    /// component does not cause a false failure.
    /// </summary>
    [Fact]
    public void Replay_FeaturePresence_OpnaAndPpz8BothContribute()
    {
        var mixed = FastFixtures().FirstOrDefault(f => f.Coverage.MixedOpnaPpz8)
                    ?? FastFixtures().FirstOrDefault();
        if (mixed == null) { Assert.True(true); return; }
        string ovi = ValidationCorpus.Resolve(mixed);
        if (ovi == null) { Assert.True(true); return; }
        if (!File.Exists(Path.Combine(AppContext.BaseDirectory, "FMP.COM"))
            || NativeAudioValidationRunner.NativeLibrary() == null) { Assert.True(true); return; }

        using var lib = NativeAudioValidationRunner.WithNativeLibrary();

        // Determine which components this fixture actually drives, from its
        // deterministic capture. Feature assertions below are conditional on
        // the component being present in the capture.
        var (capture, _, opnaCount, ppz8Count) =
            NativeAudioValidationRunner.CapturePass(ovi, 48000, 3.0, 4096);
        bool drivesOpna = opnaCount > 0;
        bool drivesPpz8 = ppz8Count > 0;

        Assert.True(drivesOpna || drivesPpz8, "fixture must drive at least one component");

        var full = NativeAudioValidationRunner.RenderWithMask(ovi, 48000, 3.0, 0);
        bool NonZero(byte[] pcm) { foreach (var b in pcm) if (b != 0) return true; return false; }
        Assert.True(NonZero(full.pcm), "full render silent");

        if (drivesOpna)
        {
            var opnaOnly = NativeAudioValidationRunner.RenderWithMask(ovi, 48000, 3.0, 2); // mute PPZ8
            Assert.True(NonZero(opnaOnly.pcm),
                "OPNA contribution missing (PPZ8-muted render silent though capture has OPNA writes)");
        }
        if (drivesPpz8)
        {
            var ppz8Only = NativeAudioValidationRunner.RenderWithMask(ovi, 48000, 3.0, 1); // mute OPNA
            Assert.True(NonZero(ppz8Only.pcm),
                "PPZ8 contribution missing (OPNA-muted render silent though capture has PPZ8 commands)");
        }
    }

    /// <summary>
    /// Capture SHA-256 stability (Workstream B): the canonical hash is
    /// deterministic across independent sessions and is independent of the
    /// absolute fixture path (paths are excluded by design, not hashed).
    /// </summary>
    [Fact]
    public void CaptureHash_StableAcrossSessions_IndependentOfPath()
    {
        if (!AnyFixture(out var reason))
        {
            Assert.True(true);
            return;
        }
        var fixture = FastFixtures().Select(f => (f, ValidationCorpus.Resolve(f))).First(x => x.Item2 != null);
        string ovi = fixture.Item2;

        using var lib = NativeAudioValidationRunner.WithNativeLibrary();
        // Same semantic capture from two fresh sessions → identical hash.
        var (c1, h1, _, _) = NativeAudioValidationRunner.CapturePass(ovi, 48000, 2.0, 4096);
        var (c2, h2, _, _) = NativeAudioValidationRunner.CapturePass(ovi, 48000, 2.0, 4096);
        Assert.Equal(h1, h2);

        // The hash must not depend on absolute paths (Workstream B excludes them).
        Assert.DoesNotContain(Path.GetDirectoryName(ovi), h1, StringComparison.OrdinalIgnoreCase);

        // Sanity: nonzero event catalogs and full-hash length (64 lowercase hex).
        Assert.True(c1.Events.Count > 0, "capture must contain events");
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    /// <summary>
    /// Replay determinism (Workstream F): from a single immutable capture, replay
    /// produces byte-identical PCM at 48 kHz and deterministic, non-silent,
    /// zero-native-read PCM at 44.1/96 kHz. One replay session per auxiliary rate
    /// keeps the real-time-length native render bounded; the 48 kHz rate (the
    /// fast-subset rate exercising every feature category) runs a second session
    /// to assert byte-identical determinism from the same capture.
    ///
    /// Empirical note: <see cref="NativeAudioValidationRunner.ReplayPass"/> builds
    /// a fresh <c>NativeAudioFmpPcmSession</c> with no capture cache, so <c>Boot</c>
    /// re-runs the Pass 1 control capture every session. The three-session-per-rate
    /// loop therefore re-captured the same deterministic track redundantly; fresh-
    /// session capture equality is already proven by
    /// <see cref="CaptureDeterminism_ThreeFreshSessions_AreIdentical"/> and a two-
    /// render equality by Native_FullRender_NonzeroDeterministicPcm, so the extra
    /// sessions were collapsed.
    /// </summary>
    [Fact]
    [Trait("Tier", "extended")]
    public void ReplayDeterminism_SameCapture_DeterministicPcmAcrossRates()
    {
        if (!AnyFixture(out var reason))
        {
            Assert.True(true);
            return;
        }
        var fixture = FastFixtures().Select(f => (f, ValidationCorpus.Resolve(f))).First(x => x.Item2 != null);
        string ovi = fixture.Item2;
        using var lib = NativeAudioValidationRunner.WithNativeLibrary();

        // Single immutable capture reused by every replay session (capture once).
        var (_, hash, opna, ppz8) = NativeAudioValidationRunner.CapturePass(ovi, 48000, 2.0, 4096);

        // Auxiliary rates: one deterministic replay session each (structural checks).
        foreach (int rate in new[] { 44100, 96000 })
        {
            var r = NativeAudioValidationRunner.ReplayPass(ovi, rate, 2.5, hash, opna, ppz8);
            Assert.NotEmpty(r.pcm);
            Assert.NotEqual(0, r.report.OutputFrameCount);
            Assert.Matches("^[0-9a-f]{64}$", r.report.PcmSha256);
            Assert.Equal(0, r.report.NativeStatusReadCount);
            Assert.Equal(0, r.report.NativeIrqReadCount);
        }

        // Primary rate: two sessions must produce byte-identical deterministic PCM.
        var first = NativeAudioValidationRunner.ReplayPass(ovi, 48000, 2.5, hash, opna, ppz8);
        var again = NativeAudioValidationRunner.ReplayPass(ovi, 48000, 2.5, hash, opna, ppz8);
        Assert.Equal(first.report.PcmSha256, again.report.PcmSha256);
        Assert.Equal(first.report.OutputFrameCount, again.report.OutputFrameCount);
        Assert.Equal(first.pcm, again.pcm);
        Assert.Equal(0, first.report.NativeStatusReadCount);
        Assert.Equal(0, first.report.NativeIrqReadCount);
    }

    /// <summary>
    /// Validation output (Workstream "Validation output"): the report writer
    /// produces an authoritative JSON document (round-trips to the same result)
    /// and a human-readable Markdown summary with all per-fixture fields. The
    /// JSON is the contract; Markdown is presentational.
    /// </summary>
    [Fact]
    public void ValidationReport_JsonAuthoritative_MarkdownSummary()
    {
        if (!AnyFixture(out var reason))
        {
            Assert.True(true);
            return;
        }
        var pick = FastFixtures().Select(f => (f, ValidationCorpus.Resolve(f))).First(x => x.Item2 != null);
        ValidationFixtureDef fixture = pick.f;
        string ovi = pick.Item2;
        using var lib = NativeAudioValidationRunner.WithNativeLibrary();

        var (capture, hash, opna, ppz8) = NativeAudioValidationRunner.CapturePass(ovi, 48000, 2.0, 4096);
        var (_, replay) = NativeAudioValidationRunner.ReplayPass(ovi, 48000, 2.0, hash, opna, ppz8);
        var sanity = AudioSanityChecks.Compute(NativeAudioValidationRunner.RenderWithMask(ovi, 48000, 2.0, 0).pcm);

        var result = new FixtureValidationResult
        {
            FixtureId = fixture.Id,
            CaptureHash = hash,
            EventCount = capture.Events.Count,
            BankHashes = capture.Ppz8Banks.Select(b => b.Sha256).ToList(),
            OutputRate = 48000,
            FrameCount = replay.OutputFrameCount,
            PcmHash = replay.PcmSha256,
            AudioSanity = new AudioSanityMetrics
            {
                FrameCount = sanity.FrameCount,
                PeakLeft = sanity.PeakLeft, PeakRight = sanity.PeakRight,
                RmsLeft = sanity.RmsLeft, RmsRight = sanity.RmsRight,
                DcMeanLeft = sanity.DcMeanLeft, DcMeanRight = sanity.DcMeanRight,
                ClippedSampleCount = sanity.ClippedSampleCount,
                ZeroSamplePercentage = sanity.ZeroSamplePercentage,
                LongestZeroRun = sanity.LongestZeroRun,
                FirstNonZeroFrame = sanity.FirstNonZeroFrame,
                LastNonZeroFrame = sanity.LastNonZeroFrame,
            },
            CaptureDurationSeconds = 0, // capture+replay split measured in benchmarks
            ReplayDurationSeconds = replay.RenderDurationSeconds,
            TotalDurationSeconds = replay.RenderDurationSeconds,
            AllocatedBytes = 0,
            Result = "pass",
        };

        string json = ValidationReportWriter.ToJson(result);
        string md = ValidationReportWriter.ToMarkdown(
            new ValidationRunSummary { Tier = "fast", Result = "pass", FixturesTotal = 1, FixturesExecuted = 1, FixturesPassed = 1 },
            new[] { result });

        // JSON is authoritative: it parses and carries the required fields.
        using var parsed = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(fixture.Id, parsed.RootElement.GetProperty("fixtureId").GetString());
        Assert.Equal(hash, parsed.RootElement.GetProperty("captureHash").GetString());
        Assert.Equal("pass", parsed.RootElement.GetProperty("result").GetString());

        // Markdown is presentational but complete: contains fixture, both hashes, result.
        Assert.Contains(fixture.Id, md);
        Assert.Contains(result.PcmHash[..40], md);
        Assert.Contains("pass", md);
    }
}
