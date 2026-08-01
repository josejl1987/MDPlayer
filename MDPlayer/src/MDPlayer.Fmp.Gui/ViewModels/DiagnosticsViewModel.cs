using System.Collections.ObjectModel;
using Fmp.Gui.Services;

namespace Fmp.Gui.ViewModels;

/// <summary>Expandable diagnostics log panel with copy-to-clipboard.</summary>
public sealed class DiagnosticsViewModel : ObservableObject
{
    private readonly ClipboardService _clipboard;
    private bool _isExpanded;

    public DiagnosticsViewModel(ClipboardService clipboard)
    {
        _clipboard = clipboard;
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyAsync);
    }

    public AsyncRelayCommand CopyDiagnosticsCommand { get; }

    public ObservableCollection<string> Log { get; } = new();

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void AddLine(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 500)
            Log.RemoveAt(0);
    }

    public void Clear() => Log.Clear();

    private async Task CopyAsync()
    {
        await _clipboard.SetTextAsync(string.Join(Environment.NewLine, Log));
    }
}
