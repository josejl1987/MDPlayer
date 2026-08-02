using System.Text.Json;
using Fmp.Core.Audio;
using Fmp.Core.Audio.Mdsound;
using Fmp.Core.IO;

namespace Fmp.Core.Rendering;

/// <summary>
/// Single-pass stem renderer that produces per-channel WAV files
/// and a scope metadata manifest.
///
/// DESIGN NOTE: MDSound does not expose per-channel PCM taps, so we run
/// one Nise98/FMP emulation pass and broadcast every register write to a
/// collection of independent chip sinks — one per stem — each masked to its
/// own channel group. This eliminates the redundant replay passes that the
/// previous implementation used.
/// </summary>
internal class ScopeRenderer
{
    private readonly FmpRuntimeAssets _assets;
    private readonly IFmpFileSystem _fileSystem;
    private readonly int _sampleRate;
    private readonly int _loopCount;
    private readonly double _fadeSeconds;
    private readonly double _tailSeconds;
    private readonly double? _maxDurationSeconds;
    private readonly double _ssgGainDb;

    public class StemResult
    {
        public string Name { get; init; }
        public string Label { get; init; }
        public ScopeSemanticClass SemanticClass { get; init; } = ScopeSemanticClass.Mixed;
        /// <summary>
        /// Backend channel identifier the stem was rendered from (e.g.
        /// "ym2608.0.fm.1"). Empty for stems that have no stable channel
        /// identity of their own (such as the generic master fallback).
        /// Used by layout projection to match stems to panels without
        /// consulting a backend-specific catalog.
        /// </summary>
        public string PresentationTrackId { get; init; } = "";
        public int StableOrder { get; init; } = int.MaxValue;
        public int WindowWidth { get; init; } = 1;
        public double DefaultAmplification { get; init; } = 1.0;
        public string DefaultColor { get; init; }
        public string WavPath { get; init; }
        public long RenderedSamples { get; set; }
        public int Channels { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
    }

    public class ScopeResult
    {
        public bool Success { get; set; }
        public string InputPath { get; set; }
        public string OutputDir { get; set; }
        public List<StemResult> Stems { get; init; } = new();
        public long MasterSamples { get; set; }
        public int SampleRate { get; set; }
        public string CompletionReason { get; set; }
        public string LastError { get; set; }
        public double RenderSeconds { get; set; }
    }

    public ScopeRenderer(
        FmpRuntimeAssets assets,
        IFmpFileSystem fileSystem = null,
        int sampleRate = 44100,
        int loopCount = 2,
        double fadeSeconds = 5.0,
        double tailSeconds = 0.5,
        double? maxDurationSeconds = null,
        double ssgGainDb = 0)
    {
        _assets = assets ?? throw new ArgumentNullException(nameof(assets));
        _fileSystem = fileSystem;
        _sampleRate = sampleRate;
        _loopCount = loopCount;
        _fadeSeconds = fadeSeconds;
        _tailSeconds = tailSeconds;
        _maxDurationSeconds = maxDurationSeconds;
        _ssgGainDb = ssgGainDb;
    }

