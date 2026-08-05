using Fmp.Core.Audio;
using Fmp.Core.IO;
using Fmp.Core.Nise98;
using Fmp.Core.Tracing;
using musicDriverInterface;

namespace Fmp.Core.Rendering;

/// <summary>
/// Portable FMP runtime that owns the Nise98 emulator, loads FMP.COM,
/// drives OVI playback, and emits chip events through IFmpChipSink.
/// </summary>
internal class FmpRuntime
{
    private readonly Nise98.Nise98 _nise98;
    private readonly fileTemp _fileTemp;
    private readonly IFmpChipSink _chipSink;
    private readonly FmpRuntimeAssets _assets;
    private readonly IFmpFileSystem _fileSystem;
    private Register286 _regs;
    private int _step;
    private long _samplePosition;
    private bool _running;
    private bool _playbackEnded;
    private int _loopCount;
    private int _loopCounter;
    private string _trackFileName = "";

    // Absolute YM2608 master-clock timeline. Advanced by one control tick at
    // the control tick rate (output sample rate, since FmpRuntime.Tick is
    // invoked once per output frame) so the accumulated master clock tracks
    // real elapsed musical time. This is the authoritative capture time
    // coordinate — never Nise286 instruction cycles (which are reset per
    // driver call and do not advance during startup waits or timer ticks).
    private ulong _opnaMasterClock;
    private ulong _clockRemainder;
    private int _controlTickRate;
    public bool PlaybackEnded => _playbackEnded;
    public int LoopCount => _loopCount;
    public int CurrentLoop => _loopCounter;
    public long SamplePosition => _samplePosition;
    public bool IsStopped => !_running;
    public void Stop() => _running = false;
    public string LastError { get; private set; }

    /// <summary>
    /// Optional trace writer for register event logging.
    /// Set before calling <see cref="Initialize"/> to capture boot events.
    /// </summary>
    public RegisterTraceWriter TraceWriter { get; set; }

    /// <summary>
    /// Optional Pass-1 capture tap. Defaults to null (disabled): the ordinary
    /// MDSound path performs no capture allocation. When set before
    /// <see cref="Initialize"/>, every YM2608 write and PPZ8 command is
    /// recorded at its authoritative absolute YM2608 master-clock position.
    /// </summary>
    public IFmpExecutionCaptureSink CaptureSink { get; set; }

    /// <summary>
    /// Control tick rate (Hz) for the absolute OPNA master-clock timeline —
    /// the rate at which <see cref="Tick"/> advances. Because the renderer
    /// invokes <see cref="Tick"/> once per output stereo frame, this is the
    /// output sample rate. Must be set before any capture writes occur.
    /// </summary>
    public int ControlTickRate
    {
        get => _controlTickRate;
        set
        {
            if (value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "control tick rate must be positive");
            _controlTickRate = value;
        }
    }

    /// <summary>Absolute YM2608 master-clock position at the current control tick.</summary>
    public ulong OpnaMasterClock => _opnaMasterClock;

    /// <summary>Absolute YM2608 master-clock position of the most recent tick.</summary>
    internal ulong FinalOpnaMasterClock => _opnaMasterClock;

    /// <summary>
    /// Optional explicit CPU clock frequency (Hz) applied before the driver
    /// boots. Affects Nise286 emulation timing (wait-loop durations). It does
    /// NOT drive the capture or replay timeline — event timestamps come from
    /// the absolute YM2608 master clock accumulated at the control tick rate.
    /// </summary>
    public uint? CpuClockFrequencyHz { get; set; }

    public FmpRuntime(IFmpChipSink chipSink, FmpRuntimeAssets assets, IFmpFileSystem fileSystem = null)
    {
        _chipSink = chipSink;
        _assets = assets;
        _fileSystem = fileSystem;
        _fileTemp = new fileTemp();
        _nise98 = new Nise98.Nise98();
        _step = 0;
        _running = false;
        _playbackEnded = false;
        _loopCount = 2;
        _loopCounter = 0;
        LastError = "";
    }

