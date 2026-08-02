using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
// Pin the Avalonia type: `using Fmp.Application.*` otherwise makes
// `Application` resolve to the Fmp.Application namespace.
using Application = Avalonia.Application;
using Fmp.Cli;
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

            var dialogs = new FileDialogService();
            var clipboard = new ClipboardService();
            var export = new ExportProcessService(settings.Settings.RenderCliPath);

            // Uses the in-process preview session factory, so UI previews run
            // inside the app and reuse the captured timeline, prepared source
            // and frame renderer across seeks. Final video rendering still goes
            // through the standalone CLI via ExportProcessService below.
            var previewFactory =
                new InProcessVisualizationPreviewSessionFactory();

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
}
