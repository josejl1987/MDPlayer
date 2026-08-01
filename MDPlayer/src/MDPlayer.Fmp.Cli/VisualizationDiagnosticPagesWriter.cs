using System.Net;
using Fmp.Core.Visualization;
using Fmp.Core.Visualization.Rendering;

namespace Fmp.Cli;

/// <summary>
/// Exports stable, paginated SVG diagnostic sheets for large topologies.
/// Pages retain track order and use the same event timeline as video output.
/// </summary>
internal static class VisualizationDiagnosticPagesWriter
{
    private const int TracksPerPage = 12;
    private const int Width = 1600;
    private const int Height = 900;

    public static void Write(
        string directory,
        VisualizationTimeline timeline,
        VisualizationLayoutPlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(timeline);
        ArgumentNullException.ThrowIfNull(plan);

        string output = Path.GetFullPath(directory);
        Directory.CreateDirectory(output);
        VisualizationLayoutTrackPlan[] tracks = plan.Tracks.ToArray();
        int pageCount = Math.Max(1, (tracks.Length + TracksPerPage - 1) / TracksPerPage);
        var links = new List<string>(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int first = page * TracksPerPage;
            VisualizationLayoutTrackPlan[] pageTracks = tracks
                .Skip(first)
                .Take(TracksPerPage)
                .ToArray();
            string fileName = $"page-{page + 1:000}.svg";
            File.WriteAllText(
                Path.Combine(output, fileName),
                BuildSvg(timeline, pageTracks, page + 1, pageCount));
            links.Add($"<li><a href=\"{fileName}\">Page {page + 1}</a> ({pageTracks.Length} tracks)</li>");
        }

        File.WriteAllText(
            Path.Combine(output, "index.html"),
            $"<!doctype html><meta charset=\"utf-8\"><title>MDPlayer diagnostic pages</title>" +
            $"<h1>MDPlayer diagnostic pages</h1><p>{pageCount} pages</p><ul>{string.Join("", links)}</ul>");
    }

    private static string BuildSvg(
        VisualizationTimeline timeline,
        IReadOnlyList<VisualizationLayoutTrackPlan> tracks,
        int page,
        int pageCount)
    {
        long length = Math.Max(1, timeline.EndSample - timeline.StartSample);
        int laneTop = 90;
        int laneHeight = Math.Max(40, (Height - laneTop - 40) / Math.Max(1, tracks.Count));
        var svg = new System.Text.StringBuilder();
        string title = timeline.Source?.Title ?? "Untitled";
        svg.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {Width} {Height}\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"#0b0c13\"/>");
        svg.Append($"<text x=\"32\" y=\"40\" fill=\"#dee2ee\" font-family=\"sans-serif\" font-size=\"24\">MDPlayer diagnostic page {page}/{pageCount}</text>");
        svg.Append($"<text x=\"32\" y=\"68\" fill=\"#8b92a7\" font-family=\"sans-serif\" font-size=\"14\">{Escape(title)}</text>");

        for (int index = 0; index < tracks.Count; index++)
        {
            VisualizationLayoutTrackPlan track = tracks[index];
            int y = laneTop + index * laneHeight;
            svg.Append($"<rect x=\"0\" y=\"{y}\" width=\"{Width}\" height=\"{laneHeight - 2}\" fill=\"#0f1119\" stroke=\"#303442\"/>");
            svg.Append($"<text x=\"12\" y=\"{y + 20}\" fill=\"#dee2ee\" font-family=\"sans-serif\" font-size=\"14\">{Escape(track.Label)} · {Escape(track.Kind)}</text>");
            foreach (NoteEvent note in timeline.Notes.Where(note => track.SourceVoiceIds.Contains(note.ChannelId)))
            {
                double x = 190 + (note.StartSample - timeline.StartSample) / (double)length * (Width - 210);
                double right = 190 + (note.EndSample - timeline.StartSample) / (double)length * (Width - 210);
                svg.Append($"<rect x=\"{x:0.##}\" y=\"{y + 28}\" width=\"{Math.Max(2, right - x):0.##}\" height=\"{Math.Max(4, laneHeight - 36)}\" fill=\"{Color(note.InstrumentId)}\" opacity=\".82\"/>");
            }
        }

        svg.Append("</svg>");
        return svg.ToString();
    }

    private static string Color(string value)
    {
        uint hash = 2166136261;
        foreach (char character in value ?? string.Empty)
            hash = (hash ^ character) * 16777619;
        return $"#{(hash >> 16 & 0x7f) + 0x60:X2}{(hash >> 8 & 0x7f) + 0x60:X2}{(hash & 0x7f) + 0x70:X2}";
    }

    private static string Escape(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
