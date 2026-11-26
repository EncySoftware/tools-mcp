using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

namespace ToolsMcp;

// Private to the current Windows profile. Saved answers are bound to project contents, units and work assignment.
internal sealed class OperationAnswerStore(string directory)
{
    private sealed record Remembered(string ProjectHash,string GeometryHash,ProjectPlanGeometry Values);
    internal static OperationAnswerStore For(AuditReport report) => ForProject(report.ProjectPath);
    internal static OperationAnswerStore ForProject(string path) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EncySoftware","ToolsMcp","operation-answers",
        Hash(Path.GetFullPath(path).ToUpperInvariant())));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string FileOf(string id) => Path.Combine(directory,Hash(id.ToUpperInvariant())+".json");
    internal bool HasAnswer(string id) => !string.IsNullOrWhiteSpace(id) && File.Exists(FileOf(id));

    internal async Task<(ProjectPlanGeometry? Answer,string? Error)> ForSelection(string path,string id,int pid,
        string units,Func<Task<AuditReport?>> audit)
    {
        if(!HasAnswer(id)) return (null,null);
        var report=await audit();
        if(report==null) return (null,null);
        if(report.ProcessId!=pid || !string.Equals(report.ProjectPath,path,StringComparison.OrdinalIgnoreCase) ||
           !string.Equals(report.SelectedOperationId,id,StringComparison.OrdinalIgnoreCase) || report.OperationUnits!=units)
            return (null,"ERROR: selected project, operation or units changed while checking remembered answers.");
        var rows=report.Operations.Where(r=>string.Equals(r.OperationId,id,StringComparison.OrdinalIgnoreCase)).ToList();
        if(rows.Count!=1) return (null,"ERROR: selected operation could not be identified in the project audit.");
        Read(report with {Operations=rows}).TryGetValue(id,out var answer);
        return (answer,null);
    }

    // Explicit input and known geometry have already been applied to the brief.
    internal static OperationBrief FillMissing(OperationBrief current,ProjectPlanGeometry? saved,
        bool explicitBreakthrough,List<string> fields)
    {
        if(saved==null) return current;
        if(current.Operation=="drilling") {
            if(current.Diameter==null && saved.WidthMm!=null) { current=current with{Diameter=saved.WidthMm};fields.Add("diameter"); }
            bool bottomMissing=current.Bottom==null || current.Bottom=="blind" && saved.Mode is ("tip" or "cylinder");
            if(bottomMissing && saved.Mode is ("through" or "tip" or "cylinder")) {
                current=current with{Bottom=saved.Mode};fields.Add("bottom");
            }
            if(!explicitBreakthrough && current.Bottom=="through" && saved.BreakthroughMm!=null) {
                current=current with{Breakthrough=saved.BreakthroughMm};fields.Add("breakthrough");
            }
        } else {
            if(current.Operation=="milling" && saved.Mode is ("slot" or "shoulder" or "pocket")) {
                current=current with{Operation=saved.Mode};fields.Add("operation");
            }
            if(current.Width==null && saved.WidthMm!=null) {
                current=current with{Width=saved.WidthMm,WidthSource="remembered"};fields.Add("width");
            }
            if(current.InnerRadius==null && current.NoInsideCorner!=true && saved.InnerRadiusMm!=null) {
                current=current with{InnerRadius=saved.InnerRadiusMm};fields.Add("innerRadius");
            } else if(current.InnerRadius==null && current.NoInsideCorner==null && saved.NoInsideCorner==true) {
                current=current with{NoInsideCorner=true};fields.Add("noInsideCorner");
            }
            if(current.Operation=="pocket" && current.BottomRadius==null && saved.BottomRadiusMm!=null) {
                current=current with{BottomRadius=saved.BottomRadiusMm,BottomRadiusSource="remembered"};fields.Add("bottomRadius");
            }
            if(current.PassDepth==null && saved.PassDepthMm!=null) {current=current with{PassDepth=saved.PassDepthMm};fields.Add("passDepth");}
        }
        if(current.Depth==null && saved.DepthMm!=null) {current=current with{Depth=saved.DepthMm};fields.Add("depth");}
        return current;
    }

    private static string Stamp(ProjectAuditRow row,AuditReport report) => ProjectToolChangePlan.Digest(new {
        report.OperationUnits,row.OperationId,row.OperationType,row.Setup,row.Machine,row.Material,row.MillingGeometry,row.Review.HoleGroups
    });
    private static string? SavedHash(AuditReport report)
    {
        if(report.ProjectModified!=false || !File.Exists(report.ProjectPath)) return null;
        using var stream=File.Open(report.ProjectPath,FileMode.Open,FileAccess.Read,FileShare.Read);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
    internal Dictionary<string,ProjectPlanGeometry> Read(AuditReport report)
    {
        var result=new Dictionary<string,ProjectPlanGeometry>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(directory) || report.ProjectModified!=false) return result;
        string? hash=null;
        foreach(var row in report.Operations) {
            string file=FileOf(row.OperationId);
            if (!File.Exists(file)) continue;
            try {
                hash??=SavedHash(report);
                if(hash==null) break;
                var entry=JsonSerializer.Deserialize<Remembered>(File.ReadAllText(file),AuditOutput.Json);
                if(entry is { Values: {} values } && entry.ProjectHash==hash &&
                   string.Equals(values.OperationId,row.OperationId,StringComparison.OrdinalIgnoreCase) && entry.GeometryHash==Stamp(row,report))
                    result[row.OperationId]=values;
            } catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    internal static ProjectPlanGeometry Merge(ProjectPlanGeometry? old,ProjectPlanGeometry next) => old==null?next:next with {
        Mode=next.Mode??old.Mode,BreakthroughMm=next.BreakthroughMm??(next.Mode is null or "through"?old.BreakthroughMm:null),WidthMm=next.WidthMm??old.WidthMm,DepthMm=next.DepthMm??old.DepthMm,
        InnerRadiusMm=next.NoInsideCorner==true?null:next.InnerRadiusMm??old.InnerRadiusMm,
        NoInsideCorner=next.InnerRadiusMm.HasValue?false:next.NoInsideCorner??old.NoInsideCorner,Magazine=next.Magazine??old.Magazine,BottomRadiusMm=next.BottomRadiusMm??old.BottomRadiusMm,PassDepthMm=next.PassDepthMm??old.PassDepthMm
    };
    internal string Remember(AuditReport report,ProjectPlanGeometry answer)
    {
        var row=report.Operations.SingleOrDefault(o=>o.OperationId.Equals(answer.OperationId,StringComparison.OrdinalIgnoreCase));
        if(row==null)return "ERROR: unknown operation ID.";
        var hash=SavedHash(report);
        if(hash==null)return "ERROR: save the current project before remembering answers. You can supply dimensions for this search without saving.";
        Read(report).TryGetValue(row.OperationId,out var old);
        var value=Merge(old,answer);
        var checkedRow=ProjectToolPlan.Rows(report,new Dictionary<string,ProjectPlanGeometry>(StringComparer.OrdinalIgnoreCase){{row.OperationId,value}})
            .Single(r=>r.Step.OperationId==row.OperationId);
        if(checkedRow.Error!=null)return "ERROR: "+checkedRow.Error;
        Directory.CreateDirectory(directory);
        string file=FileOf(row.OperationId), tmp=file+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(tmp,JsonSerializer.Serialize(new Remembered(hash,Stamp(row,report),value),AuditOutput.Json));
            File.Move(tmp,file,true);
        } finally {File.Delete(tmp);}
        return "Remembered local answers for "+row.OperationName;
    }
    internal void Forget(string id) {if(Directory.Exists(directory))File.Delete(FileOf(id));}
}

