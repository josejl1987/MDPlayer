using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Fmp.Application.Contracts;
using System.Text;

namespace Fmp.Application.Review;

public static class ContactSheetWriter
{
    private const int MaxRowsPerSheet = 12;
    private const int OverviewWidth = 480;
    private const int OverviewHeight = 270;
    private const int LargeWidth = 640;
    private const int LargeHeight = 360;
    private const int CaptionHeight = 58;

    public static IReadOnlyList<string> WriteAll(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        string directory = Path.Combine(result.OutputPath, "contact-sheets");
        string chipDirectory = Path.Combine(directory, "chips");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(chipDirectory);
        var paths = new List<string>();

        foreach (ReviewResolution resolution in ReviewResolution.All)
        {
            paths.AddRange(WriteOverview(result, resolution, directory));
            foreach (CompositionKind composition in Enum.GetValues<CompositionKind>())
                paths.AddRange(WriteComposition(result, composition, resolution, directory));
        }

        foreach (string chip in result.DetectedChips)
            paths.AddRange(WriteChip(result, chip, chipDirectory));
        return paths;
    }

    private static IEnumerable<string> WriteOverview(
        ReviewResult result,
        ReviewResolution resolution,
        string directory)
    {
        var rows = new List<IReadOnlyList<SheetTile>>();
        foreach (ReviewSourceResult source in result.Sources)
        {
            ReviewCaseResult? first = source.Cases.FirstOrDefault();
            if (first is null)
                continue;
            ReviewMoment moment = source.Entry.Moments.FirstOrDefault()
                ?? first.Moment;
            rows.Add(Enum.GetValues<CompositionKind>().Select(composition =>
                TileFor(source, moment, composition, resolution)).ToArray());
        }
        return WriteSheets(result, "overview-" + resolution.Name, rows, OverviewWidth, OverviewHeight, directory);
    }

    private static IEnumerable<string> WriteComposition(
        ReviewResult result,
        CompositionKind composition,
        ReviewResolution resolution,
        string directory)
    {
        var rows = new List<IReadOnlyList<SheetTile>>();
        foreach (ReviewSourceResult source in result.Sources)
        {
            ReviewMoment[] moments = source.Entry.Moments.Count == 0
                ? source.Cases.Select(item => item.Moment).Distinct().ToArray()
                : source.Entry.Moments.ToArray();
            if (moments.Length == 0)
                continue;
            rows.Add(moments.Select(moment => TileFor(source, moment, composition, resolution)).ToArray());
        }
        return WriteSheets(
            result,
            $"{ReviewGenerator.CompositionName(composition)}-{resolution.Name}",
            rows,
            LargeWidth,
            LargeHeight,
            directory);
    }

    private static IEnumerable<string> WriteChip(ReviewResult result, string chip, string directory)
    {
        var rows = new List<IReadOnlyList<SheetTile>>();
        foreach (ReviewSourceResult source in result.Sources.Where(source =>
            source.DetectedChips.Contains(chip, StringComparer.OrdinalIgnoreCase)
            || source.ActiveChips.Contains(chip, StringComparer.OrdinalIgnoreCase)))
        {
            ReviewCaseResult? first = source.Cases.FirstOrDefault();
            if (first is null)
                continue;
            ReviewMoment moment = source.Entry.Moments.FirstOrDefault() ?? first.Moment;
            rows.Add(Enum.GetValues<CompositionKind>().Select(composition =>
                TileFor(source, moment, composition, ReviewResolution.P1080)).ToArray());
        }
        return WriteSheets(result, chip, rows, LargeWidth, LargeHeight, directory);
    }

    private static SheetTile TileFor(
        ReviewSourceResult source,
        ReviewMoment moment,
        CompositionKind composition,
        ReviewResolution resolution)
    {
        ReviewCaseResult? item = source.Cases.FirstOrDefault(candidate =>
            candidate.Moment.Name == moment.Name
            && candidate.Composition == composition
            && candidate.Resolution.Name == resolution.Name);
        return new SheetTile(
            item,
            source.Entry.Label ?? Path.GetFileName(source.Entry.Path),
            moment,
            composition,
            resolution,
            source.DetectedChips);
    }

