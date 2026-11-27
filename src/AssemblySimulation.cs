using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.Loader;
using System.Text.Json;

namespace ToolsMcp;

internal sealed record SimulationRequest(string ProjectPath,string OperationUnits,string[]? OperationIds,
    string? SnapshotPath=null,ProjectToolChange[]? Changes=null,string? PreviewId=null)
{
    internal bool Valid() => !string.IsNullOrWhiteSpace(ProjectPath) && OperationUnits is ("metric" or "imperial") &&
        (OperationIds == null || OperationIds.Length > 0 && OperationIds.All(s=>Guid.TryParse(s,out _))) &&
        (SnapshotPath==null || Path.IsPathFullyQualified(SnapshotPath)) &&
        (Changes==null ? PreviewId==null : !string.IsNullOrWhiteSpace(PreviewId) &&
            ProjectToolChangePlan.Validate(new(ProjectPath,OperationUnits,Changes))==null);
}

internal sealed record SimulationEvidence(bool CachedToolpathCleared,bool CachedSimulationCleared,bool Calculated,bool Simulated,
    bool AssemblyVerified,bool HolderChecks,bool MachineChecks, IReadOnlyList<string> Errors)
{
    /// <summary>ENCY's API simulates only fast, and its fast simulation does not compute holder or machine collisions.</summary>
    public bool CollisionsChecked => false;

    public string Status => !CachedToolpathCleared || !CachedSimulationCleared || !AssemblyVerified || !HolderChecks || !MachineChecks
        ? "unverified" : Errors.Count>0 ? "errorsDetected" : !Calculated || !Simulated ? "incomplete" : "noErrorsReportedCollisionsUnchecked";
}

internal static partial class EncyIpc
{
    private sealed record SimulationOp(Handle Operation,string Id,string Name,ToolBinding? Tool);

    private static Dictionary<Guid,SimulationOp> SimulationOperations(Handle project,AssemblyLoadContext runtime,double scale)
    {
        var tech=project.Object("GetTechnologist");
        var enumType=tech.Type.GetMethod("GetOperations")!.GetParameters()[0].ParameterType;
        var iter=tech.Object("GetOperations",Enum.ToObject(enumType,0));
        var inventory=Inventory(project,scale,_=>true,includeIncomplete:true);
        var result=new Dictionary<Guid,SimulationOp>();
        Type? assemblyType = project.Type.GetMethod("get_MachineTools")==null ? null :
            runtime.LoadFromAssemblyName(new AssemblyName("CAMIPC.ToolAssemblies")).GetType("CAMIPC.ToolAssemblies.ICamIpcTechOperationWithToolAssembly");
        void Children() {
            if(iter.Count("MoveToChild")==0) return;
            do {
                var op=iter.Object("Current"); var xml=op.Object("get_XMLProp");
                if (!xml.Text("get_Name").EndsWith("Group",StringComparison.OrdinalIgnoreCase)) {
                    string id=op.Text("GetId");
                    var assigned=Assignment(op,xml,assemblyType);
                    var t=assigned.NodeId!=null ? inventory.FirstOrDefault(t=>t.Node && t.ID==assigned.NodeId) :
                        inventory.FirstOrDefault(t=>!t.Node && t.ID==assigned.LegacyId);
                    result.Add(Guid.Parse(id),new(op,id,op.Text("GetName"),t==null?null:Binding(t)));
                }
                Children();
            } while(iter.Count("MoveToSibling")!=0);
            if (iter.Count("MoveToParent")==0) throw new InvalidOperationException("Operation tree changed.");
        }
        Children();return result;
    }

