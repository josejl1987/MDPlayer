using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// A selectable composition in the data-driven selector. Adding a new
/// CompositionKind to the request contract automatically exposes it here via
/// <see cref="CreateCompositionOptions"/>; no XAML or main-window change is
/// needed.
/// </summary>
public sealed record CompositionOptionViewModel(CompositionKind Value, string Name, string Description);

/// <summary>Aggregates the five settings sections.</summary>
public sealed class SettingsViewModel
{
    public SettingsViewModel(MainWindowViewModel owner)
    {
        Basic = new BasicSettingsViewModel(owner);
        Content = new ContentSettingsViewModel(owner);
        Style = new StyleSettingsViewModel(owner);
        Metadata = new MetadataSettingsViewModel(owner);
        Advanced = new AdvancedSettingsViewModel(owner);
    }

    public BasicSettingsViewModel Basic { get; }
    public ContentSettingsViewModel Content { get; }
    public StyleSettingsViewModel Style { get; }
    public MetadataSettingsViewModel Metadata { get; }
    public AdvancedSettingsViewModel Advanced { get; }

    public static IReadOnlyList<CompositionOptionViewModel> CreateCompositionOptions() =>
        Enum.GetValues<CompositionKind>()
            .Select(kind => new CompositionOptionViewModel(kind, kind.ToString(), Describe(kind)))
            .ToList();

    private static string Describe(CompositionKind kind) => kind switch
    {
        CompositionKind.Diagnostic => "Channel-focused semantic visualization",
        _ => "Semantic visualization",
    };

    public void Synchronize(VisualizationRequest request)
    {
        Basic.Synchronize(request);
        Content.Synchronize(request);
        Style.Synchronize(request);
        Metadata.Synchronize(request);
        Advanced.Synchronize(request);
    }

    public void SynchronizePlan(VisualizationPlanResult? plan, VisualizationInputInfo? input)
    {
        Content.SynchronizePlan(plan, input);
        Advanced.SynchronizePlan(plan, input);
    }
}
