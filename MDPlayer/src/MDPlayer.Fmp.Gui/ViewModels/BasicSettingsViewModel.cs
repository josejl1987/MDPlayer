using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// BASIC settings: composition (data-driven selector), output path, quality,
/// resolution and frame rate.
/// </summary>
public sealed class BasicSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private string _selectedQuality = RenderQuality.Standard.ToString();
    private string _selectedResolution = "1920x1080";
    private bool _isCustomResolution;
    private decimal? _customWidth = 1920;
    private decimal? _customHeight = 1080;
    private string _selectedFps = "60";
    private CompositionOptionViewModel? _selectedComposition;

    public BasicSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        Compositions = SettingsViewModel.CreateCompositionOptions();
        _selectedComposition = Compositions.FirstOrDefault();
    }

    /// <summary>Data-driven composition selector entries. Extends automatically with new kinds.</summary>
    public IReadOnlyList<CompositionOptionViewModel> Compositions { get; }

    public CompositionOptionViewModel? SelectedComposition
    {
        get => _selectedComposition;
        set
        {
            if (!SetProperty(ref _selectedComposition, value) || _suppress || value is null)
                return;
            _owner.ApplyVisualSetting(r => r with { Composition = value.Value });
        }
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
                _owner.ApplyVisualSetting(r => r with { Output = r.Output with { Quality = quality } });
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
                _owner.ApplyVisualSetting(r => r with { Output = r.Output with { Width = width, Height = height } });
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
                _owner.ApplyVisualSetting(r => r with { Output = r.Output with { Width = (int)width } });
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
                _owner.ApplyVisualSetting(r => r with { Output = r.Output with { Height = (int)height } });
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
            SelectedComposition = Compositions.FirstOrDefault(c => c.Value == request.Composition) ?? _selectedComposition;
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
            _owner.ApplyVisualSetting(r => r with { Output = r.Output with { FpsNumerator = 60000, FpsDenominator = 1001 } });
        }
        else if (int.TryParse(display, out int fps))
        {
            _owner.ApplyVisualSetting(r => r with { Output = r.Output with { FpsNumerator = fps, FpsDenominator = 1 } });
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