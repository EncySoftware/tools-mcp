using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace ToolsMcp;

internal static partial class EncyIpc
{
    private static bool SameProjectPath(string? first,string? second) =>
        !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second) &&
        Path.IsPathFullyQualified(first) && Path.IsPathFullyQualified(second) &&
        string.Equals(Path.GetFullPath(first),Path.GetFullPath(second),StringComparison.OrdinalIgnoreCase);

    private static ToolBinding Binding(Installed t) => new(t.ID,t.Identity,t.Node?"machineMagazine":"projectToolList",
        t.Caption,t.Type,t.Magazine,t.Position,PositiveMm(t.DiameterMm),PositiveMm(t.ReachMm),
        InstalledDrillPoint.ValidAngle(t.Angle)?t.Angle:null,t.Adapters);

    private static string? GeometryConflict(ProjectToolChange c, OperationSnapshot s, ToolBinding target)
    {
        if (s.Error != null) return "Could not verify the operation geometry: "+s.Error;
        bool mill = c.Mode is "slot" or "shoulder";
        if (mill && s.Mill == null || !mill && s.Holes == null) return "The selected tool type does not suit this cycle.";
        if (mill) {
            var b=s.Mill!;
            if (b.Operation == "shoulder" && c.Mode != b.Operation || c.DepthMm < b.Depth || c.WidthMm > b.Width ||
                b.InnerRadius is double radius && (c.NoInsideCorner || c.InnerRadiusMm > radius))
                return "The requested milling clearance or depth conflicts with recognized project geometry.";
        } else {
            foreach (var h in s.Holes!) {
                if (h.DiameterMm is double d && Math.Abs(d-c.WidthMm)>1e-7 ||
                    h.DepthMm > c.DepthMm || h.Bottom is ("through" or "tip" or "cylinder") && h.Bottom != c.Mode ||
                    h.Bottom == "blind" && c.Mode == "through")
                    return "The requested drill geometry conflicts with one or more assigned holes.";
            }
        }
        if (target.DiameterMm is not double dia || target.ReachMm is not double reach) return "The tool geometry is unverified.";
        if (mill && (!target.Type.Split('.').Last().Equals("EndMill",StringComparison.OrdinalIgnoreCase) || dia>c.WidthMm || dia>2*c.InnerRadiusMm))
            return "The installed end mill does not meet the milling geometry.";
        if (!mill && (!InstalledDrillPoint.IsStandardDrill(target.Type) || Math.Abs(dia-c.WidthMm)>1e-7))
            return "The installed drill has the wrong type or diameter.";
        double? required = mill || c.Mode == "tip" ? c.DepthMm :
            InstalledDrillPoint.RequiredDepth(dia,c.DepthMm,c.BreakthroughMm,target.PointAngle);
        if (required == null) return "Missing installed drill point angle.";
        return reach < required ? "Insufficient working reach." : null;
    }

    private sealed record NativePlanReport(string Status,int ProcessId,string ProjectPath,string? PreviewId,
        List<ToolChangeRow> Rows,List<ChangeOutcome>? Outcomes);

    private static int ToolPlanCommand(Handle app,Handle project,AssemblyLoadContext runtime,int pid,string path,string command)
    {
        if (command is not ("preview" or "apply")) throw new ArgumentException("Unknown plan action.");
        if (command == "apply" && !LiveProjectChangeTools.Enabled)
            throw new InvalidOperationException("Live batch tool changes are disabled.");
        var request=JsonSerializer.Deserialize<ToolPlanRequest>(Console.In.ReadToEnd(),AuditOutput.Json)
            ?? throw new ArgumentException("Missing plan.");
        var report=RunToolPlan(app,project,runtime,pid,path,command,request);
        Console.WriteLine(JsonSerializer.Serialize(report,AuditOutput.Json));
        return 0;
    }

    private static NativePlanReport RunToolPlan(Handle app,Handle project,AssemblyLoadContext runtime,int pid,string path,
        string command,ToolPlanRequest request,bool isolatedCopy=false)
    {
        if (ProjectToolChangePlan.Validate(request) is string invalid) throw new ArgumentException(invalid);
        using var guard = new Mutex(false, "EncySoftware.ToolsMcp.ToolPlan."+pid);
        bool acquired=false;
        try {
            try { acquired=guard.WaitOne(0); } catch (AbandonedMutexException) { acquired=true; }
            if (!acquired) throw new InvalidOperationException("Another tool plan is being checked or applied to this process.");
            CheckProject();
            var tech=project.Object("GetTechnologist");
            var assemblyType = project.Type.GetMethod("get_MachineTools") == null ? null :
                runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.ToolAssemblies")).GetType("CAMIPC.ToolAssemblies.ICamIpcTechOperationWithToolAssembly");
            double scale=request.OperationUnits=="metric"?1:25.4;
            bool NeedsAngle(double d) => request.Changes.Any(c=>c.Mode is ("through" or "cylinder") && Math.Abs(d-c.WidthMm)<=1e-7);
            var inventory=Inventory(project,scale,NeedsAngle,includeIncomplete:true);
            Handle Operation(string id) {
                var op=tech.Object("GetOperationById",id);
                if (!Guid.TryParse(op.Text("GetId"),out var actual) || actual!=Guid.Parse(id))
                    throw new InvalidOperationException("Operation is no longer present.");
                return op;
            }
            ToolChangeRow Read(ProjectToolChange c, List<Installed> tools) {
                CheckProject();
                try {
                    var op=Operation(c.OperationId);
                    var s=Snapshot(op,project,runtime,scale,null,assemblyType,strictAssignments:true);
                    string? beforeId=s.NodeId??s.LegacyId;
                    var current=tools.FirstOrDefault(t=>t.ID==beforeId && t.Node==(s.NodeId!=null));
                    var before=current==null?null:Binding(current);
                    var matches=tools.Where(t=>t.ID==c.ToolId && t.Identity==c.Identity &&
                        (c.Source==null || t.Node==(c.Source=="machineMagazine"))).ToArray();
                    var after=matches.Length==1?Binding(matches[0]):null;
                    string? problem = !string.IsNullOrWhiteSpace(beforeId) && (before==null || string.IsNullOrWhiteSpace(before.Identity))
                        ? "Original tool identity could not be verified." : after==null
                        ? "Replacement tool identity is missing or ambiguous." : GeometryConflict(c,s,after);
                    return new(c,s.Name,ProjectToolChangePlan.Digest(s),before,after,problem);
                } catch(Exception e) when (ReadError(e)) {
                    return new(c,"","",null,null,e.GetBaseException().Message);
                }
            }
            var rows=request.Changes.Select(c=>Read(c,inventory)).ToList();
            if (command=="preview") {
                bool ready=rows.All(r=>r.Problem==null);
                return new(ready?"ready":"needsReview",pid,path,ready?ProjectToolChangePlan.Fingerprint(pid,request,rows):null,rows,null);
            } else {
                var result=ProjectToolChangePlan.Apply(request,pid,rows,
                    c=>Read(c,Inventory(project,scale,NeedsAngle,includeIncomplete:true)), row=>{
                        CheckProject();
                        var after=row.After!;
                        var op=Operation(row.Change.OperationId);
                        SetInstalled(project,runtime,op,row.Change.OperationId,after.Id,after.Source=="machineMagazine");
                    }, isolatedCopy?null:o=>Console.Error.WriteLine(ProjectToolChangePlan.CheckpointPrefix+JsonSerializer.Serialize(o,AuditOutput.Json)),
                    approve: isolatedCopy ? null : () => LiveProjectChangeTools.Enabled && LiveProjectChangeTools.ConfirmOnDesktop(pid, path, rows));
                return new(result.Status,pid,path,request.PreviewId,rows,result.Outcomes);
            }
        } finally { if (acquired) guard.ReleaseMutex(); }

        void CheckProject() {
            bool onSource = SameProjectPath(path,request.ProjectPath);
            bool correctTarget = isolatedCopy ? !onSource : onSource;
            if (!correctTarget || !SameProjectPath(app.Object("GetActiveProject").Text("GetFilePath"),path))
                throw new InvalidOperationException("Active project changed.");
        }
    }
}
