using System.IO;
using Fmp.Core.Visualization;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Streaming stem-energy analyzer (Visualization 2.0 §6.4). Reads each stem
/// WAV file once with fixed-size sequential reads, computing per-frame peak
/// and RMS. No full-file loads. Completes in under 2% of track duration.
/// </summary>
internal static class ChannelEnergyAnalyzer
{
    /// <summary>
    /// Fixed-size read block: 8192 sample frames (32 KB per stereo 16-bit read).
    /// </summary>
    private const int BlockSampleFrames = 8192;

    /// <summary>
    /// Maps stem names (from <see cref="ScopeRenderer.StemResult.Name"/>) to
    /// the compatibility panel IDs used by the legacy stem pipeline.
    /// </summary>
    /// <summary>
    /// Analyzes all stem WAV files from a scope render result, producing
    /// per-channel energy envelopes with one entry per output frame.
    /// </summary>
    /// <param name="stems">The stem results from <see cref="ScopeRenderer"/>.</param>
    /// <param name="totalFrames">The total number of output video frames.</param>
    /// <param name="sampleRate">The audio sample rate (Hz).</param>
    /// <param name="fpsNumerator">Output FPS numerator.</param>
    /// <param name="fpsDenominator">Output FPS denominator.</param>
    /// <returns>Array of energy envelopes, one per channel that has a stem file.</returns>
    public static ChannelEnergyEnvelope[] Analyze(
        IReadOnlyList<(string Name, string WavPath)> stems,
        int totalFrames,
        int sampleRate,
        int fpsNumerator,
        int fpsDenominator)
    {
        if (totalFrames <= 0 || sampleRate <= 0 || fpsNumerator <= 0)
            return Array.Empty<ChannelEnergyEnvelope>();

        double samplesPerFrame = (double)sampleRate * fpsDenominator / fpsNumerator;
        var envelopes = new List<ChannelEnergyEnvelope>(stems.Count);

        foreach (var (name, wavPath) in stems)
        {
            if (!VisualizationTopologyCompatibility.TryGetStemPanel(name, out string channelId))
                continue;
            if (!File.Exists(wavPath))
                continue;

            var env = AnalyzeStem(wavPath, channelId, totalFrames, samplesPerFrame,
                fpsDenominator / (double)fpsNumerator);
            if (env != null)
                envelopes.Add(env);
        }

        return envelopes.ToArray();
    }

    /// <summary>
    /// Reads a single 16-bit stereo PCM WAV file and computes per-frame peak/RMS.
    /// Uses sequential reads with a fixed block size — no full-file load.
    /// </summary>
    private static ChannelEnergyEnvelope AnalyzeStem(
        string wavPath,
        string channelId,
        int totalFrames,
        double samplesPerFrame,
        double frameSeconds)
    {
        var framePeak = new float[totalFrames];
        var frameRms = new float[totalFrames];

        using var stream = new FileStream(wavPath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        int channels;
        int bitsPerSample;
        int dataOffset = FindDataChunk(stream, out channels, out bitsPerSample);
        if (dataOffset < 0 || channels <= 0 || bitsPerSample != 16)
            return null;

        stream.Position = dataOffset;

        // Read signed 16-bit PCM. Master WAVs are stereo; per-channel scope
        // stems are mono. Downmix any wider file by averaging its channels.
        int bytesPerFrame = channels * 2;
        byte[] buffer = new byte[BlockSampleFrames * bytesPerFrame];

        long globalSample = 0; // absolute sample frame index in the stem
        int currentFrame = 0;
        double frameEnd = samplesPerFrame; // end sample of current output frame

        float frameMaxAbs = 0;
        double frameSumSq = 0;
        long frameCount = 0;

        while (currentFrame < totalFrames)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            int sampleFramesRead = read / bytesPerFrame;
            for (int i = 0; i < sampleFramesRead; i++)
            {
                int offset = i * bytesPerFrame;
                int sum = 0;
                for (int channel = 0; channel < channels; channel++)
                {
                    int sampleOffset = offset + channel * 2;
                    sum += (short)(buffer[sampleOffset] | (buffer[sampleOffset + 1] << 8));
                }
                float mono = sum / (float)channels;

                float absVal = Math.Abs(mono);
                if (absVal > frameMaxAbs)
                    frameMaxAbs = absVal;
                frameSumSq += mono * mono;
                frameCount++;

                globalSample++;
                if (globalSample >= frameEnd)
                {
                    // Finalize the current frame, but guard against the final
                    // sample landing exactly on a boundary past the last frame.
                    if (currentFrame >= totalFrames)
                        break;

                    framePeak[currentFrame] = Math.Clamp(frameMaxAbs / 32767f, 0f, 1f);
                    float rms = frameCount > 0
                        ? MathF.Sqrt((float)(frameSumSq / frameCount)) / 32767f
                        : 0f;
                    frameRms[currentFrame] = Math.Clamp(rms, 0f, 1f);

                    currentFrame++;
                    frameEnd += samplesPerFrame;
                    frameMaxAbs = 0;
                    frameSumSq = 0;
                    frameCount = 0;
                }
            }
        }

        // Fill any remaining frames with zero (silence).
        for (int f = currentFrame; f < totalFrames; f++)
        {
            framePeak[f] = 0;
            frameRms[f] = 0;
        }

        float[] frameActivity = SmoothActivity(frameRms, frameSeconds);

        return new ChannelEnergyEnvelope
        {
            ChannelId = channelId,
            FramePeak = framePeak,
            FrameRms = frameRms,
            FrameActivity = frameActivity,
        };
    }

