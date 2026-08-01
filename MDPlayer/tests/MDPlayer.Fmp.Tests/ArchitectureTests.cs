using System.Reflection;
using Xunit;

namespace MDPlayer.Fmp.Tests;

/// <summary>
/// Architecture-enforcement tests that guard against regressions
/// toward Windows-specific or forbidden dependencies.
///
/// These tests ensure the production assemblies remain portable
/// (plain net8.0) and free of WinForms, NAudio, MDPlayerx64,
/// real-chip libraries, VST, MIDI and other Windows-harness baggage.
/// </summary>
public class ArchitectureTests
{
    /// <summary>
    /// Forbidden assembly reference name fragments.
    /// Any referenced assembly whose name starts with one of these
    /// prefixes triggers a test failure.
    /// </summary>
    private static readonly string[] ForbiddenReferencePrefixes =
    [
        "System.Windows.Forms",
        "NAudio",
        "MDPlayerx64",
        "ChipRegister",
        "Audio",
        "RealChip",
        "VST",
        "MIDI",
    ];

    /// <summary>
    /// Load the Core assembly via a known type.
    /// </summary>
    private static Assembly CoreAssembly =>
        typeof(global::Fmp.Core.Rendering.FmpRuntime).Assembly;

    /// <summary>
    /// Verify that MDPlayer.Fmp.Core references no forbidden assemblies.
    /// </summary>
    [Fact]
    public void Core_HasNoForbiddenAssemblyReferences()
    {
        var refs = CoreAssembly.GetReferencedAssemblies();
        var violations = new List<string>();

        foreach (var r in refs)
        {
            foreach (var prefix in ForbiddenReferencePrefixes)
            {
                if (r.Name != null && r.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{r.Name} (matches prefix '{prefix}')");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Verify that MDPlayer.Fmp.Core has no types that depend on
    /// forbidden namespaces (e.g. System.Windows.Forms, NAudio).
    /// Catches transitive dependencies not visible as direct assembly refs.
    /// </summary>
    [Fact]
    public void Core_HasNoTypesFromForbiddenNamespaces()
    {
        // Scan all methods in Core for parameter/return types from forbidden namespaces
        var types = CoreAssembly.GetTypes();
        var violations = new List<string>();

        foreach (var type in types)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);

            foreach (var method in methods)
            {
                if (method.Name == "Finalize" || method.Name == "MemberwiseClone")
                    continue;

                CheckType(method.ReturnType, type, method, violations);

                foreach (var param in method.GetParameters())
                    CheckType(param.ParameterType, type, method, violations);
            }

            // Check fields
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
                CheckType(field.FieldType, type, field, violations);

            // Check properties
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var prop in properties)
            {
                if (prop.CanRead)
                    CheckType(prop.PropertyType, type, prop, violations);
            }

            // Check base type
            if (type.BaseType != null && type.BaseType.Assembly != CoreAssembly)
                CheckType(type.BaseType, type, type, violations);
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Verify that MDPlayer.Fmp.Cli references no forbidden assemblies.
    /// Searches for the CLI DLL in both Debug and Release configurations.
    /// </summary>
    [SkippableFact]
    public void Cli_HasNoForbiddenAssemblyReferences()
    {
        // Load CLI assembly — search both Debug and Release output paths
        string cliDir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",
            "src", "MDPlayer.Fmp.Cli", "bin"
        ));

        string[] configs = ["Debug", "Release"];
        string cliPath = null;
        foreach (var cfg in configs)
        {
            foreach (string assemblyName in new[] { "mdplayer-render.dll", "fmp-render.dll" })
            {
                var candidate = Path.Combine(cliDir, cfg, "net8.0", assemblyName);
                if (File.Exists(candidate))
                {
                    cliPath = candidate;
                    break;
                }
            }
            if (cliPath != null) break;
        }

        Skip.If(cliPath == null, "CLI assembly is not built; build the CLI before running this architecture test.");

        var cliAsm = Assembly.LoadFrom(cliPath);
        var refs = cliAsm.GetReferencedAssemblies();
        var violations = new List<string>();

        foreach (var r in refs)
        {
            foreach (var prefix in ForbiddenReferencePrefixes)
            {
                if (r.Name != null && r.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{r.Name} (matches prefix '{prefix}')");
                }
            }
        }

        Assert.Empty(violations);
    }

    /// <summary>
    /// Verify that no production assembly targets a Windows-specific TFM.
    /// Every project must target plain net8.0 (no -windows suffix).
    /// </summary>
    [Fact]
    public void ProductionAssemblies_TargetNet8_NotWindowsSpecific()
    {
        var coreAsm = CoreAssembly;
        // Check that the assembly doesn't have Windows-specific attributes
        var attrs = coreAsm.GetCustomAttributesData();
        foreach (var attr in attrs)
        {
            var fullName = attr.AttributeType.FullName ?? "";
            Assert.DoesNotContain("Windows", fullName);
        }
    }

    /// <summary>
    /// Verify the Core project defines no types in Windows-only namespaces.
    /// </summary>
    [Fact]
    public void Core_HasNoWindowsNamespace()
    {
        var types = CoreAssembly.GetTypes();
        foreach (var type in types)
        {
            var ns = type.Namespace ?? "";
            Assert.DoesNotContain("Windows", ns);
        }
    }

    // ----------------------------------------------------------------
    // Helpers
    // ----------------------------------------------------------------

    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Windows.Forms",
        "NAudio",
        "MDPlayerx64",
        "Audio.",
        "ChipRegister",
        "RealChip",
        "VST",
        "MIDI",
    ];

    private static void CheckType(Type t, Type declaringType, MemberInfo member, List<string> violations)
    {
        if (t == null || t.Assembly == CoreAssembly)
            return;

        var ns = t.Namespace ?? "";
        foreach (var prefix in ForbiddenNamespacePrefixes)
        {
            if (ns.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var memberInfo = member != null ? $"{declaringType.FullName}.{member.Name}" : declaringType.FullName;
                violations.Add($"{memberInfo} uses forbidden type {t.FullName} in namespace '{ns}' (matches prefix '{prefix}')");
            }
        }
    }
}
