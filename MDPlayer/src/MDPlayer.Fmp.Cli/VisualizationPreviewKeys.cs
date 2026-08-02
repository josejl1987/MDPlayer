using Fmp.Application.Contracts;

namespace Fmp.Cli;

/// <summary>
/// Staged invalidation keys for the in-process preview session. Each request
/// is split into four staged keys that change independently so the GUI and the
/// session rebuild only the cheapest stage affected by a user action.
///
/// <para>Conceptually:</para>
/// <list type="bullet">
/// <item><see cref="TimelineCaptureKey"/> — what must be replayed/emulated to
/// reproduce the song's semantic timeline and master audio.</item>
/// <item><see cref="ScopeAssetKey"/> — channel stems / scope projection identity
/// (track selection), independent of playback capture.</item>
/// <item><see cref="PlanKey"/> — panel topology, layout geometry, frame count
/// and time window grid.</item>
/// <item><see cref="FrameStyleKey"/> — the final frame look (quality, style,
/// presentation text).</item>
/// </list>
/// Output path, encoder and overwrite are deliberately excluded: they do not
/// affect any preview stage.
/// </summary>
internal readonly record struct TimelineCaptureKey(
    string InputPath,
    long InputLength,
    long InputLastWriteUtcTicks,
    string Backend,
    int LoopCount,
    double FadeSeconds,
    double TailSeconds,
    double? MaximumDurationSeconds,
    int SampleRate,
    double SsgGainDb,
    SpcPitchInterpretation SpcPitch)
{
    /// <summary>
    /// Builds the key from a real <see cref="FileInfo"/> so input identity
    /// (path, size, last-write time) is part of the capture identity. A missing
    /// file yields placeholder values (−1 length / 0 ticks) rather than
    /// throwing: this path exists only for comparing request snapshots, and the
    /// real session rejects a missing input in its own <c>KeyFor</c> step.
    /// </summary>
    public static TimelineCaptureKey From(
        VisualizationRequest request,
        FileInfo file,
        RenderRuntimeOptions runtime)
    {
        long length = file.Exists ? file.Length : -1;
        long lastWriteTicks =
            file.Exists ? file.LastWriteTimeUtc.Ticks : 0;

        return new(
            Path.GetFullPath(request.InputPath),
            length,
            lastWriteTicks,
            runtime.Backend ?? "auto",
            request.Playback.LoopCount,
            request.Playback.FadeSeconds,
            request.Playback.TailSeconds,
            request.Playback.MaximumDurationSeconds,
            request.Playback.SampleRate,
            request.Playback.SsgGainDb,
            request.Playback.SpcPitch);
    }
}

/// <summary>
/// Normalized request-specific track/scope projection identity. Track
/// selection drives scope projection and channel-energy preparation, so
/// changing it must not rerun playback capture but may rebuild the plan.
/// It does not cause the raw stem set to be regenerated.
/// Equality is value-based and ID-order-insensitive: included/excluded IDs are
/// compared by content (ordinal sequence), not by array reference.
/// </summary>
internal sealed class ScopeAssetKey : IEquatable<ScopeAssetKey>
{
    public ScopeAssetKey(
        TimelineCaptureKey timeline,
        TrackSelectionMode selection,
        IReadOnlyList<string> includedIds,
        IReadOnlyList<string> excludedIds,
        bool includeInactiveDiagnosticTracks)
    {
        Timeline = timeline;
        Selection = selection;
        IncludedIds = includedIds;
        ExcludedIds = excludedIds;
        IncludeInactiveDiagnosticTracks = includeInactiveDiagnosticTracks;
    }

    public TimelineCaptureKey Timeline { get; }
    public TrackSelectionMode Selection { get; }
    public IReadOnlyList<string> IncludedIds { get; }
    public IReadOnlyList<string> ExcludedIds { get; }
    public bool IncludeInactiveDiagnosticTracks { get; }

    public static ScopeAssetKey From(
        VisualizationRequest request,
        TimelineCaptureKey timeline)
        => new(
            timeline,
            request.Tracks.Selection,
            NormalizeIds(request.Tracks.IncludedIds),
            NormalizeIds(request.Tracks.ExcludedIds),
            request.Tracks.IncludeInactiveDiagnosticTracks);

    private static string[] NormalizeIds(IReadOnlyList<string> ids)
        => ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

    public bool Equals(ScopeAssetKey? other)
    {
        if (other is null)
            return false;
        if (ReferenceEquals(this, other))
            return true;
        return Timeline == other.Timeline
            && Selection == other.Selection
            && IncludeInactiveDiagnosticTracks == other.IncludeInactiveDiagnosticTracks
            && ListEquals(IncludedIds, other.IncludedIds)
            && ListEquals(ExcludedIds, other.ExcludedIds);
    }

    public override bool Equals(object? obj) => obj is ScopeAssetKey other && Equals(other);

    public override int GetHashCode()
    {
        HashCode h = new();
        h.Add(Timeline);
        h.Add(Selection);
        h.Add(IncludeInactiveDiagnosticTracks);
        AddList(ref h, IncludedIds);
        AddList(ref h, ExcludedIds);
        return h.ToHashCode();
    }

    private static bool ListEquals(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count != b.Count)
            return false;
        for (int i = 0; i < a.Count; i++)
            if (!string.Equals(a[i], b[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    private static void AddList(
        ref HashCode hash,
        IReadOnlyList<string> list)
    {
        foreach (string id in list)
            hash.Add(id, StringComparer.Ordinal);
    }
}

/// <summary>
/// Identity of panel topology and layout geometry. Recomputed (and the plan
/// rebuilt) only when composition, preview dimensions, frame rate, time window
/// or time/structure grids change. Does not include style or title text.
/// </summary>
internal sealed record PlanKey(
    ScopeAssetKey ScopeAssets,
    CompositionKind Composition,
    int Width,
    int Height,
    int FpsNumerator,
    int FpsDenominator,
    ViewSettings View)
{
    public static PlanKey From(VisualizationRequest request, ScopeAssetKey scopeAssets)
        => new(
            scopeAssets,
            request.Composition,
            request.Output.Width,
            request.Output.Height,
            request.Output.FpsNumerator,
            request.Output.FpsDenominator,
            request.View);
}

/// <summary>
/// Identity of the final rendered frame's look: render quality, visual style
/// and presentation text on top of the (already-planned) source. Output path,
/// encoder and overwrite never appear here.
/// </summary>
internal sealed record FrameStyleKey(
    PlanKey Plan,
    RenderQuality Quality,
    StyleSettings Style,
    PresentationSettings Presentation)
{
    public static FrameStyleKey From(VisualizationRequest request, PlanKey plan)
        => new(
            plan,
            request.Output.Quality,
            request.Style,
            request.Presentation);
}
