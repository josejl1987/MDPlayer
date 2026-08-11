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

    public required IReadOnlyList<CapturedEvent> Events { get; init; }

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
/// The device tap that produced a captured event.
/// </summary>
internal enum CapturedEventKind : byte
{
    OpnaWrite = 0,
    Ppz8Command = 1,
}

/// <summary>
/// One globally-ordered captured event, stored as a tagged value type so the
/// capture stream holds events UNBOXED inline in its backing array (zero
/// boxing, zero per-event allocations). Ordered by
/// <see cref="OpnaMasterClock"/> ascending then <see cref="Sequence"/>
/// strictly increasing (call order). Both the YM2608 and PPZ8 taps feed this
/// single stream so cross-device ordering is preserved. Only the payload half
/// matching <see cref="Kind"/> is populated.
/// </summary>
internal readonly struct CapturedEvent
{
    public readonly CapturedEventKind Kind;
    public readonly ulong OpnaMasterClock;
    public readonly ulong Sequence;
    public readonly CapturedEventPayload Payload;

    public CapturedEvent(CapturedEventKind kind, ulong opnaMasterClock, ulong sequence, in CapturedEventPayload payload)
    {
        Kind = kind;
        OpnaMasterClock = opnaMasterClock;
        Sequence = sequence;
        Payload = payload;
    }

    public static CapturedEvent OpnaWrite(ulong OpnaMasterClock, ulong Sequence, byte Port, byte Address, byte Data)
        => new(CapturedEventKind.OpnaWrite, OpnaMasterClock, Sequence,
            CapturedEventPayload.ForOpnaWrite(Port, Address, Data));

    public static CapturedEvent Ppz8Command(ulong OpnaMasterClock, ulong Sequence, int Port, int Address, int Data, int BankId = -1)
        => new(CapturedEventKind.Ppz8Command, OpnaMasterClock, Sequence,
            CapturedEventPayload.ForPpz8Command(Port, Address, Data, BankId));
}

/// <summary>
/// Fixed-size payload slot holding either an OPNA write (byte fields) or a
/// PPZ8 command (int fields). The half not matching the event kind is left at
/// its default value; consumers read only the matching half.
/// </summary>
internal readonly struct CapturedEventPayload
{
    public readonly OpnaWritePayload Opna;
    public readonly Ppz8CommandPayload Ppz8;

    private CapturedEventPayload(in OpnaWritePayload opna, in Ppz8CommandPayload ppz8)
    {
        Opna = opna;
        Ppz8 = ppz8;
    }

    public static CapturedEventPayload ForOpnaWrite(byte port, byte address, byte data)
        => new(new OpnaWritePayload(port, address, data), default);

    public static CapturedEventPayload ForPpz8Command(int port, int address, int data, int bankId)
        => new(default, new Ppz8CommandPayload(port, address, data, bankId));
}

/// <summary>
/// Payload of a captured YM2608 register write. <see cref="Port"/> is the
/// logical YM2608 port convention (0 or 1) already normalized at capture;
/// PC-98 I/O addresses are not retained.
/// </summary>
internal readonly struct OpnaWritePayload
{
    public readonly byte Port;
    public readonly byte Address;
    public readonly byte Data;

    public OpnaWritePayload(byte port, byte address, byte data)
    {
        Port = port;
        Address = address;
        Data = data;
    }
}

/// <summary>
/// Payload of a captured PPZ8 command. <see cref="BankId"/> references a
/// capture-local <see cref="Ppz8BankSnapshot"/> for the load command (or -1
/// when none). Commands are mapped to output frames only during replay.
/// </summary>
internal readonly struct Ppz8CommandPayload
{
    public readonly int Port;
    public readonly int Address;
    public readonly int Data;
    public readonly int BankId;

    public Ppz8CommandPayload(int port, int address, int data, int bankId)
    {
        Port = port;
        Address = address;
        Data = data;
        BankId = bankId;
    }
}

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
