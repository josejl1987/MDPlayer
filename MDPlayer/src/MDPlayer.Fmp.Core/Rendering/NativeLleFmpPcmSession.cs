using Fmp.Core.IO;
using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;
using Nise98Machine = Fmp.Core.Nise98.Nise98;

namespace Fmp.Core.Rendering;

/// <summary>
/// Native low-level-emulation FMP host-PCM session. Boots the real FMP driver
/// on Nise98 exactly like the legacy path (same LoadRun/FMPRegistPPZ8/load/play
/// sequence), but every YM2608 access crosses the clocked
/// <see cref="ClockedNise98OpnaBridge"/> and is timestamped with the
/// authoritative Nise286 cycle count, and the device is advanced only by
/// cycles actually executed (never by a drain, never by a slice budget).
///
/// Musical frame scheduling: the FMP driver's tempo comes from the real OPNA
/// timer. The session advances the chip to the next sample boundary each
/// output sample and, when the device reports a timer IRQ, runs the driver's
/// one-frame routine (the exact 0x14/0xd2 register sequence the legacy
/// sample-position path uses) so register writes land at real cycle times.
/// No sample-based timer, no legacy OpnaTimer, no interrupt vector machinery
/// is used on this path.
///
/// PPZ8: commands raised by the driver (bank loads and port writes) are
/// captured with their CPU cycle, mapped to an output sample by the exact
/// rational mapper, and applied to the shared MDSound PPZ8 renderer at that
/// sample. The PPZ8 output is delayed by the native device's fixed output
/// latency and mixed with the drained OPNA frames with pure-integer
/// arithmetic. The session is fully deterministic: the same driver execution
/// yields the same output regardless of how the render is split into slices.
/// </summary>
internal sealed class NativeLleFmpPcmSession : IFmpPcmSession
{
    /// <summary>Active machine CPU clock; 8 MHz PC-9801 (matches the clocked tests).</summary>
    private const uint CpuClockHz = 8_000_000;

    /// <summary>Startup wait reproduced from the legacy path (500 ms).</summary>
    private const int StartupWaitMs = 500;

    /// <summary>Segment holding the CPU idle spin loop (above the 0xE000 driver stack).</summary>
    private const ushort SpinSegment = 0xE800;

    private readonly FmpPlaybackContext _context;
    private readonly int _sampleRate;
    private readonly Nise98Machine _nise98;
    private readonly NativeOpnaDevice _device;
    private readonly ClockedFmpExecutionSession _clocked;
    private readonly NisePpz8CommandMapper _ppz8Mapper;
    private readonly Ppz8OutputDelayBuffer _ppz8Delay;
    private readonly OpnaPpz8IntegerMixer _mixer;
    private readonly MDSound.PPZ8 _ppz8;
    private readonly int _outputLatencyFrames;
    private readonly long _waitSamples;
    private readonly long _fadeSamples;
    private readonly long _tailSamples;
    private readonly long _maxSamples;
    private readonly List<(long Sample, int Port, int Address, int Data)> _ppz8Writes = new();
    private readonly List<(long Sample, int Bank, int Mode, byte[][] Pcm)> _ppz8Loads = new();
    private readonly Queue<(short Left, short Right)> _opnaFifo = new();

    private Register286 _regs;
    private int _step;
    private long _position;
    private int _currentLoop;
    private bool _playbackEnded;
    private bool _booted;
    private bool _waitDone;
    private bool _driverActive = true;
    private PlaybackTermination _termination;

    public NativeLleFmpPcmSession(FmpPlaybackContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _sampleRate = context.SampleRate;

        _nise98 = new Nise98Machine();
        _nise98.CpuClockFrequencyHz = CpuClockHz;
        _device = NativeOpnaDevice.Open(_sampleRate);
        _clocked = new ClockedFmpExecutionSession(_nise98, _device);
        _ppz8Mapper = new NisePpz8CommandMapper(CpuClockHz, _sampleRate);

        _outputLatencyFrames = _device.OutputLatencyFrames;
        _ppz8Delay = new Ppz8OutputDelayBuffer(_outputLatencyFrames);
        _mixer = new OpnaPpz8IntegerMixer();

        _ppz8 = new MDSound.PPZ8();
        _ppz8.Start(0, 7_987_200);

        _waitSamples = (long)_sampleRate * StartupWaitMs / 1000;
        _fadeSamples = checked((long)Math.Ceiling(context.FadeSeconds * _sampleRate));
        _tailSamples = checked((long)Math.Ceiling(context.TailSeconds * _sampleRate));
        _maxSamples = checked((long)Math.Ceiling((context.MaxDurationSeconds ?? 3600.0) * _sampleRate));
    }

    public int OutputSampleRate => _sampleRate;

    public bool IsCompleted =>
        _position >= _maxSamples || (_termination != null && _termination.IsComplete(_position));

    public bool NaturallyStopped => _termination != null && _termination.Started && _termination.StopReason == "natural_stop";
    public string StopReason =>
        _termination == null || string.IsNullOrEmpty(_termination.StopReason)
            ? "max_duration"
            : _termination.StopReason;

