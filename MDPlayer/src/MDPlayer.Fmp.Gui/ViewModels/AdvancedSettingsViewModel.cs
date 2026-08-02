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
    private int _loopCount = 2;
    private decimal _fadeSeconds = 5;
    private decimal _tailSeconds = 0.5m;
    private decimal? _maximumDurationSeconds = 300;
    private int _sampleRate = 48_000;
    private string _selectedEncoder = VideoEncoder.Auto.ToString();
    private bool _overwrite;
    private double _ssgGainDb;
    private string _selectedSpcPitch = SpcPitchInterpretation.Estimate.ToString();
    private bool _showSsgGain;
    private bool _showSpcPitch;

    public AdvancedSettingsViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
    }

    public IReadOnlyList<string> EncoderOptions { get; } = new[] { "Auto", "LibX264", "Nvenc" };
    public IReadOnlyList<string> SpcPitchOptions { get; } = Enum.GetNames<SpcPitchInterpretation>();
    // Estimate resolves each BRR sample's root pitch from the S-DSP registers
    // for pitch-accurate notes; Relative keeps the raw relative pitch without
    // estimation. This affects note display, not song tempo.
    public string SpcPitchNote => "Estimate resolves each sample's root pitch for pitch-accurate notes; Relative keeps the raw relative pitch.";
    // Applied via mdsound SetVolumeYM2608PSG: the YM2608 SSG (PSG) channel
    // volume for FMP-family sources, in dB. FM channels are unaffected.
    public string SsgGainNote => "Volume of the YM2608 SSG (PSG) channels for FMP-family sources, in dB. FM channels are unaffected.";

    public bool ShowSsgGain
    {
        get => _showSsgGain;
        private set => SetProperty(ref _showSsgGain, value);
    }

    public bool ShowSpcPitch
    {
        get => _showSpcPitch;
        private set => SetProperty(ref _showSpcPitch, value);
    }

    public double SsgGainDb
    {
        get => _ssgGainDb;
        set
        {
            if (!SetProperty(ref _ssgGainDb, value) || _suppress)
                return;
            _owner.ApplyVisualSetting(
                r => r with { Playback = r.Playback with { SsgGainDb = value } });
        }
    }

    public string SelectedSpcPitch
    {
        get => _selectedSpcPitch;
        set
        {
            if (!SetProperty(ref _selectedSpcPitch, value) || _suppress)
                return;
            if (Enum.TryParse<SpcPitchInterpretation>(value, out var pitch))
                _owner.ApplyVisualSetting(
                    r => r with { Playback = r.Playback with { SpcPitch = pitch } });
        }
    }

    public int LoopCount
    {
        get => _loopCount;
        set
        {
            if (!SetProperty(ref _loopCount, value) || _suppress)
                return;
            _owner.ApplyVisualSetting(
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
            _owner.ApplyVisualSetting(
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
            _owner.ApplyVisualSetting(
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
            _owner.ApplyVisualSetting(
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
            _owner.ApplyVisualSetting(
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
                _owner.ApplyExportSetting(
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
            _owner.ApplyExportSetting(
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
            SsgGainDb = request.Playback.SsgGainDb;
            SelectedSpcPitch = request.Playback.SpcPitch.ToString();
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>Shows backend-specific controls only when the active source uses them.</summary>
    public void SynchronizePlan(VisualizationPlanResult? plan, VisualizationInputInfo? input)
    {
        _suppress = true;
        try
        {
            string format = input?.Format ?? "";
            ShowSsgGain = format.Contains("ovi", StringComparison.OrdinalIgnoreCase)
                || format.Contains("mpi", StringComparison.OrdinalIgnoreCase)
                || format.Contains("ozi", StringComparison.OrdinalIgnoreCase)
                || IsFmpLike(format);
            ShowSpcPitch = format.Equals(".spc", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            _suppress = false;
        }
    }

    private static bool IsFmpLike(string format)
    {
        string f = format.TrimStart('.').ToLowerInvariant();
        return f is "opi" or "mvi" or "mzi" || f.StartsWith("ov");
    }
}