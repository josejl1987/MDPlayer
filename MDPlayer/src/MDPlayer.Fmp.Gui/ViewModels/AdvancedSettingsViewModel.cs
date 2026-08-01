using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>ADVANCED settings: playback, capture, encoder, tool overrides.</summary>
public sealed class AdvancedSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded;
    private decimal? _loopCount = 2;
    private decimal? _fadeSeconds = 5;
    private decimal? _tailSeconds = 0.5m;
    private decimal? _maximumDurationSeconds = 300;
    private decimal? _sampleRate = 44_100;
    private decimal? _ssgGainDb;
    private string _selectedEncoder = VideoEncoder.Auto.ToString();
    private string _selectedBackend = BackendPreference.Auto.ToString();
    private string _selectedScopeMode = ScopeMode.Auto.ToString();
    private bool? _overwrite;
    private string? _corrscopePath;
    private string? _ffmpegPath;
    private string? _analysisPython;
    private bool? _analysisForce;
    private decimal? _toolTimeoutMinutes;

    public AdvancedSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IReadOnlyList<string> EncoderOptions { get; } = Enum.GetNames<VideoEncoder>();
    public IReadOnlyList<string> BackendOptions { get; } = Enum.GetNames<BackendPreference>();
    public IReadOnlyList<string> ScopeModeOptions { get; } = Enum.GetNames<ScopeMode>();

    public decimal? LoopCount
    {
        get => _loopCount;
        set
        {
            if (!SetProperty(ref _loopCount, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.LoopCount), r => r with { LoopCount = (int)(value ?? 2) });
        }
    }

    public decimal? FadeSeconds
    {
        get => _fadeSeconds;
        set
        {
            if (!SetProperty(ref _fadeSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.FadeSeconds), r => r with { FadeSeconds = (double)(value ?? 5m) });
        }
    }

    public decimal? TailSeconds
    {
        get => _tailSeconds;
        set
        {
            if (!SetProperty(ref _tailSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.TailSeconds), r => r with { TailSeconds = (double)(value ?? 0.5m) });
        }
    }

    public decimal? MaximumDurationSeconds
    {
        get => _maximumDurationSeconds;
        set
        {
            if (!SetProperty(ref _maximumDurationSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.MaximumDurationSeconds),
                r => r with { MaximumDurationSeconds = value is null ? null : (double)value });
        }
    }

    public decimal? SampleRate
    {
        get => _sampleRate;
        set
        {
            if (!SetProperty(ref _sampleRate, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.SampleRate), r => r with { SampleRate = (int)(value ?? 44_100) });
        }
    }

    public decimal? SsgGainDb
    {
        get => _ssgGainDb;
        set
        {
            if (!SetProperty(ref _ssgGainDb, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.SsgGainDb), r => r with { SsgGainDb = (double)(value ?? 0m) });
        }
    }

    public string SelectedEncoder
    {
        get => _selectedEncoder;
        set
        {
            if (!SetProperty(ref _selectedEncoder, value) || _suppress)
                return;
            if (Enum.TryParse<VideoEncoder>(value, out var encoder))
                _owner.ApplySetting(nameof(VisualizationRequest.Encoder), r => r with { Encoder = encoder });
        }
    }

    public string SelectedBackend
    {
        get => _selectedBackend;
        set
        {
            if (!SetProperty(ref _selectedBackend, value) || _suppress)
                return;
            if (Enum.TryParse<BackendPreference>(value, out var backend))
                _owner.ApplySetting(nameof(VisualizationRequest.Backend), r => r with { Backend = backend });
        }
    }

    public string SelectedScopeMode
    {
        get => _selectedScopeMode;
        set
        {
            if (!SetProperty(ref _selectedScopeMode, value) || _suppress)
                return;
            if (Enum.TryParse<ScopeMode>(value, out var scopeMode))
                _owner.ApplySetting(nameof(VisualizationRequest.ScopeMode), r => r with { ScopeMode = scopeMode });
        }
    }

    public bool? Overwrite
    {
        get => _overwrite;
        set
        {
            if (!SetProperty(ref _overwrite, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.Overwrite), r => r with { Overwrite = value == true });
        }
    }

    public string? CorrscopePath
    {
        get => _corrscopePath;
        set
        {
            if (!SetProperty(ref _corrscopePath, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.Tools),
                r => r with { Tools = r.Tools with { CorrscopePath = value } });
        }
    }

    public string? FfmpegPath
    {
        get => _ffmpegPath;
        set
        {
            if (!SetProperty(ref _ffmpegPath, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.Tools),
                r => r with { Tools = r.Tools with { FfmpegPath = value } });
        }
    }

    public string? AnalysisPython
    {
        get => _analysisPython;
        set
        {
            if (!SetProperty(ref _analysisPython, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.Tools),
                r => r with { Tools = r.Tools with { AnalysisPython = value } });
        }
    }

    public bool? AnalysisForce
    {
        get => _analysisForce;
        set
        {
            if (!SetProperty(ref _analysisForce, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.Tools),
                r => r with { Tools = r.Tools with { AnalysisForce = value == true } });
        }
    }

    public decimal? ToolTimeoutMinutes
    {
        get => _toolTimeoutMinutes;
        set
        {
            if (!SetProperty(ref _toolTimeoutMinutes, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(VisualizationRequest.Tools),
                r => r with { Tools = r.Tools with { ToolTimeoutMinutes = value is null ? null : (int)value } });
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            LoopCount = request.LoopCount;
            FadeSeconds = (decimal)request.FadeSeconds;
            TailSeconds = (decimal)request.TailSeconds;
            MaximumDurationSeconds = request.MaximumDurationSeconds is double max ? (decimal)max : null;
            SampleRate = request.SampleRate;
            SsgGainDb = (decimal)request.SsgGainDb;
            SelectedEncoder = request.Encoder.ToString();
            SelectedBackend = request.Backend.ToString();
            SelectedScopeMode = request.ScopeMode.ToString();
            Overwrite = request.Overwrite;
            CorrscopePath = request.Tools.CorrscopePath;
            FfmpegPath = request.Tools.FfmpegPath;
            AnalysisPython = request.Tools.AnalysisPython;
            AnalysisForce = request.Tools.AnalysisForce;
            ToolTimeoutMinutes = request.Tools.ToolTimeoutMinutes;
        }
        finally
        {
            _suppress = false;
        }
    }
}
