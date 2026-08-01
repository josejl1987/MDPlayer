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
        "Auto", "Unified Roll", "Split Roll", "Scopes", "Hybrid", "Diagnostic", "Legacy Diagnostic",
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
            _owner.ApplySetting(nameof(VisualizationRequest.Layout), r => r with { Layout = ParseLayout(value) });
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
        "Unified Roll" => VisualizationLayout.UnifiedRoll,
        "Split Roll" => VisualizationLayout.SplitRoll,
        "Scopes" => VisualizationLayout.Scopes,
        "Hybrid" => VisualizationLayout.Hybrid,
        "Diagnostic" => VisualizationLayout.Diagnostic,
        "Legacy Diagnostic" => VisualizationLayout.LegacyDiagnostic,
        _ => VisualizationLayout.Auto,
    };

    private static string LayoutDisplay(VisualizationLayout layout) => layout switch
    {
        VisualizationLayout.UnifiedRoll => "Unified Roll",
        VisualizationLayout.SplitRoll => "Split Roll",
        VisualizationLayout.Scopes => "Scopes",
        VisualizationLayout.Hybrid => "Hybrid",
        VisualizationLayout.Diagnostic => "Diagnostic",
        VisualizationLayout.LegacyDiagnostic => "Legacy Diagnostic",
        _ => "Auto",
    };

    private static string ResolvedDisplayName(string resolved) => resolved switch
    {
        "unified" => "Unified Roll",
        "split" => "Split Roll",
        "scope" or "scopes" => "Scopes",
        "hybrid" => "Hybrid",
        "diagnostic" => "Diagnostic",
        "diagnostic-v2" => "Legacy Diagnostic",
        _ => resolved,
    };

    private static string LayoutExplanationFor(VisualizationLayout layout) => layout switch
    {
        VisualizationLayout.Auto => "Auto picks the layout that best fits this input and its captured timeline.",
        VisualizationLayout.UnifiedRoll => "One rolling timeline holds every channel.",
        VisualizationLayout.SplitRoll => "Channels are separated into individual rolling panels.",
        VisualizationLayout.Scopes => "An oscilloscope wall renders the synchronized stems.",
        VisualizationLayout.Hybrid => "A rolling timeline plus a scope wall.",
        VisualizationLayout.Diagnostic => "Diagnostic layout showing channel and rendering internals.",
        VisualizationLayout.LegacyDiagnostic => "The previous-generation diagnostic layout.",
        _ => "Auto picks the layout that best fits this input and its captured timeline.",
    };
}
