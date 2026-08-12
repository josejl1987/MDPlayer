namespace Fmp.Core.Rendering;

/// <summary>
/// Pass-1 capture builder. Implements <see cref="IFmpExecutionCaptureSink"/>,
/// assigning one monotonically increasing global <c>Sequence</c> to every OPNA
/// and PPZ8 event in exact landing order. Banks are captured once by content
/// (SHA-256), deduplicated, and referenced by capture-local bank ID so replay
/// never touches the mutable source file.
///
/// The implementation is deterministic: the same FMP execution yields the
/// same event stream. It uses <see cref="List{T}"/> of the unboxed
/// <see cref="CapturedEvent"/> value type; per-event class allocations and
/// boxing are avoided. The time coordinate is the absolute YM2608 master clock
/// supplied by the capture tap — it is never derived here.
/// </summary>
internal sealed class FmpExecutionCaptureBuilder : IFmpExecutionCaptureSink, IDisposable
{
    private readonly int _outputSampleRate;

    private readonly List<CapturedEvent> _events = new();
    private readonly List<Ppz8BankSnapshot> _banks = new();
    private readonly Dictionary<string, Ppz8BankSnapshot> _bankBySha = new();

    private ulong _sequence;
    private bool _disposed;

    public FmpExecutionCaptureBuilder(int outputSampleRate)
    {
        if (outputSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        _outputSampleRate = outputSampleRate;
    }

    public int EventCount => _events.Count;

    public void CaptureOpnaWrite(ulong opnaMasterClock, byte port, byte address, byte data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _events.Add(CapturedEvent.OpnaWrite(opnaMasterClock, NextSequence(), port, address, data));
    }

    public void CapturePpz8Command(ulong opnaMasterClock, in Ppz8Command command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _events.Add(CapturedEvent.Ppz8Command(
            opnaMasterClock, NextSequence(), command.Port, command.Address, command.Data, command.BankId));
    }

    public int CapturePpz8Bank(string logicalName, int bankIndex, int mode, ReadOnlySpan<byte[]> dataChannels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(logicalName);

        // Flatten the per-channel content into one immutable snapshot and hash
        // only the concatenated channel bytes (the authoritative content).
        int total = 0;
        foreach (var ch in dataChannels)
        {
            if (ch == null) throw new ArgumentException("PPZ8 bank channel is null", nameof(dataChannels));
            total = checked(total + ch.Length);
        }
        var raw = new byte[total];
        var lengths = new int[dataChannels.Length];
        int off = 0;
        for (int i = 0; i < dataChannels.Length; i++)
        {
            dataChannels[i].CopyTo(raw, off);
            off += dataChannels[i].Length;
            lengths[i] = dataChannels[i].Length;
        }
        string sha = Sha256(raw);

        // Deduplicate by content hash, not by filename.
        if (_bankBySha.TryGetValue(sha, out var existing))
            return existing.BankId;

        int bankId = _banks.Count;
        var snap = new Ppz8BankSnapshot(bankId, logicalName, raw, lengths, sha);
        _banks.Add(snap);
        _bankBySha[sha] = snap;
        return bankId;
    }

    public ulong FinalOpnaMasterClock { get; private set; }

    public void SetFinalOpnaMasterClock(ulong opnaMasterClock)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (opnaMasterClock < FinalOpnaMasterClock)
            throw new ArgumentOutOfRangeException(nameof(opnaMasterClock), "final master clock regressed");
        FinalOpnaMasterClock = opnaMasterClock;
    }

    /// <summary>
    /// Validates that events are already globally ordered (master clock
    /// ascending, sequence strictly increasing) and finalizes the capture.
    /// Safe to call once; subsequent sink writes throw.
    /// </summary>
    public FmpExecutionCapture Finish(
        long finalOutputFrame,
        long fadeStartFrame,
        long fadeEndFrame,
        long tailEndFrame,
        int loopCount,
        string terminationReason)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateOrder();
        _disposed = true;
        return new FmpExecutionCapture
        {
            OutputSampleRate = _outputSampleRate,
            Events = _events,
            FinalOpnaMasterClock = FinalOpnaMasterClock,
            FinalOutputFrame = finalOutputFrame,
            FadeStartOutputFrame = fadeStartFrame,
            FadeEndOutputFrame = fadeEndFrame,
            TailEndOutputFrame = tailEndFrame,
            LoopCount = loopCount,
            TerminationReason = terminationReason,
            Ppz8Banks = _banks,
        };
    }

    private void ValidateOrder()
    {
        ulong prevClock = 0;
        ulong prevSeq = 0;
        for (int i = 0; i < _events.Count; i++)
        {
            var e = _events[i];
            if (e.OpnaMasterClock < prevClock)
                throw new InvalidOperationException($"capture out of order at index {i}: master clock {e.OpnaMasterClock} < {prevClock}");
            if (e.Sequence <= prevSeq)
                throw new InvalidOperationException($"capture sequence not strictly increasing at index {i}");
            prevClock = e.OpnaMasterClock;
            prevSeq = e.Sequence;
        }
    }

    private ulong NextSequence()
    {
        // Checked increment: the global sequence must never silently wrap.
        _sequence = checked(_sequence + 1);
        return _sequence;
    }

    private static string Sha256(byte[] data) => CaptureHasher.Sha256Hex(data);

    /// <summary>Access to the captured banks (test/diagnostic intra-session use only).</summary>
    public IReadOnlyList<Ppz8BankSnapshot> Banks => _banks;

    public void Dispose()
    {
        _events.Clear();
        _banks.Clear();
        _bankBySha.Clear();
        _disposed = true;
    }
}
