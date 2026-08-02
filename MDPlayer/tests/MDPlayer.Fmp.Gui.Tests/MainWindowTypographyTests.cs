using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.VisualTree;
using Fmp.Gui.Services;
using Fmp.Gui.ViewModels;
using Fmp.Gui.Views;
using Xunit;

namespace Fmp.Gui.Tests;

/// <summary>
/// Patch 4 typography/alignment headless tests: render the real MainWindow at
/// the supported sizes and assert the new metric-based invariants (compact
/// control height, minimum text size, aligned form columns, aligned preview
/// chrome, no sidebar overlap) from actual Bounds — no golden images.
/// </summary>
public sealed class MainWindowTypographyTests
{
    public static TheoryData<double, double> Sizes()
    {
        return new TheoryData<double, double>
        {
            { 1280, 820 },
            { 1600, 900 },
            { 1920, 1080 },
        };
    }

    private static async Task<MainWindowFixture> CreateWindowAsync() =>
        await MainWindowFixture.CreateReadyAsync();

    private static List<Control> Descendants(Control root)
    {
        var result = new List<Control>();
        foreach (Control child in root.GetVisualDescendants().OfType<Control>())
            result.Add(child);
        return result;
    }

    private static List<T> WithClass<T>(IEnumerable<Control> descendants, string className)
        where T : Control
    {
        return descendants
            .OfType<T>()
            .Where(c => ((Avalonia.StyledElement)c).Classes.Contains(className))
            .Where(c => c.Bounds.Width > 0 && c.Bounds.Height > 0)
            .ToList();
    }

    /// <summary>
    /// Compact controls that sit in a field row: a labelled 96px-column Grid
    /// (i.e. a Grid whose direct children include a <c>field-label</c>). These
    /// are the controls that must share one left edge.
    /// </summary>
    private static List<Control> FieldControls(MainWindow window)
    {
        var descendants = Descendants(window);
        var fieldGrids = new HashSet<Control>();
        foreach (var label in WithClass<TextBlock>(descendants, "field-label"))
        {
            if (label.Parent is Grid g)
                fieldGrids.Add(g);
        }
        return WithClass<Control>(descendants, "compact")
            .Where(c => c is NumericUpDown or ComboBox or TextBox)
            .Where(c => fieldGrids.Contains(c.Parent))
            .ToList();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task EveryCompactControl_IsAtLeast34PxTall(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);

        var descendants = Descendants(window.Window);
        var compact = WithClass<Control>(descendants, "compact").ToList();

        // The compact badge is allowed to be smaller; exclude text-only badges.
        var forms = compact
            .Where(c => c is NumericUpDown or ComboBox or TextBox)
            .ToList();

        Assert.NotEmpty(forms);
        foreach (Control c in forms)
            Assert.True(
                c.Bounds.Height >= 34 - 1, // tolerate 1px layout rounding
                $"{c.GetType().Name}#{c.Name} compact control is {c.Bounds.Height}px tall (<34)");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task EveryPersistentText_IsAtLeast12Px(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);

        var badgeTexts = new HashSet<TextBlock>();
        foreach (Control descendant in Descendants(window.Window))
        {
            if (descendant is Border b && Math.Abs(b.CornerRadius.TopLeft - 9) < 0.01)
            {
                foreach (var tb in b.GetVisualDescendants().OfType<TextBlock>())
                    badgeTexts.Add(tb);
            }
        }

        foreach (TextBlock c in Descendants(window.Window).OfType<TextBlock>())
        {
            if (!c.IsVisible || c.Bounds.Width <= 0 || c.Bounds.Height <= 0)
                continue;
            if (c.Text is not (string s) || string.IsNullOrWhiteSpace(s))
                continue;
            // The compact preview badge is the one allowed sub-12px element.
            if (badgeTexts.Contains(c))
                continue;
            double fontSize = c.FontSize;
            Assert.True(
                fontSize >= 12 - 0.001,
                $"persistent TextBlock '{c.Text}' uses {fontSize}px (<12)");
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task FormControls_ShareOneLeftEdge(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);

        var forms = FieldControls(window.Window);

        Assert.NotEmpty(forms);
        double firstX = forms.First().Bounds.X;
        foreach (Control c in forms)
            Assert.True(
                Math.Abs(c.Bounds.X - firstX) <= 1.5,
                $"compact control {c.GetType().Name}#{c.Name} X={c.Bounds.X:f1}, expected ≈{firstX:f1}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task FieldLabels_ShareOneRightEdge(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);

        var labels = WithClass<TextBlock>(Descendants(window.Window), "field-label").ToList();
        Assert.NotEmpty(labels);
        double right = labels.First().Bounds.Right;
        foreach (TextBlock l in labels)
            Assert.True(
                Math.Abs(l.Bounds.Right - right) <= 1.5,
                $"field-label '{l.Text}' right={l.Bounds.Right:f1}, expected ≈{right:f1}");
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task PreviewHeaderViewportTransport_ShareHorizontalBounds(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);
        window.Window.Show();
        window.Window.UpdateLayout();

        Border viewport = Required<Border>(window.Window, "PreviewViewport");
        Border transport = Required<Border>(window.Window, "TimelineTransport");

        // The preview header, viewport and transport live in the same grid
        // (RowDefinitions 40,*,44) inside PreviewPanel, so they share X/width.
        Assert.True(viewport.Bounds.X <= transport.Bounds.X + 0.5);
        Assert.True(Math.Abs(viewport.Bounds.Width - transport.Bounds.Width) <= 1.5);
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task SidebarChildren_DoNotOverlap(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);

        // Within every labelled field grid: the label (col 0) and its control
        // (col 1) must not overlap, and the labels of one field must not cover
        // the control of another on a different row. Comparing whole-window
        // absolute bounds is confounded by the scrollable sidebar, so each grid
        // is validated in isolation.
        var descendantList = Descendants(window.Window);
        var fieldGrids = new HashSet<Grid>();
        foreach (var label in WithClass<TextBlock>(descendantList, "field-label"))
        {
            if (label.Parent is Grid g)
            {
                fieldGrids.Add(g);
                Assert.True(
                    label.Bounds.Right <= g.Bounds.X + 97 + 1,
                    $"field-label '{label.Text}' not confined to the 96px label column");
            }
        }

        foreach (Grid grid in fieldGrids)
        {
            var labels = grid.GetVisualDescendants().OfType<TextBlock>()
                .Where(c => c.Classes.Contains("field-label")).ToList();
            var controls = grid.GetVisualDescendants().OfType<Control>()
                .Where(c => c is NumericUpDown or ComboBox or TextBox)
                .Where(c => c.Bounds.Width > 0 && c.Bounds.Height > 0 && c.Bounds.X >= 0 && c.Bounds.Y >= 0)
                .ToList();
            foreach (var label in labels)
            {
                Point ll = label.TranslatePoint(new Point(0, 0), grid) ?? default;
                Point lr = label.TranslatePoint(new Point(label.Bounds.Width, label.Bounds.Height), grid) ?? default;
                Rect labelInGrid = new Rect(ll, lr);
                foreach (var control in controls)
                {
                    Point cl = control.TranslatePoint(new Point(0, 0), grid) ?? default;
                    Point cr = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), grid) ?? default;
                    Rect controlInGrid = new Rect(cl, cr);
                    bool overlaps = labelInGrid.X < controlInGrid.Right
                        && controlInGrid.X < labelInGrid.Right
                        && labelInGrid.Y < controlInGrid.Bottom
                        && controlInGrid.Y < labelInGrid.Bottom;
                    Assert.False(
                        overlaps,
                        $"field-label '{label.Text}' overlaps control: label[{labelInGrid}] control[{control.GetType().Name} {controlInGrid}]");
                }
            }
        }
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public async Task StatusBar_StaysWithinWindow(double width, double height)
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, width, height);

