using System;
using System.IO;
using System.Linq;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Source-boundary guard for the native-audio rendering path: the NativeAudio
/// replay session must NEVER execute the FMP driver or a Nise286 instruction
/// directly. All driver execution happens only in Pass 1 via the legacy
/// MDSound runtime; Pass 2 is a pure offline native replay. Consequently the
/// production native-audio session file must not call the raw Nise98 driver
/// API (<c>CallRunfunctionCall</c> / <c>StepExecute</c>) at all.
///
/// The legacy MDSound engine (FmpRuntime/LegacyMdsoundFmpPcmSession) remains
/// the unchanged default and keeps its own execution path.
/// </summary>
public sealed class DriverExecutionBoundaryTests
{
    const string NativeSessionFile = "NativeAudioFmpPcmSession.cs";

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
                $"'Rendering/{NativeSessionFile}' must never call the raw Nise98 API ({raw}); Pass 1 uses the legacy MDSound runtime and Pass 2 is a pure native replay.");
        }
    }

    [Fact]
    public void AllRawCalls_LiveOnlyInTheLegacyAndRuntime()
    {
        var coreDir = LocateSourceDir("MDPlayer.Fmp.Core");
        var rawFiles = Directory.EnumerateFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Where(p => File.ReadAllText(p).Contains("CallRunfunctionCall("))
            .Select(Path.GetFileName)
            .Distinct()
            .ToList();

        // CallRunfunctionCall is legitimate only in the Nise98 runtime (which
        // defines and routes it) and the legacy FMP runtime used by Pass 1. The
        // production native-audio rerenderter must not be among the callers.
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