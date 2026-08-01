using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Holds the current preview bitmap plus loading/stale/error badges. While
/// loading a new frame the last valid image is kept on screen (spinner overlay
/// instead of clearing). Motion preview plays PNG frames on a dispatcher timer.
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private readonly DispatcherTimer _motionTimer;
    private readonly List<Bitmap> _motionFrames = new();
    private int _motionIndex;
    private int _motionFps = 15;
    private double _motionStart;
    private double _motionDuration;

    private Bitmap? _currentImage;
    private bool _isLoading;
    private bool _isStale;
    private bool _hasApproximations;
    private string _approximationText = "";
    private bool _hasError;
    private string _errorText = "";
    private bool _isPlaying;
    private bool _isReducedMotion;
    private PreviewFidelity _fidelity = PreviewFidelity.Layout;
    private string _fidelityText = "No preview";

    public PreviewViewModel()
    {
        _motionTimer = new DispatcherTimer();
        _motionTimer.Tick += OnMotionTick;
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke());
    }

    /// <summary>Raised when the user clicks the error banner's Retry button.</summary>
    public event Action? RetryRequested;

    /// <summary>Raised on every motion frame tick with the frame's absolute time.</summary>
    public event Action<double>? MotionFrameChanged;

    public RelayCommand RetryCommand { get; }

    public Bitmap? CurrentImage
    {
        get => _currentImage;
        private set
        {
            if (SetProperty(ref _currentImage, value))
                OnPropertyChanged(nameof(HasImage));
        }
    }

    /// <summary>True once a frame has ever been displayed.</summary>
    public bool HasImage => _currentImage is not null;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True when the request changed but the displayed frame is from an older revision.</summary>
    public bool IsStale
    {
        get => _isStale;
        private set => SetProperty(ref _isStale, value);
    }

    public string FidelityText
    {
        get => _fidelityText;
        private set => SetProperty(ref _fidelityText, value);
    }

    public PreviewFidelity Fidelity
    {
        get => _fidelity;
        private set => SetProperty(ref _fidelity, value);
    }

    public bool HasApproximations
    {
        get => _hasApproximations;
        private set => SetProperty(ref _hasApproximations, value);
    }

    public string ApproximationText
    {
        get => _approximationText;
        private set => SetProperty(ref _approximationText, value);
    }

    public bool HasError
    {
        get => _hasError;
        private set => SetProperty(ref _hasError, value);
    }

    public string ErrorText
    {
        get => _errorText;
        private set => SetProperty(ref _errorText, value);
    }

    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    public bool IsReducedMotion
    {
        get => _isReducedMotion;
        private set => SetProperty(ref _isReducedMotion, value);
    }

    public string ReducedMotionHint => "Reduced motion: automatic preview updates are paused. Use F5 to refresh manually.";

    /// <summary>Applies a freshly rendered frame, replacing the current image.</summary>
    public void ApplyFrame(PreviewFrameResult result)
    {
        try
        {
            using var stream = new MemoryStream(result.PngBytes);
            var bitmap = new Bitmap(stream);
            CurrentImage = bitmap;
            HasError = result.Warning is not null;
            ErrorText = result.Warning?.Message ?? "";
            Fidelity = result.Fidelity;
            FidelityText = result.Fidelity switch
            {
                PreviewFidelity.Layout => "Layout preview",
                PreviewFidelity.AccurateStill => "Accurate still",
                _ => "Motion preview",
            };
            HasApproximations = result.HasApproximations;
            ApproximationText = BuildApproximationText(result.ApproximationNotes);
            IsLoading = false;
            IsStale = false;
        }
        catch (Exception ex)
        {
            SetError("Could not decode preview frame: " + ex.Message);
        }
    }

    /// <summary>Starts looping a motion sequence; frames are decoded lazily into memory.</summary>
    public void PlayMotion(IReadOnlyList<string> framePaths, int fps, double startSeconds, double durationSeconds)
    {
        StopMotion();
        _motionFrames.Clear();
        foreach (string path in framePaths)
        {
            try
            {
                _motionFrames.Add(new Bitmap(path));
            }
            catch
            {
                // Skip unreadable frames.
            }
        }

        if (_motionFrames.Count == 0)
        {
            SetError("Motion preview produced no readable frames.");
            return;
        }

        _motionFps = Math.Max(1, fps);
        _motionStart = startSeconds;
        _motionDuration = Math.Max(0.001, durationSeconds);
        _motionIndex = 0;

        CurrentImage = _motionFrames[0];
        Fidelity = PreviewFidelity.Motion;
        FidelityText = "Motion preview";
        HasError = false;
        IsLoading = false;
        IsStale = false;
        IsPlaying = true;
        _motionTimer.Interval = TimeSpan.FromSeconds(1.0 / _motionFps);
        _motionTimer.Start();
    }

    public void StopMotion()
    {
        _motionTimer.Stop();
        _motionFrames.Clear();
        _motionIndex = 0;
        IsPlaying = false;
    }

    public void SetLoading(bool loading) => IsLoading = loading;

    public void SetStale(bool stale) => IsStale = stale;

    public void SetReducedMotion(bool reduced)
    {
        IsReducedMotion = reduced;
        if (reduced)
            OnPropertyChanged(nameof(ReducedMotionHint));
    }

    public void SetError(string message)
    {
        HasError = !string.IsNullOrEmpty(message);
        ErrorText = message ?? "";
        IsLoading = false;
    }

    private void OnMotionTick(object? sender, EventArgs e)
    {
        if (_motionFrames.Count == 0)
            return;
        _motionIndex = (_motionIndex + 1) % _motionFrames.Count;
        CurrentImage = _motionFrames[_motionIndex];

        double time = _motionStart + ((_motionIndex / (double)_motionFps) % _motionDuration);
        MotionFrameChanged?.Invoke(time);
    }

    private static string BuildApproximationText(IReadOnlyList<string> notes)
    {
        if (notes is null || notes.Count == 0)
            return "Approximations";
        string joined = string.Join("; ", notes.Take(3));
        if (notes.Count > 3)
            joined += " …";
        return joined;
    }
}
