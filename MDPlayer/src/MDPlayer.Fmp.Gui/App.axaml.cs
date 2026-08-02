using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
// Pin the Avalonia type: `using Fmp.Application.*` otherwise makes
// `Application` resolve to the Fmp.Application namespace.
using Application = Avalonia.Application;
using Fmp.Application.Preview;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Fmp.Gui.Views;

namespace Fmp.Gui;

public partial class App : global::Avalonia.Application
{
    /// <summary>Set by <see cref="Program.Main"/> when the app was launched with an input file.</summary>
    public static string? InitialInputPath { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new GuiSettingsStore();
            ApplyTheme(settings.Settings.ThemeVariant);

            var dialogs = new FileDialogService();
            var clipboard = new ClipboardService();
            var export = new ExportProcessService(settings.Settings.RenderCliPath);

            // Uses the process-based session factory from the Application layer
            // (drives `mdplayer-render plan/preview` with request JSON).
            var previewFactory = new CliPreviewSessionFactory();

            var vm = new MainWindowViewModel(
                settings, dialogs, clipboard, export, previewFactory, App.InitialInputPath);

            var window = new MainWindow(vm);
            dialogs.TopLevelProvider = () => window;
            clipboard.TopLevelProvider = () => window;

            desktop.MainWindow = window;
            window.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(string variant)
    {
        RequestedThemeVariant = variant switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
