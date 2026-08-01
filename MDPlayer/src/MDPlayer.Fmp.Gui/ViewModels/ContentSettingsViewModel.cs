using System.Collections.ObjectModel;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>CONTENT settings: channel selection, tracks, grouping, scopes, analysis.</summary>
public sealed class ContentSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded;
    private string _selectedChannelMode = ChannelSelectionMode.Active.ToString();
    private bool _isCustomChannels;
    private string _selectedGrouping = TrackGroupingMode.None.ToString();
    private bool _isScopePanelEnabled;
    private string _scopeDisabledReason = "Scopes are unavailable because no synchronized stems can be captured for this input.";
    private decimal? _scopeRatio;
    private string _selectedScopePosition = ScopePosition.Bottom.ToString();
    private bool? _analysisEnabled;
    private string _selectedAnalysisDetail = AnalysisDetail.Standard.ToString();
    private string _selectedAnalysisOverlay = AnalysisOverlayMode.None.ToString();
    private string _analysisStatusText = "Not run";

    public ContentSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IReadOnlyList<string> ChannelModeOptions { get; } = Enum.GetNames<ChannelSelectionMode>();
    public IReadOnlyList<string> GroupingOptions { get; } = Enum.GetNames<TrackGroupingMode>();
    public IReadOnlyList<string> ScopePositionOptions { get; } = Enum.GetNames<ScopePosition>();
    public IReadOnlyList<string> AnalysisDetailOptions { get; } = Enum.GetNames<AnalysisDetail>();
    public IReadOnlyList<string> AnalysisOverlayOptions { get; } = Enum.GetNames<AnalysisOverlayMode>();

    public ObservableCollection<TrackItemViewModel> Tracks { get; } = new();

    public string SelectedChannelMode
    {
        get => _selectedChannelMode;
        set
        {
            if (!SetProperty(ref _selectedChannelMode, value) || _suppress)
                return;
            if (Enum.TryParse<ChannelSelectionMode>(value, out var mode))
                _owner.ApplySetting(nameof(VisualizationRequest.ChannelSelection), r => r with { ChannelSelection = mode });
        }
    }

    public bool IsCustomChannels
    {
        get => _isCustomChannels;
        private set => SetProperty(ref _isCustomChannels, value);
    }

    public string SelectedGrouping
    {
        get => _selectedGrouping;
        set
        {
            if (!SetProperty(ref _selectedGrouping, value) || _suppress)
                return;
            if (Enum.TryParse<TrackGroupingMode>(value, out var grouping))
                _owner.ApplySetting(nameof(VisualizationRequest.Grouping), r => r with { Grouping = grouping });
        }
    }

    public bool IsScopePanelEnabled
    {
        get => _isScopePanelEnabled;
        private set => SetProperty(ref _isScopePanelEnabled, value);
    }

    public string ScopeDisabledReason
    {
        get => _scopeDisabledReason;
        private set => SetProperty(ref _scopeDisabledReason, value);
    }

    public decimal? ScopeRatio
    {
        get => _scopeRatio;
        set
        {
            if (!SetProperty(ref _scopeRatio, value) || _suppress)
                return;
            double? ratio = value is > 0 ? (double)value : null;
            _owner.ApplySetting(nameof(VisualizationRequest.ScopeRatio), r => r with { ScopeRatio = ratio });
        }
    }

    public string SelectedScopePosition
    {
        get => _selectedScopePosition;
        set
        {
            if (!SetProperty(ref _selectedScopePosition, value) || _suppress)
                return;
            if (Enum.TryParse<ScopePosition>(value, out var position))
                _owner.ApplySetting(nameof(VisualizationRequest.ScopePosition), r => r with { ScopePosition = position });
        }
    }

    public bool? AnalysisEnabled
    {
        get => _analysisEnabled;
        set
        {
            if (!SetProperty(ref _analysisEnabled, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.AnalysisEnabled), r => r with { AnalysisEnabled = value == true });
        }
    }

    public string SelectedAnalysisDetail
    {
        get => _selectedAnalysisDetail;
        set
        {
            if (!SetProperty(ref _selectedAnalysisDetail, value) || _suppress)
                return;
            if (Enum.TryParse<AnalysisDetail>(value, out var detail))
                _owner.ApplySetting(nameof(VisualizationRequest.AnalysisDetail), r => r with { AnalysisDetail = detail });
        }
    }

    public string SelectedAnalysisOverlay
    {
        get => _selectedAnalysisOverlay;
        set
        {
            if (!SetProperty(ref _selectedAnalysisOverlay, value) || _suppress)
                return;
            if (Enum.TryParse<AnalysisOverlayMode>(value, out var overlay))
                _owner.ApplySetting(nameof(VisualizationRequest.AnalysisOverlay), r => r with { AnalysisOverlay = overlay });
        }
    }

    public string AnalysisStatusText
    {
        get => _analysisStatusText;
        private set => SetProperty(ref _analysisStatusText, value);
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            SelectedChannelMode = request.ChannelSelection.ToString();
            IsCustomChannels = request.ChannelSelection == ChannelSelectionMode.Custom;
            SelectedGrouping = request.Grouping.ToString();
            if (request.ScopeRatio is double ratio)
                ScopeRatio = (decimal)ratio;
            else
                ScopeRatio = null;
            SelectedScopePosition = request.ScopePosition.ToString();
            AnalysisEnabled = request.AnalysisEnabled;
            SelectedAnalysisDetail = request.AnalysisDetail.ToString();
            SelectedAnalysisOverlay = request.AnalysisOverlay.ToString();
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
            bool scopesSupported = input?.SupportsScopeCapture == true;
            if (plan is not null)
                scopesSupported = scopesSupported && plan.Capabilities.Scope;
            IsScopePanelEnabled = scopesSupported;

            if (plan is null || plan.Tracks.Count == 0)
            {
                Tracks.Clear();
            }
            else
            {
                var rebuilt = plan.Tracks.Select(TrackItemViewModel.From).ToList();
                foreach (TrackItemViewModel item in rebuilt)
                    item.OnToggle = OnTrackToggled;
                // Stable order: only reorder when the plan changes (full rebuild).
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

    public void SynchronizeAnalysisStatus(VisualizationSessionCapabilities? capabilities)
    {
        bool enabled = _analysisEnabled == true;
        AnalysisStatusText = !enabled
            ? "Not run"
            : capabilities?.HasAnalysisResult == true
                ? "Cached"
                : capabilities?.AnalysisAvailable == true
                    ? "Available"
                    : capabilities is null
                        ? "Not run"
                        : "Unavailable";
    }

    private void OnTrackToggled(TrackItemViewModel item)
    {
        if (_suppress)
            return;
        bool selected = item.IsSelected == true;
        _owner.ApplySetting(nameof(VisualizationRequest.IncludedTrackIds), request =>
        {
            var included = request.IncludedTrackIds.ToHashSet();
            var excluded = request.ExcludedTrackIds.ToHashSet();
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
                ChannelSelection = ChannelSelectionMode.Custom,
                IncludedTrackIds = included.OrderBy(id => id).ToArray(),
                ExcludedTrackIds = excluded.OrderBy(id => id).ToArray(),
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