    public void LoadTrack(byte[] trackData, string trackFileName)
    {
        _trackData = trackData;
        _trackFileName = trackFileName;
    }

    private byte[] _trackData = Array.Empty<byte>();
    private string _trackFileName = "";

    public void Boot()
    {
        try
        {
            _nise98.Init(
                msgWrite: (msg, args) => { },
                opnaWrite: dat => { },
                fileTemp: new Fmp.Core.Nise98.fileTemp(),
                ongen: Nise98Machine.enmOngenBoardType.SpeakBoard,
                fileSystem: _context.FileSystem
            );
            _nise98.GetDos().FileSystem = _context.FileSystem;

            string fmpComPath = _context.Assets.FmpComPath;
            _nise98.LoadRun(fmpComPath, "s -s -#42", 0x2000);
            _regs = _nise98.GetRegisters();

            _nise98.GetPPZ8().FMPRegistPPZ8(out _step, out _regs);
            _nise98.GetPPZ8().SetCallBack(OnPpz8LoadPcm, OnPpz8Write);

            var dos = _nise98.GetDos();
            var enc = new myEncoding();
            string dir = Path.GetDirectoryName(_trackFileName);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            byte[] m = enc.GetSjisArrayFromString(Path.GetFileName(_trackFileName));
            dos.SetPath(dir);
            dos.LoadImage(m, (0x5000 << 4) + 0x000);

            _step = 0;
            _regs.AL = 0x02;
            _regs.DS = 0x5000;
            _regs.DX = 0x0000;
            _regs.SS = unchecked((short)0xE000);
            _regs.SP = 0x0000;
            _nise98.CallRunfunctionCall(0xd2, true, true, true, 10_000_000_000, 0);

            // Idle spin loop: EB FE (jmp $) in free memory above the driver stack.
            var mem = _nise98.GetMem();
            int spinLinear = (SpinSegment << 4) + 0;
            mem.PokeB(spinLinear, 0xEB);
            mem.PokeB(spinLinear + 1, 0xFE);

            _booted = true;
        }
        catch (Exception ex)
        {
            _booted = false;
            throw new InvalidOperationException(
                "Native FMP init failed: " + DescribeBootFailure(ex), ex);
        }
    }

    private static string DescribeBootFailure(Exception ex)
    {
        if (ex is OpnaUnsupportedCadenceException)
        {
            return "the FMP driver's boot sequence is not compatible with the native fixed-cadence " +
                   "profile: the driver polls the OPNA status registers, which perturbs the LLE serial " +
                   "frame phase away from the supported 144-master-clock cadence. " +
                   $"({ex.Message})";
        }
        return ex.Message;
    }

    private void OnPpz8LoadPcm(int bank, int mode, byte[][] pcmdata)
    {
        if (pcmdata == null) return;
        ulong cycle = _nise98.GetCPU().TotalCycles;
        long sample = _ppz8Mapper.MapSample(cycle);
        _ppz8Loads.Add((sample, bank, mode, pcmdata));
    }

    private void OnPpz8Write(int port, int address, int data)
    {
        ulong cycle = _nise98.GetCPU().TotalCycles;
        long sample = _ppz8Mapper.MapSample(cycle);
        _ppz8Writes.Add((sample, port, address, data));
    }

