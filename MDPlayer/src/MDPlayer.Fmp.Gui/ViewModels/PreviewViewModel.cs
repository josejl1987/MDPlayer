using Avalonia.Media.Imaging;
using Fmp.Application.Contracts;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Holds the current accurate-still preview bitmap plus loading/error badges.
/// While loading a new frame the last valid image is kept on screen (spinner
/// overlay instead of clearing).
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private Bitmap? _currentImage;
    private bool _isLoading;
    private bool _hasError;
    private string _errorText = "";

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
    }

    private void ReplaceCurrentImage(Bitmap? next)
    {
        Bitmap? previous = _currentImage;
        if (ReferenceEquals(previous, next))
            return;
        CurrentImage = next;
        previous?.Dispose();
    }

    public void SetLoading(bool loading) => IsLoading = loading;

    public void SetError(string message)
    {
        HasError = !string.IsNullOrEmpty(message);
        ErrorText = message ?? "";
        IsLoading = false;
    }
}