        Border statusBar = Required<Border>(window.Window, "StatusBar");
        Assert.True(statusBar.Bounds.Bottom <= window.Window.ClientSize.Height + 1);
        Assert.True(statusBar.Bounds.Top >= 0);
    }

    [AvaloniaFact]
    public async Task RendererViewport_Keeps60PercentAtMinimumWidth()
    {
        await using var window = await CreateWindowAsync();
        SetSize(window.Window, 1280, 820);

        Grid mainContent = Required<Grid>(window.Window, "MainContent");
        Grid preview = Required<Grid>(window.Window, "PreviewPanel");
        Assert.True(
            preview.Bounds.Width >= mainContent.Bounds.Width * 0.60,
            $"preview {preview.Bounds.Width} < 60% of main {mainContent.Bounds.Width}");
    }

    private static void SetSize(MainWindow window, double width, double height)
    {
        window.Width = width;
        window.Height = height;
        window.Show();
        window.UpdateLayout();
    }

    private static T Required<T>(Control root, string name)
        where T : Control
    {
        T? control = root.FindControl<T>(name);
        Assert.NotNull(control);
        return control!;
    }

    private sealed class MainWindowFixture : IAsyncDisposable
    {
        public MainWindow Window { get; }

        public static async Task<MainWindowFixture> CreateReadyAsync()
        {
            string inputPath = Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-typo-" + Guid.NewGuid() + ".vgz");
            await File.WriteAllBytesAsync(inputPath, new byte[] { 0x56, 0x67, 0x6d });
            string settingsPath = Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-typo-" + Guid.NewGuid() + ".json");

            var factory = new RecordingPreviewFactory();
            var viewModel = new MainWindowViewModel(
                new Fmp.Gui.Services.GuiSettingsStore(settingsPath),
                new FileDialogService(),
                new ClipboardService(),
                new ExportProcessService(null),
                factory,
                initialInputPath: null);

            var window = new MainWindow(viewModel);
            await viewModel.OpenInputAsync(inputPath);
            await viewModel.WaitForPreviewRefreshAsync();
            viewModel.SetOutputPath(Path.Combine(
                Path.GetTempPath(), "mdplayer-gui-typo-" + Guid.NewGuid() + ".mp4"));
            return new MainWindowFixture(window, viewModel, inputPath, settingsPath);
        }

        private MainWindowFixture(MainWindow window, MainWindowViewModel vm, string input, string settings)
        {
            Window = window;
            _vm = vm;
            _input = input;
            _settings = settings;
        }

        private readonly MainWindowViewModel _vm;
        private readonly string _input;
        private readonly string _settings;

        public async ValueTask DisposeAsync()
        {
            await _vm.ShutdownAsync();
            Window.Hide();
            File.Delete(_input);
            File.Delete(_settings);
        }
    }
}
