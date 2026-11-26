using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

namespace ToolsMcp;

[McpServerToolType]
[SupportedOSPlatform("windows")]
public sealed class LocalEncyTools(IToolsApi api, TokenProvider tokens)
{
    [McpServerTool(Name = "list_project_tools", ReadOnly = true), Description(
        "Reads installed tools from the current ENCY machine magazines. Returns the ENCY process id, project path, magazine, actual T position and component caption. If several ENCY processes are open, choose a processId.")]
    public Task<string> ListProjectTools(int? processId = null) => Run("list", processId, []);

    [McpServerTool(Name = "import_project_gtc"), Description(
        "Installs a local GTC zip into the specified open ENCY project in advanced tools mode. Creates a component library, modifies the open project and reports installed tools and skipped reasons. Use list_project_tools to identify the target; never assume a shelf T number is its magazine position. Does not save the project.")]
    public Task<string> ImportProjectGtc(int processId, string projectPath, string packagePath, string? magazine = null)
        => Run("import", processId, [projectPath, packagePath, magazine ?? ""]);

    [McpServerTool(Name = "list_tool_operations", ReadOnly = true), Description(
        "Shows the names and ids of operations that use a tool position in a specific magazine of the open ENCY project. Use the magazine name or caption and T number from list_project_tools.")]
    public Task<string> ListToolOperations(int processId, string magazine, int position) =>
        Run("operations", processId, [magazine, position.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    [McpServerTool(Name = "apply_operation_cutting"), Description(
        "Apply a supplier's speed and feed from one article condition to a named ENCY operation. " +
        "Use get_cutting_data for the condition number and list_tool_operations for the operation id. " +
        "Supply the active operation's displayed measurement system (metric or imperial), the exact speed and feed units from the row, " +
        "and either minimum or maximum. Changes the open project without saving it.")]
    public Task<string> ApplyOperationCutting(string id, int condition, string boundary, string speedUnit,
        string feedUnit, int processId, string projectPath, string operationId, string operationUnits) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } cid) return Ids.NotAComponent(id);
            if (boundary is not ("minimum" or "maximum") || operationUnits is not ("metric" or "imperial"))
                return "ERROR: specify minimum or maximum, and the operation units (metric or imperial).";
            var rows = (await api.GetCompat(cid, token))?.ArticleCutting;
            if (rows == null || condition < 1 || condition > rows.Count) return "ERROR: unknown article condition.";
            var settings = CuttingSetting.FromArticle(rows[condition - 1].Fields, boundary == "maximum")
                .Where(s => s.Unit == speedUnit || s.Unit == feedUnit).ToList();
            if (!CuttingSetting.IsPair(settings) || speedUnit is not ("rpm" or "m/min"))
                return "ERROR: select one available spindle or cutting speed and one available feed from the condition.";
            return await Run("cutting", processId, [projectPath, operationId, operationUnits,
                .. settings.SelectMany(s => new[] { s.Unit, s.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) })]);
        });

    [McpServerTool(Name = "install_shelf_in_project"), Description(
        "Imports a saved catalogue shelf into an open ENCY project in one call. Use a libraryId from list_libraries and the PID and exact project path from list_project_tools. Creates a component library, installs compatible tools, reports actual holders and positions. Does not save the project.")]
    public Task<string> InstallShelfInProject(string libraryId, int processId, string projectPath, string? magazine = null) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(libraryId) is not { } id) return Ids.NotALibrary(libraryId);
            var shelf = await LibraryTools.BuildShelfGtc(api, id, token);
            var file = SavedFiles.Save(shelf.Package.File, null);
            var result = await ImportProjectGtc(processId, projectPath, file, magazine);
            return $"{result}\n{shelf.Summary} GTC: {file}";
        });

    [McpServerTool(Name = "get_drilling_operation", ReadOnly = true), Description(
        "Read the selected drilling operation, its hole dimensions, material if specified, operation id and project path. " +
        "Supply metric or imperial according to the project's displayed units. Dimensions returned are mm. " +
        "Blind/through classification is read from features linked to the assigned faces. Unresolved dimensions or depth references require user input.")]
    public Task<string> GetDrillingOperation(string operationUnits, int? processId = null) =>
        operationUnits is "metric" or "imperial" ? Run("drill-context", processId, [operationUnits])
            : Task.FromResult("ERROR: specify metric or imperial.");

    [McpServerTool(Name="get_milling_operation", ReadOnly=true), Description("Read the selected ENCY milling operation and recognized pocket/side height and inside radius. Supply available slot or shoulder width when not specified by geometry.")]
    public Task<string> GetMillingOperation(string operationUnits,int? processId=null) =>
        Run("mill-context",processId,[operationUnits]);

    private static string[] GeometryArgs(string units,string mode,double width,double depth,double extra,double? radius) =>
        [units,mode,width.ToString("R",System.Globalization.CultureInfo.InvariantCulture),depth.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
         extra.ToString("R",System.Globalization.CultureInfo.InvariantCulture),radius?.ToString("R",System.Globalization.CultureInfo.InvariantCulture)??""];

    [McpServerTool(Name="find_tools_for_selected_operation", ReadOnly=true), Description(
        "Search the selected project's installed magazine/tools first, then matching warehouse assemblies and catalogue cutters. " +
        "mode: tip/cylinder/through for drilling, slot/shoulder for milling. widthMm is the exact hole diameter or available milling width. " +
        "Omit unknown geometry to read it from the operation; ask only the returned missing fields. All dimensions in mm. operationUnits is metric or imperial as displayed by ENCY. Tools in unverified lack a value needed to confirm the fit. Use ID and Identity from installed for assignment.")]
    public async Task<string> FindToolsForSelectedOperation(string operationUnits, string? mode = null, double? widthMm = null,
        double? depthMm = null, int? processId = null, double breakthroughMm = 0, double? innerRadiusMm = null,
        string material = "", string? spindle = null, int page = 0, int size = 20, bool? noInsideCorner = null)
    {
        var raw = await Run("operation-context", processId, [operationUnits]);
        if (raw.StartsWith("ERROR", StringComparison.Ordinal)) return raw;
        var context = JsonNode.Parse(raw)!.AsObject();
        var brief = OperationBrief.FromContext(context, mode, widthMm, depthMm, breakthroughMm, innerRadiusMm,
            noInsideCorner, material, spindle, page, size);
        return await Replies.WithToken(tokens, async token => {
            var result = await api.FindOperationTools(brief, token);
            result["operationId"] = context["operationId"]?.DeepClone();
            result["projectPath"] = context["projectPath"]?.DeepClone();
            if (result["status"]?.GetValue<string>() != "ready") return result.ToJsonString();
            var fit = await Run("machine-fit", context["processId"]?.GetValue<int>() ?? processId,
                GeometryArgs(operationUnits, brief.Bottom ?? brief.Operation!, (brief.Diameter ?? brief.Width)!.Value,
                    brief.Depth!.Value, breakthroughMm, brief.InnerRadius));
            if (fit.StartsWith("ERROR", StringComparison.Ordinal)) return fit;
            var local = JsonNode.Parse(fit)!.AsObject();
            if (!JsonNode.DeepEquals(context["operationId"], local["operationId"]) ||
                !JsonNode.DeepEquals(context["projectPath"], local["projectPath"]))
                return "ERROR: selected operation changed during the search.";
            result["installed"] = local["installed"]?.DeepClone();
            result["assignedTool"] = local["assignedTool"]?.DeepClone();
            result["unverified"] = local["unverified"]?.DeepClone();
            return result.ToJsonString();
        });
    }

    [McpServerTool(Name="assign_installed_operation_tool"),Description("Assign an already installed project or magazine tool, without import. Confirm the target with the user. Repeat the exact search dimensions, ID and Identity from find_tools_for_selected_operation. Does not save the project.")]
    public Task<string> AssignInstalledOperationTool(int processId,string projectPath,string operationId,string toolId,string identity,
        string operationUnits,string mode,double widthMm,double depthMm,double breakthroughMm=0,double? innerRadiusMm=null) =>
        Run("machine-assign",processId,[.. GeometryArgs(operationUnits,mode,widthMm,depthMm,breakthroughMm,innerRadiusMm),projectPath,operationId,toolId,identity]);

    [McpServerTool(Name="assign_operation_mill"),Description("After user confirmation, assign the selected catalogue end mill or saved assembly to the selected milling operation. Use IDs from get_milling_operation and find_operation_mills. Does not save the project.")]
    public Task<string> AssignOperationMill(int processId,string projectPath,string operationId,string componentId,string? libraryId=null,string? assemblyId=null) =>
        AssignCutter(true,processId,projectPath,operationId,componentId,libraryId,assemblyId);

    [McpServerTool(Name = "assign_operation_drill"), Description(
        "Import one selected drill or saved warehouse assembly and assign it to the selected ENCY drilling operation. " +
        "Confirm the change with the user first. Use the processId, projectPath and operationId from get_drilling_operation " +
        "and the componentId, optional libraryId and assemblyId from find_operation_drills. Does not save the project.")]
    public Task<string> AssignOperationDrill(int processId, string projectPath, string operationId, string componentId,
        string? libraryId = null, string? assemblyId = null) =>
        AssignCutter(false,processId,projectPath,operationId,componentId,libraryId,assemblyId);

    private Task<string> AssignCutter(bool mill,int processId,string projectPath,string operationId,string componentId,string? libraryId,string? assemblyId) =>
        Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(componentId) is not { } cid) return Ids.NotAComponent(componentId);
            if (string.IsNullOrWhiteSpace(projectPath) || !Guid.TryParse(operationId, out _)
                || (libraryId == null) != (assemblyId == null)
                || (libraryId != null && (!Guid.TryParse(libraryId,out _) || !Guid.TryParse(assemblyId,out _))))
                return "ERROR: specify the selected project and operation, and both warehouse IDs or neither.";
            var file = mill ? await api.ExportMill(cid,libraryId,assemblyId,token) : await api.ExportDrill(cid, libraryId, assemblyId, token);
            var path = DrillFileCache.Store(file.Bytes);
            return await Run(mill ? "mill-assign" : "drill-assign", processId, [projectPath, operationId, path]);
        });

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
