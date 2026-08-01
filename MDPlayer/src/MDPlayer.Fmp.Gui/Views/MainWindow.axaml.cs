using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Fmp.Application.Contracts;
using Fmp.Gui.ViewModels;

namespace Fmp.Gui.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _vm;
    private bool _closeConfirmed;

    public MainWindow(MainWindowViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;

        vm.RecoveryPrompt = project => Modal.ConfirmAsync(
            this,
            "Recover unsaved work?",
            $"A recovery snapshot from {project.LastSavedUtc:g} was found for:\n\n{project.Request.InputPath}\n\nRestore it?",
            "Restore",
            "Discard");

        vm.CloseConfirm = () => Modal.CloseAsync(
            this,
            "Unsaved changes",
            "Save your changes before closing?",
            "Save",
            "Discard",
            "Cancel");
        vm.Tools.DetailsRequested += ShowToolDetails;

        vm.CommandPanelFocusRequested += () =>
        {
            if (this.FindControl<TextBox>("CommandText") is { } commandText)
            {
                commandText.Focus();
                commandText.SelectAll();
            }
        };

        Opened += OnOpened;
        Closing += OnClosing;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void OnOpened(object? sender, EventArgs e) => _ = _vm.InitializeAsync();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
            return;

        e.Cancel = true;
        if (!_vm.IsDirty)
        {
            _closeConfirmed = true;
            await _vm.ShutdownAsync();
            Close();
            return;
        }

        CloseDecision decision = await (_vm.CloseConfirm?.Invoke() ?? Task.FromResult(CloseDecision.Cancel));
        if (decision == CloseDecision.Cancel)
            return;
        if (decision == CloseDecision.Save && !await _vm.SaveProjectAsync())
            return;

        _closeConfirmed = true;
        await _vm.ShutdownAsync();
        Close();
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

    private void ShowToolDetails()
    {
        ToolDetailsDialog.Show(this, _vm.Tools.Statuses);
    }

    private void ShowToolSettings()
    {
        ToolSettingsDialog.Show(this, _vm.ToolSettings);
    }

    private void OnToolSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ShowToolSettings();
}

/// <summary>Small code-built modal dialogs (no extra XAML/compiled-binding surface).</summary>
internal static class Modal
{
    public static Task<bool> ConfirmAsync(
        Window owner,
        string title,
        string message,
        string confirmText,
        string cancelText)
    {
        var completion = new TaskCompletionSource<bool>();

        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            MinWidth = 380,
        };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var confirm = new Button { Content = confirmText };
        confirm.Classes.Add("accent");
        var cancel = new Button { Content = cancelText };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            MaxWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            Content = panel,
        };

        confirm.Click += (_, _) =>
        {
            completion.TrySetResult(true);
            dialog.Close();
        };
        cancel.Click += (_, _) =>
        {
            completion.TrySetResult(false);
            dialog.Close();
        };
        dialog.Closed += (_, _) => completion.TrySetResult(false);

        dialog.ShowDialog(owner);
        return completion.Task;
    }

    public static Task<CloseDecision> CloseAsync(
        Window owner,
        string title,
        string message,
        string saveText,
        string discardText,
        string cancelText)
    {
        var completion = new TaskCompletionSource<CloseDecision>();
        var panel = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 14,
            MinWidth = 380,
        };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 400,
        });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var save = new Button { Content = saveText };
        save.Classes.Add("accent");
        var discard = new Button { Content = discardText };
        var cancel = new Button { Content = cancelText };
        buttons.Children.Add(save);
        buttons.Children.Add(discard);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = 460,
            MaxWidth = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            SystemDecorations = SystemDecorations.BorderOnly,
            Content = panel,
        };

        save.Click += (_, _) => Complete(CloseDecision.Save);
        discard.Click += (_, _) => Complete(CloseDecision.Discard);
        cancel.Click += (_, _) => Complete(CloseDecision.Cancel);
        dialog.Closed += (_, _) => completion.TrySetResult(CloseDecision.Cancel);

        void Complete(CloseDecision decision)
        {
            completion.TrySetResult(decision);
            dialog.Close();
        }

        dialog.ShowDialog(owner);
        return completion.Task;
    }
}

