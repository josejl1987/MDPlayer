using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// ADVANCED settings: playback and encoder (schema 2). Obsolete controls
/// (backend, scope mode, SPC pitch, SSG gain, stems-only, final quality,
/// analysis detail/overlay, tool-path text boxes) are removed; tool paths
/// moved to the application Settings dialog per the greenfield reset.
/// </summary>
public sealed class AdvancedSettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private bool _suppress;
    private bool _isExpanded;
    private int _loopCount = 2;
    private decimal _fadeSeconds = 5;
    private decimal _tailSeconds = 0.5m;
    private decimal? _maximumDurationSeconds = 300;
    private int _sampleRate = 48_000;
    private string _selectedEncoder = VideoEncoder.Auto.ToString();
    private bool _overwrite;

    public AdvancedSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public IReadOnlyList<string> EncoderOptions { get; } = new[] { "Auto", "LibX264", "Nvenc" };

    public int LoopCount
    {
        get => _loopCount;
        set
        {
            if (!SetProperty(ref _loopCount, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PlaybackSettings.LoopCount),
                r => r with { Playback = r.Playback with { LoopCount = value } });
        }
    }

    public decimal FadeSeconds
    {
        get => _fadeSeconds;
        set
        {
            if (!SetProperty(ref _fadeSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PlaybackSettings.FadeSeconds),
                r => r with { Playback = r.Playback with { FadeSeconds = (double)value } });
        }
    }

    public decimal TailSeconds
    {
        get => _tailSeconds;
        set
        {
            if (!SetProperty(ref _tailSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PlaybackSettings.TailSeconds),
                r => r with { Playback = r.Playback with { TailSeconds = (double)value } });
        }
    }

    public decimal? MaximumDurationSeconds
    {
        get => _maximumDurationSeconds;
        set
        {
            if (!SetProperty(ref _maximumDurationSeconds, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PlaybackSettings.MaximumDurationSeconds),
                r => r with { Playback = r.Playback with { MaximumDurationSeconds = value is decimal d ? (double)d : null } });
        }
    }

    public int SampleRate
    {
        get => _sampleRate;
        set
        {
            if (!SetProperty(ref _sampleRate, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(PlaybackSettings.SampleRate),
                r => r with { Playback = r.Playback with { SampleRate = value } });
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
                _owner.ApplySetting(nameof(OutputSettings.Encoder),
                    r => r with { Output = r.Output with { Encoder = encoder } });
        }
    }

    public bool Overwrite
    {
        get => _overwrite;
        set
        {
            if (!SetProperty(ref _overwrite, value) || _suppress)
                return;
            _owner.ApplySetting(nameof(OutputSettings.Overwrite),
                r => r with { Output = r.Output with { Overwrite = value } });
        }
    }

    public void Synchronize(VisualizationRequest request)
    {
        _suppress = true;
        try
        {
            LoopCount = request.Playback.LoopCount;
            FadeSeconds = (decimal)request.Playback.FadeSeconds;
            TailSeconds = (decimal)request.Playback.TailSeconds;
            MaximumDurationSeconds = request.Playback.MaximumDurationSeconds is double max ? (decimal)max : null;
            SampleRate = request.Playback.SampleRate;
            SelectedEncoder = request.Output.Encoder.ToString();
            Overwrite = request.Output.Overwrite;
        }
        finally
        {
            _suppress = false;
        }
    }
}