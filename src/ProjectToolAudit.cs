using System.Text.Json.Serialization;

namespace ToolsMcp;

internal sealed record HoleReading(
    [property: JsonPropertyName("diameterMm")] double? DiameterMm,
    [property: JsonPropertyName("depthMm")] double? DepthMm,
    [property: JsonPropertyName("bottom")] string? Bottom,
    [property: JsonPropertyName("unresolved"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Unresolved = false);

internal sealed record AuditCutter(string Id, string Name, string Type, double? DiameterMm, double? ReachMm,
    double? PointAngleDegrees, string Magazine, int Position, string Source, IReadOnlyList<string>? Adapters = null, string? PointAngleState = null);

internal sealed record HoleGroup(double? DiameterMm, double? DepthMm, string? Bottom, bool Unresolved, int Count);
internal sealed record AuditReason(string Code, double? ActualMm = null, double? RequiredMm = null);
internal sealed record GeometryReview(string Status, List<string> Missing, List<AuditReason> Reasons, HoleGroup[]? HoleGroups = null, double? RequiredReachMm = null);
internal sealed record ProjectAuditRow(string OperationId, string OperationName, string OperationType, string? Material,
    string? AssignedId, AuditCutter? AssignedTool, GeometryReview Review, OperationBrief? MillingGeometry = null, string? Machine = null, string? Setup = null, string? Error = null);

internal sealed record AuditReport(int ProcessId, string ProjectPath, string OperationUnits, double AdditionalBreakthroughMm,
    List<ProjectAuditRow> Operations, List<AuditCutter> ProjectInventory, AuditQuestion[] Questions, AuditQuestion[]? CatalogueQuestions = null);
internal sealed record AuditQuestion(string Field, string[] OperationIds);

internal static class ProjectToolAudit
{
    internal static GeometryReview Drill(IReadOnlyList<HoleReading> holes, AuditCutter? cutter)
    {
        var missing = new HashSet<string>();
        var reasons = new List<AuditReason>();
        var groups = holes.GroupBy(h => h)
            .Select(g => new HoleGroup(g.Key.DiameterMm, g.Key.DepthMm, g.Key.Bottom, g.Key.Unresolved, g.Count())).ToArray();
        bool correctType = cutter != null && InstalledDrillPoint.IsStandardDrill(cutter.Type);
        if (cutter == null) missing.Add("assignedTool");
        else if (!correctType) reasons.Add(new("requiresStandardDrill"));
        double? maxRequired = null;
        bool fullReachKnown = correctType && groups.Length > 0;
        if (groups.Length == 0) missing.Add("holes");
        foreach (var h in groups)
        {
            if (h.DiameterMm == null) missing.Add("diameter");
            if (h.DepthMm == null) missing.Add("depth");
            if (h.Bottom is not ("tip" or "through" or "cylinder")) missing.Add("bottom");
            if (!correctType) continue;
            if (cutter!.DiameterMm == null) missing.Add("toolDiameter");
            else if (h.DiameterMm is double d && Math.Abs(cutter.DiameterMm.Value - d) > 1e-7)
                reasons.Add(new("diameterMismatch", cutter.DiameterMm, d));
            if (cutter.ReachMm == null) missing.Add("workingReach");
            if (h.DepthMm is not double depth) { fullReachKnown = false; continue; }
            // Even when the depth reference is unknown, the tool must reach at least this far.
            double required = depth;
            if (h.Bottom is "through" or "cylinder") {
                if (!InstalledDrillPoint.ValidAngle(cutter.PointAngleDegrees)) {
                    missing.Add("pointAngle"); fullReachKnown = false;
                } else if (cutter.DiameterMm is double diameter && h.DiameterMm is double needed &&
                           Math.Abs(diameter-needed) <= 1e-7)
                    required = InstalledDrillPoint.RequiredDepth(diameter, depth, 0, cutter.PointAngleDegrees)!.Value;
                else fullReachKnown = false;
            } else if (h.Bottom != "tip") fullReachKnown = false;
            maxRequired = Math.Max(maxRequired ?? required, required);
            if (cutter.ReachMm < required) reasons.Add(new("insufficientReach", cutter.ReachMm, required));
        }
        return Result(missing, reasons, groups) with {RequiredReachMm = fullReachKnown ? maxRequired : null};
    }

    internal static GeometryReview Mill(OperationBrief b, AuditCutter? cutter)
    {
        var missing = new HashSet<string>();
        var reasons = new List<AuditReason>();
        if (b.Operation is not ("slot" or "shoulder")) missing.Add("millingStrategy");
        if (b.Width == null) missing.Add("width");
        if (b.InnerRadius == null && b.NoInsideCorner != true) missing.Add("innerRadiusOrNoCorner");
        if (b.Depth == null) missing.Add("depth");
        if (cutter == null) missing.Add("assignedTool");
        else if (!cutter.Type.Split('.').Last().Equals("EndMill", StringComparison.OrdinalIgnoreCase)) {
            // Pocket and contour operations can legitimately use a ball or toroidal cutter.
            // A square-end-mill search cannot assess such an assignment.
            return new("unsupportedTool", missing.Order().ToList(), [new("requiresDifferentMillingToolModel")]);
        } else {
            if (cutter.DiameterMm == null) missing.Add("toolDiameter");
            else {
                if (cutter.DiameterMm > b.Width) reasons.Add(new("exceedsWidth", cutter.DiameterMm, b.Width));
                if (cutter.DiameterMm > 2*b.InnerRadius) reasons.Add(new("exceedsCornerDiameter", cutter.DiameterMm, 2*b.InnerRadius));
            }
            if (cutter.ReachMm == null) missing.Add("workingReach");
            else if (cutter.ReachMm < b.Depth) reasons.Add(new("insufficientReach", cutter.ReachMm, b.Depth));
        }
        return Result(missing, reasons) with {RequiredReachMm = b.Depth};
    }

    private static GeometryReview Result(HashSet<string> missing, List<AuditReason> reasons, HoleGroup[]? groups = null) =>
        new(reasons.Count > 0 ? "mismatch" : missing.Count > 0 ? "needsInput" : "fitsGeometry",
            missing.Order().ToList(), reasons.Distinct().ToList(), groups);

    internal static AuditQuestion[] Questions(IEnumerable<ProjectAuditRow> rows) =>
        rows.SelectMany(r => r.Review.Missing.Select(field => (Field:field, r.OperationId)))
            .GroupBy(q => q.Field).OrderBy(g => g.Key)
            .Select(g => new AuditQuestion(g.Key, g.Select(q => q.OperationId).Distinct().ToArray()))
            .ToArray();
}
