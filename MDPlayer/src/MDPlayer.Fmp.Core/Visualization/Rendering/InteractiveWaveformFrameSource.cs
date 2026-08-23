using System;

#nullable enable
namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// Random-access scope grid source for interactive GUI stills. Each isolated
/// stem WAV is read directly at the requested sample (no Corrscope, no Python,
/// no FFmpeg), so forward and backward seeks cost the same regardless of
/// position. Every channel is drawn into its exact panel cell using an
/// explicit <see cref="ProjectedScopeChannel.PanelIndex"/>; panels with no
/// usable stem stay transparent and later channels are never shifted forward.
///
/// The visible OverlayLayout timeline window is sampled directly with stable
/// per-channel amplification; no frame-rate-dependent or local auto-gain state is used.
/// Frame rendering is order-independent and deterministic — seeking backward
/// yields the same frame as seeking directly to that position.
/// </summary>
internal sealed class InteractiveWaveformFrameSource : IScopeFrameSource
{
    // Fixed temporal window (not one output video frame) so the waveform's
    // scale does not change when the user selects 30/60/120 FPS. Matches the
    // FMP Corrscope render-window default.
    private const byte FallbackColorR = 0x7A;
    private const byte FallbackColorG = 0xA4;
    private const byte FallbackColorB = 0xFF;

    private readonly IFrameOverlayRenderer _overlay;
    private readonly int _fpsNumerator;
    private readonly int _fpsDenominator;
    private readonly int _gridWidth;
    private readonly ChannelState[] _channels;

    /// <summary>Number of mapped panels whose stem WAV could not be opened.</summary>
    public int UnavailableChannelCount { get; }

    private InteractiveWaveformFrameSource(
        IFrameOverlayRenderer overlay,
        int fpsNumerator,
        int fpsDenominator,
        ChannelState[] channels,
        int unavailableChannelCount)
    {
        _overlay = overlay;
        _fpsNumerator = fpsNumerator;
        _fpsDenominator = fpsDenominator;
        _gridWidth = overlay.Layout.CorrscopeGridWidth;
        _channels = channels;
        UnavailableChannelCount = unavailableChannelCount;
    }

    /// <summary>
    /// Creates the interactive source, returning null when no channel WAV can
    /// be opened, there are no scope regions, or frame-rate arguments are
    /// invalid. Unopenable channels are skipped (their panels stay transparent).
    /// </summary>
    public static InteractiveWaveformFrameSource? TryCreate(
        IFrameOverlayRenderer overlay,
        IReadOnlyList<ProjectedScopeChannel> channels,
        int fpsNumerator,
        int fpsDenominator)
    {
        if (overlay is null
            || channels is null
            || channels.Count == 0
            || !overlay.Layout.HasScopes
            || fpsNumerator <= 0
            || fpsDenominator <= 0)
        {
            return null;
        }

        var states = new List<ChannelState>(channels.Count);
        int unavailable = 0;
        foreach (ProjectedScopeChannel channel in channels)
        {
            SeekablePcm16WaveFile? wave =
                SeekablePcm16WaveFile.TryOpen(channel.WavPath);
            if (wave is null)
            {
                unavailable++;
                continue;
            }

            var state = new ChannelState
            {
                PanelIndex = channel.PanelIndex,
                Wave = wave,
                WindowWidth = channel.WindowWidth,
                DefaultAmplification = channel.DefaultAmplification,
                Color = ParseColor(channel.DefaultColor),
            };
            BuildPeakEnvelope(state, overlay.Layout.WindowSeconds, overlay.Layout.CorrscopeGridWidth);
            states.Add(state);
        }

        if (states.Count == 0)
        {
            foreach (ChannelState s in states)
                s.Dispose();
            return null;
        }

        return new InteractiveWaveformFrameSource(
            overlay,
            fpsNumerator,
            fpsDenominator,
            states.ToArray(),
            unavailable);
    }