internal static class ToolDetailsDialog
{
    public static void Show(Window owner, IEnumerable<ToolStatus> statuses)
    {
        var content = new StackPanel { Margin = new Thickness(18), Spacing = 10 };
        foreach (ToolStatus status in statuses)
        {
            var row = new StackPanel { Spacing = 2 };
            row.Children.Add(new TextBlock
            {
                Text = $"{status.Role} · {(status.IsAvailable ? "available" : "unavailable")}{(status.IsRequired ? " · required" : " · optional")}",
                FontWeight = FontWeight.SemiBold,
            });
            row.Children.Add(new TextBlock
            {
                Text = string.Join(Environment.NewLine, new[]
                {
                    "Path: " + (status.ResolvedPath ?? "not resolved"),
                    "Version: " + (status.Version ?? "unknown"),
                    "Source: " + (status.Source ?? "unknown"),
                    status.Detail,
                    status.SuggestedAction,
                }.Where(value => !string.IsNullOrWhiteSpace(value))),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
            });
            content.Children.Add(new Border
            {
                Padding = new Thickness(8),
                Child = row,
            });
        }

        if (content.Children.Count == 0)
            content.Children.Add(new TextBlock { Text = "No tool results yet. Run a tool check first." });

        content.Children.Add(new Button
        {
            Content = "Close",
            HorizontalAlignment = HorizontalAlignment.Right,
            Command = new RelayCommand(() => { }),
        });

        var dialog = new Window
        {
            Title = "Tool details",
            Width = 680,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = content },
        };
        if (content.Children[^1] is Button close)
            close.Click += (_, _) => dialog.Close();
        dialog.ShowDialog(owner);
    }
}

/// <summary>
/// Code-built tool-path settings dialog (Phase 8). Edits the persisted runtime
/// tool paths (never serialized into projects) and triggers a tool recheck
/// after saving so the status bar reflects the new configuration.
/// </summary>
internal static class ToolSettingsDialog
{
    public static void Show(Window owner, ToolSettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(viewModel);

        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12, MinWidth = 520 };

        panel.Children.Add(new TextBlock
        {
            Text = "Runtime tool paths (stored in app settings, never in project files).",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.8,
        });

        panel.Children.Add(Row("mdplayer-render executable", nameof(ToolSettingsViewModel.RenderCliPath), viewModel));
        panel.Children.Add(Row("FMP.COM", nameof(ToolSettingsViewModel.FmpComPath), viewModel));
        panel.Children.Add(Row("Corrscope", nameof(ToolSettingsViewModel.CorrscopePath), viewModel));
        panel.Children.Add(Row("FFmpeg", nameof(ToolSettingsViewModel.FfmpegPath), viewModel));
        panel.Children.Add(Row("Analysis Python", nameof(ToolSettingsViewModel.AnalysisPython), viewModel));
        panel.Children.Add(Row("Assets directory", nameof(ToolSettingsViewModel.AssetsDir), viewModel));

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var save = new Button { Content = "Save" };
        save.Classes.Add("accent");
        var reset = new Button { Content = "Reset" };
        var close = new Button { Content = "Close" };
        buttons.Children.Add(save);
        buttons.Children.Add(reset);
        buttons.Children.Add(close);
        panel.Children.Add(buttons);

        var dialog = new Window
        {
            Title = "Tool settings",
            Width = 560,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = panel,
        };

        save.Click += (_, _) =>
        {
            viewModel.SaveCommand.Execute(null);
            dialog.Close();
        };
        reset.Click += (_, _) => viewModel.ResetCommand.Execute(null);
        close.Click += (_, _) => dialog.Close();

        dialog.ShowDialog(owner);
    }

    private static Grid Row(string label, string propertyName, ToolSettingsViewModel viewModel)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(new GridLength(170, GridUnitType.Pixel)),
                new ColumnDefinition(new GridLength(1, GridUnitType.Star)),
            },
            ColumnSpacing = 8,
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var textBox = new TextBox { Watermark = "auto (search system paths)" };
        textBox.Bind(Avalonia.Controls.TextBox.TextProperty,
            new Avalonia.Data.Binding(propertyName) { Source = viewModel });
        Grid.SetColumn(textBox, 1);
        grid.Children.Add(textBox);
        return grid;
    }
}
