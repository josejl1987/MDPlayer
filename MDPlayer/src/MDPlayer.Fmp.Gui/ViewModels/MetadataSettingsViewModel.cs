using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>PRESENTATION (metadata) settings: title, subtitle, credits, font path.</summary>
public sealed class MetadataSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded;
    private string? _title;
    private string? _subtitle;
    private string? _credits;
    private string? _fontPath;

    public MetadataSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        RestoreDetectedCommand = new RelayCommand(() => _owner.RestoreDetectedMetadata());
        BrowseFontCommand = new AsyncRelayCommand(BrowseFontAsync);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public RelayCommand RestoreDetectedCommand { get; }
    public AsyncRelayCommand BrowseFontCommand { get; }

    public string? Title
    {
        get => _title;
        set
        {
            if (!SetProperty(ref _title, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PresentationSettings.Title), r => r with
                {
                    Presentation = r.Presentation with { Title = value },
                });
        }
    }

    public string? Subtitle
    {
        get => _subtitle;
        set
        {
            if (!SetProperty(ref _subtitle, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PresentationSettings.Subtitle), r => r with
                {
                    Presentation = r.Presentation with { Subtitle = value },
                });
        }
    }

    public string? Credits
    {
        get => _credits;
        set
        {
            if (!SetProperty(ref _credits, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PresentationSettings.Credits), r => r with
                {
                    Presentation = r.Presentation with { Credits = value },
                });
        }
    }

    public string? FontPath
    {
        get => _fontPath;
        set
        {
            if (!SetProperty(ref _fontPath, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PresentationSettings.FontPath), r => r with
                {
                    Presentation = r.Presentation with { FontPath = value },
                });
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            Title = request.Presentation.Title;
            Subtitle = request.Presentation.Subtitle;
            Credits = request.Presentation.Credits;
            FontPath = request.Presentation.FontPath;
        }
        finally
        {
            _suppress = false;
        }
    }

    private async Task BrowseFontAsync()
    {
        string? path = await _owner.ChooseFontFileAsync();
        if (path is null)
            return;
        _owner.ApplySetting(nameof(PresentationSettings.FontPath), r => r with
            {
                Presentation = r.Presentation with { FontPath = path },
            });
    }
}
