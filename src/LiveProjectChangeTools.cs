using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

namespace ToolsMcp;

[McpServerToolType]
[SupportedOSPlatform("windows")]
public sealed class LiveProjectChangeTools
{
    internal const string Setting = "TOOLS_MCP_ENABLE_LIVE_BATCH_CHANGES";
    internal static bool Enabled => Environment.GetEnvironmentVariable(Setting) == "1";

    [McpServerTool(Name="apply_project_tool_changes"), Description(
        "Request local operator approval for a previewed batch of tool replacements in the open project. " +
        "A matching preview and confirmation in the ENCY desktop session are required. " +
        "Reports each assignment. Changes are in memory and stop at the first failure.")]
    public Task<string> ApplyProjectToolChanges(int processId, string projectPath, string operationUnits,
        ProjectToolChange[] changes, string previewId, bool confirmed=false, CancellationToken cancellationToken=default)
    {
        if (!Enabled) return Task.FromResult("ERROR: live batch tool changes are disabled.");
        if (!confirmed) return Task.FromResult("ERROR: review the plan before requesting desktop approval.");
        return LocalEncyTools.ReviewChanges(processId,"apply",new(projectPath,operationUnits,changes,previewId,true),cancellationToken);
    }

    internal static bool ConfirmOnDesktop(int pid, string projectPath, IReadOnlyList<ToolChangeRow> rows, Func<bool> disconnected)
    {
        if (!Environment.UserInteractive) return false;
        using var current = Process.GetCurrentProcess();
        using var ency = Process.GetProcessById(pid);
        if (current.SessionId != ency.SessionId || ency.MainWindowHandle == IntPtr.Zero) return false;
        var summary = new System.Text.StringBuilder();
        summary.AppendLine($"Проект: {projectPath}").AppendLine();
        foreach (var row in rows)
        {
            summary.AppendLine($"{row.OperationName} ({row.Change.OperationId})")
                .AppendLine($"Сейчас: {Label(row.Before)}")
                .AppendLine($"Заменить на: {Label(row.After)}").AppendLine();
        }
        summary.AppendLine("При ошибке уже выполненные замены останутся в памяти проекта.")
            .Append($"Время для подтверждения — {LiveBatchWorker.OperatorLimit.TotalMinutes:0} минут.");
        return BatchApprovalDialog.Show(summary.ToString(), rows.Count, disconnected);
    }

    private static string Label(ToolBinding? binding) => binding == null ? "—" :
        $"{binding.Caption} · {binding.Magazine} T#{binding.Position} · ID {binding.Id}";
}
