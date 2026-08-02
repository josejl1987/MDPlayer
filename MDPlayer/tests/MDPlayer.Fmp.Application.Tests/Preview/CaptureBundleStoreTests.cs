using Fmp.Application.Preview;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>
/// End-to-end tests of the session-local <see cref="CaptureBundleStore"/>:
/// validation rules, atomic commit, temporary cleanup and "existing valid
/// bundle wins" behaviour.
/// </summary>
public sealed class CaptureBundleStoreTests : IDisposable
{
    private const string InputPath = "/music/song.vgz";
    private static readonly DateTime LastWrite = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string CaptureKey = "capture-v2-" + new string('a', 64);

    private readonly string _root;
    private readonly CaptureBundleStore _store;

    public CaptureBundleStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "mdplayer-capture-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _store = new CaptureBundleStore(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private CaptureBundleManifest ManifestValid(string? key = null)
    {
        return new CaptureBundleManifest
        {
            CaptureKey = key ?? CaptureKey,
            InputPath = InputPath,
            InputLength = 100,
            InputLastWriteUtcTicks = LastWrite.Ticks,
            CreatedUtc = DateTime.UtcNow,
            TimelineFileName = "timeline.json",
            MasterWaveFileName = "master.wav",
            StemsDirectoryName = "stems",
        };
    }

    private static void WriteTimeline(CaptureBundle bundle)
    {
        Directory.CreateDirectory(bundle.DirectoryPath);
        File.WriteAllText(bundle.TimelinePath, "{}");
    }

    private void CommitBundle(CaptureBundleManifest manifest)
    {
        CaptureBundle temp = _store.CreatePaths(manifest.CaptureKey);
        WriteTimeline(temp);
        File.WriteAllBytes(Path.Combine(temp.DirectoryPath, "master.wav"), new byte[] { 1, 2, 3, 4 });
        Directory.CreateDirectory(Path.Combine(temp.DirectoryPath, "stems"));
        CaptureBundle committed = _store.Commit(temp, manifest);
        Assert.True(committed.HasTimeline);
    }

    // ---- Valid bundle opens ------------------------------------------------

    [Fact]
    public void ValidBundle_Opens()
    {
        CommitBundle(ManifestValid());

        CaptureBundle? bundle = _store.TryOpenValid(CaptureKey, InputPath, 100, LastWrite);
        Assert.NotNull(bundle);
        Assert.Equal(CaptureKey, bundle!.Key);
        Assert.True(bundle.HasTimeline);
        Assert.True(bundle.HasMasterWave);
        Assert.NotNull(bundle.StemsDirectoryPath);
        Assert.True(File.Exists(bundle.TimelinePath));
    }

    // ---- Rejections ----------------------------------------------------------

    [Fact]
    public void MissingManifest_IsRejected()
    {
        string finalDir = _store.GetBundleDirectory(CaptureKey);
        Directory.CreateDirectory(finalDir);
        File.WriteAllText(Path.Combine(finalDir, "timeline.json"), "{}");

        CaptureBundle? bundle = _store.TryOpenValid(CaptureKey, InputPath, 100, LastWrite);
        Assert.Null(bundle);
        // The invalid final directory is deleted when encountered.
        Assert.False(Directory.Exists(finalDir));
    }

    [Fact]
    public void WrongKey_IsRejected()
    {
        CommitBundle(ManifestValid());
        Assert.Null(_store.TryOpenValid("capture-v2-" + new string('b', 64), InputPath, 100, LastWrite));
    }

    [Fact]
    public void WrongSchema_IsRejected()
    {
        CommitBundle(ManifestValid());

        // Corrupt the committed manifest's schema version in place.
        string finalDir = _store.GetBundleDirectory(CaptureKey);
        CaptureBundleManifest wrongSchema = ManifestValid() with { SchemaVersion = 99 };
        VisualizationManifestReflection.Write(Path.Combine(finalDir, "manifest.json"), wrongSchema);

        Assert.Null(_store.TryOpenValid(CaptureKey, InputPath, 100, LastWrite));
        // The schema-invalid final bundle is treated as invalid and cleaned up.
        Assert.False(Directory.Exists(finalDir));
    }

    [Fact]
    public void WrongInputLength_IsRejected()
    {
        CommitBundle(ManifestValid());
        Assert.Null(_store.TryOpenValid(CaptureKey, InputPath, 999, LastWrite));
        // The mismatched final bundle is not deleted (identity mismatch, not lazily stale).
    }

    [Fact]
    public void WrongInputLastWrite_IsRejected()
    {
        CommitBundle(ManifestValid());
        Assert.Null(
            _store.TryOpenValid(CaptureKey, InputPath, 100, LastWrite.AddHours(3)));
    }

    [Fact]
    public void MissingTimeline_IsRejected()
    {
        // Commit won't accept a bundle without a timeline, so simulate a
        // pre-existing final dir with a manifest but no timeline artifact.
        string finalDir = _store.GetBundleDirectory(CaptureKey);
        Directory.CreateDirectory(finalDir);
        VisualizationManifestReflection.Write(Path.Combine(finalDir, "manifest.json"), ManifestValid());

        Assert.Null(_store.TryOpenValid(CaptureKey, InputPath, 100, LastWrite));
        Assert.False(Directory.Exists(finalDir));
    }

    // ---- Commit / delete behaviour -------------------------------------------

    [Fact]
    public void Commit_ProducesFinalDirectory()
    {
        CommitBundle(ManifestValid());

        string finalDir = _store.GetBundleDirectory(CaptureKey);
        Assert.True(Directory.Exists(finalDir));
        Assert.True(File.Exists(Path.Combine(finalDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(finalDir, "timeline.json")));
        Assert.False(Directory.Exists(_root + "-temp-guard"));
    }

    [Fact]
    public void FailedTemporaryBundles_AreDeleted()
    {
        CaptureBundle temp = _store.CreatePaths(CaptureKey);
        Directory.CreateDirectory(temp.DirectoryPath);
        File.WriteAllText(temp.TimelinePath, "{}");

        // A temp bundle that is never committed and then explicitly cleaned is removed.
        _store.DeleteIncomplete(temp.DirectoryPath);
        Assert.False(Directory.Exists(temp.DirectoryPath));

        // A committed bundle with the same key stays after a later temp cleanup call.
        CommitBundle(ManifestValid());
        string finalDir = _store.GetBundleDirectory(CaptureKey);
        _store.DeleteIncomplete(finalDir + "-other.tmp");
        Assert.True(Directory.Exists(finalDir));
    }

    [Fact]
    public void ExistingValidFinalBundle_WinsOverDuplicateTemporary()
    {
        CommitBundle(ManifestValid());
        string finalDir = _store.GetBundleDirectory(CaptureKey);

        // A second commit attempt for the same key must discard its temp copy
        // and return the existing (identical) bundle, not create a duplicate.
        CaptureBundle temp = _store.CreatePaths(CaptureKey);
        WriteTimeline(temp);
        CaptureBundle committed = _store.Commit(temp, ManifestValid());

        Assert.Equal(finalDir, committed.DirectoryPath);
        Assert.False(Directory.Exists(temp.DirectoryPath));
        // Only one final dir exists.
        Assert.Single(Directory.GetDirectories(Path.Combine(_root, "captures")));
    }
}

/// <summary>Test seam to reach the store's package-private manifest writer.</summary>
internal static class VisualizationManifestReflection
{
    public static void Write(string path, CaptureBundleManifest manifest)
    {
        var json = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };
        System.IO.File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(manifest, json));
    }
}
