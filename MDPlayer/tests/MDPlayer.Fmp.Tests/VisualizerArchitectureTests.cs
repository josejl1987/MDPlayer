using System.Reflection;
using Fmp.Application.Contracts;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Enforces the MDPlayer Visualizer architecture invariants (product spec §37):
/// dependency direction Core → Application → {Cli, Gui}, no Avalonia below the
/// GUI, no GUI↔CLI coupling, no chip-specific identifiers in the GUI, and the
/// shared request/formatting model living in Application.
/// </summary>
public class VisualizerArchitectureTests
{
    private static readonly Assembly CoreAssembly =
        typeof(global::Fmp.Core.Rendering.FmpRuntime).Assembly;

    private static readonly Assembly ApplicationAssembly =
        typeof(VisualizationRequest).Assembly;

    private static readonly Assembly GuiAssembly =
        typeof(global::Fmp.Gui.ViewModels.MainWindowViewModel).Assembly;

    private static bool References(Assembly assembly, string nameFragment)
        => assembly.GetReferencedAssemblies()
            .Any(reference => reference.Name is not null
                && reference.Name.StartsWith(nameFragment, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Core_DoesNotReferenceAvalonia() => Assert.False(
        References(CoreAssembly, "Avalonia"),
        "MDPlayer.Fmp.Core must not reference Avalonia.");

    [Fact]
    public void Application_DoesNotReferenceAvalonia() => Assert.False(
        References(ApplicationAssembly, "Avalonia"),
        "MDPlayer.Fmp.Application must not reference Avalonia.");

    [Fact]
    public void Application_ReferencesCore_NotCli() => Assert.True(
        References(ApplicationAssembly, "MDPlayer.Fmp.Core"),
        "MDPlayer.Fmp.Application must depend on MDPlayer.Fmp.Core.");

    [Fact]
    public void Gui_ReferencesApplication_NotCliOrCore()
    {
        Assert.True(
            References(GuiAssembly, "MDPlayer.Fmp.Application"),
            "MDPlayer.Fmp.Gui must reference MDPlayer.Fmp.Application.");
        Assert.False(
            References(GuiAssembly, "MDPlayer.Fmp.Cli"),
            "MDPlayer.Fmp.Gui must not reference the CLI parser assembly.");
        Assert.False(
            References(GuiAssembly, "MDPlayer.Fmp.Core"),
            "MDPlayer.Fmp.Gui must reach Core only through Application.");
    }

    /// <summary>
    /// GUI code must not contain chip-specific identifiers (spec §37 item 4):
    /// it must not know which chip produced a track.
    /// </summary>
    [Theory]
    [InlineData("ym2608")]
    [InlineData("ym2612")]
    [InlineData("ym2203")]
    [InlineData("ym2413")]
    [InlineData("sn76489")]
    [InlineData("ay8910")]
    [InlineData("ppz8")]
    [InlineData("opna")]
    [InlineData("nesapu")]
    public void Gui_HasNoChipSpecificIdentifiers(string chipToken)
    {
        foreach (Type type in GuiAssembly.GetTypes())
        {
            foreach (string member in AllDeclaredText(type))
            {
                Assert.DoesNotContain(chipToken, member);
            }
        }
    }

    /// <summary>
    /// GUI code must not construct renderer geometry directly (spec §37 item 5):
    /// panel regions come from the Application plan result.
    /// </summary>
    [Fact]
    public void Gui_DoesNotConstructRendererGeometry()
    {
        foreach (Type type in GuiAssembly.GetTypes())
        {
            Assert.DoesNotContain(nameof(global::Fmp.Core.Visualization.Rendering.OverlayLayout), type.FullName);
            Assert.DoesNotContain(nameof(global::Fmp.Core.Visualization.Rendering.PanelOverlayRenderer), type.FullName);
        }
    }

    /// <summary>
    /// The canonical command formatter exists in exactly one implementation
    /// (Application); the GUI consumes it rather than formatting commands.
    /// </summary>
    [Fact]
    public void CanonicalCommandFormatterLivesInApplicationOnly()
    {
        Type formatter = typeof(global::Fmp.Application.Export.IVisualizationCommandFormatter);
        Assert.NotNull(formatter);
        // No GUI type may define its own command-formatter-like method.
        Assert.Empty(GuiAssembly.GetTypes()
            .Where(type => type.GetMethods()
                .Any(method => method.Name.Contains("FormatCanonical", StringComparison.OrdinalIgnoreCase)
                    || method.Name.Contains("BuildCommand", StringComparison.OrdinalIgnoreCase))));
    }

    private static IEnumerable<string> AllDeclaredText(Type type)
    {
        foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            yield return field.Name;
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            yield return property.Name;
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            yield return method.Name;
    }
}
