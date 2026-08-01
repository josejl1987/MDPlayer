using Fmp.Core.Audio;
using Fmp.Core.Decoding.SnesDsp;
using Fmp.Core.Visualization;

namespace Fmp.Core.Playback.Spc;

/// <summary>
/// SPC playback backend. Recognizes .spc by header signature, not extension,
/// and reports the SNES S-DSP device. PR 2 renders the full resolved duration
/// at the native 32,000 Hz S-DSP rate through the MDPlayer SPC native backend,
/// applies the linear fade to the master, writes master.wav, and completes.
/// </summary>
internal sealed class SpcPlaybackBackend : IPlaybackBackend
{
    public const int NativeSampleRate = 32_000;
    public string Id => "spc";

    public PlaybackProbeResult Probe(FileInfo input, PlaybackEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Exists)
            return new PlaybackProbeResult(false, "SPC", [], [], [$"input not found: {input.FullName}"]);

        byte[] data;
        try { data = File.ReadAllBytes(input.FullName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new PlaybackProbeResult(false, "SPC", [], [], [$"reading input — {ex.Message}"]); }

        try { var snapshot = SpcSnapshot.Parse(data); return Ok(snapshot); }
        catch (SpcFormatException ex) { return new PlaybackProbeResult(false, "SPC", [], [], [ex.Message]); }
    }

    private static PlaybackProbeResult Ok(SpcSnapshot snapshot) => new(true, "SPC", [], [], snapshot.Warnings ?? [])
    {
        Visualizable = true,
        Portable = false,
        Availability = PlaybackAvailability.PlatformSpecific,
        NativeSampleRate = NativeSampleRate,
    };

    /// <summary>
    /// Parses the snapshot, resolves duration/fade via <see cref="SpcDurationResolver"/>,
    /// opens the native session, renders the full duration at 32 kHz applying the
    /// linear fade to the master, writes master.wav through <see cref="WavWriter"/>,
    /// emits the S-DSP device, and returns a completed capture session. When the
    /// native library is missing, throws an actionable <see cref="SpcFormatException"/>.
    /// </summary>
    public IPlaybackCaptureSession Open(FileInfo input, PlaybackOptions options, IPlaybackEventSink eventSink)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(eventSink);

        byte[] data;
        try { data = File.ReadAllBytes(input.FullName); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new SpcFormatException($"reading input — {ex.Message}"); }

        SpcSnapshot snapshot = SpcSnapshot.Parse(data);

        // PR 10 (§25.3): the SPC pitch mode flows through the generic
        // PlaybackOptions object — no .spc conditionals in the renderer. In
        // Relative (diagnostic) mode the PR 9 BRR root estimator is NOT run:
        // instruments keep PitchAccuracy "relative" and EstimatedRootHz stays
        // null. Estimate (default) runs the estimator.
        SpcInstrumentBuilder instruments = BuildInstruments(snapshot, options.SpcPitchMode);

        SpcDurationResolver.Resolution resolution = SpcDurationResolver.Resolve(
            options.MaxDurationSeconds, options.FadeSeconds, snapshot.Metadata);

        long totalFrames = (long)(resolution.DurationSeconds * NativeSampleRate);
        long fadeStartFrame = resolution.FadeSeconds <= 0
            ? totalFrames
            : totalFrames - (long)Math.Ceiling(resolution.FadeSeconds * NativeSampleRate);
        if (fadeStartFrame < 0)
            fadeStartFrame = 0;

        // PR 6: when requested, additionally export per-voice mono stems
        // (voice-01.wav .. voice-08.wav) and the stereo echo stem (echo.wav)
        // next to master.wav, all at 32 kHz with the same duration as master.
        bool writeStems = options.WriteSpcStems && !string.IsNullOrEmpty(options.OutputAudioPath);

        // Emit the device BEFORE rendering so the sink creates the SPC timeline
        // decoder and can receive OnSpcEvent during the render loop below. The
        // per-source root estimates follow immediately so every key-on — even
        // one in the first rendered block — is already anchored (§25.3).
        eventSink.OnDevice(VisualizationDeviceCatalog.SnesDsp());
        eventSink.OnSpcInstruments(instruments.ResolveSourceRoots(snapshot));
        eventSink.OnSpcSamples(instruments.Samples.ToArray());

