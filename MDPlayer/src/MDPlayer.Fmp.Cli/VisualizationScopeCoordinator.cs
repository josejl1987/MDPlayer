using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Application.Contracts;

namespace Fmp.Cli;

internal sealed record GenericScopeArtifacts(
    StemPlan Plan,
    ScopeRenderer.ScopeResult Result,
    bool Enabled,
    bool HasIsolatedStems);

internal sealed class VisualizationScopeException : Exception
{
    public int ExitCode { get; }

    public VisualizationScopeException(string message, int exitCode)
        : base(message)
    {
        ExitCode = exitCode;
    }
}

internal static class VisualizationScopeCoordinator
{
    public static GenericScopeArtifacts Render(
        string backendId,
        FileInfo input,
        VisualizationWorkspace workspace,
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        long masterSamples)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runtime);
        PlaybackSettings playback = request.Playback;

        StemPlan plan = ScopePlanner.Plan(
            backendId,
            devices,
            voices,
            "auto");
        if (!plan.Supported)
        {
            throw new VisualizationScopeException(
                $"requested scope mode 'auto' is unavailable: {plan.Reason}",
                4);
        }

        ScopeRenderer.ScopeResult result = plan.Strategy switch
        {
            StemStrategy.VgmRenderedStems => VgmScopeRenderer.Render(
                input.FullName,
                workspace.ScopeDir,
                workspace.MasterAudioPath,
                playback.SampleRate,
                playback.LoopCount,
                playback.FadeSeconds,
                playback.TailSeconds,
                playback.MaximumDurationSeconds ?? 300),
            StemStrategy.MasterCaptured or StemStrategy.None => null,
            StemStrategy.FmpParallelSynthesis => throw new VisualizationScopeException(
                "FMP channel scopes must use the FMP visualization runner",
                7),
            _ => throw new VisualizationScopeException(
                $"scope strategy '{plan.Strategy}' has no executor",
                7),
        };

        bool isolated = result?.Success == true
            && result.Stems.Any(stem => stem.Name != "master" && stem.Success);
        if (plan.Strategy == StemStrategy.VgmRenderedStems && !isolated
            && request.Tracks.Selection != TrackSelectionMode.All)
        {
            string detail = string.IsNullOrWhiteSpace(result?.LastError)
                ? "no isolated stems were produced"
                : result.LastError;
            throw new VisualizationScopeException(
                $"channel scope rendering failed: {detail}",
                7);
        }

        if (!isolated)
        {
            if (!File.Exists(workspace.MasterAudioPath))
            {
                throw new VisualizationScopeException(
                    "master WAV was not produced by playback capture",
                    7);
            }
            result = CreateMasterResult(
                input,
                workspace,
                playback.SampleRate,
                masterSamples,
                plan.Strategy == StemStrategy.None
                    ? "scope_disabled"
                    : "master_fallback");
        }

        return new GenericScopeArtifacts(
            plan,
            result,
            plan.Strategy != StemStrategy.None,
            isolated);
    }

    private static ScopeRenderer.ScopeResult CreateMasterResult(
        FileInfo input,
        VisualizationWorkspace workspace,
        int sampleRate,
        long masterSamples,
        string completionReason)
    {
        var result = new ScopeRenderer.ScopeResult
        {
            Success = true,
            InputPath = input.FullName,
            OutputDir = workspace.ScopeDir,
            MasterSamples = masterSamples,
            SampleRate = sampleRate,
            CompletionReason = completionReason,
        };
        result.Stems.Add(new ScopeRenderer.StemResult
        {
            Name = "master",
            Label = "Master",
            WavPath = workspace.MasterAudioPath,
            RenderedSamples = masterSamples,
            Channels = 2,
            Success = true,
        });
        return result;
    }
}
