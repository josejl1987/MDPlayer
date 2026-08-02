using Avalonia.Media.Imaging;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Holds the current still-preview bitmap plus loading/error/refinement badges.
/// While loading a new frame the last valid image is kept on screen (spinner
/// overlay instead of clearing). Also tracks the fidelity of the current frame
/// and optional refinement warnings for non-blocking preview failures.
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private Bitmap? _currentImage;
    private bool _isLoading;
    private bool _hasError;
    private string _errorText = "";
    private string _loadingText = "";
    private bool _hasWarning;
    private string _warningText = "";
    private PreviewFidelity? _currentFidelity;
    private PreviewScopeKind? _currentScopeKind;

    public PreviewViewModel()
    {
        RetryCommand = new RelayCommand(() => RetryRequested?.Invoke());
    }

    /// <summary>Raised when the user clicks the error banner's Retry button.</summary>
    public event Action? RetryRequested;

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

    public string LoadingText
    {
        get => _loadingText;
        private set => SetProperty(ref _loadingText, value);
    }

    /// <summary>True when a non-blocking refinement warning is shown.</summary>
    public bool HasWarning
    {
        get => _hasWarning;
        private set => SetProperty(ref _hasWarning, value);
    }

    public string WarningText
    {
        get => _warningText;
        private set => SetProperty(ref _warningText, value);
    }

    /// <summary>Fidelity of the frame currently displayed, or null before any frame exist.</summary>
    public PreviewFidelity? CurrentFidelity
    {
        get => _currentFidelity;
        private set
        {
            if (SetProperty(ref _currentFidelity, value))
            {
                OnPropertyChanged(nameof(IsRefined));
                OnPropertyChanged(nameof(BadgeText));
            }
        }
    }

    /// <summary>True when the displayed frame is a fully prepared interactive/accurate still.</summary>
    public bool IsRefined =>
        CurrentFidelity is PreviewFidelity.InteractiveStill
            or PreviewFidelity.AccurateStill;

    /// <summary>
    /// The scope-source kind of the frame currently displayed, or null before
    /// any frame exists. Drives the header badge.
    /// </summary>
    public PreviewScopeKind? CurrentScopeKind
    {
        get => _currentScopeKind;
        private set
        {
            if (SetProperty(ref _currentScopeKind, value))
                OnPropertyChanged(nameof(BadgeText));
        }
    }

    /// <summary>
    /// Short badge shown in the preview header, bound to the scope-source
    /// actually used by the displayed frame rather than background completion.
    /// </summary>
    public string BadgeText => CurrentScopeKind switch
    {
        null => "No preview",
        PreviewScopeKind.Corrscope => "Accurate",
        PreviewScopeKind.PerChannel => "Refined",
        PreviewScopeKind.MasterFallback => "Master scope",
        _ => "Quick",
    };

    /// <summary>Applies a freshly rendered frame, replacing the current image.</summary>
    public void ApplyFrame(PreviewFrameResult result)
    {
        try
        {
            using var stream = new MemoryStream(result.PngBytes);
            var bitmap = new Bitmap(stream);
            ReplaceCurrentImage(bitmap);
            IsLoading = false;
            HasError = false;
            ErrorText = "";
            CurrentFidelity = result.Fidelity;
            CurrentScopeKind = result.ScopeKind;
            // A successful refined frame clears any previous refinement warning.
            if (IsRefined)
                SetRefinementWarning("");
            else
                SetRefinementWarning(WarningText);
        }
        catch (Exception ex)
        {
            SetError("Could not decode preview frame: " + ex.Message);
        }
    }

    public void Dispose()
    {
        ReplaceCurrentImage(null);
    }

    /// <summary>Clears the stage when the input/session changes.</summary>
    public void Clear()
    {
        ReplaceCurrentImage(null);
        HasError = false;
        ErrorText = "";
        IsLoading = false;
        LoadingText = "";
        CurrentFidelity = null;
        CurrentScopeKind = null;
        SetRefinementWarning("");
    }

    private void ReplaceCurrentImage(Bitmap? next)
    {
        Bitmap? previous = _currentImage;
        if (ReferenceEquals(previous, next))
            return;
        CurrentImage = next;
        previous?.Dispose();
    }

    public void SetLoading(bool loading, string message = "")
    {
        IsLoading = loading;
        LoadingText = loading ? (message ?? "") : "";
    }

    public void SetRefinementWarning(string message)
    {
        HasWarning = !string.IsNullOrWhiteSpace(message);
        WarningText = message ?? "";
        IsLoading = false;
    }

    public void SetError(string message)
    {
        HasError = !string.IsNullOrEmpty(message);
        ErrorText = message ?? "";
        IsLoading = false;
    }
}