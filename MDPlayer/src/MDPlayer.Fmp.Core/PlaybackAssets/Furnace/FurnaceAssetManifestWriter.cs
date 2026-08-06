using System.Text.Json;
using System.Text.Json.Serialization;
using Fmp.Core.PlaybackAssets.Opn;

namespace Fmp.Core.PlaybackAssets.Furnace;

/// <summary>
/// Serializes the export manifest describing every deduplicated FM instrument
/// written for a session. One manifest accompanies a set of .tfi files.
/// </summary>
internal static class FurnaceAssetManifestWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string Write(string fmDirectoryName, IReadOnlyList<CapturedFmInstrument> instruments)
    {
        var doc = new ManifestDocument
        {
            Version = 1,
            Instruments = new List<ManifestInstrument>(instruments.Count),
        };

        foreach (CapturedFmInstrument instr in instruments)
        {
            doc.Instruments.Add(new ManifestInstrument
            {
                Id = $"fm-{instr.Ordinal:000}",
                File = $"{fmDirectoryName}/fm_{instr.Ordinal:000}_{instr.Sha256Short}.tfi",
                Format = "TFI",
                Size = TfiFormat.FileSize,
                Sha256 = instr.Sha256Hex,
                IdentityNormalization = new ManifestIdentityNormalization
                {
                    CarrierTlNormalized = true,
                    FeedbackCarrierPreserved = instr.FeedbackCarrierPreserved,
                    ObservedVolumeOffsetMin = instr.MinimumObservedVolumeOffset,
                    ObservedVolumeOffsetMax = instr.MaximumObservedVolumeOffset,
                },
                ChipTypesObserved = instr.ChipTypesObserved
                    .OrderBy(t => t.ToString(), StringComparer.Ordinal)
                    .Select(t => t.ToString())
                    .ToArray(),
                ChipInstancesObserved = instr.ChipInstancesObserved.OrderBy(x => x).ToArray(),
                ChannelsObserved = instr.ChannelsObserved.OrderBy(x => x).ToArray(),
                FirstSeenWriteIndex = instr.FirstSeenWriteIndex < 0 ? null : instr.FirstSeenWriteIndex,
                FirstSeenPlaybackSample = instr.FirstSeenPlaybackSample,
                ObservationCount = instr.ObservationCount,
            });
        }

        return JsonSerializer.Serialize(doc, Options);
    }

    private sealed class ManifestDocument
    {
        public int Version { get; set; }
        public List<ManifestInstrument> Instruments { get; set; } = new();
    }

    private sealed class ManifestInstrument
    {
        public string Id { get; set; }
        public string File { get; set; }
        public string Format { get; set; }
        public int Size { get; set; }
        public string Sha256 { get; set; }
        public ManifestIdentityNormalization IdentityNormalization { get; set; }
        public string[] ChipTypesObserved { get; set; }
        public int[] ChipInstancesObserved { get; set; }
        public int[] ChannelsObserved { get; set; }
        public long? FirstSeenWriteIndex { get; set; }
        public long? FirstSeenPlaybackSample { get; set; }
        public int ObservationCount { get; set; }
    }

    /// <summary>Describes how carrier total level was handled for an exported
    /// instrument. Every export normalizes the loudest carrier to TL = 0 while
    /// preserving the relative attenuation among carriers.</summary>
    private sealed class ManifestIdentityNormalization
    {
        public bool CarrierTlNormalized { get; set; }
        public bool FeedbackCarrierPreserved { get; set; }
        public byte ObservedVolumeOffsetMin { get; set; }
        public byte ObservedVolumeOffsetMax { get; set; }
    }
}
