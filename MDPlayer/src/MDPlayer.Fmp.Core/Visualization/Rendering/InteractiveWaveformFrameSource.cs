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
/// Scope triggering is deliberately approximated (fixed 16 ms window, local
/// normalization) rather than reproducing Corrscope's stateful correlation
/// trigger; exact output remains available through the production renderer.
/// Frame rendering is order-independent and deterministic — seeking backward
/// yields the same frame as seeking directly to that position.
/// </summary>
internal sealed class InteractiveWaveformFrameSource : IScopeFrameSource
{
    // Fixed temporal window (not one output video frame) so the waveform's
    // scale does not change when the user selects 30/60/120 FPS. Matches the
    // FMP Corrscope render-window default.
    private const double BaseWindowMilliseconds = 16.0;

    private const byte FallbackColorR = 0x7A;
    private const byte FallbackColorG = 0xA4;
    private const byte FallbackColorB = 0xFF;

    private readonly PanelOverlayRenderer _overlay;
    private readonly int _fpsNumerator;
    private readonly int _fpsDenominator;
    private readonly int _gridWidth;
    private readonly ChannelState[] _channels;

    /// <summary>Number of mapped panels whose stem WAV could not be opened.</summary>
    public int UnavailableChannelCount { get; }

    private InteractiveWaveformFrameSource(
        PanelOverlayRenderer overlay,
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
        PanelOverlayRenderer overlay,
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

            states.Add(new ChannelState
            {
                PanelIndex = channel.PanelIndex,
                Wave = wave,
                WindowWidth = channel.WindowWidth,
                DefaultAmplification = channel.DefaultAmplification,
                Color = ParseColor(channel.DefaultColor),
            });
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
        byte[] fallback = { FallbackColorR, FallbackColorG, FallbackColorB, 0xFF };
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
        // Start fully transparent; the overlay's static scope background (and
        // any other panel's waveform) remains visible beneath each channel.
        destination.Clear();

        int columnCount = _overlay.Layout.ColumnCount;
        int scopeHeight = _overlay.Layout.ScopeHeight;

        foreach (ChannelState channel in _channels)
        {
            int row = channel.PanelIndex / columnCount;
            int column = channel.PanelIndex % columnCount;

            OverlayRect scope = _overlay.Layout.GetScopeRect(channel.PanelIndex);
            int width = Math.Min(_gridWidth / Math.Max(1, columnCount), scope.Width);
            if (width <= 0 || scopeHeight <= 0)
                continue;

            DrawCell(
                destination,
                column * width,
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

        int samples = (int)Math.Ceiling(
            sampleRate * BaseWindowMilliseconds / 1000.0 * Math.Max(1, channel.WindowWidth));
        if (samples <= 0)
            return;

        long startSample = currentSample - samples / 2;
        if (channel.Samples.Length < samples)
            channel.Samples = new short[samples];
        Span<short> window = channel.Samples.AsSpan(0, samples);
        channel.Wave.ReadWindow(startSample, window);

        // Window-local normalization: keeps quiet channels visible without a
        // full-file peak scan, and keeps rendering order-independent.
        int peak = MaxAbsolute(window);
        double autoGain = peak <= 0
            ? 1.0
            : Math.Clamp(0.82 * short.MaxValue / peak, 0.5, 8.0);
        double gain = Math.Clamp(autoGain * channel.DefaultAmplification, 0.25, 12.0);

        int yCenter = cellY + height / 2;
        int scale = Math.Max(1, height / 2 - 1);

        for (int x = 0; x < width; x++)
        {
            long s0 = (long)x * samples / width;
            long s1 = (long)(x + 1) * samples / width;
            if (s1 <= s0)
                s1 = s0 + 1;

            int min = 0;
            int max = 0;
            for (long s = s0; s < s1 && s < samples; s++)
            {
                short v = window[(int)s];
                if (v < min) min = v;
                if (v > max) max = v;
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

    private static int MaxAbsolute(ReadOnlySpan<short> samples)
    {
        int peak = 0;
        foreach (short s in samples)
        {
            int abs = s < 0 ? -s : s;
            if (abs > peak) peak = abs;
        }
        return peak;
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
        public short[] Samples = Array.Empty<short>();

        public void Dispose()
        {
            Wave.Dispose();
        }
    }
}
