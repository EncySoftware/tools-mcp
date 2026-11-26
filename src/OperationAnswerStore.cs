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
    internal static OperationAnswerStore For(AuditReport report) => new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EncySoftware","ToolsMcp","operation-answers",
        Hash(Path.GetFullPath(report.ProjectPath).ToUpperInvariant())));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private string FileOf(string id) => Path.Combine(directory,Hash(id.ToUpperInvariant())+".json");
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
                if(entry?.ProjectHash==hash && entry.GeometryHash==Stamp(row,report)) result[row.OperationId]=entry.Values;
            } catch(Exception e) when(e is JsonException or IOException or UnauthorizedAccessException) { }
        }
        return result;
    }
    internal static ProjectPlanGeometry Merge(ProjectPlanGeometry? old,ProjectPlanGeometry next) => old==null?next:next with {
        Mode=next.Mode??old.Mode,BreakthroughMm=next.BreakthroughMm??(next.Mode is null or "through"?old.BreakthroughMm:null),WidthMm=next.WidthMm??old.WidthMm,DepthMm=next.DepthMm??old.DepthMm,
        InnerRadiusMm=next.NoInsideCorner==true?null:next.InnerRadiusMm??old.InnerRadiusMm,
        NoInsideCorner=next.InnerRadiusMm.HasValue?false:next.NoInsideCorner??old.NoInsideCorner,Magazine=next.Magazine??old.Magazine
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
    public async Task<string> RememberProjectToolAnswers(string operationUnits,ProjectPlanGeometry answer,int? processId=null,bool forget=false)
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
