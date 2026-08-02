using System.Text.Json;

namespace Fmp.Core.Visualization.Rendering;

/// <summary>
/// User-selectable renderer palette. Colors are resolved before frame rendering
/// so palette choice cannot introduce per-frame parsing or allocations.
/// </summary>
internal sealed record VisualizationPalette(
    OverlayColor CanvasBackground,
    OverlayColor HeaderBackground,
    OverlayColor TimelineBackground,
    OverlayColor BlackKeyBand,
    OverlayColor GridLine,
    OverlayColor Border,
    OverlayColor MutedText,
    OverlayColor BrightText,
    OverlayColor Playhead,
    IReadOnlyList<OverlayColor> NoteColors,
    IReadOnlyList<OverlayColor> AccentColors)
{
    public static VisualizationPalette Default { get; } = new(
        new OverlayColor(11, 12, 19),
        new OverlayColor(19, 21, 31),
        new OverlayColor(15, 17, 25),
        new OverlayColor(11, 13, 20),
        new OverlayColor(48, 52, 66, 110),
        new OverlayColor(76, 82, 103, 210),
        new OverlayColor(139, 146, 167),
        new OverlayColor(222, 226, 238),
        new OverlayColor(238, 241, 250, 125),
        Array.Empty<OverlayColor>(),
        Array.Empty<OverlayColor>());

    /// <summary>
    /// High-contrast, colorblind-safe palette. Text and grid lines are brighter
    /// against a deeper background for WCAG-style readability, and note/accent
    /// colours use a blue/orange ramp that remains distinguishable for the most
    /// common forms of colour-vision deficiency (protanopia/deuteranopia).
    /// </summary>
    public static VisualizationPalette Accessible { get; } = new(
        new OverlayColor(4, 5, 8),
        new OverlayColor(14, 16, 24),
        new OverlayColor(10, 12, 18),
        new OverlayColor(6, 8, 14),
        new OverlayColor(140, 148, 168, 210),
        new OverlayColor(150, 158, 178, 255),
        new OverlayColor(196, 202, 218),
        new OverlayColor(246, 248, 252),
        new OverlayColor(255, 214, 84, 160),
        Array.Empty<OverlayColor>(),
        new[]
        {
            new OverlayColor(0, 114, 189),   // blue
            new OverlayColor(230, 159, 0),   // orange
            new OverlayColor(86, 180, 233),  // sky
            new OverlayColor(240, 228, 66),  // yellow
            new OverlayColor(204, 121, 167), // amethyst
        });

    public OverlayColor ResolveAccent(string stableId, int fallbackIndex)
    {
        if (AccentColors.Count == 0)
            return InstrumentColorResolver.ResolveChannelAccent(stableId);
        return AccentColors[(int)(StableHash(stableId) % (uint)AccentColors.Count)];
    }

    public OverlayColor ResolveNote(
        NoteColorMode mode,
        string instrumentId,
        string channelId,
        double midiNote)
    {
        if (NoteColors.Count == 0)
            return InstrumentColorResolver.ResolveFill(mode, instrumentId, channelId, midiNote);

        string key = mode switch
        {
            NoteColorMode.Channel => channelId,
            NoteColorMode.Pitch => $"pitch:{(int)Math.Round(midiNote)}",
            _ => instrumentId,
        } ?? string.Empty;
        return NoteColors[(int)(StableHash(key) % (uint)NoteColors.Count)];
    }

    public static VisualizationPalette ParseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new ArgumentException("palette JSON is empty", nameof(json));

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        VisualizationPalette defaults = Default;
        return new VisualizationPalette(
            ParseColor(root, "canvasBackground", defaults.CanvasBackground),
            ParseColor(root, "headerBackground", defaults.HeaderBackground),
            ParseColor(root, "timelineBackground", defaults.TimelineBackground),
            ParseColor(root, "blackKeyBand", defaults.BlackKeyBand),
            ParseColor(root, "gridLine", defaults.GridLine),
            ParseColor(root, "border", defaults.Border),
            ParseColor(root, "mutedText", defaults.MutedText),
            ParseColor(root, "brightText", defaults.BrightText),
            ParseColor(root, "playhead", defaults.Playhead),
            ParseColors(root, "noteColors"),
            ParseColors(root, "accentColors"));
    }

    private static OverlayColor[] ParseColors(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out JsonElement values))
            return Array.Empty<OverlayColor>();
        if (values.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"palette property '{property}' must be an array");
        return values.EnumerateArray()
            .Select(value => ParseColor(value.GetString(), property))
            .ToArray();
    }

    private static OverlayColor ParseColor(
        JsonElement root,
        string property,
        OverlayColor fallback)
        => root.TryGetProperty(property, out JsonElement value)
            ? ParseColor(value.GetString(), property)
            : fallback;

    private static OverlayColor ParseColor(string value, string property)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"palette color '{property}' is empty");
        string hex = value.Trim().TrimStart('#');
        if (hex.Length is not (6 or 8)
            || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out uint packed))
        {
            throw new ArgumentException(
                $"palette color '{property}' must be #RRGGBB or #RRGGBBAA");
        }

        return hex.Length == 6
            ? new OverlayColor(
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed)
            : new OverlayColor(
                (byte)(packed >> 24),
                (byte)(packed >> 16),
                (byte)(packed >> 8),
                (byte)packed);
    }

    private static uint StableHash(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value ?? string.Empty)
        {
            hash = (hash ^ character) * 16777619;
        }
        return hash;
    }
}
