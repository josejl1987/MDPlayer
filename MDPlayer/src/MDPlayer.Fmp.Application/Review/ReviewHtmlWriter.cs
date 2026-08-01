using System.Globalization;
using System.Net;
using System.Text;
using Fmp.Application.Contracts;

namespace Fmp.Application.Review;

public static class ReviewHtmlWriter
{
    public static string Write(ReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var html = new StringBuilder("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        html.Append("<meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        html.Append("<title>MDPlayer visual review</title><style>");
        html.Append(Css);
        html.Append("</style></head><body><main><h1>MDPlayer visual review</h1>");
        html.Append("<p class=\"meta\">Generated ")
            .Append(DateTimeOffset.Now.ToString("u", CultureInfo.InvariantCulture))
            .Append("</p>");
        WriteSummary(html, result);
        WriteFilters(html, result);
        html.Append("<section class=\"coverage\"><h2>Chip coverage</h2>");
        html.Append("<p><strong>Detected:</strong> ").Append(EncodeList(result.DetectedChips)).Append("</p>");
        html.Append("<p><strong>Covered by active timeline data:</strong> ")
            .Append(EncodeList(result.CoveredChips)).Append("</p>");
        if (result.MissingChips.Count > 0)
            html.Append("<p class=\"missing\"><strong>Missing:</strong> ")
                .Append(EncodeList(result.MissingChips)).Append("</p>");
        html.Append("</section>");
        foreach (ReviewSourceResult source in result.Sources)
            WriteSource(html, source);
        html.Append("</main><script>").Append(JavaScript).Append("</script></body></html>");
        string path = Path.Combine(result.OutputPath, "index.html");
        File.WriteAllText(path, html.ToString());
        return path;
    }

    private static void WriteSummary(StringBuilder html, ReviewResult result)
    {
        html.Append("<div class=\"summary\">");
        WriteMetric(html, "Source files", result.Files);
        WriteMetric(html, "Cases", result.Cases);
        WriteMetric(html, "Rendered", result.Rendered);
        WriteMetric(html, "Unavailable", result.Unavailable);
        WriteMetric(html, "Failed", result.Failed);
        html.Append("</div>");
    }

    private static void WriteMetric(StringBuilder html, string label, int value)
        => html.Append("<div class=\"metric\"><span>")
            .Append(Encode(label)).Append("</span><strong>").Append(value).Append("</strong></div>");

    private static void WriteFilters(StringBuilder html, ReviewResult result)
    {
        string[] compositions = result.Sources.SelectMany(source => source.Cases)
            .Select(item => ReviewGenerator.CompositionName(item.Composition))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray();
        string[] resolutions = result.Sources.SelectMany(source => source.Cases)
            .Select(item => item.Resolution.Name).Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray();
        string[] moments = result.Sources.SelectMany(source => source.Cases)
            .Select(item => item.Moment.Name).Distinct(StringComparer.Ordinal).OrderBy(value => value).ToArray();
        string[] tags = result.Sources.SelectMany(source => source.Entry.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value).ToArray();

        html.Append("<section class=\"filters\"><h2>Filters</h2><form onsubmit=\"return false\">");
        WriteSelect(html, "composition", "Composition", compositions);
        WriteSelect(html, "resolution", "Resolution", resolutions);
        WriteSelect(html, "moment", "Moment", moments);
        WriteSelect(html, "status", "Status", Enum.GetNames<ReviewCaseStatus>());
        WriteSelect(html, "tag", "Tag", tags);
        WriteSelect(html, "chip", "Chip", result.DetectedChips);
        html.Append("<label>Source <input id=\"filter-source\" type=\"search\" placeholder=\"filename\"></label>");
        html.Append("<button type=\"button\" id=\"clear-filters\">Clear</button></form></section>");
    }

    private static void WriteSelect(StringBuilder html, string key, string label, IEnumerable<string> values)
    {
        html.Append("<label>").Append(Encode(label)).Append(" <select data-filter=\"")
            .Append(key).Append("\"><option value=\"\">All</option>");
        foreach (string value in values)
            html.Append("<option value=\"").Append(Encode(value)).Append("\">")
                .Append(Encode(value)).Append("</option>");
        html.Append("</select></label>");
    }

    private static void WriteSource(StringBuilder html, ReviewSourceResult source)
    {
        VisualizationInputInfo? inspection = source.Inspection;
        string chips = source.DetectedChips.Count == 0 ? "none" : string.Join(", ", source.DetectedChips);
        html.Append("<section class=\"source\" data-source=\"")
            .Append(Encode(source.Entry.Path)).Append(" ").Append(Encode(source.Entry.Label)).Append("\">");
        html.Append("<h2>").Append(Encode(source.Entry.Label ?? Path.GetFileName(source.Entry.Path))).Append("</h2>");
        html.Append("<dl class=\"inspection\">");
        Detail(html, "Path", source.Entry.Path);
        Detail(html, "Format", inspection?.Format ?? "unknown");
        Detail(html, "Backend", inspection is null ? "unknown" : BackendName(inspection));
        Detail(html, "Chips", chips);
        Detail(html, "Duration", FormatDuration(source, inspection));
        Detail(html, "Tags", source.Entry.Tags.Count == 0 ? "none" : string.Join(", ", source.Entry.Tags));
        html.Append("</dl>");
        if (inspection?.Issues.Count > 0)
        {
            html.Append("<div class=\"warnings\"><strong>Inspection warnings</strong><ul>");
            foreach (ValidationIssue issue in inspection.Issues)
                html.Append("<li>").Append(Encode(issue.Message)).Append("</li>");
            html.Append("</ul></div>");
        }
        if (!string.IsNullOrWhiteSpace(source.Error))
            html.Append("<p class=\"source-error\">").Append(Encode(source.Error)).Append("</p>");
        html.Append("<div class=\"cards\">");
        foreach (ReviewCaseResult item in source.Cases)
            WriteCase(html, item, source.Entry.Tags, source.DetectedChips);
        if (source.Cases.Count == 0)
            html.Append("<p>No cases selected.</p>");
        html.Append("</div></section>");
    }

    private static void Detail(StringBuilder html, string label, string? value)
        => html.Append("<dt>").Append(Encode(label)).Append("</dt><dd>")
            .Append(Encode(value ?? "unknown")).Append("</dd>");

    private static void WriteCase(
        StringBuilder html,
        ReviewCaseResult item,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> sourceChips)
    {
        string composition = ReviewGenerator.CompositionName(item.Composition);
        string chips = string.Join(' ', item.Chips.Count == 0 ? sourceChips : item.Chips);
        string tagData = string.Join(' ', tags);
        html.Append("<article class=\"card\" data-composition=\"").Append(Encode(composition))
            .Append("\" data-resolution=\"").Append(Encode(item.Resolution.Name))
            .Append("\" data-moment=\"").Append(Encode(item.Moment.Name))
            .Append("\" data-status=\"").Append(Encode(item.Status.ToString()))
            .Append("\" data-chip=\"").Append(Encode(chips))
            .Append("\" data-tag=\"").Append(Encode(tagData)).Append("\">");
        html.Append("<header><strong>").Append(Encode(composition)).Append("</strong> ")
            .Append(Encode(item.Resolution.Name)).Append(" <span class=\"status ")
            .Append(item.Status.ToString().ToLowerInvariant()).Append("\">")
            .Append(Encode(item.Status.ToString())).Append("</span></header>");
        html.Append("<p class=\"caption\">").Append(Encode(item.Moment.Name)).Append(" — ")
            .Append(item.Moment.TimeSeconds.ToString("0.###", CultureInfo.InvariantCulture)).Append(" s</p>");
        if (item.Status == ReviewCaseStatus.Rendered && item.ImagePath is not null)
        {
            html.Append("<a href=\"").Append(Encode(item.ImagePath)).Append("\"><img loading=\"lazy\" src=\"")
                .Append(Encode(item.ImagePath)).Append("\" alt=\"")
                .Append(Encode($"{composition} {item.Resolution.Name} {item.Moment.Name}"))
                .Append("\" width=\"").Append(item.Resolution.Width).Append("\" height=\"")
                .Append(item.Resolution.Height).Append("\"></a>");
        }
        else
        {
            html.Append("<div class=\"placeholder\"><strong>")
                .Append(Encode(item.Reason ?? item.Status.ToString())).Append("</strong>");
            if (!string.IsNullOrWhiteSpace(item.Error))
                html.Append("<small>").Append(Encode(item.Error)).Append("</small>");
            html.Append("</div>");
        }
        html.Append("</article>");
    }

    private static string BackendName(VisualizationInputInfo inspection)
        => inspection.Devices.Count == 0 ? inspection.Format : inspection.Devices[0].Type;

    private static string FormatDuration(ReviewSourceResult source, VisualizationInputInfo? inspection)
    {
        if (source.CapturedDurationSeconds is > 0)
            return source.CapturedDurationSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture) + " s";
        TimeSpan? duration = inspection?.DeclaredDuration ?? inspection?.EstimatedDuration;
        return duration is null ? "unknown" : duration.Value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";
    }