    private static IEnumerable<string> WriteSheets(
        ReviewResult result,
        string name,
        IReadOnlyList<IReadOnlyList<SheetTile>> rows,
        int thumbnailWidth,
        int thumbnailHeight,
        string directory)
    {
        if (rows.Count == 0)
            return Array.Empty<string>();
        int columns = rows.Max(row => row.Count);
        var paths = new List<string>();
        for (int chunkStart = 0, page = 1; chunkStart < rows.Count; chunkStart += MaxRowsPerSheet, page++)
        {
            IReadOnlyList<IReadOnlyList<SheetTile>> chunk = rows
                .Skip(chunkStart).Take(MaxRowsPerSheet).ToArray();
            string fileName = rows.Count <= MaxRowsPerSheet
                ? $"{name}.png"
                : $"{name}-{page}.png";
            string path = Path.Combine(directory, fileName);
            using var sheet = new Image<Rgba32>(
                Math.Max(1, columns * thumbnailWidth),
                chunk.Count * (thumbnailHeight + CaptionHeight));
            Fill(sheet, new Rgba32(12, 15, 21, 255));
            for (int row = 0; row < chunk.Count; row++)
            {
                IReadOnlyList<SheetTile> tiles = chunk[row];
                for (int column = 0; column < tiles.Count; column++)
                {
                    DrawTile(result.OutputPath, sheet, tiles[column], column * thumbnailWidth,
                        row * (thumbnailHeight + CaptionHeight), thumbnailWidth, thumbnailHeight);
                }
            }
            sheet.SaveAsPng(path);
            paths.Add(path);
        }
        return paths;
    }

    private static void DrawTile(
        string outputPath,
        Image<Rgba32> sheet,
        SheetTile tile,
        int x,
        int y,
        int width,
        int height)
    {
        DrawTileImage(outputPath, sheet, tile, x, y, width, height);
        string caption = $"{tile.SourceName} | {tile.Moment.Name} {tile.Moment.TimeSeconds:0.###}s | " +
                         $"{ReviewGenerator.CompositionName(tile.Composition)} {tile.Resolution.Name} | " +
                         $"chips: {(tile.Chips.Count == 0 ? "none" : string.Join(',', tile.Chips))}";
        DrawCaption(sheet, caption, x, y + height, width, CaptionHeight);
    }

    private static void DrawTileImage(
        string outputPath,
        Image<Rgba32> sheet,
        SheetTile tile,
        int x,
        int y,
        int width,
        int height)
    {
        if (tile.Case?.Status == ReviewCaseStatus.Rendered && tile.Case.ImagePath is not null)
        {
            string imagePath = Path.Combine(
                outputPath,
                tile.Case.ImagePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(imagePath))
            {
                using Image<Rgba32> source = Image.Load<Rgba32>(imagePath);
                source.Mutate(context => context.Resize(width, height));
                var pixels = new Rgba32[width * height];
                source.ProcessPixelRows(sourceAccessor =>
                {
                    for (int row = 0; row < height; row++)
                        sourceAccessor.GetRowSpan(row).CopyTo(pixels.AsSpan(row * width, width));
                });
                sheet.ProcessPixelRows(sheetAccessor =>
                {
                    for (int row = 0; row < height; row++)
                        pixels.AsSpan(row * width, width).CopyTo(
                            sheetAccessor.GetRowSpan(y + row).Slice(x, width));
                });
                return;
            }
        }
        DrawPlaceholder(sheet, x, y, width, height, tile.Case?.Status, tile.Case?.Reason);
    }

    private static void DrawPlaceholder(
        Image<Rgba32> sheet,
        int x,
        int y,
        int width,
        int height,
        ReviewCaseStatus? status,
        string? reason)
    {
        Rgba32 color = status == ReviewCaseStatus.Failed
            ? new Rgba32(91, 32, 29, 255)
            : new Rgba32(96, 71, 13, 255);
        FillRect(sheet, x, y, width, height, color);
        DrawText(sheet, x + 12, y + height / 2 - 4, status?.ToString().ToUpperInvariant() ?? "NOT GENERATED", 2,
            new Rgba32(255, 239, 205, 255), width - 24);
        if (!string.IsNullOrWhiteSpace(reason))
            DrawText(sheet, x + 12, y + height / 2 + 14, reason.ToUpperInvariant(), 1,
                new Rgba32(255, 239, 205, 255), width - 24);
    }