        using (SpcNativeSession native = SpcNativeSession.Open(data, StemOptions(writeStems)))
        {
            var stereo = new short[SpcNativeSession.DefaultBlockFrames * 2];
            var eventBuffer = new SpcNativeSession.SpcEvent[4096];
            if (!string.IsNullOrEmpty(options.OutputAudioPath))
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(options.OutputAudioPath));
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                using var wav = new WavWriter(options.OutputAudioPath, NativeSampleRate, channels: 2, bitsPerSample: 16);
                if (writeStems)
                {
                    List<WavWriter> stems = CreateStemWriters(options.OutputAudioPath);
                    try
                    {
                        RenderAll(native, stereo, totalFrames, fadeStartFrame, wav, stems, eventBuffer, eventSink);
                    }
                    finally
                    {
                        foreach (WavWriter stem in stems)
                            stem.Close();
                    }
                }
                else
                {
                    RenderAll(native, stereo, totalFrames, fadeStartFrame, wav, null, eventBuffer, eventSink);
                }
                wav.Close();
            }
            else
            {
                // Render anyway so SamplePosition stays truthful without an output file.
                RenderAll(native, stereo, totalFrames, fadeStartFrame, null, null, eventBuffer, eventSink);
            }
        }

        return new SpcPlaybackSession(
            NativeSampleRate,
            totalFrames,
            new List<DeviceDescriptor> { VisualizationDeviceCatalog.SnesDsp() },
            instruments.Instruments.ToArray(),
            instruments.Samples.ToArray());
    }

    /// <summary>
    /// Builds the SPC instruments for all eight S-DSP voices from the snapshot.
    /// <see cref="SpcPitchMode.Estimate"/> enables the PR 9 BRR root estimator;
    /// <see cref="SpcPitchMode.Relative"/> skips it (PitchAccuracy stays
    /// "relative", EstimatedRootHz stays null). Never throws on malformed DSP
    /// registers — <see cref="SpcInstrumentBuilder.Resolve"/> is defensive.
    /// </summary>
    private static SpcInstrumentBuilder BuildInstruments(SpcSnapshot snapshot, SpcPitchMode mode)
    {
        bool estimate = mode == SpcPitchMode.Estimate;
        var builder = new SpcInstrumentBuilder(
            pitchAccuracy: "relative",
            enablePitchEstimation: estimate);
        for (int voice = 0; voice < SpcNativeSession.VoiceCount; voice++)
            builder.Resolve(snapshot, voice);
        return builder;
    }

    /// <summary>
    /// Block-based render (1024-frame blocks, §9.1) with a linear master fade.
    /// When <paramref name="stems"/> is non-null the native core additionally
    /// fills per-voice mono PCM and the stereo echo PCM (PR 6) and each stem
    /// writer receives exactly the same number of frames as the master, so the
    /// stems are sample-count aligned with master.wav. The master fade is not
    /// applied to the stems (they are the raw §8.2/§8.4 voice signals).
    /// </summary>
    private static void RenderAll(
        SpcNativeSession native,
        short[] stereo,
        long totalFrames,
        long fadeStartFrame,
        WavWriter wav,
        IReadOnlyList<WavWriter> stems,
        SpcNativeSession.SpcEvent[] eventBuffer,
        IPlaybackEventSink eventSink)
    {
        int blockFrames = SpcNativeSession.DefaultBlockFrames;
        short[][] voices = stems != null ? CreateVoiceBuffers(blockFrames) : null;
        short[] echo = stems != null ? new short[blockFrames * 2] : null;
        long rendered = 0;
        while (rendered < totalFrames)
        {
            long remaining = totalFrames - rendered;
            int need = (int)Math.Min(blockFrames, remaining);
            // Native block rendering requires an even frame count. Round the final
            // partial block down before applying the minimum; a remaining count of
            // one is rendered in the minimum block and only that frame is kept.
            int request = Math.Max(need & ~1, SpcNativeSession.MinBlockFrames);
            SpcNativeSession.SpcRenderResult rr;
            if (stems != null)
                rr = native.RenderStemsAndCapture(stereo, request, voices, echo, eventBuffer);
            else
                rr = native.RenderAndCapture(stereo, request, eventBuffer);
            int got = rr.FramesRendered;
            if (got <= 0)
                break;
            int keep = (int)Math.Min(got, remaining);

            // Forward native S-DSP events to the timeline decoder (§9.2).
            int eventsWritten = Math.Min(rr.EventsWritten, eventBuffer.Length);
            for (int i = 0; i < eventsWritten; i++)
            {
                ref SpcNativeSession.SpcEvent ev = ref eventBuffer[i];
                long samplePos = ev.Frame; // Native event frames are absolute frame positions.
                SpcSemanticEvent semantic = TranslateEvent(ev, samplePos);
                eventSink.OnSpcEvent(in semantic);
            }

            ApplyFade(stereo, keep, rendered, fadeStartFrame, totalFrames);
            wav?.Write(stereo.AsSpan(0, keep * 2));
            if (stems != null)
            {
                for (int c = 0; c < SpcNativeSession.VoiceCount; c++)
                    stems[c].Write(voices[c].AsSpan(0, keep));
                stems[SpcNativeSession.VoiceCount].Write(echo.AsSpan(0, keep * 2));
            }
            rendered += keep;
        }
    }

    /// <summary>
    /// Maps a native <see cref="SpcNativeSession.SpcEvent"/> to the managed
    /// <see cref="SpcSemanticEvent"/> consumed by the timeline decoder. The
    /// native enum values (1=KeyOn, 2=ReleaseStart, ...) differ from the
    /// managed <see cref="SpcSemanticEventKind"/> ordering, so this is an
    /// explicit switch, not a cast.
    /// </summary>
    private static SpcSemanticEvent TranslateEvent(in SpcNativeSession.SpcEvent ev, long samplePos)
    {
        int voice = ev.Channel;
        int param0 = ev.Param0;
        int param1 = ev.Param1;
        // The native core reports source numbers, effective pitch (14-bit),
        // volume, noise/pmon/eon flags via Param0/Param1 depending on type.
        switch ((SpcNativeSession.SpcEventType)ev.Type)
        {
            case SpcNativeSession.SpcEventType.KeyOn:
                return SpcSemanticEvent.KeyOn(samplePos, voice,
                    sourceNumber: param0,
                    effectivePitch: (ushort)param1);
            case SpcNativeSession.SpcEventType.ReleaseStart:
                return SpcSemanticEvent.ReleaseStart(samplePos, voice, param0);
            case SpcNativeSession.SpcEventType.VoiceEnd:
                return SpcSemanticEvent.VoiceEnd(samplePos, voice);
            case SpcNativeSession.SpcEventType.SourceChanged:
                return SpcSemanticEvent.SourceLatched(samplePos, voice, param0);
            case SpcNativeSession.SpcEventType.PitchChanged:
                return SpcSemanticEvent.PitchChanged(samplePos, voice, (ushort)param0);
            case SpcNativeSession.SpcEventType.VolumeChanged:
                return SpcSemanticEvent.VolumeChanged(samplePos, voice, param0, param1);
            case SpcNativeSession.SpcEventType.NoiseChanged:
                return new SpcSemanticEvent(samplePos, voice, SpcSemanticEventKind.NoiseChanged, param0);
            case SpcNativeSession.SpcEventType.PitchModChanged:
                return new SpcSemanticEvent(samplePos, voice, SpcSemanticEventKind.PitchModChanged, param0);
            case SpcNativeSession.SpcEventType.EchoSendChanged:
                return new SpcSemanticEvent(samplePos, voice, SpcSemanticEventKind.EchoSendChanged, param0);
            case SpcNativeSession.SpcEventType.EnvelopeModeChanged:
                return new SpcSemanticEvent(samplePos, voice, SpcSemanticEventKind.EnvelopeModeChanged, param0, param1);
            default:
                return default;
        }
    }

    /// <summary>
    /// PR 6: native open options that arm voice/echo tap capture (the DSP only
    /// fills the tap buffers when the session was opened with
    /// EnableVoicePcm/EnableEchoPcm). Without stems the PR 2 defaults apply.
    /// </summary>
    private static SpcNativeSession.OpenOptions StemOptions(bool writeStems)
    {
        if (!writeStems)
            return SpcNativeSession.OpenOptions.Default;
        return new SpcNativeSession.OpenOptions
        {
            EventCapacity = 64,
            EnableVoicePcm = 1,
            EnableEchoPcm = 1,
            AccurateDsp = 1,
        };
    }

    private static short[][] CreateVoiceBuffers(int frames)
    {
        var buffers = new short[SpcNativeSession.VoiceCount][];
        for (int c = 0; c < SpcNativeSession.VoiceCount; c++)
            buffers[c] = new short[frames];
        return buffers;
    }

    /// <summary>
    /// PR 6 stem writers next to the master file: voice-01.wav .. voice-08.wav
    /// (mono 32 kHz) and echo.wav (stereo 32 kHz). The 9 writers share the
    /// master's directory (spec §8.5/§22.2).
    /// </summary>
    private static List<WavWriter> CreateStemWriters(string masterPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(masterPath)) ?? ".";
        var writers = new List<WavWriter>(SpcNativeSession.VoiceCount + 1);
        for (int c = 0; c < SpcNativeSession.VoiceCount; c++)
            writers.Add(new WavWriter(
                Path.Combine(dir, $"voice-{c + 1:00}.wav"),
                NativeSampleRate,
                channels: 1,
                bitsPerSample: 16));
        writers.Add(new WavWriter(
            Path.Combine(dir, "echo.wav"),
            NativeSampleRate,
            channels: 2,
            bitsPerSample: 16));
        return writers;
    }

    private static void ApplyFade(short[] stereo, int frames, long startFrame, long fadeStartFrame, long totalFrames)
    {
        if (frames <= 0 || fadeStartFrame >= totalFrames)
            return;
        long fadeLength = totalFrames - fadeStartFrame;
        for (int i = 0; i < frames; i++)
        {
            long frame = startFrame + i;
            if (frame < fadeStartFrame)
                continue;
            double gain = fadeLength > 1
                ? 1.0 - (double)(frame - fadeStartFrame) / (double)(fadeLength - 1)
                : 1.0;
            if (gain <= 0)
            {
                stereo[i * 2] = 0;
                stereo[i * 2 + 1] = 0;
            }
            else
            {
                stereo[i * 2] = (short)(stereo[i * 2] * gain);
                stereo[i * 2 + 1] = (short)(stereo[i * 2 + 1] * gain);
            }
        }
    }
}
