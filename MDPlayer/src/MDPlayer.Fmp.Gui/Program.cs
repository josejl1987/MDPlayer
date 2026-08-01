using Avalonia;
using Avalonia.Controls;
using Fmp.Gui;

namespace Fmp.Gui;

/// <summary>MDPlayer Visualizer entry point (Avalonia desktop).</summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // args[0] is the initial input file when the shell handed one to us.
        if (args.Length > 0 && File.Exists(args[0]))
            App.InitialInputPath = Path.GetFullPath(args[0]);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
