using Fmp.Core.IO;
using Fmp.Core.Nise98;
using Fmp.Core.Playback.Opna;

namespace Fmp.Core.Rendering;

/// <summary>
/// Native-audio FMP host-PCM session: a two-pass trace-driven renderer.
///
///  * Pass 1 (control capture) runs the existing legacy MDSound FMP session to
///    completion with an event tap enabled, discarding its PCM. Every YM2608
///    write and PPZ8 command is recorded at its authoritative Nise286 cycle in
///    one global sequence; banks are captured immutably (content-hash
///    deduplicated); loop/fade/tail/termination come exactly from that session.
///  * Pass 2 (native audio replay) drives the native YM2608 device offline from
///    the captured trace (no Nise98 execution, no status/IRQ reads) and the
///    shared PPZ8 renderer, then mixes the two streams with the fixed integer
///    mixer and applies the captured output envelope.
///
/// MDSound/FMP control timing is authoritative; the native device only renders
/// audio. This is a deliberate, documented compromise — not full hardware-driven
/// LLE execution.
/// </summary>
internal sealed class NativeAudioFmpPcmSession : IFmpPcmSession
{
    /// <summary>Active machine CPU clock; 8 MHz PC-9801 (matches the clocked tests).</summary>
    private const uint CpuClockHz = 8_000_000;

    private readonly FmpPlaybackContext _context;
    private readonly int _sampleRate;
    private readonly FmpExecutionCaptureBuilder _builder;
    private readonly IClockedOpnaDevice _device; // opened up-front: validates native availability
    private readonly bool _ownsDevice;
    private readonly Queue<(short Left, short Right)> _opnaFifo = new();
    private readonly short[] _opnaScratch;
    private readonly short[] _ppz8One = new short[2];
    private readonly NativeAudioIntegerMixer _mixer = new();

    private byte[] _trackData = Array.Empty<byte>();
    private string _trackFileName = "";
    private LegacyMdsoundFmpPcmSession _legacy;
    private FmpExecutionCapture _capture;
    private NativeOpnaTraceRenderer _opna;
    private Ppz8TraceRenderer _ppz8;
    private long _fadeStart;
    private long _fadeEnd;
    private bool _booted;
    private long _pos;

    /// <summary>
    /// Cooperative cancellation. When set, the capture pass, replay and the
    /// final drain all observe it and stop immediately (throwing
    /// <see cref="OperationCanceledException"/>); a cancelled capture never
    /// starts replay and a cancelled replay never reports completion. No
    /// background work is ever left running.
    /// </summary>
    internal System.Threading.CancellationToken CancelToken { get; set; }

    /// <summary>
    /// Test-only replay mute seam (Workstream H — feature presence). Bit 0 mutes
    /// the OPNA contribution, bit 1 mutes the PPZ8 contribution. The default
    /// (0) leaves the production path byte-identical. Only the *replay* output
    /// is affected; capture is unaffected. Exposed for feature-presence
    /// validation, never as a public stem-rendering feature.
    /// </summary>
    internal byte ReplayMuteMask { get; set; }

    public NativeAudioFmpPcmSession(FmpPlaybackContext context)
        : this(context, NativeOpnaDevice.Open(context!.SampleRate), ownsDevice: true)
    {
    }

