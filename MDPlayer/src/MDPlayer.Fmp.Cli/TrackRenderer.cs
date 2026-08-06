using Fmp.Core.Metadata;
using Fmp.Core.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Application-level single-track render. Owns the renderer construction,
/// render-options wiring, WAV rendering and metadata extraction — no console
/// output. Commands own presentation (messages, exit codes, JSON output).
/// </summary>
internal sealed class TrackRenderer
{
    public RenderOutcome Render(PreparedTrack track, string output, BatchRenderSettings settings)
    {
        try
        {
            var renderer = new FmpRenderer(track.Assets, track.FileSystem, settings.SampleRate, settings.SsgGainDb);
            var renderOpts = new FmpRenderer.Options
            {
                LoopCount = settings.Loops,
                FadeSeconds = settings.Fade,
                TailSeconds = settings.Tail,
                MaxDurationSeconds = settings.Duration ?? settings.MaxDuration,
                TimeoutSeconds = settings.Timeout,
                TracePath = settings.TracePath,
                OpnaBackend = settings.OpnaBackend switch
                {
                    "native-audio" => FmpOpnaBackend.NativeAudio,
                    _ => FmpOpnaBackend.Mdsound,
                },
                AssetDumpDirectory = settings.FurnaceAssetDumpDirectory,
            };

            var result = renderer.RenderToWav(track.Data, track.Input.FullName, output, renderOpts);

            var metadata = FmpMetadata.FromFmpFile(
                track.Input.FullName,
                settings.SampleRate,
                settings.Loops,
                settings.Fade);
            if (result.Success)
            {
                metadata.RenderedSamples = result.RenderedSamples;
                metadata.RenderedDuration = TimeSpan.FromSeconds(
                    (double)result.RenderedSamples / settings.SampleRate).ToString(@"hh\:mm\:ss\.fff");
            }

            return new RenderOutcome
            {
                Success = result.Success,
                LastError = result.LastError,
                RenderedSamples = result.RenderedSamples,
                FinalOpnaMasterClock = result.FinalOpnaMasterClock,
                StopReason = result.StopReason,
                Metadata = metadata,
                AssetDumpError = result.AssetDumpError,
            };
        }
        catch (Exception ex)
        {
            return new RenderOutcome { Success = false, LastError = ex.Message };
        }
    }
}

internal sealed class RenderOutcome
{
    public bool Success { get; init; }
    public string LastError { get; init; }
    public long RenderedSamples { get; init; }
    public ulong FinalOpnaMasterClock { get; init; }
    public string StopReason { get; init; }
    public string AssetDumpError { get; init; }
    public FmpMetadata Metadata { get; init; }
}
