using System.Text.Json;
using Fmp.Application.Contracts;

namespace Fmp.Gui.Services;

/// <summary>
/// User settings persisted as JSON at %LocalAppData%/MDPlayer/Visualizer/settings.json
/// (platform-aware via <see cref="Environment.SpecialFolder.LocalApplicationData"/>).
/// </summary>
public sealed class GuiSettingsStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly string _path;

    public GuiSettingsStore(string? storagePath = null)
    {
        _path = storagePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MDPlayer", "Visualizer", "settings.json");
        Settings = Load();
    }

    public GuiSettings Settings { get; private set; }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Settings, Json));
        }
        catch
        {
            // Best-effort persistence; never crash the UI over settings.
        }
    }

    /// <summary>Mutates the in-memory settings and persists them.</summary>
    public void Update(Action<GuiSettings> mutate)
    {
        mutate(Settings);
        Save();
    }

    private GuiSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(_path), Json) ?? new GuiSettings();
        }
        catch
        {
            // Corrupt settings fall back to defaults.
        }
        return new GuiSettings();
    }
}

/// <summary>Persisted GUI settings (flat, nullable-friendly).</summary>
public sealed class GuiSettings
{
    /// <summary>Explicit path override for the mdplayer-render executable.</summary>
    public string? RenderCliPath { get; set; }

    /// <summary>Explicit FMP.COM path (runtime tool; never serialized into projects).</summary>
    public string? FmpComPath { get; set; }

    /// <summary>Explicit Corrscope executable path.</summary>
    public string? CorrscopePath { get; set; }

    /// <summary>Explicit FFmpeg executable path.</summary>
    public string? FfmpegPath { get; set; }

    /// <summary>Explicit analysis Python interpreter path.</summary>
    public string? AnalysisPython { get; set; }

    /// <summary>Assets directory for chip sample packs.</summary>
    public string? AssetsDir { get; set; }

    /// <summary>Preview frame render width cap.</summary>
    public int PreviewMaxWidth { get; set; } = 960;

    /// <summary>Preview frame render height cap.</summary>
    public int PreviewMaxHeight { get; set; } = 540;

    /// <summary>"Default", "Light" or "Dark".</summary>
    public string ThemeVariant { get; set; } = "Default";

    /// <summary>When on, automatic preview updates are paused (accessibility).</summary>
    public bool ReducedMotion { get; set; }

    /// <summary>Most recently opened inputs/projects (most recent first).</summary>
    public List<string> RecentFiles { get; set; } = new();

    /// <summary>Builds the runtime tool paths from the persisted settings.</summary>
    public ToolPaths ToToolPaths() => new()
    {
        FmpComPath = FmpComPath,
        AssetsDir = AssetsDir,
        CorrscopePath = CorrscopePath,
        FfmpegPath = FfmpegPath,
        AnalysisPython = AnalysisPython,
    };
}