    /// <summary>
    /// Test-seam constructor: accepts an externally-provided device (e.g. a
    /// validation recording wrapper) instead of opening the native library
    /// directly. The production path uses the single-argument constructor and
    /// is unaffected. Ownership of the injected device transfers to the session
    /// when <paramref name="ownsDevice"/> is true.
    /// </summary>
    internal NativeAudioFmpPcmSession(FmpPlaybackContext context, IClockedOpnaDevice device, bool ownsDevice)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _ownsDevice = ownsDevice;
        _sampleRate = context.SampleRate;
        _builder = new FmpExecutionCaptureBuilder(_sampleRate, CpuClockHz);
        // Validate/resolve the native library before any capture work: an
        // unavailable native library must fail clearly without a wasted capture.
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _opnaScratch = new short[NativeOpnaTraceRenderer.DefaultChunkFrames * 2];
    }

    public int OutputSampleRate => _sampleRate;

    public bool IsCompleted => _booted && _capture != null && _pos >= _capture.FinalOutputFrame;
    public bool NaturallyStopped =>
        _capture != null && string.Equals(_capture.TerminationReason, "natural_stop", System.StringComparison.Ordinal);
    public string StopReason =>
        _capture == null || string.IsNullOrEmpty(_capture.TerminationReason)
            ? "max_duration"
            : _capture.TerminationReason;

    public void LoadTrack(byte[] trackData, string trackFileName)
    {
        _trackData = trackData;
        _trackFileName = trackFileName;
    }

    public void Boot()
    {
        if (_booted) return;

        // ---- Pass 1: control capture via the proven legacy path ----
        _legacy = new LegacyMdsoundFmpPcmSession(_context, _builder);
        _legacy.CpuClockFrequencyHz = CpuClockHz;
        _legacy.LoadTrack(_trackData, _trackFileName);
        _legacy.Boot();

        var discard = new short[4096 * 2];
        // Render at least once so the legacy session's termination policy is
        // created, then continue until the legacy session reports completion.
        do
        {
            CancelToken.ThrowIfCancellationRequested();
            int n = _legacy.Render(discard);
            if (n == 0)
                break; // legacy reported completion with no output; stop capture
        }
        while (!_legacy.IsCompleted);

        long fadeLen = checked((long)Math.Ceiling(_context.FadeSeconds * _sampleRate));
        var term = _legacy.TerminationState;
        bool fadeActive = term != null && term.FadeActive;
        _fadeStart = fadeActive ? term.FadeStartSample : 0;
        _fadeEnd = fadeActive ? checked(term.FadeStartSample + fadeLen) : 0;
        long tailEnd = term != null ? term.StopAtSample : _legacy.TotalSamples;

        _builder.SetFinalCpuCycle(_legacy.FinalCpuCycle);
        _capture = _builder.Finish(
            finalOutputFrame: _legacy.TotalSamples,
            fadeStartFrame: _fadeStart,
            fadeEndFrame: _fadeEnd,
            tailEndFrame: tailEnd,
            loopCount: _legacy.CurrentLoop,
            terminationReason: _legacy.StopReason);

        // Release the capture session (its PCM and driver are no longer needed).
        _legacy.Dispose();
        _legacy = null;

        // ---- Pass 2: replay state ----
        _device.ResetChip();
        _opna = new NativeOpnaTraceRenderer(_device, _capture.Events, CpuClockHz, _sampleRate);
        _ppz8 = new Ppz8TraceRenderer(_capture.Events, _capture.Ppz8Banks, CpuClockHz, _sampleRate, _device.OutputLatencyFrames);
        _pos = 0;
        _booted = true;
    }

    public int Render(Span<short> interleavedStereo)
    {
        if (!_booted)
            throw new InvalidOperationException("NativeAudioFmpPcmSession.Render called before Boot().");
        if ((interleavedStereo.Length & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(interleavedStereo), "span length must be even");

        int requested = interleavedStereo.Length / 2;
        if (requested == 0)
            return 0;

        int produced = 0;
        while (produced < requested && _pos < _capture.FinalOutputFrame)
        {
            CancelToken.ThrowIfCancellationRequested();
            // Refill the OPNA FIFO in bounded host-output chunks when low.
            if (_opnaFifo.Count == 0)
                RefillOpna();

            short opnaL = 0, opnaR = 0;
            if ((ReplayMuteMask & 1) == 0 && _opnaFifo.Count > 0)
            {
                var f = _opnaFifo.Dequeue();
                opnaL = f.Left;
                opnaR = f.Right;
            }
            else if (_opnaFifo.Count > 0)
            {
                // Muted OPNA: consume the FIFO but contribute silence.
                _opnaFifo.Dequeue();
            }

            // Generate the aligned PPZ8 frame for this host position.
            short ppz8L = 0, ppz8R = 0;
            if ((ReplayMuteMask & 2) == 0)
            {
                int ppz8Got = _ppz8.RenderFrames(_pos, 1, _ppz8One);
                ppz8L = ppz8Got > 0 ? _ppz8One[0] : (short)0;
                ppz8R = ppz8Got > 0 ? _ppz8One[1] : (short)0;
            }

            _mixer.Mix(opnaL, opnaR, ppz8L, ppz8R, out short left, out short right);
            ApplyEnvelope(_pos, ref left, ref right);

            interleavedStereo[produced * 2] = left;
            interleavedStereo[produced * 2 + 1] = right;
            produced++;
            _pos++;
        }

        return produced;
    }

    private void RefillOpna()
    {
        // Advance the device to the next chunk boundary (bounded host-output chunk).
        long chunkEnd = Math.Min(_pos + NativeOpnaTraceRenderer.DefaultChunkFrames, _capture.FinalOutputFrame);
        ulong clockBefore = _opna.MasterClock;
        int cursorBefore = _opna.EventCursor;

        _opna.ReplayToChunkBoundary(chunkEnd);
        int drained = _opna.DrainAudio(_opnaScratch, _opnaScratch.Length / 2);
        for (int i = 0; i < drained; i++)
            _opnaFifo.Enqueue((_opnaScratch[i * 2], _opnaScratch[i * 2 + 1]));

        if (drained == 0
            && _opna.MasterClock == clockBefore
            && _opna.EventCursor == cursorBefore
            && _pos < _capture.FinalOutputFrame)
        {
            // No chip time advanced, no events consumed and no audio: a genuine
            // stall. Report it — never retry or busy-loop.
            throw new NativeFmpNoProgressException(
                $"eventCursor={_opna.EventCursor} lastCpuCycle={_capture.FinalCpuCycle} " +
                $"nativeMasterClock={_opna.MasterClock} producedFrames={_pos} " +
                $"requestedFrames={chunkEnd} availableNativeFrames={drained}");
        }
    }

    private void ApplyEnvelope(long pos, ref short left, ref short right)
    {
        if (_fadeEnd <= _fadeStart)
            return; // no fade — natural tail rings through naturally
        if (pos < _fadeStart)
            return; // before the fade region: un-faded
        if (pos >= _fadeEnd)
        {
            left = 0;
            right = 0;
            return;
        }
        double gain = 1.0 - (double)(pos - _fadeStart) / (_fadeEnd - _fadeStart);
        left = (short)Math.Clamp((int)(left * gain), short.MinValue, short.MaxValue);
        right = (short)Math.Clamp((int)(right * gain), short.MinValue, short.MaxValue);
    }

    public void Dispose()
    {
        if (_legacy != null)
        {
            _legacy.Dispose();
            _legacy = null;
        }
        _ppz8?.Dispose();
        _opna?.Dispose(); // disposes the native device
        if (_ownsDevice)
            _device?.Dispose();
        _builder.Dispose();
    }
}
