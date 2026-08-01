namespace Fmp.Core.Visualization;

internal enum StemStrategy
{
    None,
    NativeVoiceTaps,
    ParallelSynthesis,
    DeterministicMaskedReplay,
    DeviceReplay,
    Master,
}

internal sealed record StemPlan(
    bool Supported,
    ScopeSupport Support,
    StemStrategy Strategy,
    int SynthesizerInstances,
    int OutputStreams,
    string Reason);

/// <summary>
/// Selects a scope strategy from backend-reported capabilities. The planner
/// never promises isolation merely because a chip has a keyboard decoder.
/// </summary>
internal static class ScopePlanner
{
    public static StemPlan Plan(
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        string request,
        int synthesisInstanceBudget = 24)
    {
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

        StemPlan channel = ChannelPlan(devices, voices, synthesisInstanceBudget);
        StemPlan device = DevicePlan(devices);
        StemPlan master = MasterPlan(devices);

        return mode switch
        {
            "channel" => channel,
            "device" => device,
            "master" => master,
            _ when channel.Supported => channel,
            _ when device.Supported => device,
            _ => master,
        };
    }

    private static StemPlan ChannelPlan(
        IReadOnlyList<DeviceDescriptor> devices,
        IReadOnlyList<VoiceDescriptor> voices,
        int budget)
    {
        if (devices.Count == 0)
            return Unsupported("no active devices");
        if (devices.Any(device => device.ScopeSupport != ScopeSupport.Channel))
            return Unsupported("at least one active device has no reliable channel isolation");

        int instances = 1 + devices.Sum(device => Math.Max(
            1,
            voices.Count(voice => voice.Id.Device == device.Id)));
        if (instances > budget)
        {
            return new StemPlan(
                false,
                ScopeSupport.Channel,
                StemStrategy.ParallelSynthesis,
                instances,
                voices.Count,
                $"channel scopes require {instances} synthesis instances; budget is {budget}");
        }

        bool parallel = devices.All(device =>
            device.Capabilities.HasFlag(DeviceCapabilities.ParallelSynthesis));
        return new StemPlan(
            true,
            ScopeSupport.Channel,
            parallel ? StemStrategy.ParallelSynthesis : StemStrategy.DeterministicMaskedReplay,
            instances,
            voices.Count,
            parallel ? "parallel synthesis" : "deterministic masked replay");
    }

    private static StemPlan DevicePlan(IReadOnlyList<DeviceDescriptor> devices)
    {
        if (devices.Count == 0 || devices.Any(device =>
            device.ScopeSupport is not (ScopeSupport.Device or ScopeSupport.Channel)))
        {
            return Unsupported("at least one active device has no reliable device scope");
        }

        return new StemPlan(
            true,
            ScopeSupport.Device,
            StemStrategy.DeviceReplay,
            devices.Count,
            devices.Count,
            "one scope per active device");
    }

    private static StemPlan MasterPlan(IReadOnlyList<DeviceDescriptor> devices) =>
        devices.Count == 0
            ? Unsupported("no active devices")
            : new StemPlan(true, ScopeSupport.Master, StemStrategy.Master, 1, 1, "master scope");

    private static StemPlan Unsupported(string reason) =>
        new(false, ScopeSupport.None, StemStrategy.None, 0, 0, reason);
}
