using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;
using Fmp.Core.Visualization;

namespace Fmp.Core.Rendering;

/// <summary>
/// Generic backend adapter for the existing portable FMP/Nise98 path. The
/// driver and synthesizer remain FMP-specific; all emitted writes cross the
/// same normalized event seam used by VGM.
/// </summary>
internal sealed class FmpPlaybackBackend : IPlaybackBackend
{
    private readonly FmpRuntimeAssets _assets;
    private readonly IFmpFileSystem _fileSystem;

    public FmpPlaybackBackend(FmpRuntimeAssets assets, IFmpFileSystem fileSystem = null)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _fileSystem = fileSystem;
    }

    public string Id => "fmp";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input);
        string extension = input.Extension.ToLowerInvariant();
        if (!FmpFormat.IsSupportedExtension(extension))
        {
            return new PlaybackProbeResult(
                false,
                extension.TrimStart('.'),
                [],
                [],
                [$"FMP backend does not handle {extension}"]);
        }

        var required = new RequiredAsset(
            "FMP.COM",
            AssetKind.DriverData,
            true,
            [_assets.FmpComPath]);
        if (!File.Exists(_assets.FmpComPath))
        {
            return new PlaybackProbeResult(
                false,
                "fmp",
                [required],
                ["FMP.COM"],
                [$"FMP.COM not found at {_assets.FmpComPath}"])
            {
                Portable = true,
                Availability = PlaybackAvailability.AssetMissing,
            };
        }

        return new PlaybackProbeResult(true, "fmp", [required], [], [])
        {
            Visualizable = true,
            Portable = true,
            Availability = PlaybackAvailability.Available,
        };
    }

    public IPlaybackCaptureSession Open(
        FileInfo input,
        PlaybackOptions options,
        IPlaybackEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);
        if (!input.Exists)
            throw new FileNotFoundException("FMP input not found.", input.FullName);
        if (!FmpFormat.IsSupportedExtension(input.Extension))
            throw new VgmPlaybackException($"unsupported FMP extension: {input.Extension}");

        return new FmpCaptureSession(
            File.ReadAllBytes(input.FullName),
            input.FullName,
            _assets,
            _fileSystem,
            options,
            eventSink);
    }
}

internal sealed class FmpCaptureSession : IPlaybackCaptureSession
{
    private readonly byte[] _trackData;
    private readonly string _trackFileName;
    private readonly FmpRuntimeAssets _assets;
    private readonly IFmpFileSystem _fileSystem;
    private readonly PlaybackOptions _options;
    private readonly IPlaybackEventSink _events;
    private bool _stopped;

    public FmpCaptureSession(
        byte[] trackData,
        string trackFileName,
        FmpRuntimeAssets assets,
        IFmpFileSystem fileSystem,
        PlaybackOptions options,
        IPlaybackEventSink events)
    {
        _trackData = trackData ?? throw new ArgumentNullException(nameof(trackData));
        _trackFileName = trackFileName ?? throw new ArgumentNullException(nameof(trackFileName));
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _fileSystem = fileSystem;
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        if (_options.LoopCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.LoopCount));
        Timing = new CaptureTimingInfo(_options.SampleRate, 1).Validate();
    }

    public CaptureTimingInfo Timing { get; }
    public IReadOnlyList<DeviceDescriptor> Devices =>
    [
        VisualizationDeviceCatalog.Ym2608(),
        VisualizationDeviceCatalog.Ppz8(),
    ];
    public long SamplePosition { get; private set; }
    public bool IsComplete { get; private set; }

    public void Run(CancellationToken cancellationToken = default)
    {
        foreach (DeviceDescriptor device in Devices)
            _events.OnDevice(device);
        using var audio = new MdsoundFmpChipSink(Timing.SampleRate, ssgGainDb: _options.SsgGainDb);
        var sink = new FmpPlaybackEventSinkAdapter(_events, audio);
        var runtime = new FmpRuntime(sink, _assets, _fileSystem);
        WavWriter writer = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(_options.OutputAudioPath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_options.OutputAudioPath)) ?? ".");
                writer = new WavWriter(_options.OutputAudioPath, Timing.SampleRate, 2);
            }

            audio.Start();
            runtime.Initialize(_trackData, _trackFileName);
            runtime.SetWaitSamples(Timing.SampleRate / 2);
            _events.OnLoopBoundary(new TimedLoopBoundary(0, 0));

            int blockSize = Math.Max(1, Timing.SampleRate / 100);
            var outputs = new int[2][] { new int[blockSize], new int[blockSize] };
            var interleaved = new short[blockSize * 2];
            long maxSamples = _options.MaxDurationSeconds.HasValue
                ? checked((long)Math.Ceiling(_options.MaxDurationSeconds.Value * Timing.SampleRate))
                : Timing.SampleRate * 3600L;
            long fadeSamples = checked((long)Math.Ceiling(_options.FadeSeconds * Timing.SampleRate));
            long tailSamples = checked((long)Math.Ceiling(_options.TailSeconds * Timing.SampleRate));
            var termination = new PlaybackTermination(_options.LoopCount, fadeSamples, tailSamples);
            int currentLoop = 0;

            while (!_stopped && SamplePosition < maxSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (termination.IsComplete(SamplePosition))
                    break;
                if (runtime.IsStopped && !termination.Started)
                    break;

                int samplesThisBlock = (int)Math.Min(blockSize, maxSamples - SamplePosition);
                Action callback = !termination.Started || termination.FadeActive
                    ? runtime.Tick
                    : null;
                audio.Render(outputs, samplesThisBlock, callback);
                for (int index = 0; index < samplesThisBlock; index++)
                {
                    int left = Math.Clamp(outputs[0][index], -32768, 32767);
                    int right = Math.Clamp(outputs[1][index], -32768, 32767);
                    if (termination.FadeActive)
                    {
                        long fadePos = SamplePosition + index - termination.FadeStartSample;
                        if (fadePos >= 0)
                        {
                            double gain = fadePos < fadeSamples
                                ? fadeSamples == 0 ? 0 : 1.0 - (double)fadePos / fadeSamples
                                : 0.0;
                            left = (int)(left * gain);
                            right = (int)(right * gain);
                        }
                    }
                    interleaved[index * 2] = (short)left;
                    interleaved[index * 2 + 1] = (short)right;
                }
                writer?.Write(interleaved.AsSpan(0, samplesThisBlock * 2));
                SamplePosition += samplesThisBlock;

                if (runtime.CurrentLoop != currentLoop)
                {
                    currentLoop = runtime.CurrentLoop;
                    _events.OnLoopBoundary(new TimedLoopBoundary(SamplePosition, currentLoop));
                }

                if (!termination.Started)
                {
                    termination.Observe(runtime.PlaybackEnded, currentLoop, SamplePosition);
                    if (termination.StopReason == "natural_stop")
                        runtime.Stop();
                }

                if (termination.IsComplete(SamplePosition))
                    break;
            }

            runtime.Stop();
            IsComplete = true;
        }
        finally
        {
            writer?.Close();
        }
    }

    public void Stop() => _stopped = true;

    public void Dispose() => Stop();
}
