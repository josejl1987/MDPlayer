using System.Collections.ObjectModel;
using Fmp.Application.Contracts;
using Fmp.Application.Tools;
using Fmp.Application.Validation;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// External-tool resolution status. Uses
/// <see cref="VisualizationToolResolver.ResolveAll"/> and
/// <see cref="VisualizationToolResolver.ProbeVersionAsync"/> from the
/// Application layer and combines them with the request's tool requirements to
/// produce a one-line summary.
/// </summary>
public sealed class ToolStatusViewModel : ObservableObject
{
    private readonly Func<VisualizationRequest?> _requestProvider;
    private readonly Func<VisualizationPlanResult?> _planProvider;
    private readonly Func<ToolPaths> _toolPathsProvider;
    private readonly string? _renderCliUserPath;
    private bool _isRefreshing;
    private string _summary = "Checking tools…";

    public ToolStatusViewModel(
        Func<VisualizationRequest?> requestProvider,
        Func<VisualizationPlanResult?> planProvider,
        string? renderCliUserPath,
        Func<ToolPaths>? toolPathsProvider = null)
    {
        _requestProvider = requestProvider;
        _planProvider = planProvider;
        _renderCliUserPath = renderCliUserPath;
        _toolPathsProvider = toolPathsProvider ?? (() => new ToolPaths());
        RecheckCommand = new AsyncRelayCommand(() => RefreshAsync(CancellationToken.None));
        ShowDetailsCommand = new RelayCommand(() => DetailsRequested?.Invoke());
    }

    public AsyncRelayCommand RecheckCommand { get; }
    public RelayCommand ShowDetailsCommand { get; }
    public event Action? DetailsRequested;

    public ObservableCollection<ToolStatus> Statuses { get; } = new();

    public bool IsRefreshing
    {
        get => _isRefreshing;
        private set => SetProperty(ref _isRefreshing, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (IsRefreshing)
            return;
        IsRefreshing = true;
        try
        {
            VisualizationRequest? request = _requestProvider();
            ToolPaths overrides = _toolPathsProvider();

            IReadOnlyList<ToolStatus> resolved = VisualizationToolResolver.ResolveAll(overrides, _renderCliUserPath);

            var statuses = new List<ToolStatus>();
            foreach (ToolStatus status in resolved)
            {
                ToolStatus probed = await VisualizationToolResolver.ProbeVersionAsync(status, ct);
                statuses.Add(probed);
            }

            if (request is not null)
            {
                IReadOnlyList<ToolRequirement> requirements =
                    ToolRequirementResolver.Resolve(request);
                for (int i = 0; i < statuses.Count; i++)
                {
                    ToolRequirement? req = requirements.FirstOrDefault(r => r.Role == statuses[i].Role);
                    statuses[i] = statuses[i] with { IsRequired = req?.Kind == ToolRequirementKind.Required };
                }
            }

            Statuses.Clear();
            foreach (ToolStatus status in statuses)
                Statuses.Add(status);
            UpdateSummary();
        }
        catch (OperationCanceledException)
        {
            // Cancelled — leave the previous summary.
        }
        catch (Exception ex)
        {
            Summary = "tools: check failed (" + ex.Message + ")";
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private void UpdateSummary()
    {
        if (Statuses.Count == 0)
        {
            Summary = "tools: unknown";
            return;
        }

        var parts = new List<string>();
        foreach (ToolStatus status in Statuses)
        {
            switch (status.Role)
            {
                case ToolRoles.Ffmpeg:
                {
                    ToolStatus? nvenc = Statuses.FirstOrDefault(s => s.Role == ToolRoles.Nvenc);
                    parts.Add("FFmpeg: " + (status.IsAvailable ? "available" : "unavailable")
                        + (nvenc is null
                            ? ""
                            : " · NVENC " + (nvenc.IsAvailable ? "available" : "unavailable")));
                    break;
                }
                case ToolRoles.Corrscope:
                    parts.Add("Corrscope " + (status.IsAvailable ? "available" : "unavailable"));
                    break;
                case ToolRoles.AnalysisPython:
                    parts.Add("analysis " + (status.IsAvailable ? "available" : "unavailable"));
                    break;
                case ToolRoles.RenderCli:
                    parts.Add("render CLI " + (status.IsAvailable ? "available" : "unavailable"));
                    break;
                case ToolRoles.Nvenc:
                    break; // Folded into the FFmpeg entry.
                default:
                    parts.Add(status.Role + " " + (status.IsAvailable ? "available" : "unavailable"));
                    break;
            }
        }

        Summary = string.Join(" · ", parts);
    }
}