    /// <summary>
    /// Initialize the FMP runtime: boot FMP.COM, register PPZ8, load the track.
    /// </summary>
    public void Initialize(ReadOnlyMemory<byte> trackData, string trackFileName)
    {
        try
        {
            if (CpuClockFrequencyHz is uint cpuHz && cpuHz != 0)
                _nise98.CpuClockFrequencyHz = cpuHz;
            _trackFileName = Path.GetFileName(trackFileName) ?? "track";
            // Initialize Nise98 with callbacks
            _nise98.Init(
                msgWrite: OnMsgWrite,
                opnaWrite: OnOpnaWrite,
                fileTemp: _fileTemp,
                ongen: Nise98.Nise98.enmOngenBoardType.SpeakBoard,
                fileSystem: _fileSystem
            );

            // Keep the explicit assignment for callers that replace the DOS
            // object during initialization; normal construction already wires
            // the resolver through Nise98.Init.
            _nise98.GetDos().FileSystem = _fileSystem;

            // Boot FMP.COM
            string fmpComPath = _assets.FmpComPath;
            _nise98.LoadRun(fmpComPath, "s -s -#42", 0x2000);
            _regs = _nise98.GetRegisters();

            // Register PPZ8
            var mem = _nise98.GetMem();
            _nise98.GetPPZ8().FMPRegistPPZ8(out _step, out _regs);
            _nise98.GetPPZ8().SetCallBack(OnPpz8LoadPcm, OnPpz8Write);

            // Load and play track
            var dos2 = _nise98.GetDos();
            var enc = new myEncoding();
            string dir = Path.GetDirectoryName(trackFileName);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            byte[] m = enc.GetSjisArrayFromString(Path.GetFileName(trackFileName));
            dos2.SetPath(dir);
            dos2.LoadImage(m, (0x5000 << 4) + 0x000);

            _step = 0;
            _regs.AL = 0x02;
            _regs.DS = 0x5000;
            _regs.DX = 0x0000;
            _regs.SS = unchecked((short)0xE000);
            _regs.SP = 0x0000;
            _nise98.CallRunfunctionCall(0xd2, true, true, true, 10_000_000_000, 0_000);

            _running = true;
            _samplePosition = 0;
            LastError = "";
        }
        catch (Exception ex)
        {
            _running = false;
            LastError = $"FMP init failed: {ex.Message}";
            throw;
        }
    }

    private int _waitSamplesRemaining = 0;

    /// <summary>
    /// Skip the first N output samples before allowing the FMP driver to tick,
    /// reproducing the original MDPlayer WaitTime startup latency.
    /// </summary>
    public void SetWaitSamples(int samples)
    {
        _waitSamplesRemaining = samples;
    }

    /// <summary>
    /// Advance one sample. Called once per output sample by the renderer.
    /// </summary>
    public void Tick()
    {
        // Advance the absolute master-clock timeline every control tick — this
        // includes the startup-wait phase (which returns below without running
        // the driver), so the captured timeline represents real elapsed time.
        AdvanceControlTick();

        if (!_running) return;
        if (_waitSamplesRemaining > 0)
        {
            _waitSamplesRemaining--;
            _samplePosition++;
            return;
        }

        try
        {
            _nise98.Runtimer();
            bool timerFired = _nise98.IntTimer();
            if (!timerFired)
            {
                _samplePosition++;
                return;
            }

            _regs.SS = unchecked((short)0xE000);
            _regs.SP = 0x0000;
            _nise98.CallRunfunctionCall(0x14);

            // Check playback state (AX==0 means the FMP driver reached a loop
            // boundary). The original MDPlayer sets a Stopped flag but does NOT
            // halt the driver — it continues calling oneFrameProc so the driver
            // can loop and replay. The renderer's loop-count logic handles the
            // actual stop decision. We must NOT return here, otherwise only one
            // loop renders.
            _regs.AX = 0x0004;
            _regs.SS = unchecked((short)0xE000);
            _regs.SP = 0x0000;
            _nise98.CallRunfunctionCall(0xd2);
            _playbackEnded = (_regs.AX == 0);

            // Check loop count
            _regs.AX = 0x1104;
            _regs.SS = unchecked((short)0xE000);
            _regs.SP = 0x0000;
            _nise98.CallRunfunctionCall(0xd2);
            int ptr = ((ushort)0x2000 << 4) + (ushort)_regs.AX;
            int fmpSloopC = _nise98.GetMem().PeekB(ptr + 0x17);
            _loopCounter = fmpSloopC;

            _samplePosition++;
        }
        catch (Exception ex)
        {
            _running = false;
            LastError = $"FMP tick error: {ex.Message}";
            Fmp.Core.Nise98.Log.ForcedWrite(ex);
        }
    }

