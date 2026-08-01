using Fmp.Application.Contracts;
using Fmp.Application.Presets;

namespace Fmp.Gui.ViewModels;

/// <summary>BASIC settings: preset, layout, resolution, frame rate.</summary>
public sealed class BasicSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded = true;
    private string _selectedPreset = VisualizationPreset.Balanced.ToString();
    private string _presetStatus = VisualizationPreset.Balanced.ToString();
    private string _selectedLayout = LayoutDisplay(VisualizationLayout.Auto);
    private string _layoutExplanation = LayoutExplanationFor(VisualizationLayout.Auto);
    private string _resolvedLayoutText = "";
    private string _selectedResolution = "1280x720";
    private bool _isCustomResolution;
    private decimal? _customWidth = 1280;
    private decimal? _customHeight = 720;
    private string _selectedFps = "60";

    public BasicSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        CompositionCards =
        [
            new CompositionCardViewModel(
                "Performance",
                "Large shared roll · best for publishing",
                VisualizationLayout.Performance,
                SelectComposition),
            new CompositionCardViewModel(
                "Scope Stage",
                "Large waveforms · best for timbre",
                VisualizationLayout.ScopeStage,
                SelectComposition),
            new CompositionCardViewModel(
                "Diagnostic",
                "Full channel grid · best for inspection",
                VisualizationLayout.Diagnostic,
                SelectComposition),
        ];
    }

    /// <summary>The three intentional publishing compositions, shown as cards.</summary>
    public IReadOnlyList<CompositionCardViewModel> CompositionCards { get; }

    private void SelectComposition(VisualizationLayout layout)
    {
        _owner.ApplySetting(nameof(VisualizationRequest.Layout), r => r with { Layout = layout });
        LayoutExplanation = LayoutExplanationFor(layout);
        RefreshCardSelection(layout);
    }

    private void RefreshCardSelection(VisualizationLayout layout)
    {
        foreach (CompositionCardViewModel card in CompositionCards)
            card.IsSelected = card.Layout == layout;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IReadOnlyList<string> PresetOptions { get; } =
        new[] { "Preview", "Balanced", "Final", "Diagnostic", "Custom" };

    public IReadOnlyList<string> LayoutOptions { get; } = new[]
    {
        "Auto", "Performance", "Scope Stage", "Diagnostic",
    };

    public IReadOnlyList<string> ResolutionOptions { get; } = new[]
    {
        "960x540", "1280x720", "1920x1080", "2560x1440", "3840x2160", "Custom",
    };

    public IReadOnlyList<string> FpsOptions { get; } = new[] { "24", "25", "30", "50", "60", "59.94" };

    public string SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value) || _suppress)
                return;
            if (Enum.TryParse<VisualizationPreset>(value, out var preset))
                _owner.ApplySetting(nameof(VisualizationRequest.Preset), r => VisualizationPresetCatalog.Apply(r, preset));
        }
    }

    public string PresetStatus
    {
        get => _presetStatus;
        private set => SetProperty(ref _presetStatus, value);
    }

    public string SelectedLayout
    {
        get => _selectedLayout;
        set
        {
            if (!SetProperty(ref _selectedLayout, value) || _suppress)
                return;
            VisualizationLayout layout = ParseLayout(value);
            _owner.ApplySetting(nameof(VisualizationRequest.Layout), r => r with { Layout = layout });
            RefreshCardSelection(layout);
        }
    }

    public string LayoutExplanation
    {
        get => _layoutExplanation;
        private set => SetProperty(ref _layoutExplanation, value);
    }

    /// <summary>Resolved layout after Auto resolution, e.g. "Auto → Hybrid".</summary>
    public string ResolvedLayoutText
    {
        get => _resolvedLayoutText;
        private set => SetProperty(ref _resolvedLayoutText, value);
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
                _owner.ApplySetting(nameof(VisualizationRequest.Width),
                    r => r with { Width = width, Height = height });
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
                _owner.ApplySetting(nameof(VisualizationRequest.Width), r => r with { Width = (int)width });
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
                _owner.ApplySetting(nameof(VisualizationRequest.Height), r => r with { Height = (int)height });
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
            VisualizationPreset? detected = VisualizationPresetCatalog.DetectPreset(request);
            SelectedPreset = detected?.ToString() ?? "Custom";
            PresetStatus = detected is null ? $"Custom · based on {request.Preset}" : detected.ToString()!;

            SelectedLayout = LayoutDisplay(request.Layout);
            LayoutExplanation = LayoutExplanationFor(request.Layout);
            RefreshCardSelection(request.Layout);

            string resolution = request.Width + "x" + request.Height;
            if (ResolutionOptions.Contains(resolution))
            {
                SelectedResolution = resolution;
                IsCustomResolution = false;
            }
            else
            {
                SelectedResolution = "Custom";
                IsCustomResolution = true;
                CustomWidth = request.Width;
                CustomHeight = request.Height;
            }

            SelectedFps = FpsDisplay(request.FpsNumerator, request.FpsDenominator);
        }
        finally
        {
            _suppress = false;
        }
    }

    public void SynchronizePlan(VisualizationPlanResult? plan)
    {
        if (plan is null || string.IsNullOrWhiteSpace(plan.ResolvedLayout))
        {
            ResolvedLayoutText = "";
            return;
        }
        string requested = LayoutDisplay(ParseCliLayout(plan.RequestedLayout ?? nameof(VisualizationLayout.Auto)));
        string resolved = ResolvedDisplayName(plan.ResolvedLayout);
        ResolvedLayoutText = requested.Equals("Auto", StringComparison.OrdinalIgnoreCase)
            ? $"Auto → {resolved}"
            : $"Resolved: {resolved}";
    }

    /// <summary>Parses the CLI's lower-case layout names back to the enum.</summary>
    private static VisualizationLayout ParseCliLayout(string name) => name switch
    {
        "unified" => VisualizationLayout.UnifiedRoll,
        "split" => VisualizationLayout.SplitRoll,
        "scope" or "scopes" => VisualizationLayout.Scopes,
        "hybrid" => VisualizationLayout.Hybrid,
        "diagnostic" => VisualizationLayout.Diagnostic,
        "diagnostic-v2" => VisualizationLayout.LegacyDiagnostic,
        "performance" => VisualizationLayout.Performance,
        "scope-stage" => VisualizationLayout.ScopeStage,
        _ => VisualizationLayout.Auto,
    };

    private void ApplyFps(string display)
    {
        if (display == "59.94")
        {
            _owner.ApplySetting(nameof(VisualizationRequest.FpsNumerator),
                r => r with { FpsNumerator = 60000, FpsDenominator = 1001 });
        }
        else if (int.TryParse(display, out int fps))
        {
            _owner.ApplySetting(nameof(VisualizationRequest.FpsNumerator),
                r => r with { FpsNumerator = fps, FpsDenominator = 1 });
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

    private static VisualizationLayout ParseLayout(string display) => display switch
    {
        "Performance" => VisualizationLayout.Performance,
        "Scope Stage" => VisualizationLayout.ScopeStage,
        "Diagnostic" => VisualizationLayout.Diagnostic,
        _ => VisualizationLayout.Auto,
    };

    private static string LayoutDisplay(VisualizationLayout layout) => layout switch
    {
        VisualizationLayout.Performance => "Performance",
        VisualizationLayout.ScopeStage => "Scope Stage",
        VisualizationLayout.Diagnostic => "Diagnostic",
        _ => "Auto",
    };

    private static string ResolvedDisplayName(string resolved) => resolved switch
    {
        "performance" => "Performance",
        "scope-stage" => "Scope Stage",
        "unified" => "Unified Roll (legacy)",
        "split" => "Split Roll (legacy)",
        "scope" or "scopes" => "Scope wall (legacy)",
        "hybrid" => "Hybrid (legacy)",
        "diagnostic" => "Diagnostic",
        "diagnostic-v2" => "Diagnostic (legacy)",
        _ => resolved,
    };

    private static string LayoutExplanationFor(VisualizationLayout layout) => layout switch
    {
        VisualizationLayout.Auto => "Auto picks the composition that best fits this input and its captured timeline.",
        VisualizationLayout.Performance => "One dominant shared piano roll; percussion, noise and samples become compact lanes. Best for publishing.",
        VisualizationLayout.ScopeStage => "A large oscilloscope wall with a small synchronized activity strip. Best for timbre.",
        VisualizationLayout.Diagnostic => "Full per-channel grid with headers, scopes and pitch cameras. Best for inspection.",
        _ => "Auto picks the composition that best fits this input and its captured timeline.",
    };
}

/// <summary>
/// A selectable composition card shown at the top of the BASIC settings.
/// Clicking a card applies the matching layout and highlights the card.
/// </summary>
public sealed class CompositionCardViewModel : ObservableObject
{
    private readonly Action<VisualizationLayout> _select;
    private bool _isSelected;

    public CompositionCardViewModel(
        string name,
        string tagline,
        VisualizationLayout layout,
        Action<VisualizationLayout> select)
    {
        Name = name;
        Tagline = tagline;
        Layout = layout;
        _select = select;
        SelectCommand = new RelayCommand(() => _select(Layout));
    }

    public string Name { get; }
    public string Tagline { get; }
    public VisualizationLayout Layout { get; }

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
