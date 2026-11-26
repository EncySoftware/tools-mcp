using System.ComponentModel;
using System.Text.Json;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

namespace ToolsMcp;

[SupportedOSPlatform("windows")]
public sealed partial class LocalEncyTools
{
    [McpServerTool(Name="preview_project_tool_changes",ReadOnly=true), Description(
        "Preview user-selected installed tool replacements in named operations. Supply ID and identity of each installed tool " +
        "and exact geometry in mm. The local check returns before and after assignments, problems and a fingerprint. " +
        "For milling provide innerRadiusMm or noInsideCorner=true. Leaves the open project untouched.")]
    public Task<string> PreviewProjectToolChanges(int processId,string projectPath,string operationUnits,ProjectToolChange[] changes) =>
        ReviewChanges(processId,"preview",new(projectPath,operationUnits,changes));

    [McpServerTool(Name="apply_project_tool_changes"), Description(
        "Apply the exact previously previewed installed tool changes in the open project after user confirmation. " +
        "Repeat the preview inputs and previewId with confirmed=true. Stale plans require a new preview. Returns per-operation " +
        "outcomes and stops on the first failure. Completed changes remain in memory. Does not save or recalculate the project.")]
    public Task<string> ApplyProjectToolChanges(int processId,string projectPath,string operationUnits,ProjectToolChange[] changes,
        string previewId,bool confirmed=false) => confirmed ?
        ReviewChanges(processId,"apply",new(projectPath,operationUnits,changes,previewId,true)) :
        Task.FromResult("ERROR: show the preview and obtain confirmation before applying it.");

    [McpServerTool(Name="simulate_project_tool_assemblies", ReadOnly=true), Description(
        "Copy a saved ENCY project or an explicitly provided snapshot and simulate in an isolated console. Optional changes and previewId replay a verified plan ONLY on the copy. Other unsaved edits are excluded. " +
        "Clears cached results only in the copy, recalculates all toolpaths, enables holder and machine collision checks, " +
        "and reports actual simulation flags per operation. Optional operationIds limits the report, not the simulation. " +
        "Can take minutes. Does not save, simulate or modify the original project.")]
    public Task<string> SimulateProjectToolAssemblies(int processId,string projectPath,string operationUnits,string[]? operationIds=null,
        string? snapshotPath=null,ProjectToolChange[]? changes=null,string? previewId=null)
    {
        var request=new SimulationRequest(projectPath,operationUnits,operationIds,snapshotPath,changes,previewId);
        return request.Valid() ? Run("assembly-simulation",processId,[],JsonSerializer.Serialize(request,AuditOutput.Json)) :
            Task.FromResult("ERROR: specify the project path, metric or imperial units and valid operation IDs.");
    }

    private static Task<string> ReviewChanges(int pid,string command,ToolPlanRequest request) =>
        ProjectToolChangePlan.Validate(request) is { } problem ? Task.FromResult("ERROR: "+problem) :
            Run("tool-plan",pid,[command],JsonSerializer.Serialize(request,AuditOutput.Json));
}
