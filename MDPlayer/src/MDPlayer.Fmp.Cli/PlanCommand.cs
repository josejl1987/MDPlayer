using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
using Fmp.Application.Export;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// `mdplayer-render plan` — resolves a request JSON against a captured
/// timeline and prints the concrete layout plan (JSON or human summary).
/// </summary>
public static class PlanCommand
{
    public static int Handle(string[] args)
    {
        string requestJsonPath = null;
        string timelinePath = null;
        string timelineOutPath = null;
        string captureDir = null;
        string captureKey = null;
        bool json = false;

        var reader = new ArgumentReader(args ?? Array.Empty<string>());
        try
        {
            while (reader.HasMore)
            {
                if (reader.TryReadOption(out string name, out string value))
                {
                    switch (name)
                    {
                        case "--request-json": requestJsonPath = reader.RequireValue(name); break;
                        case "--timeline": timelinePath = reader.RequireValue(name); break;
                        case "--timeline-out": timelineOutPath = reader.RequireValue(name); break;
                        case "--capture-dir": captureDir = reader.RequireValue(name); break;
                        case "--capture-key": captureKey = reader.RequireValue(name); break;
                        case "--json" when value == null: json = true; break;
                        default: throw new ArgumentException($"unknown option '{name}'");
                    }
                }
                else
                {
                    string positional = reader.Next();
                    if (positional == "--") continue;
                    throw new ArgumentException($"unexpected argument '{positional}'");
                }
            }

            if (string.IsNullOrWhiteSpace(requestJsonPath))
                throw new ArgumentException("--request-json PATH is required");

            return Run(requestJsonPath, timelinePath, timelineOutPath, captureDir, captureKey, json);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (VisualizationRequestException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }
        catch (TrackPreparationException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (VisualizationBackendResolutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (VisualizationExecutionException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 7;
        }
    }

    private static int Run(
        string requestJsonPath,
        string timelinePath,
        string timelineOutPath,
        string? captureDir,
        string? captureKey,
        bool json)
    {
        VisualizationRequest request = VisualizationRequestSerializer.ReadFromFile(requestJsonPath);
        var runtime = new RenderRuntimeOptions();
        VisualizationBackendResolution resolution =
            VisualizationBackendResolver.Resolve(request, runtime);

        // Planning must never touch the real output workspace: capture writes
        // the canonical timeline (and, for generic backends, the master WAV)
        // into the workspace, so a plain `plan` call would otherwise create or
        // replace <output>/timeline.json and <output>/audio/master.wav before
        // the user ever renders. Only an explicit --timeline-out survives.
        using TemporaryVisualizationWorkspace workspace =
            TemporaryVisualizationWorkspace.Create("Plan");
        workspace.EnsureDirectories();

        string? durableTimelineOut =
            string.IsNullOrWhiteSpace(timelineOutPath) ? null : timelineOutPath;

        // --capture-dir reuse: when the supplied capture directory already
        // holds a valid timeline.json, plan from that bundle (reuse) instead of
        // re-capturing. Otherwise the fresh capture's timeline is written into
        // the same directory so the caller can commit the bundle afterwards.
        string? bundleTimeline =
            !string.IsNullOrWhiteSpace(captureDir)
                ? Path.Combine(captureDir, "timeline.json")
                : null;
        bool reuseBundle =
            bundleTimeline != null && File.Exists(bundleTimeline);

        string? effectiveSeed =
            reuseBundle ? bundleTimeline
            : string.IsNullOrWhiteSpace(timelinePath) ? null : timelinePath;

        string? bundleOut =
            !reuseBundle && !string.IsNullOrWhiteSpace(captureDir)
                ? bundleTimeline
                : durableTimelineOut;

        // The planning path is timeline/layout-only: it captures (or seeds) the
        // semantic timeline, resolves the layout and builds the pure plan. It
        // deliberately does NOT synthesize FMP stems, render scopes, run energy
        // analysis, or write scope/master artifacts.
        PreparedTimeline timeline = VisualizationPrepareCoordinator.CaptureTimeline(
            request,
            runtime,
            workspace,
            resolution,
            PrepareFmpTrack(resolution, runtime),
            seedTimelinePath: effectiveSeed,
            timelineOutPath: bundleOut);

        ResolvedVisualizationLayout layout = VisualizationLayoutBuilder.Build(
            timeline.Timeline,
            VisualizationLayoutModeMapper.FromComposition(request.Composition),
            request.ToLayoutSettings());

        VisualizationPlanResult plan = VisualizationPlanBuilder.Build(
            request,
            timeline.Timeline,
            layout,
            durableTimelineOut ?? workspace.TimelinePath);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
        }
        else
        {
            WriteHumanSummary(plan);
        }
        return 0;
    }

    private static PreparedTrack? PrepareFmpTrack(
        VisualizationBackendResolution resolution,
        RenderRuntimeOptions runtime)
    {
        if (!string.Equals(resolution.Backend.Id, "fmp", StringComparison.Ordinal))
            return null;
        return TrackPreparation.Prepare(
            resolution.Input.FullName,
            resolution.FmpComPath,
            runtime.AssetsDir,
            resolution.SearchPaths);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static void WriteHumanSummary(VisualizationPlanResult plan)
    {
        Console.WriteLine($"Resolved layout: {plan.ResolvedLayout}");
        Console.WriteLine($"Requested layout: {plan.RequestedLayout}");
        Console.WriteLine($"Input: {plan.InputPath}");
        int selected = plan.Tracks.Count(track => track.Selected);
        Console.WriteLine($"Tracks: {plan.Tracks.Count} ({selected} selected)");
        if (plan.IncludedTrackIds.Count > 0)
            Console.WriteLine($"Included tracks: {string.Join(", ", plan.IncludedTrackIds)}");
        if (plan.ExcludedTrackIds.Count > 0)
            Console.WriteLine($"Excluded tracks: {string.Join(", ", plan.ExcludedTrackIds)}");
        Console.WriteLine($"Panels: {plan.Regions.Count}");
        Console.WriteLine($"Estimated duration: {plan.EstimatedDurationSeconds:F1}s ({plan.EstimatedFrameCount} frames)");
        foreach (ToolRequirement requirement in plan.ToolRequirements)
            Console.WriteLine($"Tool: {requirement.Role} ({requirement.Kind.ToString().ToLowerInvariant()}) — {requirement.Feature}");
        foreach (ValidationIssue issue in plan.ValidationIssues)
            Console.WriteLine($"{issue.Severity.ToString().ToLowerInvariant()}: {issue.Message}");
        if (plan.Capabilities.Scope && plan.Capabilities.Semantic)
            Console.WriteLine("Content: semantic + scope");
        else if (plan.Capabilities.Scope)
            Console.WriteLine("Content: scope only");
        else if (plan.Capabilities.Semantic)
            Console.WriteLine("Content: semantic only");
    }
}
