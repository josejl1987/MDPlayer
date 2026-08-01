using Avalonia.Controls;

namespace Fmp.Gui.Services;

/// <summary>Wraps the platform clipboard.</summary>
public sealed class ClipboardService
{
    public Func<TopLevel?>? TopLevelProvider { get; set; }

    public async Task SetTextAsync(string text)
    {
        if (TopLevelProvider?.Invoke() is { Clipboard: { } clipboard })
            await clipboard.SetTextAsync(text);
    }
}
