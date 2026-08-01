using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>BASIC settings: composition, quality, resolution, frame rate.</summary>
public sealed class BasicSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded = true;
    private string _selectedQuality = RenderQuality.Standard.ToString();
    private string _selectedResolution = "1920x1080";
    private bool _isCustomResolution;
    private decimal? _customWidth = 1920;
    private decimal? _customHeight = 1080;
    private string _selectedFps = "60";

    public BasicSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        CompositionCards =
        [
            new CompositionCardViewModel(
                "Performance",
                "Large shared roll · best for publishing",
                CompositionKind.Performance,
                SelectComposition),
            new CompositionCardViewModel(
                "Scope Stage",
                "Large waveforms · best for timbre",
                CompositionKind.ScopeStage,
                SelectComposition),
            new CompositionCardViewModel(
                "Diagnostic",
                "Technical channel grid · best for inspection",
                CompositionKind.Diagnostic,
                SelectComposition),
        ];
    }

    /// <summary>The three intentional publishing compositions, shown as cards.</summary>
    public IReadOnlyList<CompositionCardViewModel> CompositionCards { get; }

    private void SelectComposition(CompositionKind composition)
    {
        _owner.ApplySetting(nameof(VisualizationRequest.Composition),
            r => r with { Composition = composition });
        RefreshCardSelection(composition);
    }

    private void RefreshCardSelection(CompositionKind composition)
    {
        foreach (CompositionCardViewModel card in CompositionCards)
            card.IsSelected = card.Composition == composition;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IReadOnlyList<string> QualityOptions { get; } =
        new[] { RenderQuality.Draft.ToString(), RenderQuality.Standard.ToString(), RenderQuality.Final.ToString() };

    public IReadOnlyList<string> ResolutionOptions { get; } = new[]
    {
        "1280x720", "1920x1080", "2560x1440", "3840x2160", "Custom",
    };

    public IReadOnlyList<string> FpsOptions { get; } = new[] { "24", "25", "30", "50", "60", "59.94" };

    public string SelectedQuality
    {
        get => _selectedQuality;
        set
        {
            if (!SetProperty(ref _selectedQuality, value) || _suppress)
                return;
            if (Enum.TryParse<RenderQuality>(value, out var quality))
                _owner.ApplySetting(nameof(OutputSettings.Quality),
                    r => r with { Output = r.Output with { Quality = quality } });
        }
    }

    public string SelectedResolution
    {
        get => _selectedResolution;
        set
        {
            if (!SetProperty(ref _selectedResolution, value) || _suppress)
                return;
            if (value == "Custom")
            {
                IsCustomResolution = true;
                return;
            }
            if (TryParseResolution(value, out int width, out int height))
            {
                _owner.ApplySetting(nameof(OutputSettings.Width),
                    r => r with { Output = r.Output with { Width = width, Height = height } });
            }
        }
    }

    public bool IsCustomResolution
    {
        get => _isCustomResolution;
        private set => SetProperty(ref _isCustomResolution, value);
    }

    public decimal? CustomWidth
    {
        get => _customWidth;
        set
        {
            if (!SetProperty(ref _customWidth, value) || _suppress)
                return;
            if (value is decimal width)
                _owner.ApplySetting(nameof(OutputSettings.Width),
                    r => r with { Output = r.Output with { Width = (int)width } });
        }
    }

    public decimal? CustomHeight
    {
        get => _customHeight;
        set
        {
            if (!SetProperty(ref _customHeight, value) || _suppress)
                return;
            if (value is decimal height)
                _owner.ApplySetting(nameof(OutputSettings.Height),
                    r => r with { Output = r.Output with { Height = (int)height } });
        }
    }

    public string SelectedFps
    {
        get => _selectedFps;
        set
        {
            if (!SetProperty(ref _selectedFps, value) || _suppress)
                return;
            ApplyFps(value);
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            SelectedQuality = request.Output.Quality.ToString();

            string resolution = request.Output.Width + "x" + request.Output.Height;
            if (ResolutionOptions.Contains(resolution))
            {
                SelectedResolution = resolution;
                IsCustomResolution = false;
            }
            else
            {
                SelectedResolution = "Custom";
                IsCustomResolution = true;
                CustomWidth = request.Output.Width;
                CustomHeight = request.Output.Height;
            }

            SelectedFps = FpsDisplay(request.Output.FpsNumerator, request.Output.FpsDenominator);
            RefreshCardSelection(request.Composition);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void ApplyFps(string display)
    {
        if (display == "59.94")
        {
            _owner.ApplySetting(nameof(OutputSettings.FpsNumerator),
                r => r with { Output = r.Output with { FpsNumerator = 60000, FpsDenominator = 1001 } });
        }
        else if (int.TryParse(display, out int fps))
        {
            _owner.ApplySetting(nameof(OutputSettings.FpsNumerator),
                r => r with { Output = r.Output with { FpsNumerator = fps, FpsDenominator = 1 } });
        }
    }

    private static string FpsDisplay(int numerator, int denominator)
        => numerator == 60000 && denominator == 1001 ? "59.94" : numerator.ToString();

    private static bool TryParseResolution(string text, out int width, out int height)
    {
        width = 0;
        height = 0;
        string[] parts = text.Split('x');
        return parts.Length == 2
            && int.TryParse(parts[0], out width)
            && int.TryParse(parts[1], out height);
    }
}

/// <summary>
/// A selectable composition card shown at the top of the BASIC settings.
/// Clicking a card applies the matching composition and highlights the card.
/// </summary>
public sealed class CompositionCardViewModel : ObservableObject
{
    private readonly Action<CompositionKind> _select;
    private bool _isSelected;

    public CompositionCardViewModel(
        string name,
        string tagline,
        CompositionKind composition,
        Action<CompositionKind> select)
    {
        Name = name;
        Tagline = tagline;
        Composition = composition;
        _select = select;
        SelectCommand = new RelayCommand(() => _select(Composition));
    }

    public string Name { get; }
    public string Tagline { get; }
    public CompositionKind Composition { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
            {
                OnPropertyChanged(nameof(SelectedBorderBrush));
                OnPropertyChanged(nameof(SelectedBackground));
            }
        }
    }

    /// <summary>Accent border/background when this card is the active composition.</summary>
    public Avalonia.Media.IBrush SelectedBorderBrush
        => IsSelected ? AccentBrush : NormalBrush;

    public Avalonia.Media.IBrush SelectedBackground
        => IsSelected ? AccentFillBrush : TransparentBrush;

    public RelayCommand SelectCommand { get; }

    private static readonly Avalonia.Media.IBrush AccentBrush = new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.FromRgb(0x5B, 0x9B, 0xD5));
    private static readonly Avalonia.Media.IBrush NormalBrush = new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.FromArgb(90, 0x80, 0x80, 0x80));
    private static readonly Avalonia.Media.IBrush AccentFillBrush = new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.FromArgb(36, 0x5B, 0x9B, 0xD5));
    private static readonly Avalonia.Media.IBrush TransparentBrush = new Avalonia.Media.SolidColorBrush(
        Avalonia.Media.Color.FromArgb(0, 0, 0, 0));
}