    public ScopeResult Render(
        byte[] trackData,
        string trackFileName,
        string outputDir,
        IReadOnlyList<StemPass> stems = null,
        IProgress<ScopeProgress> progress = null,
        bool skipSilentStems = true,
        string audioDir = null,
        string metadataPath = null)
    {
        stems ??= DefaultStems.All;
        var activeStems = stems.Where(s => CanProduceOutput(s.Channels, skipSilentStems)).ToList();
        var skipped = stems.Except(activeStems).ToList();
        foreach (var stem in skipped)
        {
            progress?.Report(new ScopeProgress { StemName = stem.Name, RenderedSamples = 0, Elapsed = TimeSpan.Zero, Completed = true });
        }

        var renderWatch = System.Diagnostics.Stopwatch.StartNew();

        var result = new ScopeResult
        {
            InputPath = trackFileName,
            OutputDir = outputDir,
            SampleRate = _sampleRate
        };

        audioDir ??= Path.Combine(outputDir, "audio");
        Directory.CreateDirectory(audioDir);

        var stemStates = activeStems.Select(stem => new StemState
        {
            Pass = stem,
            Result = new StemResult
            {
                Name = stem.Name,
                Label = stem.Label,
                SemanticClass = stem.SemanticClass,
                PresentationTrackId = stem.PresentationTrackId,
                StableOrder = stem.StableOrder,
                WindowWidth = stem.WindowWidth,
                DefaultAmplification = stem.DefaultAmplification,
                DefaultColor = stem.DefaultColor,
                WavPath = Path.Combine(audioDir, stem.Name + ".wav"),
                Success = false
            },
            Sink = new MdsoundFmpChipSink(_sampleRate, ssgGainDb: _ssgGainDb),
            IsMaster = stem.Channels == ChannelGroup.All
        }).ToList();

        var broadcastSinks = new List<(IFmpChipSink Sink, ChannelGroup Channels)>(stemStates.Count);
        foreach (var state in stemStates)
        {
            if (state.IsMaster)
            {
                broadcastSinks.Add((state.Sink, ChannelGroup.All));
            }
            else
            {
                var mask = new MaskedChipSink(state.Sink, state.Pass.Channels);
                ApplyGroupMuting(state.Sink, state.Pass.Channels);
                broadcastSinks.Add((mask, state.Pass.Channels));
            }
        }

        var broadcast = new BroadcastChipSink(broadcastSinks);
        var runtime = new FmpRuntime(broadcast, _assets, _fileSystem);

        foreach (var state in stemStates)
            state.Sink.Start();

        runtime.Initialize(trackData, trackFileName);
        runtime.SetWaitSamples(_sampleRate * 500 / 1000);

        foreach (var state in stemStates)
        {
            int channels = state.IsMaster ? 2 : 1;
            state.Channels = channels;
            state.Writer = new WavWriter(state.Result.WavPath + ".partial", _sampleRate, channels);
        }

        int bufferSize = _sampleRate / 100; // 10ms
        var outputs = new int[2][] { new int[bufferSize], new int[bufferSize] };
        var interleaved = new short[bufferSize * 2];

        long totalSamples = 0;
        int currentLoop = 0;
        long fadeSamples = checked((long)Math.Ceiling(_fadeSeconds * _sampleRate));
        long tailSamples = checked((long)Math.Ceiling(_tailSeconds * _sampleRate));
        var termination = new PlaybackTermination(_loopCount, fadeSamples, tailSamples);
        bool maxDurationHit = false;
        double maxDuration = _maxDurationSeconds ?? 3600.0;
        long maxSamples = checked((long)Math.Ceiling(maxDuration * _sampleRate));

        try
        {
            while (totalSamples < maxSamples)
            {
                if (termination.IsComplete(totalSamples))
                {
                    runtime.Stop();
                    break;
                }

                if (runtime.IsStopped && !termination.Started)
                {
                    if (!string.IsNullOrEmpty(runtime.LastError))
                        result.LastError = runtime.LastError;
                    break;
                }

                int samplesThisBlock = (int)Math.Min(bufferSize, maxSamples - totalSamples);
                // Natural completion stops driving the emulator before the
                // explicit tail. A loop-limit fade keeps producing audio while
                // the fade is audible, then the tail is rendered as chip decay.
                if (!termination.Started || termination.FadeActive)
                {
                    for (int i = 0; i < samplesThisBlock; i++)
                        runtime.Tick();
                }

                if (runtime.CurrentLoop != currentLoop)
                    currentLoop = runtime.CurrentLoop;

                if (!termination.Started)
                {
                    termination.Observe(
                        runtime.PlaybackEnded,
                        currentLoop,
                        totalSamples + samplesThisBlock);
                    if (termination.StopReason == "natural_stop")
                        runtime.Stop();
                }

                foreach (var state in stemStates)
                {
                    state.Sink.Render(outputs, samplesThisBlock);

                    if (termination.FadeActive)
                    {
                        for (int i = 0; i < samplesThisBlock; i++)
                        {
                            long fadePos = totalSamples + i - termination.FadeStartSample;
                            if (fadePos < 0)
                            {
                                continue;
                            }
                            if (fadePos < fadeSamples)
                            {
                                double gain = fadeSamples == 0
                                    ? 0
                                    : 1.0 - (double)fadePos / fadeSamples;
                                outputs[0][i] = (int)(outputs[0][i] * gain);
                                outputs[1][i] = (int)(outputs[1][i] * gain);
                            }
                            else
                            {
                                outputs[0][i] = 0;
                                outputs[1][i] = 0;
                            }
                        }
                    }

                    if (state.IsMaster)
                    {
                        for (int i = 0; i < samplesThisBlock; i++)
                        {
                            interleaved[i * 2] = (short)Math.Clamp(outputs[0][i], -32768, 32767);
                            interleaved[i * 2 + 1] = (short)Math.Clamp(outputs[1][i], -32768, 32767);
                        }
                        state.Writer.Write(interleaved.AsSpan(0, samplesThisBlock * 2));
                    }
                    else
                    {
                        for (int i = 0; i < samplesThisBlock; i++)
                        {
                            interleaved[i] = MonoDownmix(
                                Math.Clamp(outputs[0][i], -32768, 32767),
                                Math.Clamp(outputs[1][i], -32768, 32767));
                        }
                        state.Writer.Write(interleaved.AsSpan(0, samplesThisBlock));
                    }
                }

                totalSamples += samplesThisBlock;
                if (termination.IsComplete(totalSamples))
                {
                    runtime.Stop();
                    break;
                }
            }

            if (totalSamples >= maxSamples && !termination.IsComplete(totalSamples))
                maxDurationHit = true;
        }
        catch (Exception ex)
        {
            result.LastError = $"render error: {ex.GetType().Name}: {ex.Message}";
        }

        bool masterSuccess = false;
        renderWatch.Stop();
        TimeSpan renderElapsed = renderWatch.Elapsed;
        foreach (var state in stemStates)
        {
            try
            {
                state.Writer.Close();
                string partialPath = state.Result.WavPath + ".partial";
                if (File.Exists(state.Result.WavPath))
                    File.Delete(state.Result.WavPath);
                File.Move(partialPath, state.Result.WavPath);

                state.Result.RenderedSamples = totalSamples;
                state.Result.Channels = state.Channels;
                state.Result.Success = true;

                if (state.IsMaster)
                    masterSuccess = true;

                progress?.Report(new ScopeProgress
                {
                    StemName = state.Pass.Name,
                    RenderedSamples = totalSamples,
                    Elapsed = renderElapsed,
                    Completed = true
                });
            }
            catch (Exception ex)
            {
                state.Result.Success = false;
                state.Result.Error = $"{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                state.Writer.Dispose();
            }

            result.Stems.Add(state.Result);
        }

        if (maxDurationHit)
        {
            var master = stemStates.FirstOrDefault(s => s.IsMaster);
            if (master != null)
            {
                master.Result.Error = "max_duration";
            }
            result.LastError = "max_duration";
        }

        result.MasterSamples = result.Stems.FirstOrDefault(s => s.Name == "master")?.RenderedSamples ?? totalSamples;
        result.Success = masterSuccess;
        result.CompletionReason = maxDurationHit
            ? "max_duration"
            : !string.IsNullOrEmpty(termination.StopReason)
                ? termination.StopReason
                : "completed";

        metadataPath ??= Path.Combine(outputDir, "metadata.json");
        WriteManifest(metadataPath, result);

        return result;
    }

