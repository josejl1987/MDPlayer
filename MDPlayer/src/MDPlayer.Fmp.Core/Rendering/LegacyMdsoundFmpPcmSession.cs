using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;

namespace Fmp.Core.Rendering;

/// <summary>
/// Existing MDSound host-PCM session (the default legacy path). Reuses the same
/// <see cref="FmpRuntime"/> + <see cref="MdsoundFmpChipSink"/> machinery and the
/// exact FmpRenderer register ordering, sample-position semantics, OpnaTimer
/// behavior, PPZ8 command timing, gain/pan, integer rounding, clipping,
/// termination, output length and PCM bytes. The default render therefore
/// remains byte-identical to the pre-Prompt-8 branch.
/// </summary>
internal sealed class LegacyMdsoundFmpPcmSession : IFmpPcmSession
{
    private readonly FmpRuntime _runtime;
    private readonly MdsoundFmpChipSink _sink;
    private readonly int _sampleRate;
    private readonly int _bufferSize;
    private readonly FmpPlaybackContext _context;
    private readonly long _fadeSamples;
    private readonly long _tailSamples;
    private readonly long _maxSamples;

    private readonly IFmpExecutionCaptureSink _captureSink;

    /// <summary>
    /// Optional explicit CPU clock (Hz) applied to the FMP runtime before boot;
    /// used by the capture pass so replay's exact mappers agree with the
    /// recorded authoritative cycle counts.
    /// </summary>
    internal uint? CpuClockFrequencyHz
    {
        set => _runtime.CpuClockFrequencyHz = value;
    }

    private long _totalSamples;
    private int _currentLoop;
    private bool _runtimeStopped;
    private bool _booted;

    public LegacyMdsoundFmpPcmSession(FmpPlaybackContext context, IFmpExecutionCaptureSink captureSink = null)
    {
        _captureSink = captureSink;
        _context = context;
        _sampleRate = context.SampleRate;
        _bufferSize = _sampleRate / 100; // 10ms buffer, matching FmpRenderer
        _sink = new MdsoundFmpChipSink(_sampleRate, ssgGainDb: context.SsgGainDb);
        _runtime = new FmpRuntime(_sink, context.Assets, context.FileSystem);
        // The control tick rate equals the output sample rate: the renderer
        // invokes FmpRuntime.Tick() once per output stereo frame, and each such
        // tick advances the absolute YM2608 master-clock timeline.
        _runtime.ControlTickRate = _sampleRate;
        if (captureSink != null)
            _runtime.CaptureSink = captureSink;
        _fadeSamples = checked((long)Math.Ceiling(context.FadeSeconds * _sampleRate));
        _tailSamples = checked((long)Math.Ceiling(context.TailSeconds * _sampleRate));
        _maxSamples = checked((long)Math.Ceiling((context.MaxDurationSeconds ?? 3600.0) * _sampleRate));
    }

    public int OutputSampleRate => _sampleRate;
    public bool IsCompleted => _totalSamples >= _maxSamples || _termination.IsComplete(_totalSamples) || _runtimeStopped;

    public bool NaturallyStopped => _termination != null && _termination.Started;
    public string StopReason =>
        _termination == null || string.IsNullOrEmpty(_termination.StopReason)
            ? "max_duration"
            : _termination.StopReason;

    public void Boot()
    {
        _sink.Start();
        _runtime.Initialize(_trackData, _trackFileName);
        _runtime.SetWaitSamples(_sampleRate * 500 / 1000);
        _booted = true;
    }

    private byte[] _trackData = Array.Empty<byte>();
    private string _trackFileName = "";

    public void LoadTrack(byte[] trackData, string trackFileName)
    {
        _trackData = trackData;
        _trackFileName = trackFileName;
    }

    private PlaybackTermination _termination;

    public int Render(Span<short> interleavedStereo)
    {
        if ((interleavedStereo.Length & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(interleavedStereo), "span length must be even");

        // Termination is (re)created on first render with the configured policy;
        // FmpRenderer creates it per render with the max-duration safety cap.
        _termination ??= new PlaybackTermination(_context.LoopCount, _fadeSamples, _tailSamples);
        int requested = interleavedStereo.Length / 2;
        if (requested == 0)
            return 0;

        int produced = 0;
        var outputs = new int[2][] { new int[_bufferSize], new int[_bufferSize] };
        var interleaved = new short[_bufferSize * 2];

        while (produced < requested && _totalSamples < _maxSamples)
        {
            if (_termination.IsComplete(_totalSamples))
            {
                _runtime.Stop();
                break;
            }
            if (_runtime.IsStopped && !_termination.Started)
                break;

            int samplesThisBlock = (int)Math.Min(Math.Min(_bufferSize, requested - produced), _maxSamples - _totalSamples);
            Action callback = !_termination.Started || _termination.FadeActive
                ? _runtime.Tick
                : null;
            _sink.Render(outputs, samplesThisBlock, callback);

            for (int i = 0; i < samplesThisBlock; i++)
            {
                int l = Math.Clamp(outputs[0][i], -32768, 32767);
                int r = Math.Clamp(outputs[1][i], -32768, 32767);

                if (_termination.FadeActive)
                {
                    long fadePos = _totalSamples + i - _termination.FadeStartSample;
                    if (fadePos < 0) { /* block before boundary stays un-faded */ }
                    else if (fadePos < _fadeSamples)
                    {
                        double gain = _fadeSamples == 0 ? 0 : 1.0 - (double)fadePos / _fadeSamples;
                        l = (int)(l * gain);
                        r = (int)(r * gain);
                    }
                    else { l = 0; r = 0; }
                }

                interleaved[i * 2] = (short)l;
                interleaved[i * 2 + 1] = (short)r;
            }

            interleaved.AsSpan(0, samplesThisBlock * 2).CopyTo(interleavedStereo.Slice(produced * 2, samplesThisBlock * 2));
            produced += samplesThisBlock;
            _totalSamples += samplesThisBlock;
            if (_runtime.CurrentLoop != _currentLoop)
                _currentLoop = _runtime.CurrentLoop;

            if (!_termination.Started)
            {
                _termination.Observe(_runtime.PlaybackEnded, _currentLoop, _totalSamples);
                if (_termination.StopReason == "natural_stop")
                    _runtime.Stop();
            }
        }

        _runtimeStopped = _runtime.IsStopped;
        return produced;
    }

    public void Dispose() => _sink.Dispose();

    // ---- Pass-1 capture reporting (consumed by the native-audio capture pass) ----

    /// <summary>Total stereo frames the session has produced so far.</summary>
    internal long TotalSamples => _totalSamples;

    /// <summary>The legacy session's termination decision (null until first render).</summary>
    internal PlaybackTermination TerminationState => _termination;

    /// <summary>The FMP driver's current loop count.</summary>
    internal int CurrentLoop => _runtime.CurrentLoop;

    /// <summary>Absolute YM2608 master clock at render completion.</summary>
    internal ulong FinalOpnaMasterClock => _runtime.FinalOpnaMasterClock;

    /// <summary>True once the legacy session has produced its final frame.</summary>
    internal bool CaptureReachedEnd => IsCompleted;

    ulong IFmpPcmSession.FinalOpnaMasterClock => _runtime.FinalOpnaMasterClock;
}