    private static float[] SmoothActivity(float[] rms, double frameSeconds)
    {
        var activity = new float[rms.Length];
        if (rms.Length == 0)
            return activity;

        double attack = 1.0 - Math.Exp(-Math.Max(frameSeconds, 1e-6) / 0.030);
        double release = 1.0 - Math.Exp(-Math.Max(frameSeconds, 1e-6) / 0.160);
        double previous = 0;
        for (int i = 0; i < rms.Length; i++)
        {
            double db = 20.0 * Math.Log10(Math.Max(rms[i], 1e-5));
            double target = Math.Clamp((db + 48.0) / 42.0, 0, 1);
            double coefficient = target >= previous ? attack : release;
            previous += (target - previous) * coefficient;
            activity[i] = (float)Math.Clamp(previous, 0, 1);
        }
        return activity;
    }

    /// <summary>
    /// Parses the WAV header to find the start of the PCM data chunk.
    /// Returns the byte offset of the data, or -1 if not found.
    /// </summary>
    private static int FindDataChunk(FileStream stream, out int channels, out int bitsPerSample)
    {
        channels = 0;
        bitsPerSample = 0;
        using var reader = new BinaryReader(stream, System.Text.Encoding.ASCII, leaveOpen: true);
        // RIFF header: 12 bytes
        reader.BaseStream.Position = 0;
        byte[] riff = reader.ReadBytes(12);
        if (riff.Length < 12 || riff[0] != (byte)'R' || riff[1] != (byte)'I' || riff[2] != (byte)'F' || riff[3] != (byte)'F')
            return -1;

        // Walk chunks to find "data".
        while (reader.BaseStream.Position < reader.BaseStream.Length - 8)
        {
            byte[] chunkId = reader.ReadBytes(4);
            int chunkSize = reader.ReadInt32();
            if (chunkId.Length < 4)
                break;
            long chunkEnd = reader.BaseStream.Position + Math.Max(0, chunkSize);
            if (chunkEnd > reader.BaseStream.Length)
                break;
            if (chunkId[0] == (byte)'f' && chunkId[1] == (byte)'m'
                && chunkId[2] == (byte)'t' && chunkId[3] == (byte)' ' && chunkSize >= 16)
            {
                int format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                reader.ReadUInt32(); // sample rate
                reader.ReadUInt32(); // byte rate
                reader.ReadUInt16(); // block alignment
                bitsPerSample = reader.ReadUInt16();
                if (format != 1)
                    return -1;
            }
            if (chunkId[0] == (byte)'d' && chunkId[1] == (byte)'a' && chunkId[2] == (byte)'t' && chunkId[3] == (byte)'a')
                return (int)reader.BaseStream.Position;
            reader.BaseStream.Position = chunkEnd + (chunkSize & 1);
        }
        return -1;
    }
}
