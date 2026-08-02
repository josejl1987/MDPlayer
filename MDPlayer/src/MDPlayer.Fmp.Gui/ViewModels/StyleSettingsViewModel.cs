using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>STYLE settings: effects, note color, time window.</summary>
public sealed class StyleSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private string _selectedEffects = VisualEffects.Subtle.ToString();
    private string _selectedNoteColor = NoteColorMode.Instrument.ToString();
    private decimal? _pastSeconds = 0.8m;
    private decimal? _futureSeconds = 3.2m;
    private string _selectedTimeScale = "Balanced";
    private string _selectedPalette = PaletteKind.Default.ToString();

    public StyleSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public IReadOnlyList<string> EffectsOptions { get; } = Enum.GetNames<VisualEffects>();
    public IReadOnlyList<string> NoteColorOptions { get; } = Enum.GetNames<NoteColorMode>();
    public IReadOnlyList<string> PaletteOptions { get; } = Enum.GetNames<PaletteKind>();

    public string EffectsNote => "Preview may reduce the effect frame rate but keeps the composition geometry.";

    public string SelectedEffects
    {
        get => _selectedEffects;
        set
        {
            if (!SetProperty(ref _selectedEffects, value) || _suppress)
                return;
            if (Enum.TryParse<VisualEffects>(value, out var effects))
                _owner.ApplyVisualSetting( r => r with
                {
                    Style = r.Style with { Effects = effects },
                });
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
                _owner.ApplyVisualSetting( r => r with
                {
                    Style = r.Style with { NoteColor = mode },
                });
        }
    }

    public decimal? PastSeconds
    {
        get => _pastSeconds;
        set
        {
            if (!SetProperty(ref _pastSeconds, value) || _suppress)
                return;
            _owner.ApplyVisualSetting( r => r with
            {
                View = r.View with { PastSeconds = (double)(value ?? 0.8m) },
            });
        }
    }

    public decimal? FutureSeconds
    {
        get => _futureSeconds;
        set
        {
            if (!SetProperty(ref _futureSeconds, value) || _suppress)
                return;
            _owner.ApplyVisualSetting( r => r with
            {
                View = r.View with { FutureSeconds = (double)(value ?? 3.2m) },
            });
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
                _ => (0.8, 3.2),
            };
            _owner.ApplyVisualSetting( r => r with
            {
                View = r.View with { PastSeconds = past, FutureSeconds = future },
            });
        }
    }

    public string SelectedPalette
    {
        get => _selectedPalette;
        set
        {
            if (!SetProperty(ref _selectedPalette, value) || _suppress)
                return;
            if (Enum.TryParse<PaletteKind>(value, out var palette))
                _owner.ApplyVisualSetting(r => r with
                {
                    Style = r.Style with { Palette = palette },
                });
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            SelectedEffects = request.Style.Effects.ToString();
            SelectedNoteColor = request.Style.NoteColor.ToString();
            SelectedPalette = request.Style.Palette.ToString();
            PastSeconds = (decimal)request.View.PastSeconds;
            FutureSeconds = (decimal)request.View.FutureSeconds;
            SelectedTimeScale = ClosestTimeScale(request.View.PastSeconds, request.View.FutureSeconds);
            SelectedNoteColor = request.Style.NoteColor.ToString();
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
            (0.35, 1.5), (0.8, 3.2), (1.5, 4.5),
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