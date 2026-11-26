using System.Text.Json.Nodes;

namespace ToolsMcp;

public record PlanStep(string OperationId, OperationBrief Geometry, string? Machine = null);

internal sealed record PlanCandidate(string Source, string ToolId, string? Identity, string? AssemblyId,
    string? LibraryId, string? InsertId, string? Magazine, int? Position);
internal sealed record PlanHit(PlanCandidate Key, string Name, double? WorkingReachMm, double? RequiredReachMm,
    string? Reason, bool AlreadyAssigned = false);
internal sealed class PlanRow(PlanStep step)
{
    internal PlanStep Step { get; } = step;
    internal List<PlanHit> Hits { get; } = [];
    internal PlanHit? Selected { get; set; }
    internal bool RememberedWidth { get; set; }
    internal List<string> Questions { get; } = [];
    internal List<string> Unchecked { get; } = [];
    internal string? Scope { get; set; }
    internal int? NextOffset { get; set; }
    internal string? Error { get; set; }
}

internal static class SharedToolPlan
{
    // Never merge equal T numbers, different holders/inserts, or assemblies on different machines/interfaces.
    private sealed record Choice(string? Machine, string? Spindle, string? Interface, PlanCandidate Tool);
    internal static object Select(IReadOnlyList<PlanRow> rows)
    {
        var byTool = new Dictionary<Choice, Dictionary<int, PlanHit>>();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            foreach (var hit in row.Hits)
            {
                var key = new Choice(row.Step.Machine, row.Step.Geometry.Spindle, row.Step.Geometry.HolderInterface, hit.Key);
                if (!byTool.TryGetValue(key, out var hits)) byTool[key] = hits = [];
                hits.TryAdd(i, hit);
            }
        }
        var remaining = Enumerable.Range(0, rows.Count).ToHashSet();
        var chosen = new List<object>();
        var assignments = new Dictionary<int, (Choice Key, PlanHit Hit)>();
        while (remaining.Count > 0)
        {
            var best = byTool.Select(x => new { x.Key, Hits = x.Value.Where(v => remaining.Contains(v.Key)).ToList() })
                .Where(x => x.Hits.Count > 0)
                .OrderBy(x => Priority(x.Key.Tool.Source)).ThenByDescending(x => x.Hits.Count)
                .ThenByDescending(x => x.Hits.Count(v => v.Value.AlreadyAssigned))
                .FirstOrDefault(); // Stable ordering preserves the first returned catalogue rank for otherwise equal options.
            if (best == null) break;
            chosen.Add(new
            {
                tool = best.Key.Tool,
                name = best.Hits[0].Value.Name,
                machine = best.Key.Machine,
                operations = best.Hits.Select(v => rows[v.Key].Step.OperationId)
            });
            foreach (var (index, hit) in best.Hits) { assignments[index] = (best.Key, hit); remaining.Remove(index); }
        }
        static int? CountChanges(Choice?[] choices)
        {
            if (choices.Any(c => c == null)) return null;
            int count = 0;
            foreach (var machine in choices.GroupBy(c => c!.Machine))
                count += machine.Zip(machine.Skip(1)).Count(pair => pair.First != pair.Second);
            return count;
        }
        var independent = rows.Select(row => {
            var first = row.Hits.OrderBy(hit => Priority(hit.Key.Source)).ThenByDescending(hit => hit.AlreadyAssigned).FirstOrDefault();
            return first == null ? null : new Choice(row.Step.Machine, row.Step.Geometry.Spindle, row.Step.Geometry.HolderInterface, first.Key);
        }).ToArray();
        int? changes = CountChanges(Enumerable.Range(0, rows.Count)
            .Select(i => assignments.TryGetValue(i, out var a) ? a.Key : null).ToArray());
        var operations = rows.Select((row, i) =>
        {
            var hit = assignments.TryGetValue(i, out var selected) ? selected.Hit : null;
            row.Selected = hit;
            return new
            {
                row.Step.OperationId,
                geometry = row.RememberedWidth ? row.Step.Geometry with { WidthSource = "remembered" } : row.Step.Geometry,
                selection = hit,
                status = hit != null ? "candidate" : row.Error != null ? "error" : row.Questions.Count > 0 ? "needs_input" :
                    row.NextOffset.HasValue ? "more_candidates_available" : "no_verified_match",
                missing = row.Questions,
                uncheckedConstraints = row.Unchecked,
                row.Error,
                row.Scope,
                nextCatalogueOffset = row.NextOffset
            };
        }).ToArray();
        return new
        {
            strategy = "Installed tools first, then personal assemblies, then catalogue. Within each source prefer reuse across remaining operations.",
            operations,
            tools = chosen,
            distinctTools = chosen.Count,
            individualSelection = new { distinctTools = independent.OfType<Choice>().Distinct().Count(),
                changesInGivenOrder = CountChanges(independent) },
            questionsByField = rows.SelectMany(row => row.Questions.Select(field => (field, row.Step.OperationId)))
                .GroupBy(x => x.field).Select(g => new {field=g.Key, operationIds=g.Select(x=>x.OperationId)}),
            changesInGivenOrder = changes
        };
    }

    private static int Priority(string source) => source switch
    {
        "machineMagazine" or "projectToolList" => 0,
        "warehouseAssembly" => 1,
        _ => 2
    };

    internal static async Task AddCatalogue(IReadOnlyList<PlanRow> rows, IToolsApi api, string token, int pages)
    {
        // A repeated pocket or hole geometry sees the same catalogue slice, fetched once per call.
        var cache = new Dictionary<OperationBrief, JsonObject>();
        foreach (var row in rows.Where(r => r.Error == null))
        {
            var b = row.Step.Geometry with { Size = 50 };
            for (int page = 0; page < pages; page++)
            {
                if (!cache.TryGetValue(b, out var answer))
                {
                    try { answer = await api.FindOperationTools(b, token); }
                    catch (ToolsHttpException e) when ((int)e.Status == 400) { row.Error = e.Message; break; }
                    cache[b] = answer;
                }
                if (answer["status"]?.GetValue<string>() == "needs_input")
                {
                    foreach (var q in answer["missing"]!.AsArray())
                    {
                        var field = (string)q!["field"]!;
                        if (!row.Questions.Contains(field)) row.Questions.Add(field);
                    }
                    break;
                }
                var matches = answer["matches"]!.AsObject();
                row.Scope = (string?)matches["scope"];
                foreach (var field in matches["uncheckedFilters"] as JsonArray ?? [])
                {
                    if (field is JsonValue v && !row.Unchecked.Contains(v.ToString())) row.Unchecked.Add(v.ToString());
                }
                foreach (var item in matches["items"]!.AsArray())
                {
                    string id = (string)item!["id"]!, name = (string)item["name"]!;
                    var fit = matches["fits"]![id]!;
                    string? insert = (string?)fit["insert"]?["id"];
                    void Add(string source, string? assembly = null, string? library = null, string? label = null)
                    {
                        var key = new PlanCandidate(source, id, null, assembly, library, insert, null, null);
                        row.Hits.Add(new(key, label ?? name, (double?)fit["reachMm"], (double?)fit["requiredMm"], (string?)fit["reason"]));
                    }
                    foreach (var shelf in fit["assemblies"] as JsonArray ?? [])
                        if (string.IsNullOrWhiteSpace(b.Spindle) || (string?)shelf!["mountStatus"] == "compatible")
                            Add("warehouseAssembly", (string?)shelf!["assemblyId"], (string?)shelf["libraryId"], (string?)shelf["name"]);
                    Add("catalogue");
                }
                int? next;
                if (matches["hasMore"] is JsonValue more)
                    next = more.GetValue<bool>() ? (int?)matches["nextCursor"] : null;
                else next = ((long)b.Offset + 1) * b.Size < (long?)matches["total"] ? b.Offset + 1 : null;
                row.NextOffset = next;
                if (next == null || next <= b.Offset) break;
                b = b with { Offset = next.Value };
            }
        }
    }
}

