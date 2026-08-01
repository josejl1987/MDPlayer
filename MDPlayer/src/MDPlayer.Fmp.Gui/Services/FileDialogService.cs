using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Fmp.Gui.Services;

/// <summary>Storage-provider based file/folder dialogs.</summary>
public sealed class FileDialogService
{
    /// <summary>Set once the main window exists.</summary>
    public Func<TopLevel?>? TopLevelProvider { get; set; }

    private TopLevel? TopLevel => TopLevelProvider?.Invoke();

    public async Task<string?> OpenFileAsync()
    {
        if (TopLevel is not { } top)
            return null;

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open music file or visualization project",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Music files")
                {
                    Patterns = new[] { "*.ovi", "*.opi", "*.ozi", "*.mpi", "*.mvi", "*.mzi", "*.vgm", "*.vgz",
                        "*.xgm", "*.s98", "*.mdx", "*.mid", "*.midi", "*.spc", "*.wav", "*.mp3", "*.ogg", "*.flac" },
                },
                new FilePickerFileType("Visualization project") { Patterns = new[] { "*.mdpviz.json" } },
                FilePickerFileTypes.All,
            },
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> SaveFileAsync()
    {
        if (TopLevel is not { } top)
            return null;

        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save visualization project",
            SuggestedFileName = "visualization.mdpviz.json",
            DefaultExtension = "mdpviz.json",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Visualization project") { Patterns = new[] { "*.mdpviz.json" } },
            },
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> SaveOutputFileAsync(string? currentPath)
    {
        if (TopLevel is not { } top)
            return null;

        string suggestedName = string.IsNullOrWhiteSpace(currentPath)
            ? "visualization.mp4"
            : Path.GetFileName(currentPath);
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose output video",
            SuggestedFileName = string.IsNullOrWhiteSpace(suggestedName) ? "visualization.mp4" : suggestedName,
            DefaultExtension = "mp4",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("MP4 video") { Patterns = new[] { "*.mp4" } },
                FilePickerFileTypes.All,
            },
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> ChooseFontFileAsync()
    {
        if (TopLevel is not { } top)
            return null;

        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a font",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Font files") { Patterns = new[] { "*.ttf", "*.otf", "*.ttc" } },
                FilePickerFileTypes.All,
            },
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> OpenFolderAsync()
    {
        if (TopLevel is not { } top)
            return null;

        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose a folder",
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }
}
