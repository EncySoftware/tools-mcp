using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
        ProjectToolChange[] changes, string previewId, bool confirmed=false)
    {
        if (!Enabled) return Task.FromResult("ERROR: live batch tool changes are disabled.");
        if (!confirmed) return Task.FromResult("ERROR: review the plan before requesting desktop approval.");
        return LocalEncyTools.ReviewChanges(processId,"apply",new(projectPath,operationUnits,changes,previewId,true));
    }

    [DllImport("user32.dll", EntryPoint="MessageBoxW", CharSet=CharSet.Unicode, ExactSpelling=true)]
    private static extern int MessageBox(IntPtr owner, string text, string title, uint flags);

    internal static bool ConfirmOnDesktop(int pid, string projectPath, IReadOnlyList<ToolChangeRow> rows)
    {
        if (!Environment.UserInteractive) return false;
        using var current = Process.GetCurrentProcess();
        using var ency = Process.GetProcessById(pid);
        var owner = ency.MainWindowHandle;
        if (current.SessionId != ency.SessionId || owner == IntPtr.Zero) return false;

        // Collect all answers before the first setter. Cancel, timeout or a closed window leaves the plan unapplied.
        for (int i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            string action = i + 1 == rows.Count ? "Применить согласованный план в открытом проекте?" :
                "Согласовать этот пункт и перейти к следующему?";
            string message = $"Проект: {projectPath}\n\nОперация: {row.OperationName} ({row.Change.OperationId})" +
                $"\nСейчас: {Label(row.Before)}\nЗаменить на: {Label(row.After)}\n\n" +
                "Запись начнётся после согласования всех пунктов. При ошибке уже выполненные замены останутся в памяти проекта.\n\n" + action;
            const uint yesNoDefaultNo = 0x4 | 0x100 | 0x30;
            if (MessageBox(owner,message,$"ENCY · Замена инструмента {i + 1} из {rows.Count}",yesNoDefaultNo) != 6)
                return false;
        }
        return true;
    }

    private static string Label(ToolBinding? binding) => binding == null ? "—" :
        $"{binding.Caption} · {binding.Magazine} T#{binding.Position} · ID {binding.Id}";
}
