using System.Security.Cryptography;
using System.Text.Json;

namespace ToolsMcp;

public sealed record ProjectToolChange(string OperationId, string ToolId, string Identity, string Mode, double WidthMm,
    double DepthMm, string? Source = null, double BreakthroughMm = 0, double? InnerRadiusMm = null, bool NoInsideCorner = false);

internal sealed record ToolPlanRequest(string ProjectPath, string OperationUnits, ProjectToolChange[] Changes,
    string? PreviewId = null, bool Confirmed = false);

internal sealed record ToolBinding(string Id, string Identity, string Source, string Caption, string Type, string Magazine, int Position,
    double? DiameterMm, double? ReachMm, double? PointAngle, IReadOnlyList<string>? Adapters);

internal sealed record ToolChangeRow(ProjectToolChange Change, string OperationName, string GeometryStamp,
    ToolBinding? Before, ToolBinding? After, string? Problem);

internal sealed record ChangeOutcome(string OperationId, string Status, string? Detail = null);

internal static class ProjectToolChangePlan
{
    internal static string? Validate(ToolPlanRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectPath) || request.OperationUnits is not ("metric" or "imperial") ||
            request.Changes is not {Length: >0}) return "Specify the project, units and at least one operation.";
        var ids = new HashSet<Guid>();
        foreach (var c in request.Changes) {
            if (c == null || !Guid.TryParse(c.OperationId,out var id) || !ids.Add(id)) return "Use distinct valid operation IDs.";
            if (string.IsNullOrWhiteSpace(c.ToolId) || string.IsNullOrWhiteSpace(c.Identity) ||
                c.Source is not (null or "projectToolList" or "machineMagazine")) return "Supply the installed tool ID and identity.";
            if (c.Mode is not ("slot" or "shoulder" or "tip" or "cylinder" or "through") || !double.IsFinite(c.WidthMm) ||
                !double.IsFinite(c.DepthMm) || c.WidthMm <= 0 || c.DepthMm <= 0 || !double.IsFinite(c.BreakthroughMm) ||
                c.BreakthroughMm < 0 || c.Mode != "through" && c.BreakthroughMm != 0)
                return "Invalid mode, dimensions or breakthrough.";
            bool mill = c.Mode is "slot" or "shoulder";
            if (mill && (c.InnerRadiusMm.HasValue == c.NoInsideCorner) ||
                !mill && (c.InnerRadiusMm.HasValue || c.NoInsideCorner) ||
                c.InnerRadiusMm is double r && (!double.IsFinite(r) || r < 0))
                return "For milling supply an inside radius or confirm noInsideCorner.";
        }
        return null;
    }

    internal const string CheckpointPrefix = "MCP_TOOL_OUTCOME ";

    internal static string Interrupted(ToolPlanRequest request,string stderr)
    {
        var states=new Dictionary<string,ChangeOutcome>();
        foreach (string line in stderr.Split('\n')) {
            if (!line.StartsWith(CheckpointPrefix,StringComparison.Ordinal)) continue;
            try {
                var item=JsonSerializer.Deserialize<ChangeOutcome>(line[CheckpointPrefix.Length..],AuditOutput.Json);
                if (item!=null) states[item.OperationId]=item.Status=="starting"? item with {Status="stateUnknown"}:item;
            } catch(JsonException) { /* The worker may stop partway through its last checkpoint. */ }
        }
        var outcomes=request.Changes.Select(c=>states.GetValueOrDefault(c.OperationId) ?? new(c.OperationId,"notConfirmed"));
        return JsonSerializer.Serialize(new{status="interrupted",outcomes,
            detail="Re-read the project and preview remaining changes before retrying."},AuditOutput.Json);
    }

    internal static string Digest<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, AuditOutput.Json)));

    internal static string Fingerprint(int pid, ToolPlanRequest request, IReadOnlyList<ToolChangeRow> rows) =>
        Digest(new {pid, request.ProjectPath, request.OperationUnits, rows});

    internal static bool Same(ToolBinding? a, ToolBinding? b) => a != null && b != null &&
        a.Id == b.Id && a.Identity == b.Identity && a.Source == b.Source;

    internal static (string Status, List<ChangeOutcome> Outcomes) Apply(ToolPlanRequest request, int pid,
        IReadOnlyList<ToolChangeRow> rows, Func<ProjectToolChange,ToolChangeRow> reread, Action<ToolChangeRow> assign, Action<ChangeOutcome>? checkpoint=null)
    {
        if (!request.Confirmed || string.IsNullOrWhiteSpace(request.PreviewId)) return ("confirmationRequired", []);
        if (rows.Any(r=>r.Problem!=null) || Fingerprint(pid,request,rows) != request.PreviewId) return ("stalePreview", []);
        var results = new List<ChangeOutcome>();
        void Record(ChangeOutcome outcome) { results.Add(outcome); checkpoint?.Invoke(outcome); }
        bool stop = false, changed = false;
        foreach (var row in rows) {
            string id=row.Change.OperationId;
            if (stop) { Record(new(id,"skipped")); continue; }
            bool attempted = false;
            try {
                var current = reread(row.Change);
                if (current.Problem != null || Digest(current) != Digest(row)) {
                    Record(new(id,"stale",current.Problem)); stop=true; continue;
                }
                if (Same(current.Before, current.After)) { Record(new(id,"unchanged")); continue; }
                checkpoint?.Invoke(new(id,"starting"));
                attempted=true;
                assign(current);
                var actual=reread(row.Change);
                if (actual.Problem != null || !Same(actual.Before,current.After) || Digest(actual.After) != Digest(current.After))
                    throw new InvalidOperationException("ENCY did not confirm the requested assignment.");
                Record(new(id,"applied")); changed=true;
            } catch(Exception e) {
                string status="failed";
                if (attempted) {
                    try {
                        var actual=reread(row.Change);
                        if (actual.Problem != null && actual.Before == null) status="stateUnknown";
                        else if (Same(actual.Before,row.After)) { status="appliedWithWarning"; changed=true; }
                        else if (actual.Before != null && !Same(actual.Before,row.Before) || actual.Before == null && row.Before != null)
                            status="changedUnexpectedly";
                    } catch {status="stateUnknown";}
                }
                Record(new(id,status,e.GetBaseException().Message)); stop=true;
            }
        }
        if (!stop) return ("complete",results);
        bool partial = changed || results.Any(r=>r.Status is "stateUnknown" or "changedUnexpectedly");
        return (partial ? "partial" : "stopped",results);
    }
}
