using System.Text.Json;

namespace MDPlayer.Fmp.Tests.NativeAudioValidation;

/// <summary>
/// Coverage contract for one validation-corpus entry (from
/// tests/fixtures/fmp-native-audio-validation.json). Declares the expected
/// loop/duration behavior and which chip features the fixture must exercise,
/// so feature-presence and sanity tests can be expressed against a stable
/// contract rather than embedding fixture bytes.
/// </summary>
internal sealed class ValidationFixtureDef
{
    public string Id { get; set; }
    public string ResolverKey { get; set; }
    public string Format { get; set; }
    public string ExpectedLoopBehavior { get; set; } // "looping" | "nonLooping"
    public double ExpectedApproximateDurationSeconds { get; set; }
    public CoverageDef Coverage { get; set; }
    public string Tier { get; set; } // "fast" | "extended"
}

internal sealed class CoverageDef
{
    public bool Fm { get; set; }
    public bool Ssg { get; set; }
    public bool RhythmRss { get; set; }
    public bool AdpcmB { get; set; }
    public bool Ppz8 { get; set; }
    public bool MixedOpnaPpz8 { get; set; }
    public bool TimerPolling { get; set; }
    public bool Bank0Write { get; set; }
    public bool Bank1Write { get; set; }
}

internal sealed class ValidationCorpusDoc
{
    public ulong CpuClockFrequencyHz { get; set; }
    public int[] OutputRates { get; set; }
    public string[] FastValidationSubset { get; set; }
    public List<ValidationFixtureDef> Entries { get; set; }
}

/// <summary>
/// Loads the checked-in validation-corpus manifest and resolves each fixture
/// to an actual .OVI path. Resolution order per entry: 1) the repo local
/// corpus config (tests/Corpus/local/local-config.json) by resolverKey, 2) a
/// name heuristic over the discoverable OVI pool, 3) a deterministic fallback
/// to any available OVI so category tests can still exercise the harness. When
/// no OVI is available the entry resolves to a null path and the caller skips.
/// </summary>
internal static class ValidationCorpus
{
    private static readonly string[] OviPaths = new[]
    {
        "/home/jose/Downloads",
    };

    public static ValidationCorpusDoc LoadManifest()
    {
        // The manifest is copied to the test output under its own name.
        string local = Path.Combine(AppContext.BaseDirectory, "fmp-native-audio-validation.json");
        if (File.Exists(local))
            return Read(local);
        // Fall back to a source-tree search for development shells.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        for (var dir = root; dir != null; dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, "tests", "fixtures", "fmp-native-audio-validation.json");
            if (File.Exists(cand)) return Read(cand);
        }
        throw new InvalidOperationException("native-audio validation manifest not found (tests/fixtures/fmp-native-audio-validation.json)");
    }

    private static ValidationCorpusDoc Read(string path)
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return System.Text.Json.JsonSerializer.Deserialize<ValidationCorpusDoc>(doc.RootElement.GetRawText(), options);
    }

    public static string Resolve(ValidationFixtureDef def)
    {
        // 1) Satisfied via the local corpus config by resolverKey.
        string viaConfig = ResolveViaLocalConfig(def.ResolverKey);
        if (viaConfig != null) return viaConfig;

        // 2) Name heuristic over the OVI pool: prefer a file whose base name
        //    tokenizes (case-insensitively) against the resolver key / id.
        var pool = DiscoverOviFiles();
        if (pool.Count > 0)
        {
            var tokens = Tokenize(def.ResolverKey + " " + def.Id);
            var hit = pool.FirstOrDefault(f => Tokenize(Path.GetFileNameWithoutExtension(f))
                .Any(t => tokens.Contains(t)));
            if (hit != null) return hit;

            // 3) Deterministic fallback: pick a stable index from the id so the
            //    same entry always maps to the same file on a given machine.
            int idx = Math.Abs(def.Id.GetHashCode(StringComparison.Ordinal)) % pool.Count;
            return pool[idx];
        }
        return null;
    }

    private static string ResolveViaLocalConfig(string resolverKey)
    {
        try
        {
            // The local config mirrors tests/Corpus/manifest.json tracks.
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            for (var dir = root; dir != null; dir = dir.Parent)
            {
                string p = Path.Combine(dir.FullName, "Corpus", "local", "local-config.json");
                if (File.Exists(p))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(p));
                    if (doc.RootElement.TryGetProperty("tracks", out var tracks))
                    {
                        foreach (var t in tracks.EnumerateArray())
                        {
                            if (t.TryGetProperty("name", out var n)
                                && n.GetString() == resolverKey
                                && t.TryGetProperty("path", out var pathElem))
                            {
                                string full = pathElem.GetString();
                                if (full != null && File.Exists(full)) return full;
                            }
                        }
                    }
                }
            }
        }
        catch { /* config may be absent; fall through */ }
        return null;
    }

    private static List<string> DiscoverOviFiles()
    {
        var result = new List<string>();
        foreach (var baseDir in new[]
        {
            AppContext.BaseDirectory,
        }.Concat(OviPaths))
        {
            if (!Directory.Exists(baseDir)) continue;
            foreach (var pattern in new[] { "*.OVI", "*.ovi" })
            {
                foreach (var f in Directory.GetFiles(baseDir, pattern, SearchOption.AllDirectories))
                    if (!result.Contains(f)) result.Add(f);
            }
        }
        return result.OrderBy(f => f, StringComparer.Ordinal).ToList();
    }

    private static HashSet<string> Tokenize(string s)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in s.Split(new[] { ' ', '-', '(', ')', '.', '_', '\\', '/' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Length >= 2) set.Add(word.ToLowerInvariant());
        }
        return set;
    }
}
