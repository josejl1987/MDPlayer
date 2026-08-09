using System.Diagnostics;
using Fmp.Application.Preview;
using Fmp.Cli;

namespace MDPlayer.Fmp.Tests.Fixtures;

/// <summary>
/// Shared xUnit class fixture for the real-.vgz preview parity smoke tests.
/// Opens a single in-process preview session bound once to the checked-in
/// <c>master-ninja.vgz</c> so the expensive python3+ffmpeg capture runs at most
/// once for the whole class instead of once per heavy test. It also resolves
/// the python3/ffmpeg PATH probes once so each test can preserve its
/// <c>SkippableFact</c> gating without re-probing.
/// </summary>
public sealed class PreviewParityFixture : IAsyncDisposable
{
    public PreviewParityFixture()
    {
        HasPython = CommandIsAvailable("python3");
        HasFfmpeg = CommandIsAvailable("ffmpeg");
        HasPrereqs = HasPython && HasFfmpeg;
        SkipReason =
            $"real .vgz preview tests require python3 and ffmpeg on PATH (py={HasPython}, ff={HasFfmpeg})";

        VgzPath = Path.Combine(
            AppContext.BaseDirectory, "testfixtures", "master-ninja.vgz");
        Factory = new InProcessVisualizationPreviewSessionFactory();

        // Only open (and later capture) the shared session when the external
        // pipeline is actually present; otherwise tests skip and must not fail.
        if (HasPrereqs && File.Exists(VgzPath))
        {
            _cts = new CancellationTokenSource();
            Session = Factory
                .OpenWithTimelineAsync(VgzPath, null, _cts.Token)
                .GetAwaiter()
                .GetResult();
        }
    }

    /// <summary>Whether python3 resolved on PATH.</summary>
    public bool HasPython { get; }

    /// <summary>Whether ffmpeg resolved on PATH.</summary>
    public bool HasFfmpeg { get; }

    /// <summary>Whether both python3 and ffmpeg are available (capture can run).</summary>
    public bool HasPrereqs { get; }

    /// <summary>Reason string used by SkippableFact gating when prereqs are absent.</summary>
    public string SkipReason { get; }

    /// <summary>Absolute path of the checked-in <c>master-ninja.vgz</c> fixture.</summary>
    public string VgzPath { get; }

    public InProcessVisualizationPreviewSessionFactory Factory { get; }

    /// <summary>
    /// The single session shared by the heavy parity tests, or <c>null</c> when
    /// the external prereqs (or the fixture file) are unavailable so the tests
    /// skip. When non-null, its internal per-capture cache means the expensive
    /// python3+ffmpeg capture runs once across all four heavy tests.
    /// </summary>
    public IVisualizationPreviewSession? Session { get; }

    private CancellationTokenSource? _cts;

    public async ValueTask DisposeAsync()
    {
        if (Session is not null)
            await Session.DisposeAsync();
        if (_cts is not null)
            _cts.Dispose();
    }

    /// <summary>
    /// Probes whether an external command is on PATH. Used for the python3 /
    /// ffmpeg prereq checks so every heavy test shares the same probe result.
    /// </summary>
    public static bool CommandIsAvailable(string name)
    {
        try
        {
            using var probe = Process.Start(new ProcessStartInfo
            {
                FileName = name,
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (probe is null)
                return false;
            // Drain pipes so a chatty child (e.g. ffmpeg on stderr) cannot
            // block on a full pipe buffer.
            _ = probe.StandardOutput.ReadToEnd();
            _ = probe.StandardError.ReadToEnd();
            probe.WaitForExit(5000);
            // Presence is what matters; some builds (e.g. headless ffmpeg)
            // return a nonzero "shown help/version" code, so don't require 0.
            return probe.HasExited;
        }
        catch
        {
            return false;
        }
    }
}