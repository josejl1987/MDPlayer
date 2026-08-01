using Fmp.Application.Contracts;
using Fmp.Core.IO;
using Fmp.Core.Metadata;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Application.Inspection;

/// <summary>
/// Inspects an input music file for visualization purposes: existence and
/// format support checks, playback probe, metadata title extraction and
/// device-level note/scope capabilities.
/// <para>
/// This inspector mirrors the CLI's <c>inspect --visualization</c> probing
/// logic, but never throws on a bad input: every decoding/probing failure is
/// converted into a <see cref="ValidationIssue"/> so the GUI can always show
/// the file with its problems instead of crashing.
/// </para>
/// </summary>
public static class VisualizationInputInspector
{
    private const int DefaultSampleRate = 44_100;
    private const int DefaultLoopCount = 2;
    private const double DefaultFadeSeconds = 5.0;

    private static readonly string[] FmpFamilyExtensions =
        [".opi", ".ovi", ".ozi", ".mpi", ".mvi", ".mzi"];

    private static readonly string[] DriverTrackedExtensions =
    [
        ".nrd", ".bgm", ".msd", ".ndp", ".mdr", ".mdx",
        ".mnd", ".muc", ".mub", ".mml", ".pmd", ".m",
        ".m2", ".mz", ".mus", ".o", ".ox", ".oy",
        ".zms", ".zmd", ".zgm", ".nsf", ".gbs", ".hes",
        ".sid", ".ay", ".mgs", ".rcp", ".rcs",
    ];

