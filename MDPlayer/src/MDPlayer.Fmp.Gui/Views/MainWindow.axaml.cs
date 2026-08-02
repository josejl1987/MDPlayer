using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Fmp.Gui.Layout;
using Fmp.Gui.ViewModels;

namespace Fmp.Gui.Views;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly MainWindowViewModel _vm;

    private bool _isWideLayout;
    private GridLength _diagnosticsColumnWidth = new(0);
    private bool _diagnosticsVisible;

    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        Title = "MDPlayer Visualizer";

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        Opened += (_, _) =>
        {
            ApplyLayoutPolicy(EffectiveClientWidth());
            _ = _vm.InitializeAsync();
        };

        // Observe the preview viewport's logical bounds so the preview bitmap
        // is requested at (close to) the actually displayed size, letting the
        // layout resolver choose full/overview/device grammar for those
        // dimensions instead of rendering a bitmap Avalonia scales back down.
        PreviewViewport.PropertyChanged += OnPreviewViewportPropertyChanged;
        PreviewViewport.SizeChanged += OnPreviewViewportSizeChanged;

        SizeChanged += OnWindowSizeChanged;

#if DEBUG
        this.AttachDevTools();
#endif
    }

    /// <summary>True when the window is wide enough for the fixed diagnostics rail.</summary>
    public bool IsWideLayout
    {
        get => _isWideLayout;
        private set
        {
            if (_isWideLayout == value)
                return;
            _isWideLayout = value;
            OnPropertyChanged(nameof(IsWideLayout));
        }
    }

    /// <summary>Column width for the diagnostics rail (300px when visible, 0 when collapsed).</summary>
    public GridLength DiagnosticsColumnWidth
    {
        get => _diagnosticsColumnWidth;
        private set
        {
            if (_diagnosticsColumnWidth == value)
                return;
            _diagnosticsColumnWidth = value;
            OnPropertyChanged(nameof(DiagnosticsColumnWidth));
        }
    }

    /// <summary>Whether the diagnostics rail is currently visible vs. collapsed to the flyout.</summary>
    public bool DiagnosticsVisible
    {
        get => _diagnosticsVisible;
        private set
        {
            if (_diagnosticsVisible == value)
                return;
            _diagnosticsVisible = value;
            OnPropertyChanged(nameof(DiagnosticsVisible));
        }
    }

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e)
        => ApplyLayoutPolicy(EffectiveClientWidth());

    /// <summary>Returns the client width, falling back to the window width when
    /// the client size is not yet laid out (headless/host variations).</summary>
    private double EffectiveClientWidth()
    {
        double client = ClientSize.Width;
        if (!double.IsNaN(client) && client > 0)
            return client;
        double window = Width;
        if (double.IsNaN(window) || window <= 0)
            return client;
        return window;
    }

    private void ApplyLayoutPolicy(double clientWidth)
    {
        if (double.IsNaN(clientWidth) || clientWidth <= 0)
            return;

        var decision = StudioLayoutPolicy.Resolve(clientWidth);
        DiagnosticsColumnWidth = new GridLength(decision.DiagnosticsWidth);
        DiagnosticsVisible = decision.DiagnosticsVisible;
        IsWideLayout = decision.Mode == StudioLayoutMode.Wide;

        // Nudge named controls the layout tests inspect without depending on
        // bindings reaching window code-behind properties.
        if (MainContent.ColumnDefinitions.Count > 4)
            MainContent.ColumnDefinitions[4].Width = DiagnosticsColumnWidth;
        if (DiagnosticsColumn is not null)
            DiagnosticsColumn.IsVisible = DiagnosticsVisible;
        if (DiagnosticsSplitter is not null)
            DiagnosticsSplitter.IsVisible = DiagnosticsVisible;
        if (DiagnosticsToggleButton is not null)
            DiagnosticsToggleButton.IsVisible = !DiagnosticsVisible;
    }

    private void OnPreviewViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _vm.SchedulePreviewResize(Math.Max(0, e.NewSize.Width), Math.Max(0, e.NewSize.Height));
    }

    private void OnPreviewViewportPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Panel.BackgroundProperty
            || e.Property == Avalonia.Controls.Border.BorderBrushProperty)
            return;

        if (e.Property == Visual.BoundsProperty)
        {
            Rect bounds = e.NewValue is Rect value ? value : default;
            _vm.SchedulePreviewResize(Math.Max(0, bounds.Width), Math.Max(0, bounds.Height));
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
            e.DragEffects = DragDropEffects.Copy | DragDropEffects.Link;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DataFormats.Files))
        {
            string? candidate = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(candidate))
                await _vm.OpenInputAsync(candidate);
        }
        e.Handled = true;
    }

    // The timeline slider binds Value to PreviewScrubTime. Seeking is
    // committed from the value-change event rather than PointerReleased: the
    // Fluent template's thumb/track can consume the pointer-release and mark it
    // handled, so release-driven commits never fired and dragging the scrubber
    // did nothing. Value-change commits are debounced and coalesced by the VM
    // (QueuePreviewRefresh), so rapid drag updates collapse into one frame.
    private void OnPreviewSliderValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
        => _vm.CommitScrub();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
