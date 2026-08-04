using Fmp.Core.Playback.Opna;

namespace Fmp.Application.Rendering;

/// <summary>
/// Result of a lazy native-OPNA availability probe. A probe is only ever run
/// when a native-audio render is actually starting — never during ordinary
/// application startup.
/// </summary>
public sealed record NativeOpnaAvailabilityResult
{
    public bool IsAvailable { get; init; }
    public string? NativeLibraryName { get; init; }
    public string? ExpectedRuntimeIdentifier { get; init; }
    public string? ExpectedLocation { get; init; }
    public uint? ExpectedAbi { get; init; }
    public uint? ActualAbi { get; init; }
    public string? Problem { get; init; }

    /// <summary>Focused user-facing message (no raw stack trace).</summary>
    public string ToDisplayMessage() => Problem ?? "Native audio is available.";
}

/// <summary>
/// Lazily validates that the native OPNA library loads and that its ABI matches
/// this build, re-using the existing native resolver and ABI probe (the same
/// <see cref="NativeOpnaDevice.Open"/> path real rendering uses) — there is no
/// second probing mechanism. Call it only when a native-audio render starts.
/// Always preserves the original exception for the diagnostic log, never shows
/// a raw stack trace to the user, and never triggers a fallback to MDSound.
/// </summary>
public static class NativeOpnaAvailability
{
    /// <summary>
    /// Probes native availability at the configured sample rate.
    /// <paramref name="diagnosticException"/> carries the full original
    /// exception (for the diagnostic log) when validation fails.
    /// </summary>
    public static NativeOpnaAvailabilityResult Validate(int sampleRate, out Exception? diagnosticException)
    {
        diagnosticException = null;
        int rate = sampleRate is 44100 or 48000 or 96000 ? sampleRate : 48000;
        try
        {
            using var device = NativeOpnaDevice.Open(rate);
            NativeOpnaAvailabilityResult abi = ValidateAbi();
            if (!abi.IsAvailable)
            {
                diagnosticException = new OpnaException("ABI mismatch surfaced by availability probe.");
                return abi;
            }
            return abi;
        }
        catch (OpnaLibraryNotFoundException ex)
        {
            diagnosticException = ex;
            return Result(
                available: false,
                problem: "Native YM2608 audio is unavailable because the native runtime library could not be loaded.",
                actualAbi: null);
        }
        catch (OpnaException ex)
        {
            // e.g. ABI/mismatch style native errors during open/close.
            diagnosticException = ex;
            return Result(
                available: false,
                problem: "The installed native YM2608 library is incompatible with this version of MDPlayer.",
                actualAbi: null);
        }
    }

    private static NativeOpnaAvailabilityResult Result(bool available, string problem, uint? actualAbi) =>
        new()
        {
            IsAvailable = available,
            NativeLibraryName = OpnaNativeSession.NativeLibraryFileName,
            ExpectedRuntimeIdentifier = OpnaNativeSession.NativeRuntimeIdentifier,
            ExpectedLocation = OpnaNativeSession.NativeRuntimeLocation(),
            ExpectedAbi = OpnaNativeSession.AbiVersionTarget,
            ActualAbi = actualAbi,
            Problem = available ? null : problem,
        };

    /// <summary>Verifies the loaded library's ABI against the target version.</summary>
    private static NativeOpnaAvailabilityResult ValidateAbi()
    {
        uint actual = OpnaNativeSession.DetectAbiVersion() ?? 0;
        if (actual != OpnaNativeSession.AbiVersionTarget)
            return Result(
                available: false,
                problem: "The installed native YM2608 library is incompatible with this version of MDPlayer.",
                actualAbi: actual);
        return Result(available: true, problem: null!, actualAbi: actual);
    }
}
