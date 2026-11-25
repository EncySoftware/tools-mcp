using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

namespace ToolsMcp;

[McpServerToolType]
[SupportedOSPlatform("windows")]
public sealed class LocalEncyTools
{
    [McpServerTool(Name = "list_project_tools", ReadOnly = true), Description(
        "Reads installed tools from the current ENCY machine magazines. Returns the ENCY process id, project path, magazine, actual T position and component caption. If several ENCY processes are open, choose a processId.")]
    public Task<string> ListProjectTools(int? processId = null) => Run("list", processId, []);

    [McpServerTool(Name = "import_project_gtc"), Description(
        "Installs a local GTC zip into the specified open ENCY project in advanced tools mode. Creates a component library, modifies the open project and reports installed tools and skipped reasons. Use list_project_tools to identify the target; never assume a shelf T number is its magazine position. Does not save the project.")]
    public Task<string> ImportProjectGtc(int processId, string projectPath, string packagePath, string? magazine = null)
        => Run("import", processId, [projectPath, packagePath, magazine ?? ""]);

    private static async Task<string> Run(string operation, int? pid, string[] arguments)
    {
        try
        {
            if (pid == null)
            {
                var instances = Process.GetProcessesByName("ency");
                try
                {
                    if (instances.Length == 0) return "ERROR: open a project in ENCY first.";
                    if (instances.Length != 1)
                        return "ERROR: select an ENCY processId: " + string.Join(", ", instances.Select(p => $"{p.Id} {p.MainWindowTitle}"));
                    pid = instances[0].Id;
                }
                finally { foreach (var p in instances) p.Dispose(); }
            }
            // Keep each installation's native DLLs and COM objects in a separate short-lived process.
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--ency-ipc");
            start.ArgumentList.Add(operation);
            start.ArgumentList.Add(pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in arguments) start.ArgumentList.Add(value);
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start ENCY IPC.");
            var stdout = worker.StandardOutput.ReadToEndAsync();
            var stderr = worker.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            try { await worker.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!worker.HasExited) worker.Kill();
                return "ERROR: ENCY did not reply in time. Check the magazine before repeating an import.";
            }
            var reply = (await stdout).Trim();
            var detail = (await stderr).Trim();
            if (worker.ExitCode == 0) return reply;
            return "ERROR: " + (detail.Length > 0 ? detail : $"ENCY IPC stopped ({worker.ExitCode}). Check the project before retrying.");
        }
        catch (Exception e) { return "ERROR: " + e.Message; }
    }
}