    private class StemState
    {
        public StemPass Pass { get; init; }
        public StemResult Result { get; init; }
        public MdsoundFmpChipSink Sink { get; init; }
        public WavWriter Writer { get; set; }
        public bool IsMaster { get; init; }
        public int Channels { get; set; }
    }

    private static bool CanProduceOutput(ChannelGroup group, bool skipSilent)
    {
        if (!skipSilent) return true;
        // ADPCM has no ROM data in the FMP path and stays silent by chip default.
        if (group == ChannelGroup.Adpcm) return false;
        return true;
    }

    /// <summary>
    /// Apply group-level volume muting via MDSound API for non-FM/SSG groups.
    /// </summary>
    private static void ApplyGroupMuting(MdsoundFmpChipSink sink, ChannelGroup active)
    {
        // Rhythm
        if (!active.HasFlag(ChannelGroup.Rhythm))
            sink.SetVolume(VolumeGroup.Rhythm, -192);
        else
            sink.SetVolume(VolumeGroup.Rhythm, 0);

        // ADPCM
        if (!active.HasFlag(ChannelGroup.Adpcm))
            sink.SetVolume(VolumeGroup.Adpcm, -192);
        else
            sink.SetVolume(VolumeGroup.Adpcm, 0);

        // PPZ8
        if (!active.HasFlag(ChannelGroup.Ppz8))
            sink.SetVolume(VolumeGroup.Ppz8, -192);
        else
            sink.SetVolume(VolumeGroup.Ppz8, 0);
    }

    private void WriteManifest(string path, ScopeResult result)
    {
        string partialPath = path + ".partial";

        var manifest = new Dictionary<string, object>
        {
            ["succeeded"] = result.Success,
            ["input"] = new Dictionary<string, object>
            {
                ["path"] = result.InputPath
            },
            ["render"] = new Dictionary<string, object>
            {
                ["sampleRate"] = result.SampleRate,
                ["masterSamples"] = result.MasterSamples,
                ["loopCount"] = _loopCount,
                ["fadeSeconds"] = _fadeSeconds,
                ["tailSeconds"] = _tailSeconds
            },
            ["channels"] = result.Stems.Select(s => new Dictionary<string, object>
            {
                ["name"] = s.Name,
                ["label"] = s.Label,
                ["success"] = s.Success,
                ["samples"] = s.RenderedSamples,
                ["channels"] = s.Channels,
                ["wav"] = s.Success ? Path.GetFileName(s.WavPath) : null,
                ["error"] = s.Error ?? ""
            }).ToList(),
            ["completionReason"] = result.CompletionReason ?? "",
            ["lastError"] = result.LastError ?? ""
        };

        string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        File.WriteAllText(partialPath, json);
        if (File.Exists(path)) File.Delete(path);
        File.Move(partialPath, path);
    }

    /// <summary>
    /// Mono downmix: picks the louder channel (max absolute).
    /// Unlike (L+R)/2, this preserves full volume of panned signals
    /// (e.g., FM channels panned hard left via YM2608 registers 0xB4-0xB6).
    /// </summary>
    private static short MonoDownmix(int l, int r)
    {
        return (short)(Math.Abs(l) > Math.Abs(r) ? l : r);
    }
}
