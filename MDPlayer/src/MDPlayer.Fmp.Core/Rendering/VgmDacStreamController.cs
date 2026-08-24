using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

internal sealed class VgmPcmDataBank
{
    public List<byte> Data { get; } = [];
    public List<VgmPcmDataBlock> Blocks { get; } = [];
}

internal readonly record struct VgmPcmDataBlock(int Offset, int Length);

/// <summary>
/// Executes the VGM DAC Stream Control protocol in source-time units. The
/// controller deliberately emits ordinary YM2612 writes as well as the
/// visualization-only boundaries; the audio path remains register-driven.
/// </summary>
internal sealed class VgmDacStreamController
{
    private const long PhaseScale = 1L << 32;
    private const long SourceRate = 44_100;
    private readonly VgmPcmDataBank[] _banks = new VgmPcmDataBank[0x40];
    private readonly StreamState[] _streams = new StreamState[0xFF];
    private readonly List<StreamState> _activeStreams = [];
    private readonly List<PendingControl> _pendingControls = [];
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    private readonly Action<string> _warn;
    private readonly Action<VgmRegisterWrite> _emitWrite;
    private readonly Action<Ym2612DacStreamControlKind, long, byte, DeviceId, double?> _emitControl;
    private long _sourceSample;

    public VgmDacStreamController(
        Action<string> warning,
        Action<VgmRegisterWrite> emitWrite,
        Action<Ym2612DacStreamControlKind, long, byte, DeviceId, double?> emitControl)
    {
        _warn = warning ?? throw new ArgumentNullException(nameof(warning));
        _emitWrite = emitWrite ?? throw new ArgumentNullException(nameof(emitWrite));
        _emitControl = emitControl ?? throw new ArgumentNullException(nameof(emitControl));
        for (int i = 0; i < _banks.Length; i++)
            _banks[i] = new VgmPcmDataBank();
    }

    public VgmPcmDataBank GetBank(byte bankId) => _banks[bankId & 0x3F];

    public void AppendDataBlock(byte bankId, ReadOnlySpan<byte> payload)
    {
        if (bankId >= _banks.Length)
            return;

        VgmPcmDataBank bank = _banks[bankId];
        int offset = bank.Data.Count;
        bank.Data.AddRange(payload.ToArray());
        bank.Blocks.Add(new VgmPcmDataBlock(offset, payload.Length));
    }

    public bool TryReadLegacyByte(long cursor, out byte value)
    {
        VgmPcmDataBank bank = _banks[0];
        if (cursor >= 0 && cursor < bank.Data.Count)
        {
            value = bank.Data[(int)cursor];
            return true;
        }

        value = 0;
        return false;
    }

    public bool TryHandleSetup(byte streamId, byte targetType, byte port, byte register)
    {
        if (streamId == 0xFF)
            return true;

        StreamState state = GetStream(streamId);
        bool wasPlaying = state.Playing;
        bool hadPending = RemovePendingControls(streamId);
        if ((wasPlaying || hadPending) && HasDacSemantics(state))
        {
            _emitControl(
                Ym2612DacStreamControlKind.Stop,
                _sourceSample,
                streamId,
                state.Device,
                null);
        }
        state.Configured = true;
        state.TargetChip = (byte)(targetType & 0x7F);
        state.TargetInstance = (targetType & 0x80) != 0 ? 1 : 0;
        state.TargetPort = port;
        state.TargetRegister = register;
        state.Playing = false;
        Deactivate(state);
        state.Phase = 0;
        if (!CanExecuteStream(state))
        {
            WarnOnce(
                $"target:{streamId:X2}:{state.TargetChip:X2}:{state.TargetInstance}",
                $"VGM DAC stream 0x{streamId:X2} targets unsupported chip type 0x{state.TargetChip:X2}; stream is ignored");
        }
        return true;
    }

    public void SetData(byte streamId, byte bankId, byte stepSize, byte stepBase)
    {
        if (streamId == 0xFF)
            return;
        StreamState state = RequireConfigured(streamId, "0x91");
        if (state is null)
            return;

        state.BankId = bankId;
        state.StepSize = stepSize == 0 ? 1 : stepSize;
        state.StepBase = stepBase;
    }

    public void SetFrequency(byte streamId, uint frequencyHz)
    {
        if (streamId == 0xFF)
            return;
        StreamState state = RequireConfigured(streamId, "0x92");
        if (state is null)
            return;

        state.FrequencyHz = frequencyHz;
        state.Increment = CalculateIncrement(frequencyHz);
        if (state.Playing)
        {
            NormalizePhase(state);
            if (HasDacSemantics(state))
            {
                _emitControl(
                    Ym2612DacStreamControlKind.RateChanged,
                    _sourceSample,
                    streamId,
                    state.Device,
                    frequencyHz > 0 ? frequencyHz : null);
            }
        }
    }

