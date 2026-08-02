using Fmp.Application.Contracts;

namespace Fmp.Application.Preview;

/// <summary>
/// A validated, immutable reference to a completed capture bundle in the
/// session's capture store. All artifact paths are absolute on the local
/// machine; only <see cref="CaptureBundleManifest"/> persists them relative to
/// the bundle directory so the JSON never hard-codes absolute paths.
/// </summary>
internal sealed record CaptureBundle
{
    /// <summary>The capture fingerprint that produced and validates this bundle.</summary>
    public required string Key { get; init; }

    /// <summary>Root directory of the committed bundle.</summary>
    public required string DirectoryPath { get; init; }

    /// <summary>Absolute path to the persisted manifest (<c>manifest.json</c>).</summary>
    public required string ManifestPath { get; init; }

    /// <summary>Absolute path to the captured semantic timeline (<c>timeline.json</c>).</summary>
    public required string TimelinePath { get; init; }

    /// <summary>Absolute path to the optional master WAV (<c>master.wav</c>).</summary>
    public string? MasterWavePath { get; init; }

    /// <summary>Absolute path to the optional stems directory (<c>stems/</c>).</summary>
    public string? StemsDirectoryPath { get; init; }

    public bool HasMasterWave =>
        !string.IsNullOrWhiteSpace(MasterWavePath)
        && File.Exists(MasterWavePath);

    public bool HasTimeline =>
        File.Exists(TimelinePath);
}

/// <summary>
/// The durable JSON manifest at <c>manifest.json</c> inside each capture bundle
/// directory. Names are relative to the bundle directory; absolute machine
/// paths are never persisted so a bundle stays relocatable and the manifest
/// stays free of caller-specific state.
/// </summary>
internal sealed record CaptureBundleManifest
{
    public int SchemaVersion { get; init; } = 1;

    /// <summary>The capture fingerprint this bundle validates for.</summary>
    public required string CaptureKey { get; init; }

    /// <summary>Canonical full input path, stored for identity validation.</summary>
    public required string InputPath { get; init; }

    public required long InputLength { get; init; }

    public required long InputLastWriteUtcTicks { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required string TimelineFileName { get; init; }

    public string? MasterWaveFileName { get; init; }

    public string? StemsDirectoryName { get; init; }
}
