using System.Text.Json;
using Fmp.Core.IO;
using Fmp.Core.Metadata;
using Fmp.Core.Rendering;
using Fmp.Core.Visualization;

namespace Fmp.Cli;

public static class InspectCommand
{
    public static int Handle(string[] args)
    {
        bool json = false;
        bool visualization = false;
        string inputPath = null;
        var reader = new ArgumentReader(args);
        try
        {
            while (reader.HasMore)
            {
                if (reader.TryReadOption(out string name, out string value))
                {
                    if (name == "--json" && value == null)
                    {
                        json = true;
                    }
                    else if (name == "--visualization" && value == null)
                    {
                        visualization = true;
                    }
                    else
                    {
                        Console.Error.WriteLine($"error: unknown option {name}");
                        return 2;
                    }
                }
                else
                {
                    string positional = reader.Next();
                    if (positional == "--") continue;
                    if (inputPath != null)
                    {
                        Console.Error.WriteLine($"error: unexpected argument {positional}");
                        return 2;
                    }
                    inputPath = positional;
                }
            }
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 2;
        }

        if (inputPath == null)
        { Console.Error.WriteLine("error: no input file specified"); return 2; }

        var input = new FileInfo(inputPath);
        if (!input.Exists)
        { Console.Error.WriteLine($"error: input not found: {input.FullName}"); return 3; }

        byte[] buf = File.ReadAllBytes(input.FullName);
        string ext = input.Extension.ToLowerInvariant();

        if (visualization)
            return HandleVisualization(input, ext, json);

        string title = Path.GetFileNameWithoutExtension(input.Name);
        string comment = "";

        try
        {
            if (buf.Length >= 6)
            {
                int markerOffset = buf[0] + (buf[1] << 8);
                if (markerOffset + 4 <= buf.Length && System.Text.Encoding.ASCII.GetString(buf, markerOffset, 3) == "FMC")
                {
                    int commentStart = markerOffset + 4;
                    int nullIdx = Array.IndexOf(buf, (byte)0, commentStart);
                    if (nullIdx < 0) nullIdx = buf.Length;
                    comment = FmpTextDecoder.Decode(buf.AsSpan(commentStart, nullIdx - commentStart));
                    var lines = comment.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    title = lines.Length > 0 ? lines[0].Trim() : title;
                }
            }
        }
        catch { }

        if (json)
        {
            var info = new { sourceFile = input.Name, sourceFormat = ext.TrimStart('.'), fileSize = buf.Length, title, comment = comment.Trim() };
            Console.WriteLine(JsonSerializer.Serialize(info, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }));
        }
        else
        {
            Console.WriteLine($"File:    {input.Name}");
            Console.WriteLine($"Format:  {ext.TrimStart('.')}");
            Console.WriteLine($"Size:    {buf.Length} bytes");
            Console.WriteLine($"Title:   {title}");
            if (!string.IsNullOrEmpty(comment)) Console.WriteLine($"Comment: {comment}");
        }
        return 0;
    }

    private static int HandleVisualization(FileInfo input, string extension, bool json)
    {
        ChipTimelineDecoderRegistry decoderRegistry = ChipTimelineDecoderRegistry.CreateDefault();

        if (extension is ".opi" or ".ovi" or ".ozi" or ".mpi" or ".mvi" or ".mzi"
            or ".nrd" or ".bgm" or ".msd" or ".ndp" or ".mdr" or ".mdx"
            or ".mnd" or ".muc" or ".mub" or ".mml" or ".pmd" or ".m"
            or ".m2" or ".mz" or ".mus" or ".o" or ".ox" or ".oy"
            or ".zms" or ".zmd" or ".zgm" or ".nsf" or ".gbs" or ".hes"
            or ".sid" or ".ay" or ".mgs" or ".rcp" or ".rcs")
        {
            bool isFmp = extension is ".opi" or ".ovi" or ".ozi" or ".mpi" or ".mvi" or ".mzi";
            IPlaybackBackend driverBackend = isFmp
                ? new FmpPlaybackBackend(new FmpRuntimeAssets(
                    Path.Combine(input.DirectoryName ?? ".", "FMP.COM")))
                : extension == ".mdx"
                    ? new MdxPlaybackBackend()
                    : new MdPlayerDriverBackend();
            PlaybackProbeResult probe = driverBackend.Probe(
                input,
                new PlaybackEnvironment([input.DirectoryName ?? "."]));
            var report = new
            {
                format = probe.Format,
                backend = driverBackend.Id,
                availability = probe.Availability.ToString().ToLowerInvariant(),
                portable = probe.Portable,
                visualizable = probe.Visualizable,
                sampleRate = 44_100,
                requiredAssets = probe.RequiredAssets.Select(asset => asset.Name).ToArray(),
                missingAssets = probe.MissingAssets,
                warnings = probe.Warnings,
            };
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            else
            {
                Console.WriteLine($"Format: {report.format.ToUpperInvariant()}");
                Console.WriteLine($"Backend: {report.backend}");
                Console.WriteLine($"Availability: {report.availability}");
                Console.WriteLine($"Portable: {(report.portable ? "yes" : "no")}");
                Console.WriteLine($"Visualizable: {(report.visualizable ? "yes" : "no")}");
                foreach (string asset in report.missingAssets)
                    Console.WriteLine($"Missing asset: {asset}");
                foreach (string warning in report.warnings)
                    Console.WriteLine($"Warning: {warning}");
            }
            return 0;
        }

        if (extension == ".s98")
        {
            try
            {
                S98Document document = S98Document.Parse(File.ReadAllBytes(input.FullName));
                PlaybackProbeResult probe = new S98PlaybackBackend().Probe(
                    input,
                    new PlaybackEnvironment([input.DirectoryName ?? "."]));
                bool visualizable = document.Devices.Any(device =>
                    GetNoteSupport(device, decoderRegistry) != "none");
                var report = new
                {
                    format = "s98",
                    backend = "s98",
                    availability = probe.Availability.ToString().ToLowerInvariant(),
                    portable = probe.Portable,
                    visualizable,
                    sampleRate = 44_100,
                    devices = document.Devices.Select(device => new
                    {
                        id = device.Id.ToString(),
                        type = device.Type.ToString().ToLowerInvariant(),
                        instance = device.Instance,
                        noteSupport = GetNoteSupport(device, decoderRegistry),
                        scopeSupport = device.ScopeSupport.ToString().ToLowerInvariant(),
                    }).ToArray(),
                    warnings = probe.Warnings,
                };
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                else
                {
                    Console.WriteLine("Format: S98");
                    Console.WriteLine("Backend: s98");
                    Console.WriteLine($"Portable: {(report.portable ? "yes" : "no")}");
                    Console.WriteLine($"Visualizable: {(report.visualizable ? "yes" : "no")}");
                    Console.WriteLine("Devices:");
                    foreach (var device in report.devices)
                        Console.WriteLine($"  {device.id}  note decoder: {device.noteSupport}  scopes: {device.scopeSupport}");
                    foreach (string warning in report.warnings)
                        Console.WriteLine($"Warning: {warning}");
                }
                return 0;
            }
            catch (Exception ex) when (ex is IOException or S98PlaybackException)
            {
                Console.Error.WriteLine($"error: visualization probe failed: {ex.Message}");
                return 3;
            }
        }

        if (extension == ".xgm")
        {
            try
            {
                XgmDocument document = XgmDocument.Parse(File.ReadAllBytes(input.FullName));
                var report = new
                {
                    format = "xgm",
                    backend = "xgm",
                    availability = PlaybackAvailability.Available.ToString().ToLowerInvariant(),
                    portable = true,
                    visualizable = document.Devices.Any(device =>
                        GetNoteSupport(device, decoderRegistry) != "none"),
                    sampleRate = 44_100,
                    devices = document.Devices.Select(device => new
                    {
                        id = device.Id.ToString(),
                        type = device.Type.ToString().ToLowerInvariant(),
                        instance = device.Instance,
                        noteSupport = GetNoteSupport(device, decoderRegistry),
                        scopeSupport = device.ScopeSupport.ToString().ToLowerInvariant(),
                    }).ToArray(),
                    warnings = document.Warnings,
                };
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                else
                {
                    Console.WriteLine("Format: XGM");
                    Console.WriteLine("Backend: xgm");
                    Console.WriteLine("Portable: yes");
                    Console.WriteLine($"Visualizable: {(report.visualizable ? "yes" : "no")}");
                    Console.WriteLine("Devices:");
                    foreach (var device in report.devices)
                        Console.WriteLine($"  {device.id}  note decoder: {device.noteSupport}  scopes: {device.scopeSupport}");
                    foreach (string warning in report.warnings)
                        Console.WriteLine($"Warning: {warning}");
                }
                return 0;
            }
            catch (Exception ex) when (ex is IOException or XgmPlaybackException)
            {
                Console.Error.WriteLine($"error: visualization probe failed: {ex.Message}");
                return 3;
            }
        }

        if (extension is ".mid" or ".midi")
        {
            try
            {
                MidiDocument document = MidiDocument.Parse(File.ReadAllBytes(input.FullName));
                bool visualizable = document.Events.Any(evt =>
                    evt.Type == MidiMessageType.NoteOn && evt.Data2 > 0);
                var report = new
                {
                    format = "midi",
                    backend = "midi",
                    availability = PlaybackAvailability.Available.ToString().ToLowerInvariant(),
                    portable = true,
                    visualizable,
                    sampleRate = 44_100,
                    devices = new[]
                    {
                        new
                        {
                            id = document.Device.Id.ToString(),
                            type = "midi",
                            instance = document.Device.Id.Instance,
                            noteSupport = visualizable ? "full" : "none",
                            scopeSupport = "master",
                        },
                    },
                    warnings = Array.Empty<string>(),
                };
                if (json)
                    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                else
                    Console.WriteLine($"Format: MIDI\nBackend: midi\nPortable: yes\nVisualizable: {(visualizable ? "yes" : "no")}\nDevices:\n  {document.Device.Id}  note decoder: {report.devices[0].noteSupport}  scopes: master");
                return 0;
            }
            catch (Exception ex) when (ex is IOException or MidiPlaybackException)
            {
                Console.Error.WriteLine($"error: visualization probe failed: {ex.Message}");
                return 3;
            }
        }

        if (extension is not ".vgm" and not ".vgz")
        {
            if (extension == ".spc")
            {
                var environment = new PlaybackEnvironment(
                    [input.DirectoryName ?? "."],
                    OfflineOnly: true,
                    SampleRate: 44_100);
                var registry = PlaybackBackendRegistry.CreateDefault(environment);
                if (registry.TrySelect(input, environment, "mdplayer",
                        out IPlaybackBackend backend, out PlaybackProbeResult probe)
                    && backend.Id == "spc")
                {
                    var report = new
                    {
                        format = probe.Format.ToLowerInvariant(),
                        backend = backend.Id,
                        availability = probe.Availability.ToString().ToLowerInvariant(),
                        portable = probe.Portable,
                        visualizable = probe.Visualizable,
                        sampleRate = probe.NativeSampleRate,
                        warnings = probe.Warnings,
                    };
                    if (json)
                        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    else
                    {
                        Console.WriteLine($"Format: {report.format.ToUpperInvariant()}");
                        Console.WriteLine($"Backend: {report.backend}");
                        Console.WriteLine($"Availability: {report.availability}");
                        Console.WriteLine($"Portable: {(report.portable ? "yes" : "no")}");
                        Console.WriteLine($"Visualizable: {(report.visualizable ? "yes" : "no")}");
                        Console.WriteLine($"Sample rate: {report.sampleRate} Hz");
                        foreach (string warning in report.warnings)
                            Console.WriteLine($"Warning: {warning}");
                    }
                    return 0;
                }
            }

            var unsupported = new
            {
                format = extension.TrimStart('.'),
                backend = "none",
                visualizable = false,
                warnings = new[] { "no generic backend is registered for this format" },
            };
            if (json)
                Console.WriteLine(JsonSerializer.Serialize(unsupported, new JsonSerializerOptions { WriteIndented = true }));
            else
                Console.WriteLine($"Format: {unsupported.format}\nBackend: none\nVisualizable: no\nWarning: {unsupported.warnings[0]}");
            return 0;
        }

        try
        {
            VgmDocument document = VgmDocument.Parse(VgmInput.Read(input.FullName));
            bool visualizable = document.Devices.Any(device =>
                GetNoteSupport(device, decoderRegistry) != "none");
            var report = new
            {
                format = "vgm",
                backend = "vgm",
                availability = PlaybackAvailability.Available.ToString().ToLowerInvariant(),
                portable = true,
                visualizable,
                sampleRate = 44_100,
                devices = document.Devices.Select(device => new
                {
                    id = device.Id.ToString(),
                    type = device.Type.ToString().ToLowerInvariant(),
                    instance = device.Instance,
                    noteSupport = GetNoteSupport(device, decoderRegistry),
                    scopeSupport = device.ScopeSupport.ToString().ToLowerInvariant(),
                }).ToArray(),
                warnings = document.Warnings,
            };
            if (json)
            {
                Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            }
            else
            {
                Console.WriteLine("Format: VGM");
                Console.WriteLine("Backend: vgm");
                Console.WriteLine("Availability: available");
                Console.WriteLine("Portable: yes");
                Console.WriteLine($"Visualizable: {(visualizable ? "yes" : "no")}");
                Console.WriteLine("Devices:");
                foreach (var device in report.devices)
                    Console.WriteLine($"  {device.id}  note decoder: {device.noteSupport}  scopes: {device.scopeSupport}");
                foreach (string warning in report.warnings)
                    Console.WriteLine($"Warning: {warning}");
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or VgmPlaybackException)
        {
            Console.Error.WriteLine($"error: visualization probe failed: {ex.Message}");
            return 3;
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
}