    public void Start(
        byte streamId,
        uint startOffset,
        byte flags,
        uint length,
        bool fastStart = false,
        ushort blockId = 0)
    {
        if (streamId == 0xFF)
            return;
        StreamState state = RequireConfigured(streamId, fastStart ? "0x95" : "0x93");
        if (state is null || !CanExecuteStream(state))
            return;

        if (state.BankId >= _banks.Length || _banks[state.BankId].Blocks.Count == 0)
        {
            WarnOnce(
                $"start-bank:{streamId:X2}",
                $"VGM DAC stream 0x{streamId:X2} cannot start because no PCM data bank is configured");
            return;
        }

        VgmPcmDataBank bank = _banks[state.BankId];
        long dataStart;
        long dataLimit;
        long commandCount;
        bool loop;
        bool reverse;

        if (fastStart)
        {
            if (blockId >= bank.Blocks.Count)
            {
                WarnOnce(
                    $"block:{streamId:X2}:{blockId}",
                    $"VGM DAC stream 0x{streamId:X2} references nonexistent PCM block {blockId} in bank 0x{state.BankId:X2}");
                return;
            }

            VgmPcmDataBlock block = bank.Blocks[blockId];
            dataStart = (long)block.Offset + state.StepBase;
            dataLimit = (long)block.Offset + block.Length;
            commandCount = AvailableCommands(dataStart, dataLimit, state.StepSize);
            loop = (flags & 0x01) != 0;
            reverse = (flags & 0x10) != 0;
        }
        else
        {
            dataStart = startOffset == uint.MaxValue
                ? state.DataStart
                : (long)startOffset + state.StepBase;
            dataLimit = bank.Data.Count;
            loop = (flags & 0x80) != 0;
            reverse = (flags & 0x10) != 0;
            int mode = flags & 0x0F;
            commandCount = ResolveCommandCount(state, mode, length, dataStart, dataLimit);
        }

        if (dataStart < 0 || dataStart >= dataLimit || commandCount <= 0)
        {
            WarnOnce(
                $"range:{streamId:X2}:{dataStart}:{dataLimit}",
                $"VGM DAC stream 0x{streamId:X2} start range is outside PCM bank 0x{state.BankId:X2}; playback is silent");
            return;
        }

        long maximum = AvailableCommands(dataStart, dataLimit, state.StepSize);
        if (commandCount > maximum)
            commandCount = maximum;
        if (commandCount <= 0)
        {
            WarnOnce(
                $"length:{streamId:X2}:{dataStart}",
                $"VGM DAC stream 0x{streamId:X2} has no valid stepped PCM commands; playback is silent");
            return;
        }

        bool retrigger = state.Playing;
        RemovePendingControls(streamId);
        state.DataStart = dataStart;
        state.DataLimit = dataLimit;
        state.CommandsPerCycle = commandCount;
        state.CommandIndex = 0;
        state.Loop = loop;
        state.Reverse = reverse;
        state.Playing = true;
        Activate(state);
        state.Increment = CalculateIncrement(state.FrequencyHz);
        state.Phase = InitialPhase(state.Increment);
        state.PhaseSample = _sourceSample;

        if (state.FrequencyHz == 0)
        {
            WarnOnce(
                $"frequency-zero:{streamId:X2}",
                $"VGM DAC stream 0x{streamId:X2} was started with frequency 0; no DAC data will be emitted");
        }

        if (HasDacSemantics(state))
        {
            _emitControl(
                retrigger ? Ym2612DacStreamControlKind.Retrigger : Ym2612DacStreamControlKind.Start,
                _sourceSample,
                streamId,
                state.Device,
                state.FrequencyHz > 0 ? state.FrequencyHz : null);
        }
    }

    public void Stop(byte streamId)
    {
        if (streamId == 0xFF)
        {
            for (byte id = 0; id < 0xFF; id++)
                StopOne(id);
            return;
        }
        StopOne(streamId);
    }

