using Fmp.Core.PlaybackAssets.Opn;
using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets;

/// <summary>
/// One distinct carrier total-level layout actually observed on chip, expressed
/// as the common attenuation offset (the loudest volume-normalizable carrier's
/// TL) that normalization removed for identity/dedup. Within a single
/// <see cref="CapturedFmInstrument"/>, observations can differ only by this
/// uniform carrier-level offset; lower is louder.
/// </summary>
internal sealed record CarrierLevelVariant(byte VolumeOffset);

/// <summary>
/// One deduplicated exported FM instrument together with the observation
/// metadata recorded in the manifest. Deduplication keys on the SHA-256 of the
/// normalized <see cref="OpnFmTimbreIdentity"/> bytes, not the literal 42-byte
/// TFI; the exported file is a canonicalized TFI (see <see cref="TfiBytes"/>).
/// </summary>
internal sealed class CapturedFmInstrument
{
    /// <summary>Ordinal of first appearance in this export session (1-based).</summary>
    public required int Ordinal { get; init; }

    /// <summary>Full 64-char lowercase hex SHA-256 of the normalized timbre-identity
    /// bytes. This is the deduplication key.</summary>
    public required string IdentityHash { get; init; }

    /// <summary>The canonicalized (loudest carrier at TL=0) exported instrument.</summary>
    public required OpnFmInstrument Representative { get; init; }

    /// <summary>Full 64-char lowercase hex SHA-256 of the canonicalized TFI file
    /// bytes (the exported <see cref="TfiBytes"/>).</summary>
    public required string Sha256Hex { get; init; }

    /// <summary>First eight lowercase hex chars of <see cref="Sha256Hex"/>.</summary>
    public string Sha256Short => Sha256Hex.Substring(0, 8);

    /// <summary>The exact 42-byte canonicalized TFI file content.</summary>
    public required byte[] TfiBytes { get; init; }

    /// <summary>Loudest (lowest-TL) volume-normalizable carrier TL observed for
    /// this instrument. Lower is louder.</summary>
    public byte MinimumObservedVolumeOffset { get; set; } = byte.MaxValue;

    /// <summary>Quietest (highest-TL) volume-normalizable carrier TL observed.</summary>
    public byte MaximumObservedVolumeOffset { get; set; }

    /// <summary>True when Op1 TL was preserved (algorithm 7 with feedback)
    /// rather than volume-normalized.</summary>
    public bool FeedbackCarrierPreserved { get; set; }

    /// <summary>The distinct observed carrier volume offsets.</summary>
    public HashSet<CarrierLevelVariant> CarrierLevelVariants { get; } = new();

    /// <summary>Chip types observed using this instrument.</summary>
    public HashSet<ChipType> ChipTypesObserved { get; } = new();

    /// <summary>Chip instances observed using this instrument.</summary>
    public HashSet<int> ChipInstancesObserved { get; } = new();

    /// <summary>Global FM channels observed using this instrument.</summary>
    public HashSet<int> ChannelsObserved { get; } = new();

    public long FirstSeenWriteIndex = -1;
    public long? FirstSeenPlaybackSample;

    public int ObservationCount;
}

/// <summary>
/// Immutable, export-ready view of a completed collection session: the
/// deduplicated FM instruments in first-seen order.
/// </summary>
internal sealed class PlaybackAssetSnapshot
{
    public PlaybackAssetSnapshot(IReadOnlyList<CapturedFmInstrument> instruments)
    {
        Instruments = instruments;
    }

    public IReadOnlyList<CapturedFmInstrument> Instruments { get; }
}
