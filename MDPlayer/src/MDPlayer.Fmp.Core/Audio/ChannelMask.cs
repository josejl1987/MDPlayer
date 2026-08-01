namespace Fmp.Core.Audio;

internal enum ScopeSemanticClass
{
    FmEvolving,
    PulseStable,
    Noise,
    Percussive,
    Pcm,
    Mixed,
}

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
    public StemPass()
    {
        SemanticClass = ScopeSemanticClass.Mixed;
        StableOrder = int.MaxValue;
        WindowWidth = 1;
        DefaultAmplification = 1.0;
    }

    /// <summary>Channel group enabled for this pass.</summary>
    public ChannelGroup Channels { get; init; }

    /// <summary>Output filename (without .wav extension), e.g. "ym2608-fm1".</summary>
    public string Name { get; init; }

    /// <summary>Human-readable label, e.g. "YM2608 FM1".</summary>
    public string Label { get; init; }

    /// <summary>
    /// Stable semantic track identity supplied by the presentation adapter.
    /// Scope selection never derives this identity by parsing a file name.
    /// </summary>
    public string PresentationTrackId { get; init; }

    /// <summary>Waveform semantics used by Corrscope trigger selection.</summary>
    public ScopeSemanticClass SemanticClass { get; init; } = ScopeSemanticClass.Mixed;

    /// <summary>Stable scope ordering supplied by the presentation adapter.</summary>
    public int StableOrder { get; init; }

    /// <summary>Corrscope stride multiplier for this waveform class.</summary>
    public int WindowWidth { get; init; }

    /// <summary>Fallback gain when a peak-based gain cannot be calculated.</summary>
    public double DefaultAmplification { get; init; }

    /// <summary>Stable adapter-selected line color.</summary>
    public string DefaultColor { get; init; }
}

/// <summary>
/// Default set of stem passes for a full scope render.
/// Master is always first.
/// </summary>
internal static class DefaultStems
{
    public static readonly StemPass[] All =
    [
        new() { Channels = ChannelGroup.All, Name = "master", Label = "Master", PresentationTrackId = "master", SemanticClass = ScopeSemanticClass.Mixed, StableOrder = 0, DefaultColor = "#7aa4ff" },

        new() { Channels = ChannelGroup.Fm1, Name = "ym2608-fm1", Label = "FM1", PresentationTrackId = "ym2608.0.fm.1", SemanticClass = ScopeSemanticClass.FmEvolving, StableOrder = 10, DefaultColor = "#ff665c" },
        new() { Channels = ChannelGroup.Fm2, Name = "ym2608-fm2", Label = "FM2", PresentationTrackId = "ym2608.0.fm.2", SemanticClass = ScopeSemanticClass.FmEvolving, StableOrder = 11, DefaultColor = "#ffb44c" },
        new() { Channels = ChannelGroup.Fm3, Name = "ym2608-fm3", Label = "FM3", PresentationTrackId = "ym2608.0.fm.3", SemanticClass = ScopeSemanticClass.FmEvolving, StableOrder = 12, DefaultColor = "#f2df5b" },
        new() { Channels = ChannelGroup.Fm4, Name = "ym2608-fm4", Label = "FM4", PresentationTrackId = "ym2608.0.fm.4", SemanticClass = ScopeSemanticClass.FmEvolving, StableOrder = 13, DefaultColor = "#44cc44" },
        new() { Channels = ChannelGroup.Fm5, Name = "ym2608-fm5", Label = "FM5", PresentationTrackId = "ym2608.0.fm.5", SemanticClass = ScopeSemanticClass.FmEvolving, StableOrder = 14, DefaultColor = "#44aaff" },
        new() { Channels = ChannelGroup.Fm6, Name = "ym2608-fm6", Label = "FM6", PresentationTrackId = "ym2608.0.fm.6", SemanticClass = ScopeSemanticClass.FmEvolving, StableOrder = 15, DefaultColor = "#aa44ff" },

        new() { Channels = ChannelGroup.Ssg1, Name = "ym2608-ssg1", Label = "SSG1", PresentationTrackId = "ym2608.0.ssg.1", SemanticClass = ScopeSemanticClass.PulseStable, StableOrder = 20, DefaultAmplification = 0.7, DefaultColor = "#62b8ff" },
        new() { Channels = ChannelGroup.Ssg2, Name = "ym2608-ssg2", Label = "SSG2", PresentationTrackId = "ym2608.0.ssg.2", SemanticClass = ScopeSemanticClass.PulseStable, StableOrder = 21, DefaultAmplification = 0.7, DefaultColor = "#3399ee" },
        new() { Channels = ChannelGroup.Ssg3, Name = "ym2608-ssg3", Label = "SSG3", PresentationTrackId = "ym2608.0.ssg.3", SemanticClass = ScopeSemanticClass.PulseStable, StableOrder = 22, DefaultAmplification = 0.7, DefaultColor = "#62b8ff" },

        new() { Channels = ChannelGroup.Rhythm, Name = "ym2608-rhythm", Label = "RHYTHM", PresentationTrackId = "ym2608.0.rhythm", SemanticClass = ScopeSemanticClass.Percussive, StableOrder = 30, WindowWidth = 2, DefaultAmplification = 0.75, DefaultColor = "#db72ff" },
        new() { Channels = ChannelGroup.Adpcm, Name = "ym2608-adpcm", Label = "ADPCM-B", PresentationTrackId = "ym2608.0.adpcm-b", SemanticClass = ScopeSemanticClass.Pcm, StableOrder = 31, WindowWidth = 2, DefaultColor = "#66cc66" },
        new() { Channels = ChannelGroup.Ppz8, Name = "ppz8-01", Label = "PCM", PresentationTrackId = "ppz8.0", SemanticClass = ScopeSemanticClass.Pcm, StableOrder = 32, WindowWidth = 2, DefaultColor = "#cc66ff" },
    ];
}