    public void AdvanceTime(long sourceSample, long delta)
    {
        if (delta < 0)
            throw new ArgumentOutOfRangeException(nameof(delta));
        if (sourceSample != _sourceSample)
            throw new InvalidOperationException("VGM DAC stream controller source time is not monotonic.");

        long end = checked(sourceSample + delta);
        long cursor = sourceSample;
        while (cursor < end)
        {
            StreamState selected = null;
            long selectedSample = long.MaxValue;
            for (int index = 0; index < _activeStreams.Count; index++)
            {
                StreamState state = _activeStreams[index];
                if (state is null || !state.Playing || state.Increment <= 0)
                    continue;

                long offset = NextEmissionOffset(state);
                long candidate = checked(cursor + offset);
                if (candidate < end
                    && (candidate < selectedSample
                        || candidate == selectedSample && selected is not null
                            && state.StreamId < selected.StreamId))
                {
                    selected = state;
                    selectedSample = candidate;
                }
            }

            PendingControl pending = FindPendingControl();
            if (pending is not null && (selected is null || pending.Sample <= selectedSample))
            {
                if (pending.Sample >= end)
                    break;
                for (int index = 0; index < _activeStreams.Count; index++)
                {
                    StreamState state = _activeStreams[index];
                    if (state is not null && state.Playing && state.Increment > 0)
                        AdvancePhaseTo(state, pending.Sample);
                }
                _pendingControls.Remove(pending);
                _emitControl(
                    pending.Kind,
                    pending.Sample,
                    pending.StreamId,
                    pending.Device,
                    pending.RateHz);
                cursor = pending.Sample;
                continue;
            }

            if (selected is null)
                break;

            for (int index = 0; index < _activeStreams.Count; index++)
            {
                StreamState state = _activeStreams[index];
                if (state is not null && state.Playing && state.Increment > 0)
                    AdvancePhaseTo(state, selectedSample);
            }

            EmitOne(selected, selectedSample);
            cursor = selectedSample;
        }

        FlushPendingControls(end, inclusive: false);

        for (int index = 0; index < _activeStreams.Count; index++)
        {
            StreamState state = _activeStreams[index];
            if (state is not null && state.Playing && state.Increment > 0)
                AdvancePhaseTo(state, end);
        }
        _sourceSample = end;
    }

    public void Complete()
    {
        FlushPendingControls(_sourceSample, inclusive: true);
    }

    private void EmitOne(StreamState state, long sample)
    {
        long position = state.Reverse
            ? state.DataStart + (state.CommandsPerCycle - 1 - state.CommandIndex) * state.StepSize
            : state.DataStart + state.CommandIndex * state.StepSize;
        VgmPcmDataBank bank = _banks[state.BankId];
        long blockEnd = state.DataLimit;
        if (position < 0 || position >= blockEnd || position >= bank.Data.Count)
        {
            WarnOnce(
                $"read-range:{state.StreamId:X2}",
                $"VGM DAC stream 0x{state.StreamId:X2} attempted an out-of-range PCM read; stream stopped");
            state.Playing = false;
            Deactivate(state);
            return;
        }

        _emitWrite(new VgmRegisterWrite(
            sample,
            state.Device,
            state.TargetPort,
            state.TargetRegister,
            bank.Data[(int)position]));
        state.Phase -= PhaseScale;
        state.CommandIndex++;
        if (state.CommandIndex < state.CommandsPerCycle)
            return;

        if (state.Loop)
        {
            state.CommandIndex = 0;
            if (HasDacSemantics(state))
            {
                _emitControl(
                    Ym2612DacStreamControlKind.Retrigger,
                    sample,
                    state.StreamId,
                    state.Device,
                    state.FrequencyHz > 0 ? state.FrequencyHz : null);
            }
            return;
        }

        state.Playing = false;
        Deactivate(state);
        if (HasDacSemantics(state))
        {
            _pendingControls.Add(new PendingControl(
                checked(sample + 1),
                state.StreamId,
                state.Device,
                Ym2612DacStreamControlKind.NaturalEnd,
                null));
        }
    }

    private void StopOne(byte streamId)
    {
        StreamState state = _streams[streamId];
        if (state is null)
            return;

        bool hadPending = RemovePendingControls(streamId);
        if (!state.Playing && !hadPending)
            return;

        state.Playing = false;
        Deactivate(state);
        if (HasDacSemantics(state))
        {
            _emitControl(
                Ym2612DacStreamControlKind.Stop,
                _sourceSample,
                streamId,
                state.Device,
                null);
        }
    }

    private StreamState RequireConfigured(byte streamId, string command)
    {
        StreamState state = GetStream(streamId);
        if (state.Configured)
            return state;
        WarnOnce(
            $"setup:{streamId:X2}:{command}",
            $"VGM DAC stream {command} references stream 0x{streamId:X2} before setup");
        return null;
    }

    private StreamState GetStream(byte streamId)
    {
        StreamState state = _streams[streamId];
        if (state is not null)
            return state;
        state = new StreamState(streamId);
        _streams[streamId] = state;
        return state;
    }

    private static bool CanExecuteStream(StreamState state) => state.TargetChip == 0x02;

    private static bool HasDacSemantics(StreamState state) =>
        CanExecuteStream(state) && state.TargetPort == 0 && state.TargetRegister == 0x2A;

