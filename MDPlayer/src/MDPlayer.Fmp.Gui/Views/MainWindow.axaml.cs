using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fmp.Gui.ViewModels;

namespace Fmp.Gui.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;

    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Title = "MDPlayer Visualizer";

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += (_, _) => _ = _vm.InitializeAsync();

        // Observe the preview viewport's logical bounds so the preview bitmap
        // is requested at (close to) the actually displayed size, letting the
        // layout resolver choose full/overview/device grammar for those
        // dimensions instead of rendering a bitmap Avalonia scales back down.
        PreviewViewport.PropertyChanged += OnPreviewViewportPropertyChanged;
        PreviewViewport.SizeChanged += OnPreviewViewportSizeChanged;

#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void OnPreviewViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _vm.SchedulePreviewResize(Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
    }

    private void OnPreviewViewportPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == BoundsProperty)
        {
            Size bounds = PreviewViewport.Bounds.Size;
            _vm.SchedulePreviewResize(Math.Max(0, bounds.Width), Math.Max(0, bounds.Height));
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.Data.Contains(DataFormats.Files)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        string? path = e.Data.GetFiles()?
            .Select(file => file.TryGetLocalPath())
            .FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
        if (!string.IsNullOrWhiteSpace(path))
            await _vm.OpenInputAsync(path);
        e.Handled = true;
    }

    // The timeline slider binds Value to PreviewScrubTime (no preview while
    // dragging); a preview is scheduled exactly once when the drag completes
    // (pointer release) or after a discrete keyboard jump (key up).
    private void OnPreviewSliderReleased(object? sender, PointerReleasedEventArgs e)
        => _vm.CommitScrub();

    private void OnPreviewSliderKeyUp(object? sender, KeyEventArgs e)
        => _vm.CommitScrub();
}
