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

    [McpServerTool(Name="simulate_project_tool_assemblies", ReadOnly=true), Description(
        "Copy a saved ENCY project or an explicitly provided snapshot and simulate in an isolated console. Optional changes and previewId replay a verified plan ONLY on the copy. Other unsaved edits are excluded. " +
        "Clears cached results only in the copy, recalculates all toolpaths, runs ENCY's fast simulation and reports its error flags per operation. " +
        "Holder and machine collisions are NOT checked: ENCY's API simulates only fast, which skips them. Optional operationIds limits the report, not the simulation. " +
        "Can take minutes. Does not save, simulate or modify the original project.")]
    public Task<string> SimulateProjectToolAssemblies(int processId,string projectPath,string operationUnits,string[]? operationIds=null,
        string? snapshotPath=null,ProjectToolChange[]? changes=null,string? previewId=null)
    {
        var request=new SimulationRequest(projectPath,operationUnits,operationIds,snapshotPath,changes,previewId);
        return request.Valid() ? Run("assembly-simulation",processId,[],JsonSerializer.Serialize(request,AuditOutput.Json)) :
            Task.FromResult("ERROR: specify the project path, metric or imperial units and valid operation IDs.");
    }

    internal static Task<string> ReviewChanges(int pid,string command,ToolPlanRequest request,CancellationToken cancellationToken=default) =>
        ProjectToolChangePlan.Validate(request) is { } problem ? Task.FromResult("ERROR: "+problem) :
            Run("tool-plan",pid,[command],JsonSerializer.Serialize(request,AuditOutput.Json),cancellationToken);
}
