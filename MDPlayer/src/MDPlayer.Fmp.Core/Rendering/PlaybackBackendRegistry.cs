using Fmp.Core.Visualization;
using Fmp.Core.IO;
using Fmp.Core.Playback.Spc;

namespace Fmp.Core.Rendering;

/// <summary>
/// Selects a playback backend by probing the file, not by treating its
/// extension as a visualization whitelist. Format-specific knowledge remains
/// inside each backend's probe.
/// </summary>
internal sealed class PlaybackBackendRegistry
{
    private readonly IReadOnlyList<IPlaybackBackend> _backends;

    public PlaybackBackendRegistry(IEnumerable<IPlaybackBackend> backends)
    {
        ArgumentNullException.ThrowIfNull(backends);
        _backends = backends.ToArray();
        if (_backends.Count == 0)
            throw new ArgumentException("At least one playback backend is required.", nameof(backends));
    }

    public static PlaybackBackendRegistry CreateDefault(PlaybackEnvironment environment = null)
    {
        var backends = new List<IPlaybackBackend>();
        string fmpCom = FindFmpCom(environment?.SearchPaths);
        if (fmpCom != null)
            backends.Add(new FmpPlaybackBackend(new FmpRuntimeAssets(fmpCom)));
        backends.AddRange(
        [
            new VgmPlaybackBackend(),
            new XgmPlaybackBackend(),
            new S98PlaybackBackend(),
            new MidiPlaybackBackend(),
            new MdxPlaybackBackend(),
            new MdPlayerDriverBackend(),
            new SpcPlaybackBackend(),
        ]);
        return new PlaybackBackendRegistry(backends);
    }

    private static string FindFmpCom(IReadOnlyList<string> searchPaths)
    {
        foreach (string directory in searchPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory))
                continue;
            foreach (string name in new[] { "FMP.COM", "fmp.com" })
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }

    public bool TrySelect(
        FileInfo input,
        PlaybackEnvironment environment,
        out IPlaybackBackend backend,
        out PlaybackProbeResult probe)
        => TrySelect(input, environment, "auto", out backend, out probe);

    public bool TrySelect(
        FileInfo input,
        PlaybackEnvironment environment,
        string preference,
        out IPlaybackBackend backend,
        out PlaybackProbeResult probe)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(environment);
        if (preference is not ("auto" or "fmp" or "mdplayer"))
            throw new ArgumentException($"unknown playback backend preference '{preference}'", nameof(preference));

        var warnings = new List<string>();
        var requiredAssets = new List<RequiredAsset>();
        var missingAssets = new List<string>();
        PlaybackAvailability availability = PlaybackAvailability.Unavailable;
        string format = input.Extension.TrimStart('.').ToLowerInvariant();
        foreach (IPlaybackBackend candidate in _backends)
        {
            if (preference == "fmp" && candidate.Id != "fmp")
                continue;
            if (preference == "mdplayer" && candidate.Id == "fmp")
                continue;
            PlaybackProbeResult candidateProbe = candidate.Probe(input, environment);
            if (candidateProbe.Supported)
            {
                backend = candidate;
                probe = candidateProbe;
                return true;
            }

            requiredAssets.AddRange(candidateProbe.RequiredAssets);
            missingAssets.AddRange(candidateProbe.MissingAssets);
            if (candidateProbe.Availability != PlaybackAvailability.Unavailable)
            {
                availability = candidateProbe.Availability;
                format = candidateProbe.Format;
            }
            warnings.AddRange(candidateProbe.Warnings.Select(warning =>
                $"{candidate.Id}: {warning}"));
        }

        backend = null;
        probe = new PlaybackProbeResult(
            false,
            format,
            requiredAssets,
            missingAssets,
            warnings.Count == 0
                ? [$"no playback backend accepted {input.Name}"]
                : warnings);
        probe = probe with { Availability = availability };
        return false;
    }
}