    private void OnMsgWrite(string msg, object[] args)
    {
        // Forward messages to the logger
        if (args != null && args.Length > 0)
            Fmp.Core.Nise98.Log.Write(LogLevel.INFORMATION, string.Format(msg, args));
        else
            Fmp.Core.Nise98.Log.Write(LogLevel.INFORMATION, msg);
    }

    private int _opnaWriteCount = 0;
    private void OnOpnaWrite(ChipDatum dat)
    {
        _opnaWriteCount++;
        byte port = (byte)((byte)dat.port == 0x8a ? 0 : 1);
        _chipSink.WriteYm2608(0, port, dat.address, dat.data, _samplePosition);
        if (CaptureSink != null)
            CaptureSink.CaptureOpnaWrite(_opnaMasterClock, port, (byte)dat.address, (byte)dat.data);
        TraceWriter?.WriteOpna(port, dat.address, dat.data, _samplePosition);
    }

    /// <summary>
    /// Advances the absolute YM2608 master-clock timeline by one control tick:
    /// accumulate <see cref="OpnaMasterClock.Hz"/> / <paramref name="_controlTickRate"/>
    /// using integer quotient/remainder arithmetic (never floating point), so
    /// the accumulated master clock tracks real elapsed musical time without
    /// drift. The remainder carries exactly across ticks.
    /// </summary>
    private void AdvanceControlTick()
    {
        if (_controlTickRate <= 0)
            return;
        UInt128 numerator = (UInt128)_clockRemainder + Fmp.Core.Rendering.OpnaMasterClock.Hz;
        _opnaMasterClock += (ulong)(numerator / (ulong)_controlTickRate);
        _clockRemainder = (ulong)(numerator % (ulong)_controlTickRate);
    }

    public int OpnaWriteCount => _opnaWriteCount;

    private void OnPpz8LoadPcm(int bank, int mode, byte[][] pcmdata)
    {
        if (pcmdata == null) return;
        var samples = new ReadOnlyMemory<byte>[pcmdata.Length];
        long totalBytes = 0;
        for (int i = 0; i < pcmdata.Length; i++)
        {
            samples[i] = pcmdata[i].AsMemory();
            totalBytes += pcmdata[i].Length;
        }
        _chipSink.LoadPpz8Bank(bank, mode, samples, _samplePosition);

        if (CaptureSink != null)
        {
            // Capture the immutable bank content by content-hash (deduplicated),
            // then emit a bank-load command referencing the capture-local bank id.
            int bankId = CaptureSink.CapturePpz8Bank(_trackFileName, bank, mode, pcmdata);
            CaptureSink.CapturePpz8Command(_opnaMasterClock, new Ppz8Command(bank, mode, 0, bankId));
        }

        // Compute SHA-256 of concatenated bank data for trace
        if (TraceWriter != null)
        {
            string sha256 = ComputePpz8BankSha256(pcmdata);
            TraceWriter.WritePpz8Load(bank, mode, pcmdata.Length, totalBytes, sha256, _samplePosition);
        }
    }

    private static string ComputePpz8BankSha256(byte[][] pcmdata)
    {
        using var hash = System.Security.Cryptography.SHA256.Create();
        for (int i = 0; i < pcmdata.Length; i++)
            hash.TransformBlock(pcmdata[i], 0, pcmdata[i].Length, null, 0);
        hash.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(hash.Hash ?? []);
    }

    private void OnPpz8Write(int port, int adr, int data)
    {
        _chipSink.WritePpz8(port, adr, data, _samplePosition);
        if (CaptureSink != null)
            CaptureSink.CapturePpz8Command(_opnaMasterClock, new Ppz8Command(port, adr, data));
        TraceWriter?.WritePpz8Write(port, adr, data, _samplePosition);
    }
}