    public int Render(Span<short> interleavedStereo)
    {
        if ((interleavedStereo.Length & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(interleavedStereo), "span length must be even");

        _termination ??= new PlaybackTermination(_context.LoopCount, _fadeSamples, _tailSamples);

        int requested = interleavedStereo.Length / 2;
        int produced = 0;

        var scratch = new short[Math.Max(1024, _outputLatencyFrames + 64)];
        var ppz8Out = new int[2][] { new int[1], new int[1] };

        while (produced < requested && _position < _maxSamples)
        {
            if (_termination.IsComplete(_position))
                break;

            if (_waitDone)
            {
                // During fade (loop-limit termination) the driver keeps playing;
                // after a natural stop or during the tail it stops ticking while
                // the chip renders its last state (sustain) — the legacy path's
                // exact callback selection.
                if (_driverActive && _device.IrqAsserted)
                {
                    // Reading the status clears the timer IRQ flags (real chip
                    // behavior), mirroring the legacy clear-before-call flow.
                    _device.ReadStatus(_device.MasterClock, 0);
                    RunDriverFrame();
                }

                if (!_termination.Started)
                {
                    _termination.Observe(_playbackEnded, _currentLoop, _position + 1);
                    if (_termination.StopReason == "natural_stop")
                        _driverActive = false;
                }
                else if (_termination.FadeActive)
                {
                    _driverActive = true;
                }
                else
                {
                    _driverActive = false;
                }
            }
            else
            {
                _waitDone = _position + 1 >= _waitSamples;
            }

            // Advance the device by exactly one output sample of master clock,
            // so the drain yields exactly this output frame (latency is inside
            // the resampler; PPZ8 is delayed by the same amount for alignment).
            AdvanceToSampleBoundary(_position + 1);

            // Drain whatever the device has produced up to this clock.
            int drained;
            do
            {
                drained = _device.DrainAudio(scratch, scratch.Length / 2);
                for (int i = 0; i < drained; i++)
                    _opnaFifo.Enqueue((scratch[i * 2], scratch[i * 2 + 1]));
            } while (drained > 0);

            (short opnaL, short opnaR) = _opnaFifo.Count > 0 ? _opnaFifo.Dequeue() : ((short)0, (short)0);

            // PPZ8: apply commands scheduled at this sample, render one frame.
            ApplyPpz8At(_position);
            _ppz8.Update(0, ppz8Out, 1);
            _ppz8Delay.Push((short)Math.Clamp(ppz8Out[0][0], short.MinValue, short.MaxValue),
                            (short)Math.Clamp(ppz8Out[1][0], short.MinValue, short.MaxValue),
                            out short ppz8L, out short ppz8R);

            // Apply fade to the final mix (exact FmpRenderer fade arithmetic).
            int l = opnaL, r = opnaR;
            if (_termination.FadeActive)
            {
                long fadePos = _position - _termination.FadeStartSample;
                if (fadePos < 0) { /* before boundary: un-faded */ }
                else if (fadePos < _fadeSamples)
                {
                    double gain = _fadeSamples == 0 ? 0 : 1.0 - (double)fadePos / _fadeSamples;
                    l = (int)(l * gain);
                    r = (int)(r * gain);
                }
                else { l = 0; r = 0; }
            }
            _mixer.Mix(l, r, ppz8L, ppz8R, out short left, out short right);
            interleavedStereo[produced * 2] = left;
            interleavedStereo[produced * 2 + 1] = right;
            produced++;
            _position++;
        }

        return produced;
    }

    private void AdvanceToSampleBoundary(long sample)
    {
        if (sample < 0) return;
        ulong targetClock = (ulong)((UInt128)sample * NiseOpnaClockMapper.Ym2608MasterClockHz / (ulong)_sampleRate);
        ulong current = _device.MasterClock;
        if (targetClock <= current)
            return;

        // Execute a pure CPU spin loop so cycles actually executed keep the
        // device clock in sync (driver writes inside frame calls are then
        // timestamped at or after the device's current time).
        ulong deltaCycles = (ulong)(((UInt128)(targetClock - current) * CpuClockHz) / NiseOpnaClockMapper.Ym2608MasterClockHz) + 1;
        _regs.CS = unchecked((short)SpinSegment);
        _regs.IP = 0;
        while (deltaCycles > 0)
        {
            int chunk = (int)Math.Min(deltaCycles, 1_000_000);
            _clocked.ExecuteSlice(chunk);
            deltaCycles -= (ulong)chunk;
        }
        // Snap to the exact boundary clock (monotonic; never goes backward).
        _device.AdvanceTo(targetClock);
    }

    /// <summary>
    /// Runs the driver's one-frame routine with the exact register sequence of
    /// the legacy FmpRuntime.Tick path (0x14, then 0xd2 status and loop-count
    /// queries), so PPZ8/OPNA writes inside the routine are timestamped with
    /// real CPU cycles.
    /// </summary>
    private void RunDriverFrame()
    {
        _regs.SS = unchecked((short)0xE000);
        _regs.SP = 0x0000;
        _nise98.CallRunfunctionCall(0x14);

        _regs.AX = 0x0004;
        _regs.SS = unchecked((short)0xE000);
        _regs.SP = 0x0000;
        _nise98.CallRunfunctionCall(0xd2);
        _playbackEnded = (_regs.AX == 0);

        _regs.AX = 0x1104;
        _regs.SS = unchecked((short)0xE000);
        _regs.SP = 0x0000;
        _nise98.CallRunfunctionCall(0xd2);
        int ptr = ((ushort)0x2000 << 4) + (ushort)_regs.AX;
        int loopCounter = _nise98.GetMem().PeekB(ptr + 0x17);
        if (loopCounter != _currentLoop)
        {
            _currentLoop = loopCounter;
        }
    }

    private void ApplyPpz8At(long sample)
    {
        if (_ppz8Loads.Count > 0)
        {
            int applied = 0;
            foreach (var load in _ppz8Loads)
            {
                if (load.Sample > sample) break;
                _ppz8.LoadPcm(0, (byte)load.Bank, (byte)load.Mode, load.Pcm);
                applied++;
            }
            if (applied > 0) _ppz8Loads.RemoveRange(0, applied);
        }

        if (_ppz8Writes.Count > 0)
        {
            int applied = 0;
            foreach (var w in _ppz8Writes)
            {
                if (w.Sample > sample) break;
                _ppz8.Write(0, w.Port, w.Address, w.Data);
                applied++;
            }
            if (applied > 0) _ppz8Writes.RemoveRange(0, applied);
        }
    }

    public void Dispose()
    {
        try { _ppz8.Stop(0); } catch { }
        // The clocked session owns and disposes the native device.
        _clocked.Dispose();
    }
}