    private static byte[] ParseColor(string? color)
    {
        byte[] fallback = { FallbackColorR, FallbackColorG, FallbackColorB, 0x70 };
        if (string.IsNullOrWhiteSpace(color))
            return fallback;

        string hex = color.Trim();
        if (hex.StartsWith('#'))
            hex = hex[1..];
        if (hex.Length != 6 && hex.Length != 8)
            return fallback;
        if (!byte.TryParse(hex.AsSpan(0, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out byte r)
            || !byte.TryParse(hex.AsSpan(2, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out byte g)
            || !byte.TryParse(hex.AsSpan(4, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out byte b))
        {
            return fallback;
        }

        byte a = 0xFF;
        if (hex.Length == 8
            && byte.TryParse(hex.AsSpan(6, 2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out byte parsedA))
        {
            a = parsedA;
        }

        return new[] { r, g, b, a };
    }

    public void ReadFrame(int frameIndex, Span<byte> destination)
    {
        int byteCount = checked(_gridWidth * _overlay.Layout.CorrscopeGridHeight * 4);
        if (destination.Length < byteCount)
            throw new ArgumentException(
                "scope frame destination is too small", nameof(destination));

        // Start fully transparent; the overlay's static scope background (and
        // any other panel's waveform) remains visible beneath each channel.
        destination.Clear();

        int columnCount = _overlay.Layout.ColumnCount;
        int scopeHeight = _overlay.Layout.ScopeHeight;

        foreach (ChannelState channel in _channels)
        {
            int row = channel.PanelIndex / columnCount;

            if (channel.PanelIndex < 0
                || channel.PanelIndex >= _overlay.Layout.PanelCount)
            {
                continue;
            }

            OverlayRect scope = _overlay.Layout.GetScopeRect(channel.PanelIndex);
            int width = Math.Min(_gridWidth - scope.X, scope.Width);
            if (width <= 0 || scopeHeight <= 0)
                continue;

            DrawCell(
                destination,
                scope.X,
                row * scopeHeight,
                width,
                scopeHeight,
                frameIndex,
                channel);
        }
    }

    private void DrawCell(
        Span<byte> destination,
        int cellX,
        int cellY,
        int width,
        int height,
        int frameIndex,
        ChannelState channel)
    {
        int sampleRate = channel.Wave.SampleRate;
        long currentSample = OverlayLayout.FrameToSample(
            frameIndex, sampleRate, _fpsNumerator, _fpsDenominator);

        long startSample = _overlay.Layout.WindowStartSample(currentSample, sampleRate);
        long endSample = _overlay.Layout.WindowEndSample(currentSample, sampleRate);
        long windowSamples = Math.Max(0, endSample - startSample);
        if (windowSamples <= 0)
            return;

        double requestedGain = double.IsFinite(channel.DefaultAmplification)
            ? channel.DefaultAmplification
            : 1.0;
        double gain = Math.Clamp(requestedGain, 0.25, 12.0);

        int yCenter = cellY + height / 2;
        int scale = Math.Max(1, height / 2 - 1);

        for (int x = 0; x < width; x++)
        {
            long sampleStart = startSample + x * windowSamples / width;
            long sampleEnd = startSample + (x + 1L) * windowSamples / width;
            if (sampleEnd <= sampleStart)
                sampleEnd = sampleStart + 1;
            if (sampleEnd <= 0 || sampleStart >= channel.Wave.TotalSamples)
            {
                DrawColumn(destination, cellX, cellY, height, x, 0, 0, gain, channel.Color);
                continue;
            }
            sampleStart = Math.Max(0, sampleStart);
            sampleEnd = Math.Min(channel.Wave.TotalSamples, sampleEnd);
            int firstBucket = (int)Math.Clamp(sampleStart / channel.BucketSize, 0, channel.Minimums.Length - 1);
            int lastBucket = (int)Math.Clamp((sampleEnd - 1) / channel.BucketSize, 0, channel.Minimums.Length - 1);
            int min = 0;
            int max = 0;
            for (int bucket = firstBucket; bucket <= lastBucket; bucket++)
            {
                min = Math.Min(min, channel.Minimums[bucket]);
                max = Math.Max(max, channel.Maximums[bucket]);
            }

            int yTop = yCenter - (int)Math.Round(max * gain * scale / short.MaxValue);
            int yBottom = yCenter - (int)Math.Round(min * gain * scale / short.MaxValue);
            yTop = Math.Max(cellY, yTop);
            yBottom = Math.Min(cellY + height - 1, yBottom);
            for (int y = yTop; y <= yBottom; y++)
            {
                int offset = (y * _gridWidth + cellX + x) * 4;
                destination[offset] = channel.Color[0];
                destination[offset + 1] = channel.Color[1];
                destination[offset + 2] = channel.Color[2];
                destination[offset + 3] = channel.Color[3];
            }
        }
    }

    private void DrawColumn(
        Span<byte> destination, int cellX, int cellY, int height, int x,
        int min, int max, double gain, byte[] color)
    {
        int yCenter = cellY + height / 2;
        int scale = Math.Max(1, height / 2 - 1);
        int yTop = Math.Max(cellY, yCenter - (int)Math.Round(max * gain * scale / short.MaxValue));
        int yBottom = Math.Min(cellY + height - 1, yCenter - (int)Math.Round(min * gain * scale / short.MaxValue));
        for (int y = yTop; y <= yBottom; y++)
        {
            int offset = (y * _gridWidth + cellX + x) * 4;
            destination[offset] = color[0];
            destination[offset + 1] = color[1];
            destination[offset + 2] = color[2];
            destination[offset + 3] = color[3];
        }
    }

    private static void BuildPeakEnvelope(
        ChannelState channel, double visibleSeconds, int pixelWidth)
    {
        long visibleSamples = Math.Max(1, (long)Math.Round(visibleSeconds * channel.Wave.SampleRate));
        long targetBuckets = Math.Max(1, (long)Math.Max(1, pixelWidth) * 2);
        channel.BucketSize = (int)Math.Clamp((visibleSamples + targetBuckets - 1) / targetBuckets, 1, int.MaxValue);
        int bucketCount = checked((int)((channel.Wave.TotalSamples + channel.BucketSize - 1) / channel.BucketSize));
        channel.Minimums = new int[bucketCount];
        channel.Maximums = new int[bucketCount];

        const int ChunkSamples = 64 * 1024;
        var samples = new short[ChunkSamples];
        for (long offset = 0; offset < channel.Wave.TotalSamples; offset += ChunkSamples)
        {
            int count = (int)Math.Min(ChunkSamples, channel.Wave.TotalSamples - offset);
            channel.Wave.ReadWindow(offset, samples.AsSpan(0, count));
            for (int i = 0; i < count; i++)
            {
                short value = samples[i];
                int bucket = (int)((offset + i) / channel.BucketSize);
                int min = channel.Minimums[bucket];
                int max = channel.Maximums[bucket];
                if (value < min) min = value;
                if (value > max) max = value;
                channel.Minimums[bucket] = min;
                channel.Maximums[bucket] = max;
            }
        }
    }

    public void Dispose()
    {
        foreach (ChannelState c in _channels)
            c.Dispose();
    }

    private sealed class ChannelState : IDisposable
    {
        public required int PanelIndex { get; init; }
        public required SeekablePcm16WaveFile Wave { get; init; }
        public required int WindowWidth { get; init; }
        public required double DefaultAmplification { get; init; }
        public required byte[] Color { get; init; }
        public int BucketSize;
        public int[] Minimums = Array.Empty<int>();
        public int[] Maximums = Array.Empty<int>();

        public void Dispose()
        {
            Wave.Dispose();
        }
    }
}
