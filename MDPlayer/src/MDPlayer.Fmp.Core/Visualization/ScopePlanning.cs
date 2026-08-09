namespace Fmp.Core.Visualization;

internal enum StemStrategy
{
    None,
    FmpParallelSynthesis,
    VgmRenderedStems,
    MasterCaptured,
}

internal sealed record StemPlan(
    bool Supported,
    ScopeSupport Support,
    StemStrategy Strategy,
    int SynthesizerInstances,
    int OutputStreams,
    string Reason);

/// <summary>
/// Plans only strategies that have a concrete renderer in the current tree.
/// Decoder capabilities alone never imply that isolated audio can be emitted.
/// </summary>
internal static class ScopePlanner
{
    public static StemPlan Plan(
        string backendId,
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        string request,
        int synthesisInstanceBudget = 24)
    {
        if (string.IsNullOrWhiteSpace(backendId))
            throw new ArgumentException("backend id is required", nameof(backendId));
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(voices);
        if (synthesisInstanceBudget <= 0)
            throw new ArgumentOutOfRangeException(nameof(synthesisInstanceBudget));

        string mode = string.IsNullOrWhiteSpace(request)
            ? "auto"
            : request.Trim().ToLowerInvariant();
        if (mode is not ("auto" or "channel" or "device" or "master" or "off"))
            throw new ArgumentException($"unknown scope mode '{request}'", nameof(request));

        if (mode == "off")
            return new StemPlan(true, ScopeSupport.None, StemStrategy.None, 0, 0, "scope disabled");

        StemPlan channel = ChannelPlan(
            backendId.Trim().ToLowerInvariant(),
            devices,
            voices,
            synthesisInstanceBudget);
        StemPlan master = MasterPlan(devices);

        return mode switch
        {
            "channel" => channel,
            "device" => Unsupported(
                ScopeSupport.Device,
                "device scopes have no renderer"),
            "master" => master,
            _ when channel.Supported => channel,
            _ => master,
        };
    }

    private static StemPlan ChannelPlan(
        string backendId,
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        int budget) => backendId switch
        {
            "fmp" => FmpChannelPlan(devices, voices, budget),
            "vgm" => VgmChannelPlan(devices, budget),
            _ => Unsupported(
                ScopeSupport.Channel,
                $"backend '{backendId}' has no channel stem renderer"),
        };

    private static StemPlan FmpChannelPlan(
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        int budget)
    {
        if (devices.Count == 0)
            return Unsupported(ScopeSupport.Channel, "no active devices");
        if (devices.Any(device => device.Id.Type is not (ChipType.Ym2608 or ChipType.Ppz8)))
        {
            return Unsupported(
                ScopeSupport.Channel,
                "the FMP stem renderer only isolates YM2608 and PPZ8");
        }

        int streams = Math.Max(1, voices.Count + 1);
        if (streams > budget)
        {
            return new StemPlan(
                false,
                ScopeSupport.Channel,
                StemStrategy.FmpParallelSynthesis,
                streams,
                streams,
                $"FMP channel scopes require {streams} synthesizer instances; budget is {budget}");
        }

        return new StemPlan(
            true,
            ScopeSupport.Channel,
            StemStrategy.FmpParallelSynthesis,
            streams,
            streams,
            "one FMP emulation pass broadcasting to masked synthesizers");
    }

    private static StemPlan VgmChannelPlan(
        IReadOnlyList<DeviceDescriptor> devices,
        int budget)
    {
        if (devices.Count == 0)
            return Unsupported(ScopeSupport.Channel, "no active devices");

        // VGM never carries PPZ8 data: a PPZ8 device in a VGM timeline is the
        // FMP-shared YM2608 decoder's phantom companion (it unconditionally
        // advertises Ppz8 alongside every Ym2608 device). The VGM stem
        // renderer builds no PPZ8 stems, so ignore it for planning; otherwise
        // every YM2608-only VGM (e.g. Master Ninja) would silently degrade to
        // the master-waveform fallback.
        DeviceDescriptor[] stemDevices = devices
            .Where(device => device.Id.Type != ChipType.Ppz8)
            .ToArray();
        if (stemDevices.Length == 0)
            return Unsupported(ScopeSupport.Channel, "no active devices");
        if (stemDevices.GroupBy(device => device.Id.Type).Any(group => group.Count() != 1)
            || stemDevices.Any(device => !IsVgmStemDevice(device.Id.Type)))
        {
            return Unsupported(
                ScopeSupport.Channel,
                "the VGM stem renderer requires one supported instance per active device");
        }

        int streams = 1 + stemDevices.Sum(device => device.Id.Type switch
        {
            ChipType.Huc6280 => 6,
            ChipType.Ym2612 => 6,
            ChipType.Ym2608 => 11,
            ChipType.Ym2151 => 8,
            ChipType.Sn76489 => 4,
            ChipType.Okim6295 => 1,
            ChipType.Ym2203 => 6,
            ChipType.Ym2610 => 8,
            ChipType.Ym2413 => 9,
            ChipType.Ym3526 => 9,
            ChipType.Ym3812 => 9,
            ChipType.Y8950 => 9,
            ChipType.Ymf262 => 18,
            ChipType.Ay8910 => 4,
            ChipType.NesApu => 5,
            ChipType.Dmg => 4,
            ChipType.K051649 => 6,
            _ => 0,
        });
        if (streams > budget)
        {
            return new StemPlan(
                false,
                ScopeSupport.Channel,
                StemStrategy.VgmRenderedStems,
                streams - 1,
                streams,
                $"VGM channel scopes require {streams - 1} renderers; budget is {budget}");
        }

        return new StemPlan(
            true,
            ScopeSupport.Channel,
            StemStrategy.VgmRenderedStems,
            streams - 1,
            streams,
            "VGM register replay through implemented per-channel filters");
    }

    private static bool IsVgmStemDevice(ChipType type) =>
        type is ChipType.Huc6280 or ChipType.Ym2612 or ChipType.Ym2608 or ChipType.Ym2151 or ChipType.Sn76489 or ChipType.Okim6295
            or ChipType.Ym2203 or ChipType.Ym2610 or ChipType.Ym2413 or ChipType.Ym3526 or ChipType.Ym3812
            or ChipType.Y8950 or ChipType.Ymf262 or ChipType.Ay8910 or ChipType.NesApu or ChipType.Dmg
            or ChipType.K051649;

    private static StemPlan MasterPlan(IReadOnlyList<DeviceDescriptor> devices) =>
        devices.Count == 0
            ? Unsupported(ScopeSupport.Master, "no active devices")
            : new StemPlan(
                true,
                ScopeSupport.Master,
                StemStrategy.MasterCaptured,
                1,
                1,
                "captured master audio");

    private static StemPlan Unsupported(ScopeSupport support, string reason) =>
        new(false, support, StemStrategy.None, 0, 0, reason);
}
