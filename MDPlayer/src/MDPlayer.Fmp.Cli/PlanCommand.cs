using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Application.Contracts;
using Fmp.Application.Export;

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

            return Run(requestJsonPath, timelinePath, timelineOutPath, json);
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
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 7;
        }
    }

    private static int Run(string requestJsonPath, string timelinePath, string timelineOutPath, bool json)
    {
        VisualizationRequest request = VisualizationRequestSerializer.ReadFromFile(requestJsonPath);
        VisualizationPlanning.PlanOutput output = VisualizationPlanning.Prepare(
            request, new RenderRuntimeOptions(), timelinePath, timelineOutPath);

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(output.Plan, JsonOptions));
        }
        else
        {
            WriteHumanSummary(output.Plan);
        }
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

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
