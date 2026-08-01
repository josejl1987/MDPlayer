using Avalonia;
using Avalonia.Controls;
using Avalonia.Diagnostics;
using Avalonia.Layout;
using Avalonia.Media;
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

        vm.DiscardConfirm = () => Modal.ConfirmAsync(
            this,
            "Unsaved changes",
            "This project has unsaved changes. Discard them and close?",
            "Discard",
            "Cancel");

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

#if DEBUG
        this.AttachDevTools();
#endif
    }

    private void OnOpened(object? sender, EventArgs e) => _ = _vm.InitializeAsync();

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_vm.IsDirty && !_closeConfirmed)
        {
            e.Cancel = true;
            bool discard = await (_vm.DiscardConfirm?.Invoke() ?? Task.FromResult(false));
            if (discard)
            {
                _closeConfirmed = true;
                Close();
            }
        }
        else if (!_closeConfirmed)
        {
            _closeConfirmed = true;
            _vm.Shutdown();
        }
    }
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
}
