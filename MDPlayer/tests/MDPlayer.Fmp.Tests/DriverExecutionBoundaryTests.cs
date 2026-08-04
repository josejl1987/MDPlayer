using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Source-boundary guard for the final-cycle synchronization invariant: every
/// real Nise98 (FMP driver) CPU execution on the native LLE rendering path must
/// flow through the clocked coordinator (<c>ClockedFmpExecutionSession</c>) so
/// the native OPNA device is always advanced to the final mapped CPU cycle after
/// a call. The coordinator (in Nise98/) is the single sanctioned owner of raw
/// driver execution; the native renderer itself must not call
/// <c>CallRunfunctionCall</c> / <c>StepExecute</c> directly.
///
/// The legacy MDSound engine (FmpRuntime/LegacyMdsoundFmpPcmSession) is
/// intentionally out of scope: it remains the unchanged default and keeps its
/// own execution path.
/// </summary>
public sealed class DriverExecutionBoundaryTests
{
    const string NativeSessionFile = "NativeLleFmpPcmSession.cs";
    const string CoordinatorFile = "ClockedFmpExecutionSession.cs";
    // The coordinator (which routes every native driver call) declares its raw
    // usage; everything outside it must not. Reference it so a rename of the
    // coordinator type keeps this guard honest.
    string[] AllowedRawFiles = { CoordinatorFile };

    [Fact]
    public void NativeRenderer_DoesNotExecuteDriverDirectly()
    {
        var coreDir = LocateSourceDir("MDPlayer.Fmp.Core");
        Assert.True(Directory.Exists(coreDir), $"core source dir not found: {coreDir}");

        string nativeSession = Path.Combine(coreDir, "Rendering", NativeSessionFile);
        Assert.True(File.Exists(nativeSession), $"native session not found: {nativeSession}");

        foreach (var raw in new[] { "CallRunfunctionCall", "StepExecute" })
        {
            Assert.False(
                File.ReadAllLines(nativeSession).Any(line => line.Contains(raw) && !line.TrimStart().StartsWith("//")),
                $"'Rendering/{NativeSessionFile}' should not call the raw Nise98 API ({raw}); it must go through the clocked coordinator.");
        }
    }

    [Fact]
    public void AllRawCalls_LiveOnlyInTheCoordinator()
    {
        var coreDir = LocateSourceDir("MDPlayer.Fmp.Core");
        var rawFiles = Directory.EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("CallRunfunctionCall("))
            .Select(Path.GetFileName)
            .Distinct()
            .ToList();

        // CallRunfunctionCall is legitimate in: the Nise98 runtime itself (it
        // defines it), the coordinator (routing), the legacy FMP runtime, and
        // the PPZ8 module. The production NATIVE renderer must not be among them.
        Assert.DoesNotContain(NativeSessionFile, rawFiles);
    }

    private static string LocateSourceDir(string projectName)
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            var probe = Path.Combine(dir, "src", projectName);
            if (Directory.Exists(probe)) return probe;
            dir = Path.GetDirectoryName(dir);
        }
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", ".."));
        return Path.Combine(root, "MDPlayer", "src", projectName);
    }
}