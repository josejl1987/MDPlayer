using Fmp.Core.Rendering;
using Fmp.Core.Visualization;
using Fmp.Core.Audio;
using Fmp.Application.Contracts;

namespace Fmp.Cli;

internal sealed record VisualizationScopeArtifacts(
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
    private static VisualizationScopeArtifacts RenderFmp(
        StemPlan plan,
        PreparedTrack track,
        VisualizationRequest request,
        VisualizationWorkspace workspace,
        bool scopesRequired)
    {
        PlaybackSettings playback = request.Playback;
        StemPass[] stems = scopesRequired ? DefaultStems.All : [DefaultStems.All[0]];
        ScopeRenderer.ScopeResult result = new ScopeRenderer(
            track.Assets, track.FileSystem, playback.SampleRate,
            playback.LoopCount, playback.FadeSeconds, playback.TailSeconds,
            playback.MaximumDurationSeconds ?? 300, playback.SsgGainDb).Render(
                track.Data, track.Input.FullName, workspace.ScopeDir, stems,
                progress: null, skipSilentStems: false,
                audioDir: workspace.AudioDir,
                metadataPath: workspace.ScopeMetadataPath);
        if (!result.Success)
            throw new VisualizationScopeException(
                $"scope render failed: {result.LastError}", 7);
        bool isolated = result.Stems.Any(stem => stem.Name != "master" && stem.Success);
        return new VisualizationScopeArtifacts(plan, result, scopesRequired, isolated);
    }

    public static VisualizationScopeArtifacts Render(
        string backendId,
        FileInfo input,
        VisualizationWorkspace workspace,
        VisualizationRequest request,
        RenderRuntimeOptions runtime,
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        long masterSamples,
        PreparedTrack? preparedFmpTrack = null,
        bool scopesRequired = true)
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
            scopesRequired ? "auto" : "off");
        if (!plan.Supported && plan.Strategy != StemStrategy.FmpParallelSynthesis)
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
            StemStrategy.FmpParallelSynthesis => RenderFmp(
                plan,
                preparedFmpTrack ?? throw new InvalidOperationException(
                    "FMP track was not prepared"),
                request, workspace, scopesRequired).Result,
            _ => throw new VisualizationScopeException(
                $"scope strategy '{plan.Strategy}' has no executor",
                7),
        };

        bool isolated = result?.Success == true
            && result.Stems.Any(stem => stem.Name != "master" && stem.Success);
        // Isolated stems are preferred but not mandatory: when VGM stem
        // extraction produces no isolated stems, the master-waveform fallback
        // below applies rather than failing the whole render.

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

        return new VisualizationScopeArtifacts(
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