    private static string EncodeList(IEnumerable<string> values)
        => values.Any() ? Encode(string.Join(", ", values)) : "none";

    private static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    private const string Css = "body{margin:0;background:#101217;color:#e7eaf0;font:14px system-ui,sans-serif}main{max-width:1800px;margin:auto;padding:24px}h1,h2{margin:.2em 0 .6em}.meta{color:#9aa4b2}.summary{display:flex;gap:10px;flex-wrap:wrap}.metric{background:#1b2029;border:1px solid #303846;border-radius:8px;padding:10px 16px;min-width:100px}.metric span{display:block;color:#9aa4b2}.metric strong{font-size:24px}.coverage,.filters,.source{background:#171b23;border:1px solid #303846;border-radius:10px;padding:16px;margin:16px 0}.missing,.source-error{color:#ffb4a8}.filters form{display:flex;gap:10px;flex-wrap:wrap;align-items:end}.filters label{display:flex;flex-direction:column;gap:4px;color:#aeb8c7}.filters select,.filters input,.filters button{background:#0e1117;color:#eef2f7;border:1px solid #475366;border-radius:4px;padding:7px}.inspection{display:grid;grid-template-columns:max-content 1fr;gap:5px 16px}.inspection dt{color:#9aa4b2}.inspection dd{margin:0}.warnings{color:#ffd18a}.warnings ul{margin:.3em 0}.cards{display:grid;grid-template-columns:repeat(auto-fit,minmax(280px,1fr));gap:12px}.card{background:#10141b;border:1px solid #303846;border-radius:8px;padding:10px}.card header{display:flex;gap:7px;align-items:center;flex-wrap:wrap}.status{font-size:11px;border-radius:4px;padding:2px 5px;background:#394353}.status.rendered{color:#b9f6c7;background:#164a2b}.status.unavailable{color:#ffe3a1;background:#60470d}.status.failed{color:#ffb4a8;background:#5b201d}.caption{color:#aeb8c7;margin:.35em 0}.card img{display:block;width:100%;height:auto;background:#090b0f}.placeholder{min-height:180px;background:#0b0e13;border:1px dashed #586579;display:flex;flex-direction:column;justify-content:center;align-items:center;text-align:center;padding:10px;color:#ffd18a}.placeholder small{display:block;color:#aeb8c7;margin-top:8px;white-space:pre-wrap}.source-error{white-space:pre-wrap}.hidden{display:none!important}";