    private static int AssemblySimulation(Handle helper,Handle app,Handle project,AssemblyLoadContext runtime,string path)
    {
        var request=JsonSerializer.Deserialize<SimulationRequest>(Console.In.ReadToEnd(),AuditOutput.Json)
            ?? throw new ArgumentException("Missing simulation request.");
        if (!request.Valid() || !SameProjectPath(request.ProjectPath,path))
            throw new InvalidOperationException("Verify the project, units and operation IDs before simulation.");
        double scale=request.OperationUnits=="metric"?1:25.4;
        var original=SimulationOperations(project,runtime,scale);
        var wanted=request.OperationIds?.Select(Guid.Parse).Distinct().ToArray()??original.Keys.ToArray();
        if(wanted.Length==0 || wanted.Any(id=>!original.ContainsKey(id))) throw new InvalidOperationException("No matching operations in the source project.");
        bool modified=project.Count("GetIsModified")!=0;
        string source=request.SnapshotPath??path;
        if (modified && request.Changes==null && (request.SnapshotPath==null || SameProjectPath(source,path))) {
            Console.WriteLine(JsonSerializer.Serialize(new {status="requiresSnapshot",projectPath=path,
                detail="Unsaved changes: provide a separate saved snapshot, or the exact previewed changes to simulate on the saved baseline."},AuditOutput.Json));
            return 0;
        }
        string extension=Path.GetExtension(source);
        if (!extension.Equals(".stcp",StringComparison.OrdinalIgnoreCase) && !extension.Equals(".stc",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Specify a saved ENCY .stcp or .stc project.");
        if(helper.Type.GetMethod("RunNewConsoleApp")==null)
            throw new NotSupportedException("This ENCY SDK cannot simulate an isolated project copy.");
        var scratch=Directory.CreateTempSubdirectory("ency-mcp-simulation-");
        try {
            string copy=Path.Combine(scratch.FullName,"snapshot"+extension);
            if(!SameProjectPath(app.Object("GetActiveProject").Text("GetFilePath"),path))
                throw new InvalidOperationException("Active project changed before the simulation.");
            // ExportCurrentProject can write the open project's original file. Only read saved bytes.
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=new FileStream(copy,FileMode.CreateNew,FileAccess.Write,FileShare.None)) input.CopyTo(output);
            string sha;
            using(var input=File.OpenRead(copy)) sha=Convert.ToHexString(SHA256.HashData(input));
            var worker=helper.Object("RunNewConsoleApp","/JOB_MODE");
            try {
                WorkerCall(worker,"WaitForStarted",90000);
                worker.Call("OpenProject",[copy,false]);
                var model=worker.Object("GetActiveProject");
                if (!string.Equals(Path.GetFullPath(model.Text("GetFilePath")),Path.GetFullPath(copy),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Simulation instance did not open the exported snapshot.");
                var copied=SimulationOperations(model,runtime,scale);
                if (!original.Keys.ToHashSet().SetEquals(copied.Keys))
                    throw new InvalidOperationException("The saved project has a different operation tree. Supply an up-to-date separate snapshot.");
                var plannedIds=request.Changes?.Select(c=>Guid.Parse(c.OperationId)).ToHashSet()??[];
                foreach (var id in original.Keys.Where(id=>!plannedIds.Contains(id)))
                    if (ProjectToolChangePlan.Digest(original[id].Tool)!=ProjectToolChangePlan.Digest(copied[id].Tool))
                        throw new InvalidOperationException("Saved and open tool assignments differ for "+original[id].Name+". Supply a separate saved snapshot.");
                NativePlanReport? plan=null;
                if (request.Changes!=null) {
                    int sourcePid=app.Count("get_ProcessId");
                    var replay=new ToolPlanRequest(request.ProjectPath,request.OperationUnits,request.Changes,request.PreviewId,true);
                    var preview=RunToolPlan(worker,model,runtime,sourcePid,copy,"preview",replay,isolatedCopy:true);
                    if (preview.Status!="ready" || preview.PreviewId!=request.PreviewId)
                        throw new InvalidOperationException("The saved copy is not the previewed baseline. Obtain a new preview or supply a suitable separate snapshot.");
                    foreach (var row in preview.Rows) {
                        var live=original[Guid.Parse(row.Change.OperationId)].Tool;
                        if (!SameSimulationTool(live,row.Before) && !SameSimulationTool(live,row.After))
                            throw new InvalidOperationException("The live assignment no longer matches the preview for "+row.OperationName);
                    }
                    plan=RunToolPlan(worker,model,runtime,sourcePid,copy,"apply",replay,isolatedCopy:true);
                    if (plan.Status!="complete") throw new InvalidOperationException("Could not replay the complete plan in the isolated copy: "+plan.Status);
                    copied=SimulationOperations(model,runtime,scale);
                    if(plan.Rows.Any(r=>!SameSimulationTool(copied[Guid.Parse(r.Change.OperationId)].Tool,r.After)))
                        throw new InvalidOperationException("Simulated copy did not confirm every requested tool.");
                }
                var tech=model.Object("GetTechnologist"); var sim=model.Object("get_Simulator");
                // ENCY keeps these in the operator's own settings as soon as they change: put every one back afterwards
                string[] switches=["CheckHolderCollisions","CheckMachineCollisions","BreakOnErrors","BreakOnEndOfOperation","BreakOnStopCommand"];
                var operatorSettings=switches.ToDictionary(s=>s,s=>sim.Count("get_"+s)!=0);
                try {
                sim.Call("set_CheckHolderCollisions",[true]);sim.Call("set_CheckMachineCollisions",[true]);
                sim.Call("set_BreakOnErrors",[false]);sim.Call("set_BreakOnEndOfOperation",[false]);sim.Call("set_BreakOnStopCommand",[false]);
                bool holders=sim.Count("get_CheckHolderCollisions")!=0,machine=sim.Count("get_CheckMachineCollisions")!=0;
                tech.Call("ResetAllOperationsToolpath",[]);sim.Call("ResetSimulationResults",[]);
                var cleared=wanted.ToDictionary(id=>id,id=>(Path:copied[id].Operation.Count("GetCalculated")==0,
                                                           Sim:copied[id].Operation.Count("GetSimulated")==0));
                tech.Call("CalculateAllOperationsToolpath",[true]);
                var mode=worker.Type.GetMethod("SetMainWorkMode")!.GetParameters()[0].ParameterType;
                worker.Call("SetMainWorkMode",[Enum.Parse(mode,"mwmSimulating")]);
                sim.Call("FastSimulateAllOperations",[]);
                var rows=wanted.Select(id=>{
                    var row=copied[id];var op=row.Operation;
                    var errors=new List<string>();
                    foreach(string category in new[]{"Holder","Collision","Rapid","Gouge","Plunge","Travel","Compensation","ToolOverload","TurnDirection"})
                        if(op.Count("GetIs"+category+"Error")!=0) errors.Add(category);
                    if (op.Count("GetIsError")!=0 && errors.Count==0) errors.Add("unspecified");
                    var evidence=new SimulationEvidence(cleared[id].Path,cleared[id].Sim,op.Count("GetCalculated")!=0,
                        op.Count("GetSimulated")!=0,row.Tool!=null && !string.IsNullOrWhiteSpace(row.Tool.Identity),holders,machine,errors);
                    return new {operationId=row.Id,operationName=row.Name,assignedTool=row.Tool,evidence};
                }).ToList();
                string assessment=rows.Any(r=>r.evidence.Status=="errorsDetected") ? "errorsDetected" :
                    rows.All(r=>r.evidence.Status=="noErrorsReportedCollisionsUnchecked") ? "noErrorsReportedCollisionsUnchecked" : "verificationIncomplete";
                Console.WriteLine(JsonSerializer.Serialize(new{status="complete",assessment,
                    collisionCheck="notPerformed: ENCY's API simulates only fast, and fast simulation does not check holder or machine collisions",
                    projectPath=path,source=request.SnapshotPath!=null?"providedSavedSnapshot":"savedProjectFile",
                    sourcePath=source,sourceFileSha256=sha,sourceHadUnsavedChanges=modified,
                    previewId=plan?.PreviewId,changesReplayedInCopy=plan?.Outcomes,otherUnsavedEditsIncluded=false,
                    simulatedScope="entireProject",reportedOperations=rows},AuditOutput.Json));
                return 0;
                } finally { foreach(var (name,value) in operatorSettings) sim.Call("set_"+name,[value]); }
            } finally { WorkerCall(worker,"Close",false); }
        } finally { if(scratch.Exists) scratch.Delete(true); }
    }

    // Angles may have been intentionally skipped during a milling preview.
    private static bool SameSimulationTool(ToolBinding? actual,ToolBinding? expected)
    {
        if (actual==null || expected==null) return actual==null && expected==null;
        return ProjectToolChangePlan.Digest(actual with {PointAngle=expected.PointAngle==null?null:actual.PointAngle})==
            ProjectToolChangePlan.Digest(expected);
    }

    private static void WorkerCall(Handle app,string method,object value) {
        object?[] args=[value,null];
        app.Type.GetMethod(method)!.Invoke(app.Value,args);
        var status=args[1]??throw new InvalidOperationException("Missing ENCY worker status.");
        if(status.GetType().GetField("Code")!.GetValue(status)?.ToString()=="rsError")
            throw new InvalidOperationException(method+": "+status.GetType().GetField("Description")!.GetValue(status));
    }
}
