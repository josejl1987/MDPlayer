using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

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
        Basic.SynchronizePlan(plan);
        Content.SynchronizePlan(plan, input);
    }

    public void SynchronizeAnalysisStatus(VisualizationSessionCapabilities? capabilities)
        => Content.SynchronizeAnalysisStatus(capabilities);
}
