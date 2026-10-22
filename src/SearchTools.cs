using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace ToolsMcp;

/// <summary>Search across every kit and catalogue the account can see, and what the search's filters can take.</summary>
[McpServerToolType]
public class SearchTools(IToolsApi api, TokenProvider tokens)
{
    /**
     * A page holds at most 50 rows (spec, 22 October). The catalogues are closed to anonymous visitors for legal
     * reasons, and paging through 68 thousand Sandvik rows is the export that closing is against.
     */
    internal const int MaxPageSize = 50;

    private static readonly string[] Sorts = ["name", "diameter", "length"];

    [McpServerTool(Name = "find_components"), Description(
        "Searches components across our kits and the supplier catalogues the account can see. Each row gives the id " +
        "every other tool takes, the class code, the size, whether it can go into a kit, and how many parts it goes " +
        "into and takes. For a kit or a library use buildableOnly=true: only those can go in. Call search_options " +
        "first rather than guess a class code or a maker's name. At most 50 rows a page.")]
    public Task<string> FindComponents(
        [Description("Words or a designation, e.g. CNMG 120408 or end mill")] string? query = null,
        [Description("Only in this kit or catalogue (its slug, from list_kits)")] string? kit = null,
        [Description("A class code, e.g. INSI, MIL, TRNGEI - search_options lists them")] string? classCode = null,
        [Description("A family, e.g. Milling cutter, Drill - search_options lists them")] string? family = null,
        [Description("What the machine is: mill or lathe")] string? machineType = null,
        [Description("Who makes it, e.g. Sandvik Coromant; Generic for our own kits")] string? maker = null,
        [Description("A coating, as search_options names it")] string? coating = null,
        [Description("Coolant through the tool: true or false")] string? coolant = null,
        [Description("A shank family, as search_options names it")] string? shank = null,
        [Description("Smallest cutting diameter, mm")] double? minDiameterMm = null,
        [Description("Largest cutting diameter, mm")] double? maxDiameterMm = null,
        [Description("Only what fits this component: holders it goes into and parts it takes")] string? fitsComponentId = null,
        [Description("Only what can go into a kit or a library")] bool buildableOnly = false,
        [Description("The ENCY version: 3 or 2")] int? encyVersion = null,
        [Description("name, diameter or length")] string? sort = null,
        [Description("Sort from the largest")] bool descending = false,
        [Description("Page number, from 0")] int page = 0,
        [Description("Rows a page, 1 to 50")] int pageSize = 20) =>
        Replies.WithToken(tokens, async token =>
        {
            if (sort != null && !Sorts.Contains(sort)) return "ERROR: sort is name, diameter or length";
            int size = Math.Clamp(pageSize, 1, MaxPageSize);
            var search = new ComponentQuery(query, kit, classCode, family, machineType, maker, coating, coolant, shank,
                minDiameterMm, maxDiameterMm, fitsComponentId, buildableOnly ? true : null, encyVersion, sort,
                descending, Math.Max(0, page), size);
            var found = await api.FindComponents(search, token);
            var counts = found.Items.Count == 0
                ? new Dictionary<string, CompatCounts>()
                : await api.GetCompatCounts(found.Items.Select(c => c.Id).ToList(), token);

            int pages = (int)Math.Max(1, (found.Total + size - 1) / size);
            var reply = new StringBuilder($"{found.Total} found, page {found.Page + 1} of {pages}");
            if (size != pageSize) reply.Append($" (a page holds at most {MaxPageSize} rows: showing {size})");
            reply.AppendLine();
            foreach (var c in found.Items)
            {
                reply.Append(Replies.Line(c));
                if (counts.TryGetValue(c.Id, out var n)) reply.Append($" - goes into {n.PlugsInto}, takes {n.Accepts}");
                reply.AppendLine();
            }
            if (found.Total == 0) reply.AppendLine("search_options shows what these filters can take.");
            if (found.Page + 1 < pages) reply.Append($"Next page: page={found.Page + 1}. ");
            reply.Append("describe_component(id) for parameters, what_fits(id) for what goes with it.");
            return reply.ToString();
        });

    [McpServerTool(Name = "search_options"), Description(
        "What the search filters can take for a search: the classes with their codes, families, makers, coatings, " +
        "coolant, the diameter range and the shanks, each with how many components it holds. Call it before " +
        "guessing a class code or a maker's name for find_components.")]
    public Task<string> SearchOptions(
        [Description("Words or a designation")] string? query = null,
        [Description("Only in this kit or catalogue (its slug)")] string? kit = null,
        [Description("A class code")] string? classCode = null,
        [Description("A family")] string? family = null,
        [Description("mill or lathe")] string? machineType = null,
        [Description("Who makes it")] string? maker = null,
        [Description("A coating")] string? coating = null,
        [Description("Coolant through the tool: true or false")] string? coolant = null,
        [Description("A shank family")] string? shank = null,
        [Description("Smallest cutting diameter, mm")] double? minDiameterMm = null,
        [Description("Largest cutting diameter, mm")] double? maxDiameterMm = null,
        [Description("Only what fits this component")] string? fitsComponentId = null,
        [Description("Only what can go into a kit or a library")] bool buildableOnly = false,
        [Description("The ENCY version: 3 or 2")] int? encyVersion = null) =>
        Replies.WithToken(tokens, async token =>
        {
            var values = await api.GetFilterValues(new ComponentQuery(query, kit, classCode, family, machineType, maker,
                coating, coolant, shank, minDiameterMm, maxDiameterMm, fitsComponentId, buildableOnly ? true : null,
                encyVersion), token);
            var reply = new StringBuilder();
            List("Classes (classCode)", values.Classes, s => $"{s.Title ?? s.Section} ({s.Section}): {s.Count}", reply);
            List("Families (family)", values.Families, s => $"{s.Title ?? s.Section}: {s.Count}", reply);
            List("Makers (maker)", values.Makers, s => $"{s.Title ?? s.Section}: {s.Count}", reply);
            List("Coatings (coating)", values.Coatings, s => $"{s.Title ?? s.Section}: {s.Count}", reply);
            List("Coolant through the tool (coolant)", values.Coolant, s => $"{s.Title ?? s.Section} ({s.Section}): {s.Count}", reply);
            List("Shanks (shank)", values.Shanks, s => $"{s.Title ?? s.Section} ({s.Section}): {s.Count}", reply);
            if (values.Diameters.Min is { } min && values.Diameters.Max is { } max)
                reply.AppendLine($"Diameters: Ø {Replies.Mm(min)}–{Replies.Mm(max)} mm");
            return reply.Length == 0 ? "Nothing matches these filters." : reply.ToString().TrimEnd();
        });

    private static void List(string title, IReadOnlyList<SectionCount> values, Func<SectionCount, string> line, StringBuilder reply)
    {
        if (values.Count == 0) return;
        reply.AppendLine(title + ":");
        foreach (var v in values) reply.AppendLine("  " + line(v));
    }
}
