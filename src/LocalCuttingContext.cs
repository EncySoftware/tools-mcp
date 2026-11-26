using System.ComponentModel;
using ModelContextProtocol.Server;

namespace ToolsMcp;

public sealed partial class LocalEncyTools
{
    [McpServerTool(Name="get_operation_cutting_context",ReadOnly=true),Description(
        "Read the named open ENCY operation's current speed and feed with units, material, machine, assigned tool reference, " +
        "and active lathe constant-surface-speed RPM cap when present. The CSS operation cap is distinct from axis speeds and a physical machine maximum.")]
    public Task<string> GetOperationCuttingContext(int processId,string projectPath,string operationId,string operationUnits) =>
        operationUnits is "metric" or "imperial" ? Run("cutting-context",processId,[projectPath,operationId,operationUnits]) :
            Task.FromResult("ERROR: specify metric or imperial.");
}
