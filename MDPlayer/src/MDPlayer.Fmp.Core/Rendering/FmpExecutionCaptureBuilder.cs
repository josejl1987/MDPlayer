using System.Security.Cryptography;

namespace Fmp.Core.Rendering;

/// <summary>
/// Pass-1 capture builder. Implements <see cref="IFmpExecutionCaptureSink"/>,
/// assigning one monotonically increasing global <c>Sequence</c> to every OPNA
/// and PPZ8 event in exact landing order. Banks are captured once by content
/// (SHA-256), deduplicated, and referenced by capture-local bank ID so replay
/// never touches the mutable source file.
///
/// The implementation is deterministic: the same FMP execution yields the
/// same event stream. It uses <see cref="List{T}"/> and record structs;
/// per-event class allocations are avoided.
/// </summary>
internal sealed class FmpExecutionCaptureBuilder : IFmpExecutionCaptureSink, IDisposable
{
    private readonly int _outputSampleRate;
    private readonly ulong _cpuClockHz;

    private readonly List<FmpCapturedEvent> _events = new();
    private readonly List<Ppz8BankSnapshot> _banks = new();
    private readonly Dictionary<string, Ppz8BankSnapshot> _bankBySha = new();

    private ulong _sequence;
    private bool _disposed;

    public FmpExecutionCaptureBuilder(int outputSampleRate, ulong cpuClockHz)
    {
        if (outputSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSampleRate));
        if (cpuClockHz == 0)
            throw new ArgumentOutOfRangeException(nameof(cpuClockHz));
        _outputSampleRate = outputSampleRate;
        _cpuClockHz = cpuClockHz;
    }

    public int EventCount => _events.Count;

    public void CaptureOpnaWrite(ulong cpuCycle, byte port, byte address, byte data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _events.Add(new CapturedOpnaWrite(cpuCycle, NextSequence(), port, address, data));
    }

    public void CapturePpz8Command(ulong cpuCycle, in Ppz8Command command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _events.Add(new CapturedPpz8Command(
            cpuCycle, NextSequence(), command.Port, command.Address, command.Data, command.BankId));
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

    public ulong FinalCpuCycle { get; private set; }

    public void SetFinalCpuCycle(ulong cpuCycle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cpuCycle < FinalCpuCycle)
            throw new ArgumentOutOfRangeException(nameof(cpuCycle), "final CPU cycle regressed");
        FinalCpuCycle = cpuCycle;
    }

    /// <summary>
    /// Validates that events are already globally ordered (cycle ascending,
    /// sequence strictly increasing) and finalizes the capture. Safe to call
    /// once; subsequent sink writes throw.
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
            CpuClockFrequencyHz = _cpuClockHz,
            Events = _events,
            FinalCpuCycle = FinalCpuCycle,
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
        ulong prevCycle = 0;
        ulong prevSeq = 0;
        for (int i = 0; i < _events.Count; i++)
        {
            var e = _events[i];
            if (e.CpuCycle < prevCycle)
                throw new InvalidOperationException($"capture out of order at index {i}: cycle {e.CpuCycle} < {prevCycle}");
            if (e.Sequence <= prevSeq)
                throw new InvalidOperationException($"capture sequence not strictly increasing at index {i}");
            prevCycle = e.CpuCycle;
            prevSeq = e.Sequence;
        }
    }

    private ulong NextSequence()
    {
        // Checked increment: the global sequence must never silently wrap.
        _sequence = checked(_sequence + 1);
        return _sequence;
    }

    private static string Sha256(byte[] data) =>
        Convert.ToHexString(SHA256.HashData(data));

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
