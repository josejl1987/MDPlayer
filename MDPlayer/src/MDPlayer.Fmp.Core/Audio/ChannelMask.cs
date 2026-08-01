namespace Fmp.Core.Audio;

/// <summary>
/// Volume groups that can be controlled via MDSound's group-level API.
/// </summary>
internal enum VolumeGroup
{
    Ssg,
    Rhythm,
    Adpcm,
    Ppz8,
}

/// <summary>
/// Identifies a logical channel group for per-channel stem rendering.
/// Each flag represents a single channel or channel group that can be
/// independently muted during multi-pass scope rendering.
/// </summary>
[Flags]
internal enum ChannelGroup : long
{
    None = 0,

    // YM2608 FM channels (6 individual channels)
    Fm1   = 1L << 0,
    Fm2   = 1L << 1,
    Fm3   = 1L << 2,
    Fm4   = 1L << 3,
    Fm5   = 1L << 4,
    Fm6   = 1L << 5,

    // YM2608 SSG (PSG) channels (3 individual channels)
    Ssg1  = 1L << 6,
    Ssg2  = 1L << 7,
    Ssg3  = 1L << 8,

    // YM2608 Rhythm
    Rhythm = 1L << 9,

    // YM2608 ADPCM
    Adpcm = 1L << 10,

    // PPZ8 (external ADPCM)
    Ppz8  = 1L << 11,

    /// <summary>All channels active (master).</summary>
    All = (1L << 12) - 1,

    /// <summary>All FM channels (fm1-fm6).</summary>
    AllFm = Fm1 | Fm2 | Fm3 | Fm4 | Fm5 | Fm6,

    /// <summary>All SSG channels (ssg1-ssg3).</summary>
    AllSsg = Ssg1 | Ssg2 | Ssg3,
}

/// <summary>
/// Describes a stem pass: the channel group to enable and the output filename stem.
/// </summary>
internal readonly record struct StemPass
{
    /// <summary>Channel group enabled for this pass.</summary>
    public ChannelGroup Channels { get; init; }

    /// <summary>Output filename (without .wav extension), e.g. "ym2608-fm1".</summary>
    public string Name { get; init; }

    /// <summary>Human-readable label, e.g. "YM2608 FM1".</summary>
    public string Label { get; init; }
}

/// <summary>
/// Default set of stem passes for a full scope render.
/// Master is always first.
/// </summary>
internal static class DefaultStems
{
    public static readonly StemPass[] All =
    [
        new() { Channels = ChannelGroup.All, Name = "master", Label = "Master" },

        new() { Channels = ChannelGroup.Fm1, Name = "ym2608-fm1", Label = "YM2608 FM1" },
        new() { Channels = ChannelGroup.Fm2, Name = "ym2608-fm2", Label = "YM2608 FM2" },
        new() { Channels = ChannelGroup.Fm3, Name = "ym2608-fm3", Label = "YM2608 FM3" },
        new() { Channels = ChannelGroup.Fm4, Name = "ym2608-fm4", Label = "YM2608 FM4" },
        new() { Channels = ChannelGroup.Fm5, Name = "ym2608-fm5", Label = "YM2608 FM5" },
        new() { Channels = ChannelGroup.Fm6, Name = "ym2608-fm6", Label = "YM2608 FM6" },

        new() { Channels = ChannelGroup.Ssg1, Name = "ym2608-ssg1", Label = "YM2608 SSG1" },
        new() { Channels = ChannelGroup.Ssg2, Name = "ym2608-ssg2", Label = "YM2608 SSG2" },
        new() { Channels = ChannelGroup.Ssg3, Name = "ym2608-ssg3", Label = "YM2608 SSG3" },

        new() { Channels = ChannelGroup.Rhythm, Name = "ym2608-rhythm", Label = "YM2608 Rhythm" },
        new() { Channels = ChannelGroup.Adpcm, Name = "ym2608-adpcm", Label = "YM2608 ADPCM" },
        new() { Channels = ChannelGroup.Ppz8, Name = "ppz8-01", Label = "PPZ8" },
    ];
}