[SupportedOSPlatform("windows")]
public sealed partial class LocalEncyTools
{
    [McpServerTool(Name="remember_project_tool_answers"),Description(
        "Remember or forget operator-supplied dimensions for one operation in the current saved project. " +
        "Stored privately on this computer. The common project planner checks project contents and operation geometry before reuse. " +
        "Pass forget=true to clear the saved answer. Does not save the project.")]
    public async Task<string> RememberProjectToolAnswers(ProjectPlanGeometry answer,string operationUnits="auto",int? processId=null,bool forget=false)
    {
        var raw=await AuditProjectTools(operationUnits,processId);
        if(raw.StartsWith("ERROR",StringComparison.Ordinal))return raw;
        var report=JsonSerializer.Deserialize<AuditReport>(raw,AuditOutput.Json)!;
        if(string.IsNullOrWhiteSpace(report.ProjectPath)||!Path.IsPathFullyQualified(report.ProjectPath)||
            answer==null || !report.Operations.Any(o=>o.OperationId.Equals(answer.OperationId,StringComparison.OrdinalIgnoreCase)))
            return "ERROR: select a saved project and a valid operation.";
        try {
            var memory=OperationAnswerStore.For(report);
            if(forget){memory.Forget(answer.OperationId);return "Forgot the saved answers for "+answer.OperationId;}
            return memory.Remember(report,answer);
        }catch(Exception e) when(e is IOException or UnauthorizedAccessException){return "ERROR: could not access local operation answers: "+e.Message;}
    }
}
