namespace Fmp.Core.Visualization;

/// <summary>
/// Explicit built-in device ordering shared by the timeline builder and the
/// topology builder (a single table; the two builders previously kept
/// divergent switch copies with a catch-all fallback). Groups follow the
/// visualization panel semantics: 1xx FM and FM-derived, 2xx PSG/SSG,
/// 3xx wavetable, 4xx PCM, 5xx rhythm/percussion (reserved), 6xx MIDI,
/// 7xx aggregate/platform. Every defined <see cref="ChipType"/> has an
/// explicit entry; only corrupted or externally supplied numeric values fall
/// back to <see cref="UnknownExternalPriority"/>.
/// </summary>
internal static class DeviceOrdering
{
    /// <summary>Sort position for out-of-range (corrupted/external) enum values.</summary>
    public const int UnknownExternalPriority = 10_000;

    public static readonly IReadOnlyDictionary<ChipType, int> BuiltInDevicePriority =
        new Dictionary<ChipType, int>
        {
            // 100–199: FM and FM-derived devices.
            [ChipType.Ym2203] = 100,
            [ChipType.Ym2608] = 101,
            [ChipType.Ym2610] = 102,
            [ChipType.Ym2612] = 103,
            [ChipType.Ym2151] = 104,
            [ChipType.Ym2413] = 105,
            [ChipType.Ym3526] = 106,
            [ChipType.Ym3812] = 107,
            [ChipType.Y8950] = 108,
            [ChipType.Ymf262] = 109,
            [ChipType.Ymf278b] = 110,

            // 200–299: PSG and SSG devices.
            [ChipType.Sn76489] = 200,
            [ChipType.Ay8910] = 201,
            [ChipType.Dmg] = 202,
            [ChipType.NesApu] = 203,

            // 300–399: wavetable devices.
            [ChipType.K051649] = 300,
            [ChipType.Huc6280] = 301,

            // 400–499: PCM devices.
            [ChipType.SegaPcm] = 400,
            [ChipType.Rf5c68] = 401,
            [ChipType.Rf5c164] = 402,
            [ChipType.C140] = 403,
            [ChipType.C352] = 404,
            [ChipType.K054539] = 405,
            [ChipType.Ga20] = 406,
            [ChipType.Okim6258] = 407,
            [ChipType.Okim6295] = 408,
            [ChipType.MultiPcm] = 409,
            [ChipType.Ymz280b] = 410,
            [ChipType.SnesDsp] = 411,
            [ChipType.Ppz8] = 412,
            [ChipType.Pcm] = 413,

            // 600–699: MIDI devices.
            [ChipType.Midi] = 600,

            // 700–799: aggregate or platform-specific devices.
            [ChipType.Unknown] = 700,
        };

    /// <summary>Sort priority; out-of-range values sort after every built-in device.</summary>
    public static int Priority(ChipType type) =>
        BuiltInDevicePriority.TryGetValue(type, out int priority) ? priority : UnknownExternalPriority;

    /// <summary>True when the value has no explicit built-in entry (corrupted or external enum value).</summary>
    public static bool IsExternal(ChipType type) =>
        !BuiltInDevicePriority.ContainsKey(type);
}
