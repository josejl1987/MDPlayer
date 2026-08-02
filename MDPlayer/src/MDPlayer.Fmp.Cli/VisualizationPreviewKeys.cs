using Fmp.Application.Contracts;

namespace Fmp.Cli;

/// <summary>
/// Piecewise change detection for the in-process preview session.
///
/// <see cref="CaptureKey"/> covers only settings that affect timeline or audio
/// capture (input, loop/fade/tail, duration cap, sample rate, SSG gain, SPC
/// pitch, and the explicitly selected backend). Changing title, colors or
/// dimensions must not recapture the song; changing sample rate, loop count,
/// SSG gain or SPC pitch must.
///
/// <see cref="RenderKey"/> covers every setting that affects frame construction.
/// It is deliberately compared as a whole (the simplest correctness-preserving
/// implementation) and deliberately excludes output path / encoder / overwrite.
/// </summary>
internal readonly record struct CaptureKey(
    string InputPath,
    string Backend,
    int LoopCount,
    double FadeSeconds,
    double TailSeconds,
    double? MaximumDurationSeconds,
    int SampleRate,
    double SsgGainDb,
    SpcPitchInterpretation SpcPitch)
{
    public static CaptureKey From(
        VisualizationRequest request,
        RenderRuntimeOptions runtime)
        => new(
            Path.GetFullPath(request.InputPath),
            runtime.Backend ?? "auto",
            request.Playback.LoopCount,
            request.Playback.FadeSeconds,
            request.Playback.TailSeconds,
            request.Playback.MaximumDurationSeconds,
            request.Playback.SampleRate,
            request.Playback.SsgGainDb,
            request.Playback.SpcPitch);
}

internal sealed record RenderKey(
    CompositionKind Composition,
    OutputSettings Output,
    TrackSettings Tracks,
    ViewSettings View,
    StyleSettings Style,
    PresentationSettings Presentation)
{
    public static RenderKey From(VisualizationRequest request)
        => new(
            request.Composition,
            request.Output,
            request.Tracks,
            request.View,
            request.Style,
            request.Presentation);
}