using System.ComponentModel;
using System.Runtime.Versioning;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace ToolsMcp;

public record ProjectPlanGeometry(string OperationId, string? Mode = null, double? WidthMm = null,
    double? DepthMm = null, double? InnerRadiusMm = null, bool? NoInsideCorner = null,
    double? BreakthroughMm = null, string? Magazine = null);

internal static class ProjectToolPlan
{
    internal static List<PlanRow> Rows(AuditReport audit, IReadOnlyDictionary<string, ProjectPlanGeometry> overrides)
    {
        var rows = new List<PlanRow>();
        foreach (var op in audit.Operations)
        {
            overrides.TryGetValue(op.OperationId, out var input);
            var holes = op.Review.HoleGroups;
            var b = op.MillingGeometry ?? new OperationBrief(Material: op.Material);
            var issues = new List<string>();
            string? failure = op.Error;
            if (holes != null)
            {
                var diameters = holes.Select(h => h.DiameterMm).Distinct().ToArray();
                var modes = holes.Select(h => h.Bottom).Where(m => m != null).Distinct().ToArray();
                if (diameters.OfType<double>().Distinct().Count() > 1 || modes.Contains("through") && modes.Length > 1 ||
                    modes.Contains("tip") && modes.Contains("cylinder"))
                    failure = "Split the different job diameters or depth references before planning one assignment.";
                double? knownDiameter = diameters.Length == 1 ? diameters[0] : null;
                string? knownBottom = modes.Length == 1 && holes.All(h => h.Bottom != null) ? modes[0] : null;
                double? knownDepth = holes.Length > 0 && holes.All(h => h.DepthMm != null) ? holes.Max(h => h.DepthMm) : null;
                string? mode = input?.Mode ?? knownBottom;
                if (input?.WidthMm is double wd && diameters.OfType<double>().Any(d => Math.Abs(d - wd) > 1e-7) ||
                    input?.DepthMm is double dp && holes.Any(h => h.DepthMm > dp + 1e-7) ||
                    input?.Mode != null && modes.Any(m => m == "blind" ? mode == "through" : m != mode))
                    failure = "Supplied geometry conflicts with known hole dimensions or depth reference.";
                b = b with
                {
                    Operation = "drilling",
                    Diameter = input?.WidthMm ?? knownDiameter,
                    Depth = input?.DepthMm ?? knownDepth,
                    Bottom = mode,
                    Breakthrough = input?.BreakthroughMm ?? 0
                };
                if (b.Diameter == null) issues.Add("diameter");
                if (b.Bottom is not ("through" or "tip" or "cylinder")) issues.Add("bottom");
            }
            else if (op.MillingGeometry != null)
            {
                if (input?.WidthMm > b.Width || input?.DepthMm < b.Depth || input?.InnerRadiusMm > b.InnerRadius ||
                    input?.NoInsideCorner == true && b.InnerRadius != null ||
                    input?.Mode != null && b.Operation is "shoulder" or "slot" && input.Mode != b.Operation)
                    failure = "Supplied geometry conflicts with the recognized milling feature.";
                b = b with
                {
                    Operation = input?.Mode ?? b.Operation,
                    Width = input?.WidthMm ?? b.Width,
                    WidthSource = input?.WidthMm.HasValue == true ? "user" : b.WidthSource,
                    Depth = input?.DepthMm ?? b.Depth,
                    InnerRadius = input?.InnerRadiusMm ?? b.InnerRadius,
                    NoInsideCorner = input?.NoInsideCorner ?? b.NoInsideCorner
                };
                if (b.Operation is not ("slot" or "shoulder")) issues.Add("operation");
                if (b.Width == null) issues.Add("width");
                if (b.InnerRadius == null && b.NoInsideCorner != true) issues.Add("innerRadiusOrNoCorner");
            }
            else failure ??= "Automatic project extraction is unavailable for this cycle. Use an explicit geometry request.";
            if (b.Depth == null) issues.Add("depth");
            if (input != null && (input.BreakthroughMm is double breakthrough && (!double.IsFinite(breakthrough) || breakthrough < 0) ||
                input.BreakthroughMm is >0 && b.Bottom != "through" ||
                input.WidthMm is <= 0 || input.DepthMm is <= 0 || input.InnerRadiusMm is < 0 ||
                input.InnerRadiusMm != null && input.NoInsideCorner == true ||
                holes != null && (input.InnerRadiusMm != null || input.NoInsideCorner == true) ||
                input.Mode != null && !(holes != null ? input.Mode is "through" or "tip" or "cylinder" : input.Mode is "slot" or "shoulder")))
                failure = "Invalid geometry override.";
            var row = new PlanRow(new(op.OperationId, b, op.Machine ?? op.Setup ?? op.OperationId)) { Error = failure };
            rows.Add(row);
            row.Questions.AddRange(issues);
            if (failure != null || issues.Count > 0) continue;
            var magazine = input?.Magazine ?? op.AssignedTool?.Magazine;
            foreach (var tool in audit.ProjectInventory)
            {
                if (string.IsNullOrWhiteSpace(tool.Identity) || tool.Source == "machineMagazine" &&
                    (string.IsNullOrWhiteSpace(magazine) || !string.Equals(magazine, tool.Magazine, StringComparison.OrdinalIgnoreCase))) continue;
                var fit = holes == null ? ProjectToolAudit.Mill(b, tool) :
                    ProjectToolAudit.Drill([new HoleReading(b.Diameter, b.Depth, b.Bottom)], tool);
                double? needed = fit.RequiredReachMm + (b.Breakthrough ?? 0);
                if (fit.Status != "fitsGeometry" || needed == null || tool.ReachMm < needed) continue;
                var key = new PlanCandidate(tool.Source, tool.Id, tool.Identity, null, null, null, tool.Magazine, tool.Position);
                row.Hits.Add(new(key, tool.Name, tool.ReachMm, needed, "Verified job diameter/clearance and working reach.",
                    op.AssignedTool?.Id == tool.Id && op.AssignedTool.Source == tool.Source));
            }
        }
        return rows;
    }

