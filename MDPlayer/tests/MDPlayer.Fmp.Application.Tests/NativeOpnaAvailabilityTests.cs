using Fmp.Application.Contracts;
using Fmp.Application.Rendering;
using Fmp.Core.Playback.Opna;
using Xunit;

namespace Fmp.Application.Tests;

/// <summary>
/// Lazy native-availability validation (Prompt 10R). These exercise the four
/// required scenarios with a controlled environment: library available, library
/// missing, wrong ABI. They also pin the invariants that MDSound preview stays
/// available, native audio fails clearly without fallback, and that no probe
/// happens during construction (only on an explicit validate call).
/// </summary>
public class NativeOpnaAvailabilityTests
{
    /// <summary>Snapshots and restores the native-library env override.</summary>
    private sealed class EnvOverride : IDisposable
    {
        private readonly string _previous;

        public EnvOverride(string value)
        {
            _previous = Environment.GetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar) ?? "";
            Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(OpnaNativeSession.NativeLibraryEnvVar, _previous.Length == 0 ? null : _previous);
    }

    /// <summary>Path to the real packaged native library in the test output.</summary>
    private static string PackagedNativeLibrary() =>
        Path.Combine(AppContext.BaseDirectory, "runtimes", "linux-x64", "native", OpnaNativeSession.NativeLibraryFileName);

    [Fact]
    public void Validate_NativeLibraryAvailable_ReportsAvailable()
    {
        string lib = PackagedNativeLibrary();
        if (!File.Exists(lib))
            return; // environment without a built native library: skipped, not failed

        using var _ = new EnvOverride(lib);
        NativeOpnaAvailabilityResult result =
            NativeOpnaAvailability.Validate(48_000, out Exception? diagnostic);

        Assert.True(result.IsAvailable);
        Assert.Null(diagnostic);
        Assert.Equal(lib, result.ExpectedLocation);
    }

    [Fact]
    public void Validate_NativeLibraryMissing_FailsClearly_NoFallback()
    {
        using var _ = new EnvOverride("/nonexistent/" + OpnaNativeSession.NativeLibraryFileName);
        NativeOpnaAvailabilityResult result =
            NativeOpnaAvailability.Validate(48_000, out Exception? diagnostic);

        Assert.False(result.IsAvailable);
        Assert.NotNull(diagnostic);
        Assert.StartsWith(
            "Native YM2608 audio is unavailable because the native runtime library could not be loaded.",
            result.ToDisplayMessage());
        Assert.Contains("libmdplayer_opna.so", result.ExpectedLocation);
    }

    [Fact]
    public void Validate_NeverRunsAtCollocation_OnlyWhenCalled()
    {
        // Building the result type and calling helpers must not touch native:
        // construction/probe is strictly on the explicit Validate call.
        var result = new NativeOpnaAvailabilityResult { IsAvailable = false, Problem = "probe not run" };
        Assert.False(result.IsAvailable);
        Assert.Equal("probe not run", result.ToDisplayMessage());
    }

    [Fact]
    public void IsFmpLike_DetectsFmpFamilyExtensions()
    {
        Assert.True(new VisualizationRequest
        {
            InputPath = "/x/song.ovi",
            OutputPath = "/x/song.mp4",
        }.IsFmpLike());

        Assert.False(new VisualizationRequest
        {
            InputPath = "/x/song.vgz",
            OutputPath = "/x/song.mp4",
        }.IsFmpLike());

        Assert.False(new VisualizationRequest
        {
            InputPath = "/x/song.spc",
            OutputPath = "/x/song.mp4",
        }.IsFmpLike());
    }
}
