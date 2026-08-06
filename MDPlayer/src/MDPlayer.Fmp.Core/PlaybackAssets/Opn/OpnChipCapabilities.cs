using Fmp.Core.Visualization;

namespace Fmp.Core.PlaybackAssets.Opn;

/// <summary>
/// Describes the OPN-family FM capabilities of a chip identity that this
/// asset pipe understands. The chip type is identified by the existing
/// <see cref="ChipType"/> classification used by the FMP rendering pipeline
/// rather than a parallel public taxonomy.
/// </summary>
internal readonly record struct OpnChipCapabilities(
    int FmChannelCount,
    int RegisterPortCount,
    bool SupportsSsgEg)
{
    /// <summary>Raised if capabilities could be resolved; equals
    /// <c>FmChannelCount != 0</c>.</summary>
    public bool IsSupported => FmChannelCount != 0;

    /// <summary>
    /// Resolves the OPN capabilities for a chip type. YM3438 uses the
    /// YM2612-compatible register model and is treated identically when it is
    /// ever routed through this pipe. Unsupported chip types return false.
    /// </summary>
    public static bool TryGetCapabilities(ChipType chipType, out OpnChipCapabilities capabilities)
    {
        capabilities = chipType switch
        {
            ChipType.Ym2203 => new(
                FmChannelCount: 3,
                RegisterPortCount: 1,
                SupportsSsgEg: true),

            ChipType.Ym2608 => new(
                FmChannelCount: 6,
                RegisterPortCount: 2,
                SupportsSsgEg: true),

            ChipType.Ym2612 => new(
                FmChannelCount: 6,
                RegisterPortCount: 2,
                SupportsSsgEg: false),

            // YM3438 (identical register model to YM2612) is not currently a
            // distinct ChipType in the FMP rendering pipeline. If a future
            // chip identity for it (or a YM2610) is routed here it would be
            // added with its six-channel, two-port, no-SSG-EG mapping.

            _ => default
        };

        return capabilities.IsSupported;
    }
}
