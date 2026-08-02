using System.Collections.ObjectModel;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// CONTENT settings: track selection (schema 2). Obsolete controls (scope
/// ratio/position, grouping, analysis detail/overlay) are removed per the
/// greenfield reset. Scope source selection is composition-specific and lives
/// here only for Scope Stage.
/// </summary>
public sealed class ContentSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private string _selectedTrackSelection = TrackSelectionMode.Active.ToString();
    private bool _isCustomSelection;
    private bool _includeInactiveDiagnostics = true;

    public ContentSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public IReadOnlyList<string> TrackSelectionOptions { get; } = new[] { "Active", "All", "Custom" };

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public string SelectedTrackSelection
    {
        get => _selectedTrackSelection;
        set
        {
            if (!SetProperty(ref _selectedTrackSelection, value) || _suppress)
                return;
            if (Enum.TryParse<TrackSelectionMode>(value, out var mode))
                _owner.ApplyVisualSetting( r => r with
                {
                    Tracks = r.Tracks with { Selection = mode },
                });
        }
    }

    public bool IsCustomSelection
    {
        get => _isCustomSelection;
        private set => SetProperty(ref _isCustomSelection, value);
    }

    public bool IncludeInactiveDiagnostics
    {
        get => _includeInactiveDiagnostics;
        set
        {
            if (!SetProperty(ref _includeInactiveDiagnostics, value) || _suppress)
                return;
            _owner.ApplyVisualSetting( r => r with
            {
                Tracks = r.Tracks with { IncludeInactiveDiagnosticTracks = value },
            });
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            SelectedTrackSelection = request.Tracks.Selection.ToString();
            IsCustomSelection = request.Tracks.Selection == TrackSelectionMode.Custom;
            IncludeInactiveDiagnostics = request.Tracks.IncludeInactiveDiagnosticTracks;
        }
        finally
        {
            _suppress = false;
        }
    }

    public void SynchronizePlan(VisualizationPlanResult? plan, VisualizationInputInfo? input)
    {
        _suppress = true;
        try
        {
            if (plan is null || plan.Tracks.Count == 0)
            {
                Tracks.Clear();
            }
            else
            {
                var rebuilt = plan.Tracks.Select(TrackItemViewModel.From).ToList();
                foreach (TrackItemViewModel item in rebuilt)
                    item.OnToggle = OnTrackToggled;
                if (!Tracks.SequenceEqual(rebuilt, TrackComparer.Instance))
                {
                    Tracks.Clear();
                    foreach (TrackItemViewModel item in rebuilt)
                        Tracks.Add(item);
                }
            }
        }
        finally
        {
            _suppress = false;
        }
    }

    private void OnTrackToggled(TrackItemViewModel item)
    {
        if (_suppress)
            return;
        bool selected = item.IsSelected == true;
        _owner.ApplyVisualSetting( request =>
        {
            var included = request.Tracks.IncludedIds.ToHashSet();
            var excluded = request.Tracks.ExcludedIds.ToHashSet();
            if (selected)
            {
                included.Add(item.TrackId);
                excluded.Remove(item.TrackId);
            }
            else
            {
                included.Remove(item.TrackId);
                excluded.Add(item.TrackId);
            }
            return request with
            {
                Tracks = request.Tracks with
                {
                    Selection = TrackSelectionMode.Custom,
                    IncludedIds = included.OrderBy(id => id).ToArray(),
                    ExcludedIds = excluded.OrderBy(id => id).ToArray(),
                },
            };
        });
    }

    private sealed class TrackComparer : IEqualityComparer<TrackItemViewModel>
    {
        public static readonly TrackComparer Instance = new();

        public bool Equals(TrackItemViewModel? x, TrackItemViewModel? y)
        {
            if (ReferenceEquals(x, y))
                return true;
            if (x is null || y is null)
                return false;
            return x.TrackId == y.TrackId
                && x.IsSelected == y.IsSelected
                && x.DisplayName == y.DisplayName;
        }

        public int GetHashCode(TrackItemViewModel obj) => obj.TrackId.GetHashCode();
    }
}