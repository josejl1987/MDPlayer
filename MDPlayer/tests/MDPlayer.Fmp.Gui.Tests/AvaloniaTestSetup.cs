using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using AvaloniaApplication = Avalonia.Application;

[assembly: AvaloniaTestApplication(typeof(Fmp.Gui.Tests.TestAppBuilder))]
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Fmp.Gui.Tests;

public sealed class TestApplication : AvaloniaApplication
{
    public override void Initialize()
    {
        // Mirror the production App.axaml: Fluent (dark) + the visualizer theme.
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(
            new Uri("avares://mdplayer-visualizer/Styles/VisualizerTheme.axaml"))
        {
            Source = new Uri("avares://mdplayer-visualizer/Styles/VisualizerTheme.axaml"),
        });
    }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApplication>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions
        {
            UseHeadlessDrawing = false,
        });
}
