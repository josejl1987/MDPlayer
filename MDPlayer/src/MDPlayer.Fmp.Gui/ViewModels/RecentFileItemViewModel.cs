namespace Fmp.Gui.ViewModels;

/// <summary>A persisted recent input or project entry shown in the empty state.</summary>
public sealed class RecentFileItemViewModel : ObservableObject
{
    private readonly Func<string, Task> _open;

    public RecentFileItemViewModel(string path, Func<string, Task> open)
    {
        Path = path;
        _open = open;
        OpenCommand = new AsyncRelayCommand(() => _open(Path), () => IsAvailable);
    }

    public string Path { get; }
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string ParentDirectory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string Kind => "Input";
    public bool IsAvailable => File.Exists(Path);
    public AsyncRelayCommand OpenCommand { get; }
}
