using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using ModelContextProtocol.Server;

namespace ToolsMcp;

[McpServerToolType]
[SupportedOSPlatform("windows")]
public sealed partial class LocalEncyTools(IToolsApi api, TokenProvider tokens)
{
    /// <summary>Writing into the open project waits for the operator's own switch, as the batch apply does; previews still run.</summary>
    internal static string? WritesOff() => LiveProjectChangeTools.Enabled ? null :
        "ERROR: writing to the open ENCY project is disabled. Use the read-only tools and previews; the operator enables writes locally with " +
        LiveProjectChangeTools.Setting + "=1.";

    [McpServerTool(Name = "list_project_tools", ReadOnly = true), Description(
        "Reads installed tools from the current ENCY machine magazines. Returns the ENCY process id, project path, magazine, actual T position and component caption. If several ENCY processes are open, choose a processId.")]
    public Task<string> ListProjectTools(int? processId = null) => Run("list", processId, []);

    [McpServerTool(Name = "import_project_gtc"), Description(
        "Installs a local GTC zip into the specified open ENCY project in advanced tools mode. Creates a component library, modifies the open project and reports installed tools and skipped reasons. Use list_project_tools to identify the target; never assume a shelf T number is its magazine position. Does not save the project.")]
    public Task<string> ImportProjectGtc(int processId, string projectPath, string packagePath, string? magazine = null)
        => WritesOff() is { } off ? Task.FromResult(off) : Run("import", processId, [projectPath, packagePath, magazine ?? ""]);

