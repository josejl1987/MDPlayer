using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>STYLE settings: effects, note color, time window.</summary>
public sealed class StyleSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded;
    private string _selectedEffects = VisualizationEffects.Minimal.ToString();
    private string _selectedNoteColor = NoteColorMode.Instrument.ToString();
    private decimal? _pastSeconds = 0.75m;
    private decimal? _futureSeconds = 2.25m;
    private string _selectedTimeScale = "Balanced";

    public StyleSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IReadOnlyList<string> EffectsOptions { get; } = Enum.GetNames<VisualizationEffects>();
    public IReadOnlyList<string> NoteColorOptions { get; } = Enum.GetNames<NoteColorMode>();
    public IReadOnlyList<string> TimeScaleOptions { get; } = new[] { "Dense", "Balanced", "Wide" };

    public string EffectsNote => "Preview may reduce the effect frame rate but keeps the composition geometry.";

    public string SelectedEffects
    {
        get => _selectedEffects;
        set
        {
            if (!SetProperty(ref _selectedEffects, value) || _suppress)
                return;
            if (Enum.TryParse<VisualizationEffects>(value, out var effects))
                _owner.ApplySetting(nameof(VisualizationRequest.Effects), r => r with { Effects = effects });
        }
    }

    public string SelectedNoteColor
    {
        get => _selectedNoteColor;
        set
        {
            if (!SetProperty(ref _selectedNoteColor, value) || _suppress)
                return;
            if (Enum.TryParse<NoteColorMode>(value, out var mode))
                _owner.ApplySetting(nameof(VisualizationRequest.NoteColor), r => r with { NoteColor = mode });
        }
    }

    public decimal? PastSeconds
    {
        get => _pastSeconds;
        set
        {
            if (!SetProperty(ref _pastSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.PastSeconds), r => r with { PastSeconds = (double)(value ?? 0.75m) });
        }
    }

    public decimal? FutureSeconds
    {
        get => _futureSeconds;
        set
        {
            if (!SetProperty(ref _futureSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.FutureSeconds), r => r with { FutureSeconds = (double)(value ?? 2.25m) });
        }
    }

    public string SelectedTimeScale
    {
        get => _selectedTimeScale;
        set
        {
            if (!SetProperty(ref _selectedTimeScale, value) || _suppress)
                return;
            (double past, double future) = value switch
            {
                "Dense" => (0.35, 1.5),
                "Wide" => (1.5, 4.5),
                _ => (0.75, 2.25),
            };
            _owner.ApplySetting(nameof(VisualizationRequest.PastSeconds),
                r => r with { PastSeconds = past, FutureSeconds = future });
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            SelectedEffects = request.Effects.ToString();
            SelectedNoteColor = request.NoteColor.ToString();
            PastSeconds = (decimal)request.PastSeconds;
            FutureSeconds = (decimal)request.FutureSeconds;
            SelectedTimeScale = ClosestTimeScale(request.PastSeconds, request.FutureSeconds);
        }
        finally
        {
            _suppress = false;
        }
    }

    private static string ClosestTimeScale(double past, double future)
    {
        (double past, double future)[] presets =
        {
            (0.35, 1.5), (0.75, 2.25), (1.5, 4.5),
        };
        string[] names = { "Dense", "Balanced", "Wide" };
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < presets.Length; i++)
        {
            double distance = Math.Abs(presets[i].past - past) + Math.Abs(presets[i].future - future);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }
        return names[best];
    }
}