    internal static string Result(AuditReport audit, IReadOnlyList<PlanRow> rows, bool includeCatalogue = false)
    {
        var plan = SharedToolPlan.Select(rows);
        var changes = rows.Where(r => r.Selected is { AlreadyAssigned: false, Key.Source: "machineMagazine" or "projectToolList" })
            .Select(r => new
            {
                r.Step.OperationId,
                toolId = r.Selected!.Key.ToolId,
                r.Selected.Key.Identity,
                r.Selected.Key.Source,
                mode = r.Step.Geometry.Bottom ?? r.Step.Geometry.Operation,
                widthMm = r.Step.Geometry.Diameter ?? r.Step.Geometry.Width,
                depthMm = r.Step.Geometry.Depth,
                breakthroughMm = r.Step.Geometry.Breakthrough ?? 0,
                innerRadiusMm = r.Step.Geometry.InnerRadius,
                noInsideCorner = r.Step.Geometry.NoInsideCorner ?? false
            });
        return JsonSerializer.Serialize(new
        {
            audit.ProjectPath,
            audit.ProcessId,
            audit.OperationUnits,
            includeCatalogue,
            plan,
            installationRequired = rows.Where(r => r.Selected?.Key.Source is "catalogue" or "warehouseAssembly")
                .Select(r => new { r.Step.OperationId, componentId = r.Selected!.Key.ToolId, r.Selected.Key.AssemblyId,
                    r.Selected.Key.LibraryId, r.Selected.Key.InsertId, next = "Install the chosen tool or assembly and replan using its actual project identity." }),
            previewInput = new { audit.ProjectPath, audit.ProcessId, audit.OperationUnits, changes }
        }, AuditOutput.Json);
    }
}

[SupportedOSPlatform("windows")]
public sealed partial class LocalEncyTools
{
    [McpServerTool(Name = "plan_project_tools", ReadOnly = true), Description(
        "Plan a common set of installed tools for the open project in operation order without changing the project. Use audit_project_tools to obtain IDs. " +
        "Optionally supply missing drilling or milling dimensions by operation ID (mm); mode tip/cylinder/through or slot/shoulder. " +
        "Only tools from the known or specified magazine are considered for magazine projects. " +
        "includeCatalogue adds matching personal assemblies and catalogue alternatives after sign-in. previewInput contains only installed replacements for preview_project_tool_changes.")]
    public async Task<string> PlanProjectTools(string operationUnits, int? processId = null, ProjectPlanGeometry[]? geometry = null,
        bool includeCatalogue = false, int pagesPerOperation = 1)
    {
        if (pagesPerOperation is < 1 or > 10) return "ERROR: pagesPerOperation must be 1 to 10.";
        if (operationUnits is not ("metric" or "imperial")) return "ERROR: specify metric or imperial.";
        var raw = await Run("planning-audit", processId, [operationUnits]);
        if (raw.StartsWith("ERROR", StringComparison.Ordinal)) return raw;
        var audit = JsonSerializer.Deserialize<AuditReport>(raw, AuditOutput.Json)!;
        geometry ??= [];
        if (geometry.Any(o => o == null || !audit.Operations.Any(r => string.Equals(r.OperationId, o.OperationId, StringComparison.OrdinalIgnoreCase))) ||
            geometry.DistinctBy(o => o.OperationId, StringComparer.OrdinalIgnoreCase).Count() != geometry.Length)
            return "ERROR: each override must identify one distinct operation in the current project.";
        var answers=!string.IsNullOrWhiteSpace(audit.ProjectPath) && Path.IsPathFullyQualified(audit.ProjectPath)
            ? OperationAnswerStore.For(audit).Read(audit) : new Dictionary<string,ProjectPlanGeometry>(StringComparer.OrdinalIgnoreCase);
        foreach(var supplied in geometry) {
            answers.TryGetValue(supplied.OperationId,out var previous);
            answers[supplied.OperationId]=OperationAnswerStore.Merge(previous,supplied);
        }
        var rows = ProjectToolPlan.Rows(audit, answers);
        foreach(var row in rows.Where(r=>r.Step.Geometry.WidthSource=="user" &&
                geometry.All(g=>!string.Equals(g.OperationId,r.Step.OperationId,StringComparison.OrdinalIgnoreCase)||g.WidthMm==null)))
            row.RememberedWidth=true;
        if (!includeCatalogue) return ProjectToolPlan.Result(audit, rows);
        return await Replies.WithToken(tokens, async token =>
        {
            await SharedToolPlan.AddCatalogue(rows.Where(r => r.Questions.Count == 0).ToList(), api, token, pagesPerOperation);
            return ProjectToolPlan.Result(audit, rows, true);
        });
    }
}
