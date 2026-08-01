using System.Text;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

/// <summary>
/// Reports MDPlayer driver formats that are known to the desktop application
/// but are not yet linked to the portable headless core. This keeps platform
/// availability and companion-asset diagnostics explicit without admitting an
/// extension as visualizable.
/// </summary>
internal sealed class MdPlayerDriverBackend : IPlaybackBackend
{
    private static readonly HashSet<string> DriverExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nrd", ".bgm", ".msd", ".ndp", ".mdr", ".mdx", ".mnd",
        ".muc", ".mub", ".mml", ".pmd", ".m", ".m2", ".mz", ".mus",
        ".o", ".ox", ".oy", ".zms", ".zmd", ".zgm", ".nsf", ".gbs",
        ".hes", ".sid", ".ay", ".mgs", ".rcp", ".rcs",
    };

    public string Id => "mdplayer-driver";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(environment);
        string extension = input.Extension.ToLowerInvariant();
        if (!DriverExtensions.Contains(extension))
            return new PlaybackProbeResult(false, extension.TrimStart('.'), [], [], []);
        if (!input.Exists)
            return new PlaybackProbeResult(false, extension.TrimStart('.'), [], [],
                [$"input not found: {input.FullName}"]);

        try
        {
            byte[] data = File.ReadAllBytes(input.FullName);
            return extension == ".mdx"
                ? ProbeMdx(input, environment, data)
                : PlatformUnavailable(
                    extension.TrimStart('.'),
                    $"{extension} is recognized by MDPlayer, but its headless driver bridge is not linked");
        }
        catch (IOException ex)
        {
            return new PlaybackProbeResult(false, extension.TrimStart('.'), [], [], [ex.Message]);
        }
    }

    public IPlaybackCaptureSession Open(
        FileInfo input,
        PlaybackOptions options,
        IPlaybackEventSink eventSink) =>
        throw new NotSupportedException(
            $"MDPlayer driver backend '{input.Extension.TrimStart('.')}' is not available in this headless build");

    private static PlaybackProbeResult ProbeMdx(
        FileInfo input,
        PlaybackEnvironment environment,
        byte[] data)
    {
        MdxHeader header;
        try
        {
            header = MdxHeader.Parse(data);
        }
        catch (MdPlayerDriverException ex)
        {
            return new PlaybackProbeResult(false, "mdx", [], [], [ex.Message]);
        }

        var required = new List<RequiredAsset>();
        var missing = new List<string>();
        if (!string.IsNullOrWhiteSpace(header.PdxName))
        {
            string[] searchNames = [header.PdxName, Path.GetFileName(header.PdxName)];
            required.Add(new RequiredAsset(
                header.PdxName,
                AssetKind.Pcm,
                true,
                searchNames));
            if (!FindAsset(input, environment, searchNames))
                missing.Add(header.PdxName);
        }

        var warnings = new List<string>
        {
            "MDX is recognized, but the portable headless MXDRV bridge is not linked",
            "the Windows MDPlayer MXDRV path remains platform-specific",
        };
        if (missing.Count > 0)
            warnings.Add($"required PDX asset is missing: {string.Join(", ", missing)}");

        return new PlaybackProbeResult(false, "mdx", required, missing, warnings)
        {
            Portable = false,
            Availability = PlaybackAvailability.PlatformSpecific,
        };
    }

    private static PlaybackProbeResult PlatformUnavailable(string format, string warning) =>
        new(false, format, [], [], [warning,
            "no offline emulated playback backend is registered for this format"])
        {
            Portable = false,
            Availability = PlaybackAvailability.PlatformSpecific,
        };

    private static bool FindAsset(
        FileInfo input,
        PlaybackEnvironment environment,
        IReadOnlyList<string> names)
    {
        var directories = new List<string> { input.DirectoryName ?? "." };
        directories.AddRange(environment.SearchPaths ?? []);
        return directories
            .Where(directory => !string.IsNullOrWhiteSpace(directory))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .SelectMany(directory => names.Select(name => Path.Combine(directory, name)))
            .Any(File.Exists);
    }
}

internal sealed record MdxHeader(string Title, string PdxName, int DataOffset)
{
    public static MdxHeader Parse(ReadOnlySpan<byte> data)
    {
        int lineEnd = data.IndexOfAny((byte)'\r', (byte)'\n');
        if (lineEnd <= 0)
            throw new MdPlayerDriverException("MDX title/header is missing its line terminator");

        int cursor = lineEnd;
        if (data[cursor] == (byte)'\r'
            && cursor + 1 < data.Length
            && data[cursor + 1] == (byte)'\n')
            cursor++;
        while (cursor < data.Length && data[cursor] != 0x1A)
            cursor++;
        if (cursor >= data.Length)
            throw new MdPlayerDriverException("MDX header is missing its 0x1A separator");
        cursor++;

        int pdxEnd = data[cursor..].IndexOf((byte)0);
        if (pdxEnd < 0)
            throw new MdPlayerDriverException("MDX PDX name is not NUL-terminated");

        string title = Encoding.ASCII.GetString(data[..lineEnd]);
        string pdx = Encoding.ASCII.GetString(data.Slice(cursor, pdxEnd));
        int dataOffset = cursor + pdxEnd + 1;
        return new MdxHeader(title, pdx, dataOffset);
    }
}

internal sealed class MdPlayerDriverException : MdxPlaybackException
{
    public MdPlayerDriverException(string message) : base(message) { }
}
