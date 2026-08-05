namespace Fmp.Core.Rendering;

/// <summary>
/// Deterministic in-memory control capture produced by Pass 1 (the existing
/// FMP execution path). It records, at authoritative absolute YM2608
/// master-clock positions, every YM2608 register write and every PPZ8 command
/// the FMP driver raised, in one globally-ordered event stream. It is the sole
/// contract consumed by Pass 2 (native + PPZ8 replay); it carries duration,
/// loop and termination metadata exactly as decided by the legacy session so
/// replay never recalculates them.
///
/// The time coordinate is absolute master clock accumulated by
/// <see cref="FmpRuntime"/> at the control tick rate — never Nise286
/// instruction cycles, which are reset per driver call and do not advance
/// during startup waits or timer ticks.
/// </summary>
internal sealed class FmpExecutionCapture
{
    public required int OutputSampleRate { get; init; }

    public required IReadOnlyList<FmpCapturedEvent> Events { get; init; }

    public required ulong FinalOpnaMasterClock { get; init; }
    public required long FinalOutputFrame { get; init; }

    public required long FadeStartOutputFrame { get; init; }
    public required long FadeEndOutputFrame { get; init; }
    public required long TailEndOutputFrame { get; init; }

    public required int LoopCount { get; init; }
    public required string TerminationReason { get; init; }

    public required IReadOnlyList<Ppz8BankSnapshot> Ppz8Banks { get; init; }
}

/// <summary>
/// One globally-ordered captured event (interface for value-type events to
/// share a common stream while avoiding class-per-event allocations). Ordered
/// by <see cref="OpnaMasterClock"/> ascending then <see cref="Sequence"/>
/// strictly increasing (call order). Both the YM2608 and PPZ8 taps feed this
/// single stream so cross-device ordering is preserved.
/// </summary>
internal interface FmpCapturedEvent
{
    /// <summary>Absolute YM2608 master-clock position of the event.</summary>
    ulong OpnaMasterClock { get; }
    ulong Sequence { get; }
}

/// <summary>
/// A captured YM2608 register write. <see cref="Port"/> is the logical YM2608
/// port convention (0 or 1) already normalized at capture; PC-98 I/O addresses
/// are not retained.
/// </summary>
internal readonly record struct CapturedOpnaWrite(
    ulong OpnaMasterClock,
    ulong Sequence,
    byte Port,
    byte Address,
    byte Data) : FmpCapturedEvent;

/// <summary>
/// A captured PPZ8 command. <see cref="BankId"/> references a capture-local
/// <see cref="Ppz8BankSnapshot"/> for the load command (or -1 when none).
/// Commands are mapped to output frames only during replay.
/// </summary>
internal readonly record struct CapturedPpz8Command(
    ulong OpnaMasterClock,
    ulong Sequence,
    int Port,
    int Address,
    int Data,
    int BankId = -1) : FmpCapturedEvent;

/// <summary>
/// An immutable, capture-local snapshot of one PPZ8 bank file. Content is
/// deduplicated by SHA-256 (not just filename); replay uses only these bytes,
/// never the mutable source file. <see cref="ChannelLengths"/> preserves the
/// per-channel decomposition of the source <c>byte[][]</c> so replay can
/// reconstruct it exactly for the shared PPZ8 renderer.
/// </summary>
internal sealed record Ppz8BankSnapshot(
    int BankId,
    string LogicalName,
    byte[] Data,
    int[] ChannelLengths,
    string Sha256);