    private static long ResolveCommandCount(
        StreamState state,
        int mode,
        uint length,
        long dataStart,
        long dataLimit)
    {
        return mode switch
        {
            0x00 => state.CommandsPerCycle,
            0x01 => length,
            0x02 => (long)(((ulong)state.FrequencyHz * length) / 1000),
            0x03 => AvailableCommands(dataStart, dataLimit, state.StepSize),
            0x0F => length / state.StepSize,
            _ => 0,
        };
    }

    private static long AvailableCommands(long start, long limit, int step)
    {
        if (start < 0 || start >= limit || step <= 0)
            return 0;
        return ((limit - start - 1) / step) + 1;
    }

    private static long CalculateIncrement(uint frequencyHz)
    {
        if (frequencyHz == 0)
            return 0;
        Int128 numerator = (Int128)frequencyHz * PhaseScale + SourceRate / 2;
        Int128 increment = numerator / SourceRate;
        return increment >= long.MaxValue ? long.MaxValue : (long)increment;
    }

    private static long InitialPhase(long increment)
    {
        if (increment <= 0)
            return 0;
        long remainder = increment % PhaseScale;
        return remainder == 0 ? 0 : PhaseScale - remainder;
    }

    private static long NextEmissionOffset(StreamState state)
    {
        if (state.Phase >= PhaseScale)
            return 0;
        long threshold = PhaseScale - state.Phase;
        return (threshold + state.Increment - 1) / state.Increment;
    }

    private static void AdvancePhaseTo(StreamState state, long sample)
    {
        long elapsed = sample - state.PhaseSample;
        if (elapsed <= 0)
            return;
        Int128 phase = (Int128)state.Phase + (Int128)state.Increment * elapsed;
        state.Phase = phase > long.MaxValue ? long.MaxValue : (long)phase;
        state.PhaseSample = sample;
    }

    private static void NormalizePhase(StreamState state)
    {
        if (state.Phase >= PhaseScale)
            state.Phase %= PhaseScale;
    }

    private PendingControl FindPendingControl()
    {
        PendingControl selected = null;
        for (int index = 0; index < _pendingControls.Count; index++)
        {
            PendingControl candidate = _pendingControls[index];
            if (selected is null || candidate.Sample < selected.Sample)
                selected = candidate;
        }
        return selected;
    }

    private void FlushPendingControls(long end, bool inclusive)
    {
        PendingControl pending;
        while ((pending = FindPendingControl()) is not null
            && (pending.Sample < end || inclusive && pending.Sample <= end))
        {
            for (int index = 0; index < _activeStreams.Count; index++)
            {
                StreamState state = _activeStreams[index];
                if (state is not null && state.Playing && state.Increment > 0)
                    AdvancePhaseTo(state, pending.Sample);
            }
            _pendingControls.Remove(pending);
            _emitControl(pending.Kind, pending.Sample, pending.StreamId, pending.Device, pending.RateHz);
        }
    }

    private bool RemovePendingControls(byte streamId)
    {
        bool removed = false;
        for (int index = _pendingControls.Count - 1; index >= 0; index--)
        {
            if (_pendingControls[index].StreamId == streamId)
            {
                _pendingControls.RemoveAt(index);
                removed = true;
            }
        }
        return removed;
    }

    private void Activate(StreamState state)
    {
        if (!state.Active)
        {
            state.Active = true;
            _activeStreams.Add(state);
        }
    }

    private void Deactivate(StreamState state)
    {
        if (!state.Active)
            return;
        state.Active = false;
        _activeStreams.Remove(state);
    }

    private void WarnOnce(string key, string message)
    {
        if (_warnings.Add(key))
            _warn(message);
    }

    private sealed class StreamState
    {
        public StreamState(byte streamId)
        {
            StreamId = streamId;
        }

        public byte StreamId { get; }
        public bool Configured { get; set; }
        public byte TargetChip { get; set; }
        public int TargetInstance { get; set; }
        public int TargetPort { get; set; }
        public int TargetRegister { get; set; }
        public byte BankId { get; set; }
        public int StepSize { get; set; } = 1;
        public int StepBase { get; set; }
        public uint FrequencyHz { get; set; }
        public long Increment { get; set; }
        public bool Playing { get; set; }
        public bool Reverse { get; set; }
        public bool Loop { get; set; }
        public long DataStart { get; set; }
        public long DataLimit { get; set; }
        public long CommandIndex { get; set; }
        public long CommandsPerCycle { get; set; }
        public long Phase { get; set; }
        public long PhaseSample { get; set; }
        public bool Active { get; set; }
        public DeviceId Device => new(ChipType.Ym2612, TargetInstance);
    }

    private sealed record PendingControl(
        long Sample,
        byte StreamId,
        DeviceId Device,
        Ym2612DacStreamControlKind Kind,
        double? RateHz);
}