    [McpServerTool(Name = "list_tool_operations", ReadOnly = true), Description(
        "Shows the names and ids of operations that use a tool position in a specific magazine of the open ENCY project. Use the magazine name or caption and T number from list_project_tools.")]
    public Task<string> ListToolOperations(int processId, string magazine, int position) =>
        Run("operations", processId, [magazine, position.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

    [McpServerTool(Name = "apply_operation_cutting"), Description(
        "Apply a supplier's speed and feed from one article condition to a named ENCY operation. " +
        "Use get_cutting_data for the condition number and list_tool_operations for the operation id. " +
        "Supply the active operation's displayed measurement system (metric or imperial), the exact speed and feed units from the row, " +
        "and either minimum or maximum. Supply previewOnly=true for before/after preview. For a constrained pass use review_cutting_conditions, its conditionKey and the same context (fixed rpm/mm/min). Applying returns values re-read from ENCY after writing, without saving the project.")]
    public Task<string> ApplyOperationCutting(string id, int condition, string boundary, string speedUnit,
        string feedUnit, int processId, string projectPath, string operationId, string operationUnits,
        CuttingPass? context=null,string? conditionKey=null,string? personalCorrectionId=null,bool previewOnly=false,bool acceptUnverified=false) =>
        !previewOnly && WritesOff() is { } off ? Task.FromResult(off) : Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(id) is not { } cid) return Ids.NotAComponent(id);
            if (boundary is not ("minimum" or "maximum") || operationUnits is not ("metric" or "imperial"))
                return "ERROR: specify minimum or maximum, and the operation units (metric or imperial).";
            IReadOnlyList<CuttingSetting> settings;
            if(context!=null) {
                if(string.IsNullOrWhiteSpace(conditionKey)||speedUnit!="rpm"||feedUnit!="mm/min")
                    return "ERROR: supply the reviewed condition key; verified fixed pairs use rpm and mm/min.";
                var review=await api.ReviewCutting(cid,context with {OnlyCondition=condition,Offset=0,Size=1},token);
                var row=review["items"]?.AsArray().FirstOrDefault(r=>(string?)r?["key"]==conditionKey);
                if(row==null)return "ERROR: the requested condition has changed or conflicts with this pass.";
                if(!previewOnly&&!acceptUnverified&&(string?)row["status"]=="partial")
                    return "ERROR: confirm the unverified pass conditions before applying this row.";
                System.Text.Json.Nodes.JsonNode? pair;
                if(personalCorrectionId==null)pair=row["fixedPairs"]?.AsArray().FirstOrDefault(p=>(string?)p?["boundary"]==boundary);
                else pair=review["personalCorrections"]?.AsArray().FirstOrDefault(p=>(string?)p?["id"]==personalCorrectionId &&
                    (string?)p?["conditionKey"]==conditionKey && (bool?)p?["withinSuppliedLimits"]==true);
                if(pair==null)return "ERROR: no verified fixed pair for this boundary, correction and supplied limits.";
                settings=[new("rpm",(double)pair["rpm"]!),new("mm/min",(double)pair["feedMmMin"]!)];
            } else {
                if(conditionKey!=null||personalCorrectionId!=null)return "ERROR: provide the matching pass context.";
                var rows = (await api.GetCompat(cid, token))?.ArticleCutting;
                if (rows == null || condition < 1 || condition > rows.Count) return "ERROR: unknown article condition.";
                settings = CuttingSetting.FromArticle(rows[condition - 1].Fields, boundary == "maximum")
                    .Where(s => s.Unit == speedUnit || s.Unit == feedUnit).ToList();
                if (!CuttingSetting.IsPair(settings) || speedUnit is not ("rpm" or "m/min"))
                    return "ERROR: select one available spindle or cutting speed and one available feed from the condition.";
            }
            return await Run(previewOnly?"cutting-preview":"cutting", processId, [projectPath, operationId, operationUnits,
                .. settings.SelectMany(s => new[] { s.Unit, s.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) })]);
        });

    [McpServerTool(Name = "install_shelf_in_project"), Description(
        "Imports a saved catalogue shelf into an open ENCY project in one call. Use a libraryId from list_libraries and the PID and exact project path from list_project_tools. Creates a component library, installs compatible tools, reports actual holders and positions. Does not save the project.")]
    public Task<string> InstallShelfInProject(string libraryId, int processId, string projectPath, string? magazine = null) =>
        WritesOff() is { } off ? Task.FromResult(off) : Replies.WithToken(tokens, async token =>
        {
            if (Ids.Of(libraryId) is not { } id) return Ids.NotALibrary(libraryId);
            var shelf = await LibraryTools.BuildShelfGtc(api, id, token);
            var file = SavedFiles.Save(shelf.Package.File, null);
            var result = await ImportProjectGtc(processId, projectPath, file, magazine);
            return $"{result}\n{shelf.Summary} GTC: {file}";
        });

    [McpServerTool(Name="get_selected_hole_features",ReadOnly=true),Description("Read ENCY-recognized holes and compound hole stages for selected model geometry before planning operations. Returns reported entrances, thread dimensions and feature-local axial levels in mm. Reads selected faces and recognized features without starting full-project recognition or modifying the model. For a project without a current operation, specify the displayed metric or imperial system.")]
    public Task<string> GetSelectedHoleFeatures(string operationUnits="auto",int? processId=null) =>
        Run("selected-hole-features",processId,[operationUnits]);

    [McpServerTool(Name = "get_drilling_operation", ReadOnly = true), Description(
        "Read the selected drilling operation, its hole dimensions, material if specified, operation id and project path. " +
         "Units default to the selected operation export; supply metric or imperial for older ENCY. Dimensions returned are mm. " +
        "Face-linked recognition returns simple or compound hole stages, entrance chamfers and thread dimensions when reported by ENCY. Step order follows the model. Unresolved dimensions or depth references require user input.")]
    public Task<string> GetDrillingOperation(string operationUnits = "auto", int? processId = null) =>
        operationUnits is "auto" or "metric" or "imperial" ? Run("drill-context", processId, [operationUnits])
            : Task.FromResult("ERROR: specify metric or imperial.");

    [McpServerTool(Name = "audit_project_tools", ReadOnly = true), Description(
        "Review the tools assigned to every operation of the open ENCY project, without changing selection or assignments. " +
        "Reports per-operation geometry checks for drilling and milling, identical hole groups, reasons for mismatches, " +
         "unverified dimensions and questions grouped across operations. Auto verifies the native export of each operation; pass displayed metric or imperial units to skip exports. " +
        "The check covers geometry only; through holes use zero additional breakthrough. No catalogue login required.")]
    public async Task<string> AuditProjectTools(string operationUnits = "auto", int? processId = null,
        [Description("Optional personal library ID for comparison with project tools. Shelf numbers or names alone do not identify project tools.")] string? libraryId = null)
    {
        if (operationUnits is not ("auto" or "metric" or "imperial")) return "ERROR: specify auto, metric or imperial.";
        if (libraryId != null && Ids.Of(libraryId) == null) return Ids.NotALibrary(libraryId);
        var raw = await Run("audit", processId, [operationUnits]);
        if (libraryId == null || raw.StartsWith("ERROR:", StringComparison.Ordinal)) return raw;
        return await Replies.WithToken(tokens, async token => {
            var library = await api.GetLibrary(Ids.Of(libraryId)!, token);
            if (library == null) return $"ERROR: no library {libraryId}.";
            var report = System.Text.Json.JsonSerializer.Deserialize<AuditReport>(raw, AuditOutput.Json)!;
            var result = JsonNode.Parse(raw)!.AsObject();
            result["personalLibraryComparison"] = System.Text.Json.JsonSerializer.SerializeToNode(new {
                libraryId = library.Id, libraryName = library.Name, source = "personalLibrary",
                items = AuditOutput.Compare(library, report.ProjectInventory)
            }, AuditOutput.Json);
            return result.ToJsonString();
        });
    }

    [McpServerTool(Name = "export_project_setup_sheet"), Description(
        "Write a UTF-8 CSV tooling setup sheet for the open project to Downloads or an absolute output folder. Lists machine, setup, " +
        "operation, assigned magazine and T number, tool, adapters, working reach, source, geometry findings and missing fields. " +
        "Use metric or imperial according to the project. Leaves operation assignments and the project file untouched.")]
    public async Task<string> ExportProjectSetupSheet(string operationUnits = "auto", int? processId = null, string? outputFolder = null)
    {
        var raw = await AuditProjectTools(operationUnits, processId);
        if (raw.StartsWith("ERROR:", StringComparison.Ordinal)) return raw;
        var report = System.Text.Json.JsonSerializer.Deserialize<AuditReport>(raw, AuditOutput.Json)!;
        var filename = (Path.GetFileNameWithoutExtension(report.ProjectPath) is {Length: >0} name ? name : "Project") + "-setup.csv";
        var path = SavedFiles.Save(new DownloadedFile(filename, AuditOutput.SetupCsv(report)), outputFolder);
        return $"Saved {report.Operations.Count} operation rows: {path}";
    }

    [McpServerTool(Name="get_milling_operation", ReadOnly=true), Description("Read the selected ENCY milling operation. Recognized geometry can provide pocket/side height, inside radius and available cutter width in mm. widthSource identifies a rectangular inside contour, a rectangular pocket floor or parallel walls of a recognized slot. bottomRadiusMm comes from the recognized closed-bottom pocket; zero is a verified square floor corner, null requires clarification. Check bottomRadiusSource.")]
    public Task<string> GetMillingOperation(string operationUnits="auto",int? processId=null) =>
        Run("mill-context",processId,[operationUnits]);

    private static string[] GeometryArgs(string units,string mode,double width,double depth,double extra,double? radius) =>
        [units,mode,width.ToString("R",System.Globalization.CultureInfo.InvariantCulture),depth.ToString("R",System.Globalization.CultureInfo.InvariantCulture),
         extra.ToString("R",System.Globalization.CultureInfo.InvariantCulture),radius?.ToString("R",System.Globalization.CultureInfo.InvariantCulture)??""];

    [McpServerTool(Name="find_tools_for_selected_operation", ReadOnly=true), Description(
        "Search the selected project's installed magazine/tools first, then matching warehouse assemblies and catalogue cutters. " +
        "mode: tip/cylinder/through for drilling, slot/shoulder for milling. widthMm is the exact hole diameter or available milling width. " +
        "Omit unknown geometry to read it from the operation. Missing fields can reuse verified local answers; set useRemembered=false to ignore them. All dimensions in mm. operationUnits defaults to auto, read from the selected operation. Tools in unverified lack a value needed to confirm the fit. Use ID and Identity from installed for assignment.")]
    public async Task<string> FindToolsForSelectedOperation(string operationUnits = "auto", string? mode = null, double? widthMm = null,
        double? depthMm = null, int? processId = null, double? breakthroughMm = null, double? innerRadiusMm = null,
        string material = "", string? spindle = null, int page = 0, int size = 20, bool? noInsideCorner = null,
        double? bottomRadiusMm = null,double? passDepthMm = null,bool useRemembered=true)
    {
        var raw = await Run("operation-context", processId, [operationUnits]);
        if (raw.StartsWith("ERROR", StringComparison.Ordinal)) return raw;
        var context = JsonNode.Parse(raw)!.AsObject();
        var brief = OperationBrief.FromContext(context, mode, widthMm, depthMm, breakthroughMm ?? 0, innerRadiusMm,
            noInsideCorner, material, spindle, page, size);
        if(bottomRadiusMm.HasValue && brief.BottomRadius.HasValue && Math.Abs(bottomRadiusMm.Value-brief.BottomRadius.Value)>1e-7)
            return "ERROR: supplied floor radius conflicts with the recognized pocket.";
        if(bottomRadiusMm.HasValue) brief=brief with{BottomRadius=bottomRadiusMm,BottomRadiusSource="user"};
        if(passDepthMm.HasValue) brief=brief with{PassDepth=passDepthMm};
        var rememberedFields=new List<string>();
        if(useRemembered && context["projectPath"]?.GetValue<string>() is {} path && Path.IsPathFullyQualified(path) &&
           context["processId"]?.GetValue<int>() is int pid && context["operationId"]?.GetValue<string>() is {} id) {
            var units=context["operationUnits"]!.GetValue<string>();
            var lookup=await OperationAnswerStore.ForProject(path).ForSelection(path,id,pid,units,async ()=>{
                var data=await Run("audit",pid,[units]);
                return data.StartsWith("ERROR",StringComparison.Ordinal) ? null :
                    System.Text.Json.JsonSerializer.Deserialize<AuditReport>(data,AuditOutput.Json);
            });
            if(lookup.Error!=null)return lookup.Error;
            brief=OperationAnswerStore.FillMissing(brief,lookup.Answer,breakthroughMm.HasValue,rememberedFields);
        }
        return await Replies.WithToken(tokens, async token => {
            var result = await api.FindOperationTools(brief, token);
            result["operationId"] = context["operationId"]?.DeepClone();
            result["operationUnits"] = context["operationUnits"]?.DeepClone();
            result["unitsSource"] = context["unitsSource"]?.DeepClone();
            if(rememberedFields.Count>0)result["rememberedFields"]=System.Text.Json.JsonSerializer.SerializeToNode(rememberedFields);
            if(context["holes"] is JsonArray holes) {
                var features=holes.SelectMany(h=>h?["recognized"] as JsonArray ?? []).Where(h=>h!=null)
                    .DistinctBy(h=>h!["assignedNode"]?.ToString()+"|"+h["featureId"]?.ToString());
                result["recognizedHoleGeometry"]=new JsonArray(features.Select(h=>h!.DeepClone()).ToArray());
            }
            if (context.ContainsKey("features"))
                result["widthSource"] = brief.WidthSource;
            result["projectPath"] = context["projectPath"]?.DeepClone();
            if(brief.Operation=="pocket"){
                result["bottomRadiusSource"]=brief.BottomRadiusSource;
                result["installedFitScope"]="Catalogue only: this ENCY inventory does not verify installed cutter corner radii.";
                return result.ToJsonString();
            }
            if (result["status"]?.GetValue<string>() != "ready") return result.ToJsonString();
            var fit = await Run("machine-fit", context["processId"]?.GetValue<int>() ?? processId,
                GeometryArgs(context["operationUnits"]!.GetValue<string>(), brief.Bottom ?? brief.Operation!, (brief.Diameter ?? brief.Width)!.Value,
                    brief.Depth!.Value, brief.Breakthrough ?? 0, brief.InnerRadius));
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
        WritesOff() is { } off ? Task.FromResult(off) :
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
        WritesOff() is { } off ? Task.FromResult(off) : Replies.WithToken(tokens, async token =>
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

    private static async Task<string> Run(string operation, int? pid, string[] arguments, string? input = null,CancellationToken cancellationToken=default)
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
                RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input != null, CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
            if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            start.ArgumentList.Add("--ency-ipc");
            start.ArgumentList.Add(operation);
            start.ArgumentList.Add(pid.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in arguments) start.ArgumentList.Add(value);
            if (operation=="tool-plan" && arguments[0]=="apply" && input!=null)
                return await LiveBatchWorker.Run(start,input,cancellationToken);
            if (operation == "assembly-simulation") SimulationScratch.Sweep(Path.GetTempPath(), DateTime.UtcNow, TimeSpan.FromHours(1));
            using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start ENCY IPC.");
            var stdout = worker.StandardOutput.ReadToEndAsync();
            var stderr = worker.StandardError.ReadToEndAsync();
            async Task SendInput() {
                if (input == null) return;
                try { await worker.StandardInput.WriteAsync(input); }
                catch (IOException) { /* A worker can exit before reading; its own error is on stderr. */ }
                finally { worker.StandardInput.Close(); }
            }
            var write = SendInput();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(operation == "assembly-simulation" ? 15 : 4));
            try { await worker.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException)
            {
                if (!worker.HasExited) worker.Kill(entireProcessTree: operation == "assembly-simulation");
                return "ERROR: ENCY did not reply in time. Check the magazine before repeating an import.";
            }
            await write;
            var reply = (await stdout).Trim();
            var detail = (await stderr).Trim();
            if (worker.ExitCode == 0) return reply;
            return "ERROR: " + (detail.Length > 0 ? detail : $"ENCY IPC stopped ({worker.ExitCode}). Check the project before retrying.");
        }
        catch (Exception e) { return "ERROR: " + e.Message; }
    }
}