    /// <summary>Inspects the input file synchronously. Never throws for a bad input.</summary>
    public static VisualizationInputInfo Inspect(string inputPath)
    {
        ArgumentNullException.ThrowIfNull(inputPath);

        string fullPath = inputPath;
        try
        {
            fullPath = Path.GetFullPath(inputPath);
        }
        catch
        {
            // Path normalization failed (invalid characters); keep the raw value.
        }

        string displayName = SafeFileName(fullPath);
        string format = SafeExtension(fullPath);
        var issues = new List<ValidationIssue>();

        if (!File.Exists(fullPath))
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.InputNotFound,
                Severity = ValidationSeverity.Error,
                Message = $"Input file not found: {fullPath}",
                SuggestedAction = "Relink the input file or choose another.",
            });
            return new VisualizationInputInfo
            {
                FullPath = fullPath,
                DisplayName = displayName,
                Format = format,
                Title = SafeFileNameWithoutExtension(fullPath),
                Issues = issues,
            };
        }

        string extension = SafeExtension(fullPath);
        if (!IsSupportedExtension(extension))
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.InputUnsupported,
                Severity = ValidationSeverity.Error,
                Message = $"Unsupported input format '{extension.TrimStart('.')}'.",
                SuggestedAction = "Choose a supported format (.ovi, .vgm, .vgz, .xgm, .s98, .mdx, .mid, .spc, ...).",
            });
            return new VisualizationInputInfo
            {
                FullPath = fullPath,
                DisplayName = displayName,
                Format = format,
                Title = SafeFileNameWithoutExtension(fullPath),
                Issues = issues,
            };
        }

        var devices = new List<DeviceInfo>();
        bool supportsSemantic;
        bool supportsScope;
        try
        {
            Probe(fullPath, extension, issues, devices, out supportsSemantic, out supportsScope);
        }
        catch (Exception ex)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.InputUnsupported,
                Severity = ValidationSeverity.Error,
                Message = "Could not inspect the input file.",
                Detail = ex.Message,
            });
            supportsSemantic = false;
            supportsScope = false;
        }

        string title = SafeFileNameWithoutExtension(fullPath);
        try
        {
            title = FmpMetadata.FromFmpFile(fullPath, DefaultSampleRate, DefaultLoopCount, DefaultFadeSeconds)?.Title
                    ?? SafeFileNameWithoutExtension(fullPath);
        }
        catch
        {
            // Metadata extraction is best-effort; fall back to the file name.
        }
        if (string.IsNullOrWhiteSpace(title))
            title = SafeFileNameWithoutExtension(fullPath);

        return new VisualizationInputInfo
        {
            FullPath = fullPath,
            DisplayName = displayName,
            Format = format,
            Title = title,
            Devices = devices,
            Tracks = Array.Empty<TrackCapabilityInfo>(),
            SupportsSemanticCapture = supportsSemantic,
            SupportsScopeCapture = supportsScope,
            SupportsAnalysis = false,
            Issues = issues,
        };
    }

    /// <summary>
    /// Inspects the input file asynchronously (the work itself is synchronous;
    /// the task wrapper exists for API symmetry with the session factory).
    /// Never throws for a bad input.
    /// </summary>
    public static Task<VisualizationInputInfo> InspectAsync(string inputPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Inspect(inputPath));
    }

    // ---- Probing (mirrors InspectCommand.HandleVisualization) ----

    private static void Probe(
        string fullPath,
        string extension,
        List<ValidationIssue> issues,
        List<DeviceInfo> devices,
        out bool supportsSemantic,
        out bool supportsScope)
    {
        extension = "." + extension.TrimStart('.');
        var file = new FileInfo(fullPath);
        var environment = new PlaybackEnvironment([file.DirectoryName ?? "."]);
        ChipTimelineDecoderRegistry decoderRegistry = ChipTimelineDecoderRegistry.CreateDefault();

        supportsSemantic = false;
        supportsScope = false;

        if (FmpFamilyExtensions.Contains(extension) || DriverTrackedExtensions.Contains(extension))
        {
            bool isFmp = FmpFamilyExtensions.Contains(extension);
            IPlaybackBackend backend = isFmp
                ? new FmpPlaybackBackend(new FmpRuntimeAssets(
                    Path.Combine(file.DirectoryName ?? ".", "FMP.COM")))
                : extension == ".mdx"
                    ? new MdxPlaybackBackend()
                    : new MdPlayerDriverBackend();
            PlaybackProbeResult probe = backend.Probe(file, environment);
            AddProbeIssues(issues, probe, backend.Id);
            // FMP/driver formats expose no device descriptors at probe time;
            // visualizability implies both capture paths at least at master level.
            supportsSemantic = probe.Visualizable;
            supportsScope = probe.Visualizable;
            return;
        }

        if (extension == ".s98")
        {
            S98Document document = S98Document.Parse(File.ReadAllBytes(fullPath));
            PlaybackProbeResult probe = new S98PlaybackBackend().Probe(file, environment);
            AddProbeIssues(issues, probe, "s98");
            AddDeviceRows(document.Devices, decoderRegistry, devices, ref supportsSemantic, ref supportsScope);
            return;
        }

        if (extension == ".xgm")
        {
            XgmDocument document = XgmDocument.Parse(File.ReadAllBytes(fullPath));
            AddDeviceRows(document.Devices, decoderRegistry, devices, ref supportsSemantic, ref supportsScope);
            AddDocumentWarnings(issues, document.Warnings);
            return;
        }

        if (extension is ".mid" or ".midi")
        {
            MidiDocument document = MidiDocument.Parse(File.ReadAllBytes(fullPath));
            bool visualizable = document.Events.Any(evt =>
                evt.Type == MidiMessageType.NoteOn && evt.Data2 > 0);
            devices.Add(new DeviceInfo
            {
                Id = document.Device.Id.ToString(),
                Type = "midi",
                Instance = document.Device.Id.Instance,
                NoteSupport = visualizable ? "full" : "none",
                ScopeSupport = "master",
            });
            supportsSemantic = visualizable;
            supportsScope = true;
            return;
        }

        if (extension is ".vgm" or ".vgz")
        {
            VgmDocument document = VgmDocument.Parse(VgmInput.Read(fullPath));
            AddDeviceRows(document.Devices, decoderRegistry, devices, ref supportsSemantic, ref supportsScope);
            AddDocumentWarnings(issues, document.Warnings);
            return;
        }

        if (extension == ".spc")
        {
            var spcEnvironment = new PlaybackEnvironment(
                [file.DirectoryName ?? "."],
                OfflineOnly: true,
                SampleRate: DefaultSampleRate);
            PlaybackBackendRegistry registry = PlaybackBackendRegistry.CreateDefault(spcEnvironment);
            if (registry.TrySelect(file, spcEnvironment, "mdplayer",
                    out IPlaybackBackend backend, out PlaybackProbeResult probe)
                && backend.Id == "spc")
            {
                AddProbeIssues(issues, probe, backend.Id);
                supportsSemantic = probe.Visualizable;
                supportsScope = probe.Visualizable;
            }
            else
            {
                issues.Add(new ValidationIssue
                {
                    Code = ValidationCodes.InputUnsupported,
                    Severity = ValidationSeverity.Error,
                    Message = "No SPC playback backend is available for this input.",
                });
            }
            return;
        }
    }

    private static void AddDeviceRows(
        IEnumerable<DeviceDescriptor> descriptors,
        ChipTimelineDecoderRegistry decoderRegistry,
        List<DeviceInfo> devices,
        ref bool supportsSemantic,
        ref bool supportsScope)
    {
        foreach (DeviceDescriptor device in descriptors)
        {
            string noteSupport = GetNoteSupport(device, decoderRegistry);
            string scopeSupport = device.ScopeSupport.ToString().ToLowerInvariant();
            if (noteSupport != "none")
                supportsSemantic = true;
            if (scopeSupport != "none")
                supportsScope = true;

            devices.Add(new DeviceInfo
            {
                Id = device.Id.ToString(),
                Type = device.Type.ToString().ToLowerInvariant(),
                Instance = device.Instance,
                NoteSupport = noteSupport,
                ScopeSupport = scopeSupport,
            });
        }
    }

    private static void AddDocumentWarnings(List<ValidationIssue> issues, IReadOnlyList<string> warnings)
    {
        foreach (string warning in warnings)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.CaptureFailed,
                Severity = ValidationSeverity.Warning,
                Message = warning,
            });
        }
    }

    private static void AddProbeIssues(List<ValidationIssue> issues, PlaybackProbeResult probe, string backendId)
    {
        foreach (string missing in probe.MissingAssets)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.ToolNotFound,
                Severity = ValidationSeverity.Warning,
                Message = $"{backendId}: missing runtime asset '{missing}'",
                SuggestedAction = "Provide the runtime asset (e.g. FMP.COM) in the input directory or via settings.",
            });
        }

        foreach (string warning in probe.Warnings)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.CaptureFailed,
                Severity = ValidationSeverity.Warning,
                Message = warning,
            });
        }

        if (!probe.Supported && issues.Count == 0)
        {
            issues.Add(new ValidationIssue
            {
                Code = ValidationCodes.InputUnsupported,
                Severity = ValidationSeverity.Error,
                Message = $"No playback backend accepted the input ({backendId}).",
            });
        }
    }

    private static string GetNoteSupport(
        DeviceDescriptor device,
        ChipTimelineDecoderRegistry decoderRegistry)
    {
        bool registered = decoderRegistry.HasDecoder(device.Id.Type);
        if (!registered)
            return "none";

        return device.Capabilities.HasFlag(DeviceCapabilities.Notes)
            ? "full"
            : "activity";
    }

    private static bool IsSupportedExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
            return false;
        string normalized = "." + extension.TrimStart('.');
        return FmpFamilyExtensions.Contains(normalized)
            || DriverTrackedExtensions.Contains(normalized)
            || normalized is ".s98" or ".xgm" or ".mid" or ".midi" or ".vgm" or ".vgz" or ".spc";
    }

    private static string SafeExtension(string path)
    {
        try
        {
            return (Path.GetExtension(path) ?? "").TrimStart('.').ToLowerInvariant();
        }
        catch
        {
            return "";
        }
    }

    private static string SafeFileName(string path)
    {
        try
        {
            return Path.GetFileName(path) ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string SafeFileNameWithoutExtension(string path)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(path) ?? "";
            return string.IsNullOrWhiteSpace(name) ? SafeFileName(path) : name;
        }
        catch
        {
            return SafeFileName(path);
        }
    }
}
