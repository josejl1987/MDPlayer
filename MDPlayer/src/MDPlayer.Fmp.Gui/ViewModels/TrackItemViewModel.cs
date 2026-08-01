using Avalonia.Media;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// One row in the custom channel-selection track list. Order is stable for the
/// lifetime of a plan (the collection is only rebuilt when the plan changes).
/// </summary>
public sealed class TrackItemViewModel : ObservableObject
{
    private bool? _isSelected;

    public required string TrackId { get; init; }
    public required string DisplayName { get; init; }
    public string? DeviceFamily { get; init; }
    public string? SemanticType { get; init; }
    public bool ScopeAvailable { get; init; }
    public bool ActivityDetected { get; init; }
    public bool DataIncomplete { get; init; }
    public string? ColorHex { get; init; }

    public IBrush? ColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ColorHex))
                return null;
            try
            {
                return new SolidColorBrush(Color.Parse(ColorHex));
            }
            catch
            {
                return null;
            }
        }
    }

    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value))
                return;
            OnToggle?.Invoke(this);
        }
    }

    /// <summary>Wired to the owning content settings model; kept internal so toggles are routed.</summary>
    internal Action<TrackItemViewModel>? OnToggle { get; set; }

    public static TrackItemViewModel From(TrackSelectionInfo track)
        => new()
        {
            TrackId = track.TrackId,
            DisplayName = track.DisplayName,
            DeviceFamily = track.DeviceFamily,
            SemanticType = track.SemanticType,
            ScopeAvailable = track.ScopeAvailable,
            ActivityDetected = track.ActivityDetected,
            DataIncomplete = track.DataIncomplete,
            ColorHex = track.ColorHex,
            IsSelected = track.Selected,
        };
}
