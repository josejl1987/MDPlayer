namespace Fmp.Core.Visualization.Rendering;

internal readonly record struct OverlayColor(byte R, byte G, byte B, byte A = 255)
{
    public OverlayColor WithAlpha(byte alpha) => new(R, G, B, alpha);

    public OverlayColor Lighten(double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return new OverlayColor(
            (byte)Math.Clamp(Math.Round(R + (255 - R) * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(G + (255 - G) * amount), 0, 255),
            (byte)Math.Clamp(Math.Round(B + (255 - B) * amount), 0, 255),
            A);
    }
}

/// <summary>Stable instrument fills and fixed per-channel accents.</summary>
internal static class InstrumentColorResolver
{
    private static readonly double[] ChannelHues =
    [
        5, 35, 65, 175, 205, 235,
        155, 185, 215, 30, 280, 320,
    ];

    public static OverlayColor ResolveInstrumentFill(string instrumentId)
    {
        ulong hash = StableHash64(instrumentId ?? "");
        double hue = hash % 360;
        double saturation = 0.58 + ((hash >> 9) & 0x0F) / 100.0;
        double lightness = 0.54 + ((hash >> 17) & 0x0F) / 120.0;
        return HslToRgb(hue, Math.Min(saturation, 0.76), Math.Min(lightness, 0.67));
    }

    public static OverlayColor ResolveChannelAccent(int panelIndex)
    {
        if (panelIndex < 0 || panelIndex >= ChannelHues.Length)
            return new OverlayColor(210, 210, 220);
        return HslToRgb(ChannelHues[panelIndex], 0.72, 0.68);
    }

    /// <summary>
    /// Resolves a channel accent from its stable semantic identity. Layout
    /// order is deliberately not an input, so filtering or grouping cannot
    /// recolor a voice halfway through a migration.
    /// </summary>
    public static OverlayColor ResolveChannelAccent(string stableChannelId)
    {
        ulong hash = StableHash64(stableChannelId ?? "");
        return HslToRgb(hash % 360, 0.72, 0.68);
    }

    public static OverlayColor ResolveChannelAccent(string stableChannelId, int stableOrder)
        => ResolveChannelAccent(stableChannelId);

    private static readonly double[] PitchClassHues =
    [
        20, 50, 80, 120, 160, 190, 220, 260, 290, 320, 350, 10,
    ];

    public static OverlayColor ResolvePitchClassFill(double midiNote)
    {
        int pitchClass = ((int)Math.Round(midiNote) % 12 + 12) % 12;
        int octave = Math.Clamp((int)Math.Round(midiNote) / 12 - 1, 0, 9);
        double lightness = 0.52 + (octave - 4) * 0.02;
        return HslToRgb(PitchClassHues[pitchClass], 0.68, Math.Clamp(lightness, 0.42, 0.68));
    }

    public static OverlayColor ResolveChannelFill(int panelIndex, double midiNote)
    {
        if (panelIndex < 0 || panelIndex >= ChannelHues.Length)
            return new OverlayColor(200, 200, 210);
        int octave = Math.Clamp((int)Math.Round(midiNote) / 12 - 1, 0, 9);
        double lightness = 0.48 + (octave - 4) * 0.025;
        return HslToRgb(ChannelHues[panelIndex], 0.60, Math.Clamp(lightness, 0.40, 0.66));
    }

    public static OverlayColor ResolveChannelFill(string stableChannelId, double midiNote)
    {
        ulong hash = StableHash64(stableChannelId ?? "");
        double hue = hash % 360;
        int octave = Math.Clamp((int)Math.Round(midiNote) / 12 - 1, 0, 9);
        double lightness = 0.48 + (octave - 4) * 0.025;
        return HslToRgb(hue, 0.60, Math.Clamp(lightness, 0.40, 0.66));
    }

    public static OverlayColor ResolveFill(NoteColorMode mode, string instrumentId, int panelIndex, double midiNote)
        => mode switch
        {
            NoteColorMode.Pitch => ResolvePitchClassFill(midiNote),
            NoteColorMode.Channel => ResolveChannelFill(panelIndex, midiNote),
            _ => ResolveInstrumentFill(instrumentId),
        };

    public static OverlayColor ResolveFill(
        NoteColorMode mode,
        string instrumentId,
        string stableChannelId,
        double midiNote)
        => mode switch
        {
            NoteColorMode.Pitch => ResolvePitchClassFill(midiNote),
            NoteColorMode.Channel => ResolveChannelFill(stableChannelId, midiNote),
            _ => ResolveInstrumentFill(instrumentId),
        };

    private static ulong StableHash64(string text)
    {
        const ulong offset = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = offset;
        foreach (char character in text)
        {
            hash ^= (byte)(character & 0xFF);
            hash *= prime;
            hash ^= (byte)(character >> 8);
            hash *= prime;
        }
        return hash;
    }

    private static OverlayColor HslToRgb(double hueDegrees, double saturation, double lightness)
    {
        double hue = ((hueDegrees % 360) + 360) % 360 / 360.0;
        double r;
        double g;
        double b;

        if (saturation <= 0)
        {
            r = g = b = lightness;
        }
        else
        {
            double q = lightness < 0.5
                ? lightness * (1 + saturation)
                : lightness + saturation - lightness * saturation;
            double p = 2 * lightness - q;
            r = HueToRgb(p, q, hue + 1.0 / 3.0);
            g = HueToRgb(p, q, hue);
            b = HueToRgb(p, q, hue - 1.0 / 3.0);
        }

        return new OverlayColor(
            (byte)Math.Clamp(Math.Round(r * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(g * 255), 0, 255),
            (byte)Math.Clamp(Math.Round(b * 255), 0, 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }
}