public partial class SearchTools
{
    [ModelContextProtocol.Server.McpServerTool(Name = "plan_tools_for_operations", ReadOnly = true),
        System.ComponentModel.Description("Read-only shared tooling plan for a sequence of operations before opening ENCY. Each step has a unique operationId, its geometry as in find_tools_for_operation, and optional machine (omit only for one machine). Reuses verified candidates across steps, keeps different assemblies/inserts separate, answers missing geometry per step. Scans up to pagesPerOperation pages of 50, starting at geometry.offset. A catalogue choice still needs a physical mount check.")]
    public Task<string> PlanToolsForOperations(PlanStep[] steps, int pagesPerOperation = 1) =>
        Replies.WithToken(tokens, async token =>
        {
            if (steps is not { Length: > 0 and <= 100 } || pagesPerOperation is < 1 or > 10 ||
                steps.Any(s => s?.Geometry == null || string.IsNullOrWhiteSpace(s.OperationId) || s.Geometry.Offset < 0) ||
                steps.Select(s => s.OperationId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != steps.Length)
                return "ERROR: supply 1–100 uniquely named steps with geometry and 1–10 pages per operation.";
            var rows = steps.Select(s => new PlanRow(s)).ToList();
            await SharedToolPlan.AddCatalogue(rows, api, token, pagesPerOperation);
            return System.Text.Json.JsonSerializer.Serialize(SharedToolPlan.Select(rows), AuditOutput.Json);
        });
}
