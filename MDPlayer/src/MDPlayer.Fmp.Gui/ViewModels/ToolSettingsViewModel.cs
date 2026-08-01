using Fmp.Gui.Services;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Editable snapshot of the persisted runtime tool paths (Phase 8: tool
/// settings dialog). Runtime tool paths are process-level settings and are
/// never serialized into visualization project files.
/// </summary>
public sealed class ToolSettingsViewModel : ObservableObject
{
    private readonly GuiSettingsStore _store;
    private string? _renderCliPath;
    private string? _fmpComPath;
    private string? _corrscopePath;
    private string? _ffmpegPath;
    private string? _analysisPython;
    private string? _assetsDir;
    private bool _isDirty;

    public ToolSettingsViewModel(GuiSettingsStore store)
    {
        _store = store;
        LoadFrom(store.Settings);
        SaveCommand = new RelayCommand(() => Save());
        ResetCommand = new RelayCommand(() => LoadFrom(store.Settings));
    }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetCommand { get; }

    public string? RenderCliPath
    {
        get => _renderCliPath;
        set { if (SetProperty(ref _renderCliPath, value)) MarkDirty(); }
    }

    public string? FmpComPath
    {
        get => _fmpComPath;
        set { if (SetProperty(ref _fmpComPath, value)) MarkDirty(); }
    }

    public string? CorrscopePath
    {
        get => _corrscopePath;
        set { if (SetProperty(ref _corrscopePath, value)) MarkDirty(); }
    }

    public string? FfmpegPath
    {
        get => _ffmpegPath;
        set { if (SetProperty(ref _ffmpegPath, value)) MarkDirty(); }
    }

    public string? AnalysisPython
    {
        get => _analysisPython;
        set { if (SetProperty(ref _analysisPython, value)) MarkDirty(); }
    }

    public string? AssetsDir
    {
        get => _assetsDir;
        set { if (SetProperty(ref _assetsDir, value)) MarkDirty(); }
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => SetProperty(ref _isDirty, value);
    }

    private void LoadFrom(GuiSettings settings)
    {
        _renderCliPath = settings.RenderCliPath;
        _fmpComPath = settings.FmpComPath;
        _corrscopePath = settings.CorrscopePath;
        _ffmpegPath = settings.FfmpegPath;
        _analysisPython = settings.AnalysisPython;
        _assetsDir = settings.AssetsDir;
        IsDirty = false;
        OnPropertyChanged(nameof(RenderCliPath));
        OnPropertyChanged(nameof(FmpComPath));
        OnPropertyChanged(nameof(CorrscopePath));
        OnPropertyChanged(nameof(FfmpegPath));
        OnPropertyChanged(nameof(AnalysisPython));
        OnPropertyChanged(nameof(AssetsDir));
    }

    private void MarkDirty() => IsDirty = true;

    private void Save()
    {
        _store.Update(settings =>
        {
            settings.RenderCliPath = NullIfEmpty(RenderCliPath);
            settings.FmpComPath = NullIfEmpty(FmpComPath);
            settings.CorrscopePath = NullIfEmpty(CorrscopePath);
            settings.FfmpegPath = NullIfEmpty(FfmpegPath);
            settings.AnalysisPython = NullIfEmpty(AnalysisPython);
            settings.AssetsDir = NullIfEmpty(AssetsDir);
        });
        IsDirty = false;
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
