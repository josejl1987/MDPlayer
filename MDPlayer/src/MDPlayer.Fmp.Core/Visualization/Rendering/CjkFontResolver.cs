using System.Diagnostics;
using System.Text;
using SixLabors.Fonts;
using SixLabors.Fonts.Unicode;

namespace Fmp.Core.Visualization.Rendering;

internal static class CjkFontResolver
{
    private static readonly string[] LinuxCandidates =
    [
        "/usr/share/fonts/noto-cjk/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/noto-cjk/NotoSansCJKjp-Regular.otf",
        "/usr/share/fonts/opentype/noto/NotoSansCJK-Regular.ttc",
        "/usr/share/fonts/opentype/noto/NotoSansCJKjp-Regular.otf",
        "/usr/share/fonts/TTF/NotoSansCJK-Regular.ttc",
    ];

    public static string Resolve(string explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            string fullPath = Path.GetFullPath(explicitPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"CJK font not found: {fullPath}", fullPath);
            return fullPath;
        }

        string environmentPath = Environment.GetEnvironmentVariable("FMP_RENDER_FONT");
        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            string fullPath = Path.GetFullPath(environmentPath);
            if (!File.Exists(fullPath))
                throw new FileNotFoundException($"FMP_RENDER_FONT font not found: {fullPath}", fullPath);
            return fullPath;
        }

        if (OperatingSystem.IsLinux())
        {
            string fontconfigPath = ResolveWithFontconfig();
            if (!string.IsNullOrEmpty(fontconfigPath))
                return fontconfigPath;

            foreach (string candidate in LinuxCandidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            string fonts = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
            foreach (string name in new[] { "YuGothR.ttc", "msgothic.ttc", "meiryo.ttc" })
            {
                string candidate = Path.Combine(fonts, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static string ResolveWithFontconfig()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "fc-match",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "-f", "%{file}", "Noto Sans CJK JP:lang=ja" },
            });
            if (process == null)
                return null;

            string path = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(2000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Validates the actual font face selected for the presentation. A font
    /// file existing on disk is not enough: ImageSharp can otherwise replace
    /// an uncovered scalar with its missing-glyph box.
    /// </summary>
    public static void ValidateCoverage(string fontPath, IReadOnlyList<Rune> requiredRunes)
    {
        _ = LoadFamily(fontPath, requiredRunes);
    }

    public static FontFamily LoadFamily(string fontPath, IReadOnlyList<Rune> requiredRunes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fontPath);
        ArgumentNullException.ThrowIfNull(requiredRunes);

        try
        {
            // AddCollection is important for TTC files. Add(path) selects the
            // collection header as though it were a standalone face and older
            // ImageSharp.Fonts versions reject valid Noto CJK collections.
            var collection = new FontCollection();
            IEnumerable<FontFamily> families;
            if (string.Equals(Path.GetExtension(fontPath), ".ttc", StringComparison.OrdinalIgnoreCase))
            {
                families = collection.AddCollection(fontPath);
            }
            else
            {
                try
                {
                    families = [collection.Add(fontPath)];
                }
                catch
                {
                    collection = new FontCollection();
                    families = collection.AddCollection(fontPath);
                }
            }
            Font firstFont = null;
            foreach (FontFamily family in families)
            {
                Font font = family.CreateFont(16, FontStyle.Regular);
                firstFont ??= font;
                if (HasCoverage(font, requiredRunes))
                    return family;
            }

            if (firstFont != null)
            {
                Rune missing = requiredRunes.FirstOrDefault(rune =>
                    !Rune.IsControl(rune)
                    && !HasGlyph(firstFont, rune));
                if (missing.Value != 0)
                    throw MissingGlyph(missing);
            }
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("error: ", StringComparison.Ordinal))
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"selected font '{fontPath}' could not be loaded", ex);
        }

        throw new InvalidOperationException(
            "error: selected font cannot render a required glyph in title\n" +
            "hint: pass --font PATH to a CJK-capable TrueType/OpenType font");
    }

    private static InvalidOperationException MissingGlyph(Rune rune)
        => new(
            $"error: selected font cannot render U+{rune.Value:X4} '{rune}' in title\n" +
            "hint: pass --font PATH to a CJK-capable TrueType/OpenType font");

    private static bool HasCoverage(Font font, IReadOnlyList<Rune> requiredRunes)
    {
        foreach (Rune rune in requiredRunes)
        {
            if (!Rune.IsControl(rune)
                && !HasGlyph(font, rune))
                return false;
        }
        return true;
    }

    private static bool HasGlyph(Font font, Rune rune)
        => font.FontMetrics.GetAvailableCodePoints()
            .Any(codePoint => codePoint.Value == rune.Value);
}