    private const string JavaScript = "const controls=[...document.querySelectorAll('[data-filter]')];const source=document.getElementById('filter-source');function apply(){const values=Object.fromEntries(controls.map(x=>[x.dataset.filter,x.value.toLowerCase()]));const sourceValue=source.value.toLowerCase();document.querySelectorAll('.card').forEach(card=>{const ok=(!values.composition||card.dataset.composition.toLowerCase()===values.composition)&&(!values.resolution||card.dataset.resolution.toLowerCase()===values.resolution)&&(!values.moment||card.dataset.moment.toLowerCase()===values.moment)&&(!values.status||card.dataset.status.toLowerCase()===values.status)&&(!values.tag||card.dataset.tag.toLowerCase().split(' ').includes(values.tag))&&(!values.chip||card.dataset.chip.toLowerCase().split(' ').includes(values.chip));const section=card.closest('.source');const sourceOk=!sourceValue||section.dataset.source.toLowerCase().includes(sourceValue);card.classList.toggle('hidden',!(ok&&sourceOk));});document.querySelectorAll('.source').forEach(section=>{section.classList.toggle('hidden',sourceValue&&!section.dataset.source.toLowerCase().includes(sourceValue));});}controls.forEach(x=>x.addEventListener('change',apply));source.addEventListener('input',apply);document.getElementById('clear-filters').addEventListener('click',()=>{controls.forEach(x=>x.value='');source.value='';apply();});";
}