    private static void Fill(Image<Rgba32> image, Rgba32 color)
        => image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
                accessor.GetRowSpan(y).Fill(color);
        });

    private static void FillRect(Image<Rgba32> image, int x, int y, int width, int height, Rgba32 color)
    {
        int left = Math.Max(0, x);
        int top = Math.Max(0, y);
        int right = Math.Min(image.Width, x + width);
        int bottom = Math.Min(image.Height, y + height);
        image.ProcessPixelRows(accessor =>
        {
            for (int row = top; row < bottom; row++)
                accessor.GetRowSpan(row)[left..right].Fill(color);
        });
    }

    private static void DrawCaption(Image<Rgba32> image, string text, int x, int y, int width, int height)
    {
        FillRect(image, x, y, width, height, new Rgba32(27, 32, 41, 255));
        const int scale = 2;
        int maxChars = Math.Max(1, (width - 16) / (6 * scale));
        string[] words = text.ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (string word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > maxChars)
            {
                lines.Add(line.ToString());
                line.Clear();
            }
            if (line.Length > 0)
                line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0)
            lines.Add(line.ToString());
        for (int index = 0; index < Math.Min(3, lines.Count); index++)
            DrawText(image, x + 8, y + 6 + index * 16, lines[index], scale,
                new Rgba32(231, 234, 240, 255), width - 16);
    }

    private static void DrawText(Image<Rgba32> image, int x, int y, string text, int scale, Rgba32 color, int maxWidth)
    {
        int cursor = x;
        foreach (char character in text)
        {
            if (cursor + 6 * scale > x + maxWidth)
                break;
            byte[] glyph = Glyphs.TryGetValue(character, out byte[]? value) && value is not null
                ? value
                : Glyphs['?'];
            for (int row = 0; row < 7; row++)
            {
                for (int bit = 0; bit < 5; bit++)
                {
                    if ((glyph[row] & (1 << (4 - bit))) == 0)
                        continue;
                    FillRect(image, cursor + bit * scale, y + row * scale, scale, scale, color);
                }
            }
            cursor += 6 * scale;
        }
    }

    private sealed record SheetTile(
        ReviewCaseResult? Case,
        string SourceName,
        ReviewMoment Moment,
        CompositionKind Composition,
        ReviewResolution Resolution,
        IReadOnlyList<string> Chips);

    private static readonly Dictionary<char, byte[]> Glyphs = new()
    {
        ['?'] = [14, 17, 1, 2, 4, 0, 4],
        [' '] = [0, 0, 0, 0, 0, 0, 0],
        ['-'] = [0, 0, 0, 31, 0, 0, 0],
        ['.'] = [0, 0, 0, 0, 0, 6, 6],
        [','] = [0, 0, 0, 0, 0, 6, 4],
        [':'] = [0, 6, 6, 0, 6, 6, 0],
        ['/'] = [1, 2, 4, 8, 16, 0, 0],
        ['|'] = [4, 4, 4, 4, 4, 4, 4],
        ['~'] = [0, 0, 0, 10, 21, 0, 0],
        ['('] = [2, 4, 8, 8, 8, 4, 2],
        [')'] = [8, 4, 2, 2, 2, 4, 8],
        ['_'] = [0, 0, 0, 0, 0, 0, 31],
        ['0'] = [14, 17, 19, 21, 25, 17, 14],
        ['1'] = [4, 12, 4, 4, 4, 4, 14],
        ['2'] = [14, 17, 1, 2, 4, 8, 31],
        ['3'] = [30, 1, 1, 14, 1, 1, 30],
        ['4'] = [2, 6, 10, 18, 31, 2, 2],
        ['5'] = [31, 16, 16, 30, 1, 1, 30],
        ['6'] = [14, 16, 16, 30, 17, 17, 14],
        ['7'] = [31, 1, 2, 4, 8, 8, 8],
        ['8'] = [14, 17, 17, 14, 17, 17, 14],
        ['9'] = [14, 17, 17, 15, 1, 1, 14],
        ['A'] = [14, 17, 17, 31, 17, 17, 17],
        ['B'] = [30, 17, 17, 30, 17, 17, 30],
        ['C'] = [14, 17, 16, 16, 16, 17, 14],
        ['D'] = [30, 17, 17, 17, 17, 17, 30],
        ['E'] = [31, 16, 16, 30, 16, 16, 31],
        ['F'] = [31, 16, 16, 30, 16, 16, 16],
        ['G'] = [14, 17, 16, 23, 17, 17, 15],
        ['H'] = [17, 17, 17, 31, 17, 17, 17],
        ['I'] = [14, 4, 4, 4, 4, 4, 14],
        ['J'] = [7, 2, 2, 2, 2, 18, 12],
        ['K'] = [17, 18, 20, 24, 20, 18, 17],
        ['L'] = [16, 16, 16, 16, 16, 16, 31],
        ['M'] = [17, 27, 21, 21, 17, 17, 17],
        ['N'] = [17, 25, 21, 19, 17, 17, 17],
        ['O'] = [14, 17, 17, 17, 17, 17, 14],
        ['P'] = [30, 17, 17, 30, 16, 16, 16],
        ['Q'] = [14, 17, 17, 17, 21, 18, 13],
        ['R'] = [30, 17, 17, 30, 20, 18, 17],
        ['S'] = [15, 16, 16, 14, 1, 1, 30],
        ['T'] = [31, 4, 4, 4, 4, 4, 4],
        ['U'] = [17, 17, 17, 17, 17, 17, 14],
        ['V'] = [17, 17, 17, 17, 17, 10, 4],
        ['W'] = [17, 17, 17, 21, 21, 21, 10],
        ['X'] = [17, 17, 10, 4, 10, 17, 17],
        ['Y'] = [17, 17, 10, 4, 4, 4, 4],
        ['Z'] = [31, 1, 2, 4, 8, 16, 31],
    };
}
