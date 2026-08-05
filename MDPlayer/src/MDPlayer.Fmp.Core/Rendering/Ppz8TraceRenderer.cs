namespace Fmp.Core.Rendering;

using MDSound;

/// <summary>
/// Pass-2 PPZ8 replay. Owns the shared MDSound PPZ8 renderer, the captured
/// immutable bank snapshots, the PPZ8 event cursor and the master-clock→output
/// frame mapper, plus the output-latency delay line and scratch buffers.
/// Produces host-aligned stereo PPZ8 frames for a contiguous output window,
/// applying each captured command before the output frame it maps to and
/// delaying the stream by the native output latency so OPNA and PPZ8 stay
/// aligned. It never opens the original bank file and never touches OPNA time.
/// </summary>
internal sealed class Ppz8TraceRenderer : IDisposable
{
    private readonly PPZ8 _ppz8;
    private readonly IReadOnlyDictionary<int, Ppz8BankSnapshot> _banksById;
    private readonly IReadOnlyList<FmpCapturedEvent> _events;
    private readonly OpnaMasterClockFrameMapper _frameMapper;
    private readonly Ppz8OutputDelayBuffer _delay;
    private readonly int _latencyFrames;

    private int _cursor;
    private long _generated; // generated+delayed frames pushed so far
    private readonly int[][] _ppz8Out;

    public Ppz8TraceRenderer(
        IReadOnlyList<FmpCapturedEvent> events,
        IReadOnlyList<Ppz8BankSnapshot> banks,
        int sampleRate,
        int latencyFrames)
    {
        _events = events;
        _banksById = banks.ToDictionary(b => b.BankId);
        _frameMapper = new OpnaMasterClockFrameMapper(sampleRate);
        _latencyFrames = Math.Max(0, latencyFrames);
        _delay = new Ppz8OutputDelayBuffer(_latencyFrames);
        _ppz8 = new PPZ8();
        _ppz8.Start(0, (uint)OpnaMasterClock.Hz);
        _ppz8Out = new int[2][] { new int[1], new int[1] };
    }

    /// <summary>
    /// Fills <paramref name="outStereo"/> (length <c>frameCount*2</c>) with the
    /// host-aligned PPZ8 frames for output positions
    /// <c>[hostStartFrame, hostStartFrame+frameCount)</c>. Zero-length windows
    /// return 0. There is no allocation; generated frames not yet aligned are
    /// retained in the fixed delay line across calls.
    /// </summary>
    public int RenderFrames(long hostStartFrame, int frameCount, Span<short> outStereo)
    {
        if (frameCount <= 0)
            return 0;
        if (outStereo.Length < frameCount * 2)
            throw new ArgumentException("outStereo too small", nameof(outStereo));

        long end = hostStartFrame + frameCount;
        long need = end + _latencyFrames; // generated frames required
        int written = 0;
        while (_generated < need)
        {
            long g = _generated;
            ApplyEventsAtGeneratedFrame(g);
            _ppz8.Update(0, _ppz8Out, 1);
            short gl = (short)Math.Clamp(_ppz8Out[0][0], short.MinValue, short.MaxValue);
            short gr = (short)Math.Clamp(_ppz8Out[1][0], short.MinValue, short.MaxValue);
            _delay.Push(gl, gr, out short hostL, out short hostR);
            long hostFrame = g - _latencyFrames;
            if (hostFrame >= hostStartFrame && hostFrame < end)
            {
                int slot = (int)(hostFrame - hostStartFrame);
                outStereo[slot * 2] = hostL;
                outStereo[slot * 2 + 1] = hostR;
                written++;
            }
            _generated = checked(_generated + 1);
        }
        return written;
    }

    private void ApplyEventsAtGeneratedFrame(long generatedFrame)
    {
        // Apply every remaining event whose mapped output frame equals the
        // generated frame we are about to produce, in global sequence order.
        int applied = 0;
        for (; _cursor < _events.Count; _cursor++)
        {
            var e = _events[_cursor];
            if (e is not CapturedPpz8Command cmd)
                continue; // OPNA events are not consumed by the PPZ8 cursor
            long frame = _frameMapper.MapOutputFrame(cmd.OpnaMasterClock);
            if (frame > generatedFrame)
                break;
            if (frame < generatedFrame)
                continue; // mapped earlier; already handled by a prior pass
            ApplyCommand(cmd);
            applied++;
        }
    }

    private void ApplyCommand(CapturedPpz8Command cmd)
    {
        if (cmd.BankId >= 0)
        {
            if (!_banksById.TryGetValue(cmd.BankId, out var bank))
                throw new InvalidOperationException($"captured PPZ8 bank {cmd.BankId} not found in snapshot");
            var channels = new byte[bank.ChannelLengths.Length][];
            int off = 0;
            for (int i = 0; i < channels.Length; i++)
            {
                channels[i] = new byte[bank.ChannelLengths[i]];
                Array.Copy(bank.Data, off, channels[i], 0, bank.ChannelLengths[i]);
                off += bank.ChannelLengths[i];
            }
            _ppz8.LoadPcm(0, (byte)cmd.Port, (byte)cmd.Address, channels);
        }
        else
        {
            _ppz8.Write(0, cmd.Port, cmd.Address, cmd.Data);
        }
    }

    public void Dispose()
    {
        try { _ppz8.Stop(0); } catch { }
    }
}
